// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundry.Core.Services.Autopilot;
using Foundry.Core.Services.Configuration;
using Foundry.Deploy.Models;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Configuration;
using Foundry.Deploy.Services.Deployment;
using Foundry.Deploy.Services.Deployment.Steps;
using Foundry.Deploy.Services.Deployment.Unattend;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Localization;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Operations;
using Foundry.Deploy.Services.Security;
using Foundry.Deploy.Services.System;
using Foundry.Deploy.ViewModels;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using CoreFile = Foundry.Core.Models.Configuration.Deploy.DeployUnattendFile;
using CoreSettings = Foundry.Core.Models.Configuration.Deploy.DeployUnattendSettings;

namespace Foundry.Deploy.Tests;

public sealed class UnattendRuntimeTests
{
    [Fact]
    public async Task Snapshot_WhenMediaChanges_StagesOriginalBytesAndCannotBeReusedAfterDisposal()
    {
        using var fixture = new Fixture();
        using UnattendSnapshot snapshot = fixture.Service.Read(fixture.Selection, "x64", false, AutopilotProvisioningMode.JsonProfile);
        File.Delete(fixture.Selection.AssetPath);
        await snapshot.StageAsync(fixture.Target, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Content, File.ReadAllBytes(Path.Combine(fixture.Target, "Windows", "Panther", "unattend.xml")));
        snapshot.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.StageAsync(fixture.Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Snapshot_WhenAnswerFileExists_ReplacesItWithExactValidatedBytes()
    {
        using var fixture = new Fixture();
        using UnattendSnapshot snapshot = fixture.Service.Read(fixture.Selection, "x64", false, AutopilotProvisioningMode.JsonProfile);
        string directory = Path.Combine(fixture.Target, "Windows", "Panther");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "unattend.xml");
        File.WriteAllText(path, "<existing-answer-file />");

        await snapshot.StageAsync(fixture.Target, TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Content, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(directory, ".foundry-unattend-*.tmp"));
    }

    [Fact]
    public async Task Snapshot_WhenCancelled_PreservesExistingAnswerFileWithoutTemporaryPlaintext()
    {
        using var fixture = new Fixture();
        using UnattendSnapshot snapshot = fixture.Service.Read(fixture.Selection, "x64", false, AutopilotProvisioningMode.JsonProfile);
        string directory = Path.Combine(fixture.Target, "Windows", "Panther");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "unattend.xml");
        byte[] existingContent = Encoding.UTF8.GetBytes("<existing-answer-file />");
        File.WriteAllBytes(path, existingContent);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot.StageAsync(fixture.Target, cancellation.Token));

        Assert.Equal(existingContent, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(directory, ".foundry-unattend-*.tmp"));
    }

    [Fact]
    public async Task Snapshot_WhenPublicationFails_PreservesExistingAnswerFileAndRemovesTemporaryPlaintext()
    {
        using var fixture = new Fixture();
        using UnattendSnapshot snapshot = fixture.Service.Read(fixture.Selection, "x64", false, AutopilotProvisioningMode.JsonProfile);
        string directory = Path.Combine(fixture.Target, "Windows", "Panther");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "unattend.xml");
        byte[] existingContent = Encoding.UTF8.GetBytes("<existing-answer-file />");
        File.WriteAllBytes(path, existingContent);

        using (var destinationLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = await Record.ExceptionAsync(() => snapshot.StageAsync(fixture.Target, TestContext.Current.CancellationToken));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(existingContent, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(directory, ".foundry-unattend-*.tmp"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("architecture")]
    [InlineData("envelope")]
    [InlineData("locked")]
    public void Read_WhenSelectedAssetIsInvalid_ReturnsSafeError(string failure)
    {
        using var fixture = new Fixture();
        UnattendSelection selection = fixture.Selection;
        if (failure == "missing") File.Delete(selection.AssetPath);
        if (failure == "hash") selection = selection with { File = selection.File with { ContentHash = new string('0', 64) } };
        if (failure == "envelope") File.WriteAllText(selection.AssetPath, "{secret-marker-invalid-envelope}");
        if (failure == "locked") fixture.Session.Clear();
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => fixture.Service.Read(selection,
            failure == "architecture" ? "arm64" : "amd64", false, AutopilotProvisioningMode.JsonProfile));
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret-marker", exception.ToString());
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Theory]
    [InlineData(AutopilotProvisioningMode.JsonProfile, true)]
    [InlineData(AutopilotProvisioningMode.InteractiveHardwareHashUpload, true)]
    [InlineData(AutopilotProvisioningMode.HardwareHashUpload, false)]
    public void Read_WhenAutologonTakesOverOobe_BlocksEnrollmentButAllowsHashRegistration(AutopilotProvisioningMode mode, bool blocked)
    {
        using var fixture = new Fixture("<AutoLogon><Enabled>true</Enabled></AutoLogon>", "oobeSystem");
        if (blocked)
            Assert.Throws<InvalidOperationException>(() => fixture.Service.Read(fixture.Selection, "amd64", true, mode));
        else
        {
            using UnattendSnapshot snapshot = fixture.Service.Read(fixture.Selection, "amd64", true, mode);
            Assert.True(snapshot.Inspection.ConflictsWithAutopilot);
        }
    }

    [Fact]
    public void Selection_WhenCustomSelected_SuppressesNameValidationAndRestoresNativeChoice()
    {
        using var fixture = new Fixture();
        using var preparation = new DeploymentPreparationViewModel(new LocalizationService(), false, fixture.Service);
        preparation.TargetComputerName = "NATIVE-PC";
        preparation.ApplyUnattendConfiguration(new CoreSettings { IsEnabled = true, DefaultFileId = fixture.Selection.File.Id, Files = [fixture.Selection.File] }, fixture.ConfigurationPath);
        Assert.True(preparation.UsesCustomUnattend);
        Assert.True(preparation.IsTargetComputerNameValid);
        Assert.True(preparation.IsComputerNameInputReadOnly);
        Assert.True(preparation.IsUnattendSelectionValid);
        preparation.TargetComputerName = "";
        Assert.True(preparation.IsTargetComputerNameValid);
        preparation.SelectedUnattendOption = preparation.UnattendOptions[0];
        Assert.False(preparation.IsTargetComputerNameValid);
        Assert.Equal("", preparation.TargetComputerName);
        preparation.TargetComputerName = "NATIVE-PC";
        preparation.SelectedUnattendOption = preparation.UnattendOptions[1];
        preparation.SelectedUnattendOption = preparation.UnattendOptions[0];
        Assert.Equal("NATIVE-PC", preparation.EffectiveComputerName);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("custom")]
    [InlineData("missing")]
    public void Selection_WhenLanguageChanges_PreservesChoiceAndMissingDefaultError(string mode)
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            using var fixture = new Fixture();
            var localization = new LocalizationService();
            localization.SetCulture(CultureInfo.GetCultureInfo("en-US"));
            using var preparation = new DeploymentPreparationViewModel(localization, false, fixture.Service);
            preparation.ApplyUnattendConfiguration(new CoreSettings
            {
                IsEnabled = true,
                DefaultFileId = mode == "native" ? null : mode == "custom" ? fixture.Selection.File.Id : "missing",
                Files = [fixture.Selection.File]
            }, fixture.ConfigurationPath);
            UnattendSelection? selection = preparation.SelectedUnattend;

            localization.SetCulture(CultureInfo.GetCultureInfo("fr-FR"));

            Assert.Same(selection, preparation.SelectedUnattend);
            Assert.Equal(mode != "missing", preparation.IsUnattendSelectionValid);
            Assert.Equal(localization.GetString("Unattend.Native"), preparation.UnattendOptions[0].DisplayName);
            if (mode == "native")
                Assert.Same(preparation.UnattendOptions[0], preparation.SelectedUnattendOption);
            if (mode == "custom")
                Assert.Equal(localization.GetString("Unattend.HookCompatibility"), preparation.UnattendWarning);
            if (mode == "missing")
            {
                Assert.Null(preparation.SelectedUnattendOption);
                Assert.Equal(localization.GetString("Unattend.MissingDefault"), preparation.UnattendValidationMessage);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Selection_WhenDefaultIsMissing_DoesNotFallBackToNative()
    {
        using var fixture = new Fixture();
        using var preparation = new DeploymentPreparationViewModel(new LocalizationService(), false, fixture.Service);
        preparation.ApplyUnattendConfiguration(new CoreSettings { IsEnabled = true, DefaultFileId = "missing", Files = [fixture.Selection.File] }, fixture.ConfigurationPath);
        Assert.Null(preparation.SelectedUnattendOption);
        Assert.False(preparation.IsUnattendSelectionValid);
    }

    [Fact]
    public void Selection_WhenArchitectureChanges_RevalidatesAndRecovers()
    {
        using var fixture = new Fixture();
        using var preparation = new DeploymentPreparationViewModel(new LocalizationService(), false, fixture.Service);
        preparation.ApplyUnattendConfiguration(new CoreSettings { IsEnabled = true, DefaultFileId = fixture.Selection.File.Id, Files = [fixture.Selection.File] }, fixture.ConfigurationPath);
        preparation.UpdateUnattendContext("arm64");
        Assert.False(preparation.IsUnattendSelectionValid);
        preparation.UpdateUnattendContext("x64");
        Assert.True(preparation.IsUnattendSelectionValid);
    }

    [Fact]
    public async Task CustomSteps_SkipNativeXmlAndAccountSecrets_ButRetainAiPolicies()
    {
        using var fixture = new Fixture();
        var runner = new RecordingProcessRunner();
        var service = new WindowsDeploymentService(runner, NullLogger<WindowsDeploymentService>.Instance);
        using DeploymentStepExecutionContext context = fixture.CreateContext();
        context.RuntimeState.Oobe = new DeployOobeSettings { IsEnabled = true, EnableAdministratorAccount = true, AdministratorPasswordSecret = new() };
        context.RuntimeState.AiComponentRemoval = new DeployAiComponentRemovalSettings { IsEnabled = true, DisableRecall = true };
        Assert.Equal(DeploymentStepState.Skipped, (await new ConfigureTargetComputerNameStep(service).ExecuteAsync(context, TestContext.Current.CancellationToken)).State);
        await new ConfigureOobeSettingsStep(service).ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.NotEmpty(runner.Arguments);
        Assert.Contains(runner.Arguments, args => args.Contains("DisableAIDataAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Arguments, args => args.Contains("AllowTelemetry", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "Windows", "Panther", "unattend.xml")));
    }

    [Fact]
    public async Task ValidationAndStaging_KeepSnapshotAndClearAfterStaging()
    {
        using var fixture = new Fixture();
        using DeploymentStepExecutionContext context = fixture.CreateContext();
        await new ValidateCustomUnattendStep(fixture.Service).ExecuteAsync(context, TestContext.Current.CancellationToken);
        File.Delete(fixture.Selection.AssetPath);
        await new StageCustomUnattendStep().ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Content, File.ReadAllBytes(Path.Combine(fixture.Target, "Windows", "Panther", "unattend.xml")));
        Assert.Null(context.UnattendSnapshot);
    }

    [Fact]
    public async Task DryRun_DoesNotWritePlaintextOrTouchTarget()
    {
        using var fixture = new Fixture();
        using DeploymentStepExecutionContext context = fixture.CreateContext(dryRun: true);
        await new ValidateCustomUnattendStep(fixture.Service).ExecuteAsync(context, TestContext.Current.CancellationToken);
        await new StageCustomUnattendStep().ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(fixture.Target));
        Assert.Null(context.UnattendSnapshot);
    }

    [Fact]
    public void ProtectionDetector_WhenOrphanedAssetExists_RequiresProtectionEvenWithoutManifest()
    {
        using var fixture = new Fixture();
        Assert.True(DeploymentProtectionDetector.HasProtectedArtifacts(new DeployConfigurationLoadResult { ConfigurationPath = fixture.ConfigurationPath }, fixture.Root));
    }

    [Fact]
    public async Task Orchestration_WhenCustomAssetInvalid_DoesNotReachDiskPreparation()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Selection.AssetPath);
        bool diskPrepared = false;
        IDeploymentStep[] steps = DeploymentStepNames.ExecutionOrder.Select(name => name == DeploymentStepNames.ValidateCustomUnattend
            ? (IDeploymentStep)new ValidateCustomUnattendStep(fixture.Service)
            : new CallbackStep(name, _ => { if (name == DeploymentStepNames.PrepareTargetDiskLayout) diskPrepared = true; })).ToArray();
        using DeploymentStepExecutionContext template = fixture.CreateContext();
        var orchestrator = new DeploymentOrchestrator(new OperationProgressService(), new Logs(), new Disks(), steps,
            new Foundry.Telemetry.NullTelemetryService(), NullLogger<DeploymentOrchestrator>.Instance);

        DeploymentResult result = await orchestrator.RunAsync(template.Request, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(diskPrepared);
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orchestration_WhenStoppedAfterValidation_DisposesCredentialSnapshot(bool cancelled)
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        UnattendSnapshot? retained = null;
        IDeploymentStep[] steps = DeploymentStepNames.ExecutionOrder.Select(name => name == DeploymentStepNames.ValidateCustomUnattend
            ? (IDeploymentStep)new ValidateCustomUnattendStep(fixture.Service)
            : new CallbackStep(name, context =>
            {
                if (name != DeploymentStepNames.ValidateTargetConfiguration) return;
                retained = context.UnattendSnapshot;
                if (cancelled) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("Synthetic failure after validation.");
            })).ToArray();
        using DeploymentStepExecutionContext template = fixture.CreateContext();
        var orchestrator = new DeploymentOrchestrator(new OperationProgressService(), new Logs(), new Disks(), steps,
            new Foundry.Telemetry.NullTelemetryService(), NullLogger<DeploymentOrchestrator>.Instance);

        DeploymentResult result = await orchestrator.RunAsync(template.Request, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.NotNull(retained);
        await Assert.ThrowsAsync<InvalidOperationException>(() => retained.StageAsync(fixture.Target, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Fact]
    public void Launch_CustomModeRetainsIndependentFeaturesAndOmitsNativeCredentials()
    {
        using var fixture = new Fixture();
        var shell = new Shell();
        var service = new DeploymentLaunchPreparationService(shell, fixture.Service);
        var ai = new DeployAiComponentRemovalSettings { IsEnabled = true, DisableRecall = true };
        DeploymentLaunchRequest request = fixture.CreateLaunchRequest() with
        {
            Oobe = new DeployOobeSettings { IsEnabled = true, EnableAdministratorAccount = true, AdministratorPasswordSecret = new() },
            DefaultTimeZoneId = "Romance Standard Time",
            AiComponentRemoval = ai
        };
        DeploymentLaunchPreparationResult result = service.Prepare(request);
        Assert.True(result.IsReadyToStart);
        Assert.NotNull(result.Context);
        Assert.True(result.Context.UsesCustomUnattend);
        Assert.Equal("", result.Context.TargetComputerName);
        Assert.Null(result.Context.DefaultTimeZoneId);
        Assert.False(result.Context.Oobe.IsEnabled);
        Assert.Null(result.Context.Oobe.AdministratorPasswordSecret);
        Assert.Same(ai, result.Context.AiComponentRemoval);
        Assert.Equal(1, shell.Confirmations);
    }

    [Fact]
    public void Launch_WhenCustomAssetDisappears_FailsBeforeConfirmation()
    {
        using var fixture = new Fixture();
        var shell = new Shell();
        File.Delete(fixture.Selection.AssetPath);
        DeploymentLaunchPreparationResult result = new DeploymentLaunchPreparationService(shell, fixture.Service).Prepare(fixture.CreateLaunchRequest());
        Assert.False(result.IsReadyToStart);
        Assert.Equal(0, shell.Confirmations);
    }

    [Fact]
    public void Summary_CustomModeShowsManagedOobeAndRetainsAi()
    {
        IReadOnlyList<DeploymentSummaryRowViewModel> rows = WindowsCustomizationSummaryBuilder.Build(
            new DeployOobeSettings { IsEnabled = true, EnableAdministratorAccount = true }, new(),
            new DeployAiComponentRemovalSettings { IsEnabled = true, DisableRecall = true }, new(), key => key,
            System.Globalization.CultureInfo.InvariantCulture, usesCustomUnattend: true);
        Assert.Contains(rows, row => row.Value == "Unattend.Managed");
        Assert.DoesNotContain(rows, row => row.Label == "Summary.BuiltInAdministrator");
        Assert.Contains(rows, row => row.Label == "Summary.AiComponentRemoval");
    }

    [Fact]
    public void ProtectionDetector_IgnoresUnrelatedFilesInAssetFolder()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Selection.AssetPath);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Selection.AssetPath)!, "operator-notes.txt"), "notes");
        Assert.False(DeploymentProtectionDetector.HasProtectedArtifacts(new DeployConfigurationLoadResult { ConfigurationPath = fixture.ConfigurationPath }, fixture.Root));
    }

    private sealed class CallbackStep(string name, Action<DeploymentStepExecutionContext> callback) : IDeploymentStep
    {
        public string Name => name;
        public Task<DeploymentStepResult> ExecuteAsync(DeploymentStepExecutionContext context, CancellationToken cancellationToken)
        { callback(context); return Task.FromResult(DeploymentStepResult.Succeeded("Test step completed.")); }
    }

    private sealed class Shell : Foundry.Deploy.Services.ApplicationShell.IApplicationShellService
    {
        public int Confirmations { get; private set; }
        public void ShowAbout() { }
        public void ShowBlockingError(string title, string message) { }
        public void Shutdown() { }
        public bool ConfirmWarning(string title, string message) { Confirmations++; return true; }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "foundry-unattend-test-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "Target");
        public string ConfigurationPath => Path.Combine(Root, "Config", "foundry.deploy.config.json");
        public DeploymentSecretKeySession Session { get; } = new();
        public UnattendContentService Service { get; }
        public UnattendSelection Selection { get; }
        public byte[] Content { get; }
        public Fixture(string settings = "<ComputerName>CUSTOM-PC</ComputerName>", string pass = "specialize")
        {
            Content = Encoding.UTF8.GetBytes($"<?xml version='1.0' encoding='utf-8'?><unattend xmlns='urn:schemas-microsoft-com:unattend'><settings pass='{pass}'><component name='Microsoft-Windows-Shell-Setup' processorArchitecture='amd64'>{settings}</component></settings><!-- preserved --></unattend>");
            var file = new CoreFile { Id = Guid.NewGuid().ToString("N"), DisplayName = "Custom", ContentHash = Convert.ToHexString(SHA256.HashData(Content)) };
            string directory = Path.Combine(Root, "Config", "Unattend");
            Directory.CreateDirectory(directory);
            Selection = new UnattendSelection(file, Path.Combine(directory, UnattendFileService.GetAssetFileName(file.Id)));
            byte[] key = RandomNumberGenerator.GetBytes(32);
            Session.SetKey(key);
            File.WriteAllText(Selection.AssetPath, JsonSerializer.Serialize(MediaSecretEnvelopeProtector.EncryptBytes(Content, key, MediaSecretEnvelopeProtector.DeploymentKeyId)));
            CryptographicOperations.ZeroMemory(key);
            Service = new UnattendContentService(Session);
        }
        public DeploymentStepExecutionContext CreateContext(bool dryRun = false)
        {
            var request = new DeploymentContext { Unattend = Selection, Mode = DeploymentMode.Iso, CacheRootPath = Root, TargetDiskNumber = 0, TargetComputerName = "", OperatingSystem = new OperatingSystemCatalogItem { Architecture = "amd64" }, DriverPackSelectionKind = DriverPackSelectionKind.None, IsDryRun = dryRun };
            return new DeploymentStepExecutionContext(request, new DeploymentRuntimeState { WorkspaceRoot = Root, TargetWindowsPartitionRoot = Target, TargetFoundryRoot = Path.Combine(Target, "Foundry"), IsDryRun = dryRun }, DeploymentStepNames.ExecutionOrder, new OperationProgressService(), new Logs(), new Disks(), _ => { });
        }
        public DeploymentLaunchRequest CreateLaunchRequest() => new()
        {
            Unattend = Selection,
            Mode = DeploymentMode.Iso,
            CacheRootPath = Root,
            TargetComputerName = "",
            SelectedTargetDisk = new TargetDiskInfo { DiskNumber = 1, IsSelectable = true },
            SelectedOperatingSystem = new OperatingSystemCatalogItem { Architecture = "amd64" },
            DriverPackSelectionKind = DriverPackSelectionKind.None,
            SelectedDriverPack = null,
            ApplyFirmwareUpdates = true,
            IsAutopilotEnabled = false,
            AutopilotProvisioningMode = AutopilotProvisioningMode.JsonProfile,
            SelectedAutopilotProfile = null,
            IsDryRun = false
        };
        public void Dispose() { Session.Dispose(); CryptographicOperations.ZeroMemory(Content); Directory.Delete(Root, true); }
    }

    private sealed class Logs : IDeploymentLogService
    {
        public DeploymentLogSession Initialize(string rootPath) => new() { RootPath = rootPath, LogsDirectoryPath = Path.Combine(rootPath, "Logs"), StateDirectoryPath = Path.Combine(rootPath, "State"), StateFilePath = Path.Combine(rootPath, "State", "state.json") };
        public Task AppendAsync(DeploymentLogSession session, DeploymentLogLevel level, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveStateAsync<TState>(DeploymentLogSession session, TState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Disks : ITargetDiskService
    {
        public Task<IReadOnlyList<TargetDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetDiskInfo>>([]);
        public Task<int?> GetDiskNumberForPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
    }
    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<string> Arguments { get; } = [];
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default) { Arguments.Add(arguments); return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 }); }
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default) => RunAsync(fileName, string.Join(" ", arguments), workingDirectory, cancellationToken);
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default) => RunAsync(fileName, arguments, workingDirectory, cancellationToken);
    }
}
