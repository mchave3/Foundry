// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundry.Utilities.Hardware;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Foundry.Core.Models.Configuration;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Autopilot;
using Foundry.Deploy.Services.Security;
using Foundry.Deploy.Services.System;
using Foundry.Deploy.Services.Logging;
using Foundry.Deploy.Services.Deployment.Unattend;
using ComputerNameRules = Foundry.Core.Services.Configuration.ComputerNameRules;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Performs destructive disk layout, offline Windows image servicing, boot configuration, and WinRE operations.
/// </summary>
public sealed class WindowsDeploymentService : IWindowsDeploymentService
{
    private static readonly TimeSpan MetadataExecutionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NativeExecutionTimeout = TimeSpan.FromHours(4);
    private const string WinReImageFileName = "winre.wim";
    private const string AdministratorActivationDescription = "Enable built-in Administrator account";
    private const string AdministratorActivationCommand =
        "powershell.exe -NoProfile -NonInteractive -Command \"Get-LocalUser|Where-Object SID -like '*-500'|Enable-LocalUser -ErrorAction Stop\"";
    private readonly Func<WindowsFirmwareType> _readFirmware;
    private readonly Action<DeploymentPartitionIdentity> _setRecoveryAttributes;
    private readonly Func<string, bool> _fileExists;
    private DeploymentTargetLayout? _preparedLayout;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<WindowsDeploymentService> _logger;
    private readonly UnattendDocumentService _unattendDocumentService;
    private readonly OobePolicyRegistryWriter _oobePolicyRegistryWriter;
    private readonly AiComponentRemovalRegistryWriter _aiComponentRemovalRegistryWriter;
    private readonly IDeploymentSecretKeyProvider? _deploymentSecretKeyProvider;

    /// <summary>
    /// Initializes a Windows deployment service.
    /// </summary>
    /// <param name="processRunner">The process runner used for diskpart, DISM, bcdboot, and winrecfg.</param>
    /// <param name="logger">The logger used for deployment diagnostics.</param>
    /// <param name="deploymentSecretKeyProvider">The provider used to decrypt account passwords at the unattend-writing boundary.</param>
    public WindowsDeploymentService(
        IProcessRunner processRunner,
        ILogger<WindowsDeploymentService> logger,
        IDeploymentSecretKeyProvider? deploymentSecretKeyProvider = null)
        : this(processRunner, logger, WindowsFirmwareInspector.GetCurrent, RecoveryPartitionAttributes.Apply, deploymentSecretKeyProvider)
    {
    }

    internal WindowsDeploymentService(IProcessRunner processRunner, ILogger<WindowsDeploymentService> logger,
        Func<WindowsFirmwareType> readFirmware, Action<DeploymentPartitionIdentity> setRecoveryAttributes,
        IDeploymentSecretKeyProvider? deploymentSecretKeyProvider = null, Func<string, bool>? fileExists = null)
    {
        _readFirmware = readFirmware;
        _setRecoveryAttributes = setRecoveryAttributes;
        _fileExists = fileExists ?? File.Exists;
        _processRunner = processRunner;
        _logger = logger;
        _unattendDocumentService = new UnattendDocumentService();
        _oobePolicyRegistryWriter = new OobePolicyRegistryWriter(processRunner);
        _aiComponentRemovalRegistryWriter = new AiComponentRemovalRegistryWriter(processRunner);
        _deploymentSecretKeyProvider = deploymentSecretKeyProvider;
    }

    /// <inheritdoc />
    public async Task<DeploymentTargetLayout> PrepareTargetDiskAsync(
        TargetDiskIdentity expectedDisk,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDisk);
        _preparedLayout = null;
        if (_readFirmware() != WindowsFirmwareType.Uefi)
            throw new InvalidOperationException("Deployment requires UEFI boot mode.");
        if (expectedDisk.DiskNumber < 0 || expectedDisk.SizeBytes == 0 ||
            string.IsNullOrWhiteSpace(expectedDisk.BusType) ||
            (string.IsNullOrWhiteSpace(expectedDisk.UniqueId) && string.IsNullOrWhiteSpace(expectedDisk.SerialNumber)))
            throw new InvalidOperationException("The confirmed target disk identity is incomplete.");
        (char systemLetter, char windowsLetter, char recoveryLetter) = GetPartitionLetters();
        Directory.CreateDirectory(workingDirectory);
        ProcessExecutionResult result = await RunStorageScriptAsync(
            TargetDiskPreparationScript.Create(expectedDisk, systemLetter, windowsLetter, recoveryLetter),
            workingDirectory, cancellationToken, NativeExecutionTimeout).ConfigureAwait(false);
        result.EnsureCompleteOutput();
        PreparedPartitions partitions = JsonSerializer.Deserialize<PreparedPartitions>(result.StandardOutput)
            ?? throw new InvalidOperationException("The prepared partition layout is unavailable.");
        partitions.System.Validate();
        partitions.Windows.Validate();
        partitions.Recovery.Validate();
        var layout = new DeploymentTargetLayout
        {
            DiskNumber = expectedDisk.DiskNumber,
            DiskIdentity = expectedDisk,
            SystemPartition = partitions.System,
            WindowsPartition = partitions.Windows,
            RecoveryPartition = partitions.Recovery,
            SystemPartitionRoot = partitions.System.VolumeRoot,
            WindowsPartitionRoot = partitions.Windows.VolumeRoot,
            RecoveryPartitionRoot = partitions.Recovery.VolumeRoot,
            RecoveryPartitionLetter = partitions.Recovery.DriveLetter
        };
        await RunStorageScriptAsync(TargetDiskPreparationScript.Validate(expectedDisk, partitions.Recovery),
            workingDirectory, cancellationToken).ConfigureAwait(false);
        _setRecoveryAttributes(partitions.Recovery);
        _preparedLayout = layout;
        return layout;
    }

    private sealed record PreparedPartitions(DeploymentPartitionIdentity System, DeploymentPartitionIdentity Windows, DeploymentPartitionIdentity Recovery);

    private async Task<ProcessExecutionResult> RunStorageScriptAsync(string script, string workingDirectory, CancellationToken cancellationToken, TimeSpan? executionTimeout = null)
    {
        ProcessExecutionResult result = await _processRunner.RunAsync("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(PowerShellCommand.CreateEncodedArguments(script)),
            workingDirectory, cancellationToken, executionTimeout ?? MetadataExecutionTimeout).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new DeploymentProcessException("The confirmed disk or partition operation failed.", result.ExitCode);
        return result;
    }
    /// <inheritdoc />
    public async Task<int> ResolveImageIndexAsync(
        string imagePath,
        string requestedEdition,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Operating system image was not found.", imagePath);
        }

        _logger.LogInformation("Resolving OS image index. ImagePath={ImagePath}, RequestedEdition={RequestedEdition}", imagePath, requestedEdition);
        ProcessExecutionResult execution = await _processRunner
            .RunAsync(
                "dism.exe",
                ["/English", "/Get-ImageInfo", $"/ImageFile:{imagePath}"],
                workingDirectory,
                cancellationToken,
                MetadataExecutionTimeout)
            .ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            _logger.LogError("Failed to resolve OS image index for {ImagePath}. Diagnostic={Diagnostic}", imagePath, VolumePathDiagnostics.Redact(execution.ToDiagnosticText()));
            throw new DeploymentProcessException(
                $"Unable to resolve image index for '{imagePath}'.{Environment.NewLine}{VolumePathDiagnostics.Redact(execution.ToDiagnosticText())}",
                execution.ExitCode);
        }

        execution.EnsureCompleteOutput();
        IReadOnlyList<int> imageIndexes = ParseImageIndexes(execution.StandardOutput);
        if (imageIndexes.Count == 0)
        {
            throw new InvalidOperationException($"The operating system image does not expose any image indexes: '{imagePath}'.");
        }

        WindowsEditionDefinition? requestedDefinition = WindowsEditionCatalog.Find(requestedEdition);
        if (requestedDefinition is null)
        {
            throw new InvalidOperationException($"Windows edition '{requestedEdition}' is not supported.");
        }

        var imageMetadata = new List<ImageIndexMetadata>(imageIndexes.Count);
        foreach (int imageIndex in imageIndexes)
        {
            ProcessExecutionResult detailedExecution = await _processRunner
                .RunAsync(
                    "dism.exe",
                    ["/English", "/Get-ImageInfo", $"/ImageFile:{imagePath}", $"/Index:{imageIndex}"],
                    workingDirectory,
                    cancellationToken,
                    MetadataExecutionTimeout)
                .ConfigureAwait(false);

            if (!detailedExecution.IsSuccess)
            {
                _logger.LogError(
                    "Failed to inspect OS image index {ImageIndex} for {ImagePath}. Diagnostic={Diagnostic}",
                    imageIndex,
                    imagePath,
                    VolumePathDiagnostics.Redact(detailedExecution.ToDiagnosticText()));
                throw new DeploymentProcessException(
                    $"Unable to inspect image index {imageIndex} in '{imagePath}'.{Environment.NewLine}{VolumePathDiagnostics.Redact(detailedExecution.ToDiagnosticText())}",
                    detailedExecution.ExitCode);
            }

            detailedExecution.EnsureCompleteOutput();
            imageMetadata.Add(new ImageIndexMetadata(imageIndex, ParseEditionId(detailedExecution.StandardOutput)));
        }

        ImageIndexMetadata[] matches = imageMetadata
            .Where(item => item.EditionId.Equals(requestedDefinition.EditionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length != 1)
        {
            string availableEditionIds = string.Join(
                ", ",
                imageMetadata.Select(item => $"{item.Index}: {item.EditionId}"));

            throw new InvalidOperationException(
                $"Expected exactly one '{requestedDefinition.EditionId}' image for Windows edition '{requestedDefinition.Name}' in '{imagePath}', " +
                $"but found {matches.Length}. Available edition IDs: {availableEditionIds}.");
        }

        int resolvedIndex = matches[0].Index;
        _logger.LogInformation("Resolved OS image index {ImageIndex} for ImagePath={ImagePath}", resolvedIndex, imagePath);
        return resolvedIndex;
    }

    /// <inheritdoc />
    public async Task ApplyImageAsync(
        string imagePath,
        int imageIndex,
        string windowsPartitionRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        _logger.LogInformation("Applying OS image. ImagePath={ImagePath}, Index={ImageIndex}, WindowsPartitionRoot={WindowsPartitionRoot}",
            imagePath,
            imageIndex,
            windowsPartitionRoot);
        Directory.CreateDirectory(scratchDirectory);

        string[] arguments =
        [
            "/Apply-Image",
            $"/ImageFile:{imagePath}",
            $"/Index:{imageIndex}",
            $"/ApplyDir:{windowsPartitionRoot}",
            "/CheckIntegrity",
            $"/ScratchDir:{scratchDirectory}"
        ];

        if (progress is null)
        {
            await RunRequiredProcessAsync(
                "dism.exe",
                arguments,
                workingDirectory,
                $"OS image apply failed for index {imageIndex}",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            DismProgressReporter progressReporter = new(progress);
            await RunRequiredProcessAsync(
                "dism.exe",
                arguments,
                workingDirectory,
                $"OS image apply failed for index {imageIndex}",
                cancellationToken,
                progressReporter.HandleOutput,
                progressReporter.HandleOutput).ConfigureAwait(false);

            if (progressReporter.HasReportedProgress)
            {
                progress.Report(100d);
            }
        }

        _logger.LogInformation("OS image apply completed. ImagePath={ImagePath}, Index={ImageIndex}", imagePath, imageIndex);
    }

    /// <inheritdoc />
    public async Task<string?> GetAppliedWindowsEditionAsync(
        string windowsPartitionRoot,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowsPartitionRoot))
        {
            throw new ArgumentException("Windows partition root is required.", nameof(windowsPartitionRoot));
        }

        string[] arguments =
        [
            "/English",
            $"/Image:{windowsPartitionRoot}",
            "/Get-CurrentEdition"
        ];

        ProcessExecutionResult execution = await RunRequiredProcessAsync(
            "dism.exe",
            arguments,
            workingDirectory,
            "Failed to query the applied Windows edition",
            cancellationToken, MetadataExecutionTimeout).ConfigureAwait(false);

        execution.EnsureCompleteOutput();
        Match editionMatch = Regex.Match(
            execution.StandardOutput,
            @"Current\s+Edition\s*:\s*(.+)",
            RegexOptions.IgnoreCase);

        if (!editionMatch.Success)
        {
            _logger.LogWarning("Unable to parse the applied Windows edition from DISM output.");
            return null;
        }

        string edition = editionMatch.Groups[1].Value.Trim();
        if (edition.Length == 0)
        {
            return null;
        }

        _logger.LogInformation("Detected applied Windows edition. Edition={Edition}", edition);
        return edition;
    }

    /// <inheritdoc />
    public Task ConfigureOfflineComputerNameAsync(
        string windowsPartitionRoot,
        string computerName,
        string processorArchitecture,
        string? defaultTimeZoneId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(windowsPartitionRoot))
        {
            throw new ArgumentException("Windows partition root is required.", nameof(windowsPartitionRoot));
        }

        if (!ComputerNameRules.IsValid(computerName))
        {
            throw new ArgumentException(
                "Computer name must contain 1 to 15 valid characters (letters, numbers, or hyphen).",
                nameof(computerName));
        }

        if (string.IsNullOrWhiteSpace(processorArchitecture))
        {
            _logger.LogWarning("Processor architecture was not provided when configuring the offline computer name. Falling back to amd64.");
        }

        // The specialize pass is used so computer name and time zone are applied before OOBE starts.
        XNamespace unattendNamespace = UnattendDocumentService.Namespace;
        XDocument document = _unattendDocumentService.LoadOrCreate(windowsPartitionRoot);
        XElement component = _unattendDocumentService.EnsureShellSetupComponent(document, "specialize", processorArchitecture);

        XElement computerNameElement = component.Element(unattendNamespace + "ComputerName")
            ?? new XElement(unattendNamespace + "ComputerName");

        if (computerNameElement.Parent is null)
        {
            component.Add(computerNameElement);
        }

        computerNameElement.Value = computerName;

        XElement timeZoneElement = component.Element(unattendNamespace + "TimeZone")
            ?? new XElement(unattendNamespace + "TimeZone");

        string? unattendTimeZoneId = ResolveUnattendTimeZoneId(defaultTimeZoneId);
        if (string.IsNullOrWhiteSpace(unattendTimeZoneId))
        {
            if (timeZoneElement.Parent is not null)
            {
                timeZoneElement.Remove();
            }
        }
        else
        {
            if (timeZoneElement.Parent is null)
            {
                component.Add(timeZoneElement);
            }

            timeZoneElement.Value = unattendTimeZoneId;
        }

        _unattendDocumentService.Save(windowsPartitionRoot, document);

        _logger.LogInformation(
            "Offline computer name configured. ComputerName={ComputerName}, UnattendPath={UnattendPath}, ProcessorArchitecture={ProcessorArchitecture}, DefaultTimeZoneConfigured={DefaultTimeZoneConfigured}",
            computerName,
            Path.Combine(windowsPartitionRoot, "Windows", "Panther", "unattend.xml"),
            processorArchitecture,
            !string.IsNullOrWhiteSpace(unattendTimeZoneId));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ConfigureOfflineOobeAsync(
        string windowsPartitionRoot,
        DeployOobeSettings settings,
        string processorArchitecture,
        string workingDirectory,
        string workspaceRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(windowsPartitionRoot))
        {
            throw new ArgumentException("Windows partition root is required.", nameof(windowsPartitionRoot));
        }

        Directory.CreateDirectory(workingDirectory);

        if (!settings.IsEnabled)
        {
            _logger.LogInformation("OOBE customization is disabled.");
            return;
        }

        XNamespace unattendNamespace = UnattendDocumentService.Namespace;
        XDocument document = _unattendDocumentService.LoadOrCreate(windowsPartitionRoot);
        XElement component = _unattendDocumentService.EnsureShellSetupComponent(document, "oobeSystem", processorArchitecture);
        XElement oobeElement = component.Element(unattendNamespace + "OOBE") ?? new XElement(unattendNamespace + "OOBE");
        if (oobeElement.Parent is null)
        {
            component.Add(oobeElement);
        }

        SetElementValue(oobeElement, unattendNamespace, "HideEULAPage", settings.SkipLicenseTerms ? "true" : "false");
        if (settings.HidePrivacySetup)
        {
            SetElementValue(oobeElement, unattendNamespace, "ProtectYourPC", "3");
        }
        else
        {
            RemoveElement(oobeElement, unattendNamespace, "ProtectYourPC");
        }

        SetElementValue(
            oobeElement,
            unattendNamespace,
            "HideOnlineAccountScreens",
            ShouldHideOnlineAccountScreens(settings) ? "true" : "false");

        await ApplyOobeAccountsAsync(
            document,
            component,
            settings,
            processorArchitecture,
            workspaceRootPath,
            cancellationToken).ConfigureAwait(false);

        _unattendDocumentService.Save(windowsPartitionRoot, document);

        await _oobePolicyRegistryWriter
            .ApplyAsync(windowsPartitionRoot, settings, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Offline OOBE customization configured. WindowsPartitionRoot={WindowsPartitionRoot}, DiagnosticDataLevel={DiagnosticDataLevel}, LocationAccess={LocationAccess}",
            windowsPartitionRoot,
            settings.DiagnosticDataLevel,
            settings.LocationAccess);
    }

    private async Task ApplyOobeAccountsAsync(
        XDocument document,
        XElement oobeSystemComponent,
        DeployOobeSettings settings,
        string processorArchitecture,
        string workspaceRootPath,
        CancellationToken cancellationToken)
    {
        byte[]? deploymentKey = null;
        try
        {
            if (RequiresDeploymentKey(settings))
            {
                if (_deploymentSecretKeyProvider is null)
                {
                    throw new InvalidOperationException("A deployment secret key provider is required for OOBE account passwords.");
                }

                if (string.IsNullOrWhiteSpace(workspaceRootPath))
                {
                    throw new ArgumentException("Deployment workspace root is required for encrypted OOBE account passwords.", nameof(workspaceRootPath));
                }

                deploymentKey = await _deploymentSecretKeyProvider.ReadAsync(workspaceRootPath, cancellationToken).ConfigureAwait(false);
            }

            WriteUserAccounts(oobeSystemComponent, settings, deploymentKey);
            if (settings.EnableAdministratorAccount)
            {
                WriteAdministratorActivation(document, processorArchitecture);
            }
            else
            {
                RemoveAdministratorActivation(document);
            }
        }
        finally
        {
            if (deploymentKey is not null)
            {
                CryptographicOperations.ZeroMemory(deploymentKey);
            }
        }
    }

    private static bool RequiresDeploymentKey(DeployOobeSettings settings) =>
        settings.AdministratorPasswordSecret is not null ||
        settings.AdditionalAccounts.Any(account => account.PasswordSecret is not null);

    private static bool ShouldHideOnlineAccountScreens(DeployOobeSettings settings) =>
        settings.AdditionalAccounts.Count > 0;

    private static void WriteUserAccounts(
        XElement component,
        DeployOobeSettings settings,
        byte[]? deploymentKey)
    {
        XNamespace ns = UnattendDocumentService.Namespace;
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XElement userAccounts = component.Element(ns + "UserAccounts") ?? new XElement(ns + "UserAccounts");

        if (settings.EnableAdministratorAccount)
        {
            char[] password = DecryptPassword(settings.AdministratorPasswordIsBlank, settings.AdministratorPasswordSecret, deploymentKey);
            try
            {
                userAccounts.Element(ns + "AdministratorPassword")?.Remove();
                userAccounts.Add(CreatePasswordElement(ns, "AdministratorPassword", password, "AdministratorPassword"));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            }
        }
        if (settings.AdditionalAccounts.Count > 0)
        {
            XElement localAccounts = userAccounts.Element(ns + "LocalAccounts") ?? new XElement(ns + "LocalAccounts");
            foreach (DeployOobeAdditionalAccountSettings account in settings.AdditionalAccounts)
            {
                char[] password = DecryptPassword(account.PasswordIsBlank, account.PasswordSecret, deploymentKey);
                try
                {
                    localAccounts.Elements(ns + "LocalAccount")
                        .Where(element => string.Equals(
                            element.Element(ns + "Name")?.Value,
                            account.UserName,
                            StringComparison.OrdinalIgnoreCase))
                        .Remove();
                    localAccounts.Add(
                        new XElement(ns + "LocalAccount",
                            new XAttribute(wcm + "action", "add"),
                            CreatePasswordElement(ns, "Password", password, "Password"),
                            new XElement(ns + "Description", account.UserName),
                            new XElement(ns + "DisplayName", account.UserName),
                            new XElement(ns + "Group", account.Type == OobeAccountType.Administrator ? "Administrators" : "Users"),
                            new XElement(ns + "Name", account.UserName)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
                }
            }

            if (localAccounts.Parent is null)
            {
                userAccounts.Add(localAccounts);
            }
        }
        if (userAccounts.Parent is null && userAccounts.Elements().Any())
        {
            component.Add(userAccounts);
        }
    }

    private static char[] DecryptPassword(
        bool isBlank,
        Foundry.Deploy.Models.Configuration.SecretEnvelope? secret,
        byte[]? deploymentKey)
    {
        if (isBlank)
        {
            return [];
        }

        if (secret is null || deploymentKey is null)
        {
            throw new InvalidOperationException("An encrypted OOBE account password is missing.");
        }

        return DeployMediaSecretEnvelopeProtector.DecryptDeployChars(secret, deploymentKey);
    }

    private static XElement CreatePasswordElement(
        XNamespace ns,
        string elementName,
        ReadOnlySpan<char> password,
        string hiddenValueSuffix)
    {
        if (password.IsEmpty)
        {
            return new XElement(ns + elementName,
                new XElement(ns + "Value", string.Empty),
                new XElement(ns + "PlainText", "true"));
        }

        return new XElement(ns + elementName,
            new XElement(ns + "Value", EncodeHiddenUnattendPassword(password, hiddenValueSuffix)),
            new XElement(ns + "PlainText", "false"));
    }

    private static string EncodeHiddenUnattendPassword(ReadOnlySpan<char> password, string suffix)
    {
        char[] value = new char[password.Length + suffix.Length];
        byte[]? bytes = null;
        try
        {
            password.CopyTo(value);
            suffix.AsSpan().CopyTo(value.AsSpan(password.Length));
            bytes = Encoding.Unicode.GetBytes(value);
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private void WriteAdministratorActivation(XDocument document, string processorArchitecture)
    {
        XNamespace ns = UnattendDocumentService.Namespace;
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XElement component = _unattendDocumentService.EnsureDeploymentComponent(document, "specialize", processorArchitecture);
        XElement runSynchronous = component.Element(ns + "RunSynchronous") ?? new XElement(ns + "RunSynchronous");
        runSynchronous.Elements(ns + "RunSynchronousCommand")
            .Where(IsAdministratorActivationCommand)
            .Remove();
        int order = runSynchronous.Elements(ns + "RunSynchronousCommand")
            .Select(element => int.TryParse(element.Element(ns + "Order")?.Value, out int value) ? value : 0)
            .DefaultIfEmpty()
            .Max() + 1;
        runSynchronous.Add(
            new XElement(ns + "RunSynchronousCommand",
                new XAttribute(wcm + "action", "add"),
                new XElement(ns + "Description", AdministratorActivationDescription),
                new XElement(ns + "Order", order),
                new XElement(ns + "Path", AdministratorActivationCommand)));
        if (runSynchronous.Parent is null)
        {
            component.Add(runSynchronous);
        }
    }

    private static void RemoveAdministratorActivation(XDocument document)
    {
        XNamespace ns = UnattendDocumentService.Namespace;
        XElement? component = FindDeploymentComponent(document, "specialize");
        if (component is null)
        {
            return;
        }

        XElement? runSynchronous = component.Element(ns + "RunSynchronous");
        runSynchronous?.Elements(ns + "RunSynchronousCommand")
            .Where(IsAdministratorActivationCommand)
            .Remove();
        if (runSynchronous is not null && !runSynchronous.Elements().Any())
        {
            runSynchronous.Remove();
        }
    }

    private static bool IsAdministratorActivationCommand(XElement element) =>
        string.Equals(
            element.Element(UnattendDocumentService.Namespace + "Description")?.Value,
            AdministratorActivationDescription,
            StringComparison.Ordinal);

    private static XElement? FindDeploymentComponent(XDocument document, string passName)
    {
        XNamespace ns = UnattendDocumentService.Namespace;
        return document.Root?
            .Elements(ns + "settings")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("pass")?.Value,
                passName,
                StringComparison.OrdinalIgnoreCase))?
            .Elements(ns + "component")
            .FirstOrDefault(element => string.Equals(
                element.Attribute("name")?.Value,
                "Microsoft-Windows-Deployment",
                StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task ConfigureOfflineAiComponentRemovalAsync(
        string windowsPartitionRoot,
        DeployAiComponentRemovalSettings settings,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(windowsPartitionRoot))
        {
            throw new ArgumentException("Windows partition root is required.", nameof(windowsPartitionRoot));
        }

        Directory.CreateDirectory(workingDirectory);

        if (!settings.IsEnabled || !HasAnyAiPolicyOptionEnabled(settings))
        {
            _logger.LogInformation("AI policy customization is disabled.");
            return;
        }

        await _aiComponentRemovalRegistryWriter
            .ApplyAsync(windowsPartitionRoot, settings, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Offline AI policy customization configured. WindowsPartitionRoot={WindowsPartitionRoot}, RemoveCopilot={RemoveCopilot}, DisableRecall={DisableRecall}, DisableClickToDo={DisableClickToDo}, DisableAiServiceAutoStart={DisableAiServiceAutoStart}, DisableEdgeAi={DisableEdgeAi}, DisablePaintAi={DisablePaintAi}, DisableNotepadAi={DisableNotepadAi}",
            windowsPartitionRoot,
            settings.RemoveCopilot,
            settings.DisableRecall,
            settings.DisableClickToDo,
            settings.DisableAiServiceAutoStart,
            settings.DisableEdgeAi,
            settings.DisablePaintAi,
            settings.DisableNotepadAi);
    }

    /// <inheritdoc />
    public async Task<WindowsOptionalFeatureServicingResult> ConfigureOfflineWindowsOptionalFeaturesAsync(
        string setupMediaImagePath,
        string windowsPartitionRoot,
        int appliedImageIndex,
        DeployWindowsOptionalFeatureSettings settings,
        string scratchDirectory,
        string sourceExtractionDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        Action? onInspectionStarted = null,
        Action? onSourcePreparationStarted = null,
        Action? onServicingStarted = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsEnabled || settings.Actions is null || settings.Actions.Count == 0)
        {
            return new WindowsOptionalFeatureServicingResult();
        }

        if (!WindowsOptionalFeatureActionValidator.TryNormalize(
            settings,
            out DeployWindowsOptionalFeatureSettings normalizedSettings,
            out string? validationError))
        {
            throw new InvalidOperationException(validationError);
        }

        WindowsOptionalFeatureWorkItem[] requestedItems = normalizedSettings.Actions
            .Select(action =>
            {
                WindowsOptionalFeatureCatalogEntry entry = WindowsOptionalFeatureCatalog.Find(action.Id)!;
                return new WindowsOptionalFeatureWorkItem(action, entry, WindowsOptionalFeatureCatalog.GetDepth(entry.Id));
            })
            .ToArray();
        string cleanupRoot = Path.GetFullPath(Path.Combine(workingDirectory, ".."));
        if (Directory.GetParent(cleanupRoot) is null)
        {
            throw new ArgumentException("The optional-feature cleanup boundary cannot be a filesystem root.", nameof(workingDirectory));
        }

        try
        {
            Directory.CreateDirectory(scratchDirectory);
            Directory.CreateDirectory(workingDirectory);

            onInspectionStarted?.Invoke();
            IReadOnlyDictionary<string, OfflineWindowsFeatureState> initialStates =
                await GetOfflineWindowsFeatureStatesAsync(windowsPartitionRoot, workingDirectory, cancellationToken)
                    .ConfigureAwait(false);

            List<WindowsOptionalFeatureWorkItem> pendingItems = [];
            List<string> unavailableEnableActionIds = [];
            int alreadySatisfiedCount = 0;
            foreach (WindowsOptionalFeatureWorkItem item in requestedItems)
            {
                if (!initialStates.TryGetValue(item.CatalogEntry.FeatureName, out OfflineWindowsFeatureState state))
                {
                    if (item.Action.Enable)
                    {
                        unavailableEnableActionIds.Add(item.Action.Id);
                        _logger.LogWarning(
                            "Requested Windows optional feature is not present in the applied image. FeatureId={FeatureId}",
                            item.Action.Id);
                    }
                    else
                    {
                        alreadySatisfiedCount++;
                    }

                    continue;
                }

                if (IsRequestedStateSatisfied(item.Action.Enable, state))
                {
                    alreadySatisfiedCount++;
                    continue;
                }

                WindowsOptionalFeatureCatalogEntry effectiveEntry =
                    WindowsOptionalFeatureCatalog.GetEffectiveEntry(item.CatalogEntry.Id) ?? item.CatalogEntry;
                if (item.Action.Enable &&
                    state == OfflineWindowsFeatureState.PayloadRemoved &&
                    !effectiveEntry.RequiresSetupMediaSxs)
                {
                    throw new InvalidOperationException(
                        $"Windows optional feature '{item.CatalogEntry.FeatureName}' has a removed payload and no supported local source mapping.");
                }

                pendingItems.Add(item with { CatalogEntry = effectiveEntry });
            }

            bool matchingSourceUsed = pendingItems.Any(item => item.Action.Enable && item.CatalogEntry.RequiresSetupMediaSxs);
            string? sourcePath = null;
            if (matchingSourceUsed)
            {
                if (!File.Exists(setupMediaImagePath))
                {
                    throw new FileNotFoundException(
                        "The setup-media image required for Windows optional feature servicing was not found.",
                        setupMediaImagePath);
                }

                onSourcePreparationStarted?.Invoke();
                TryCleanupOptionalFeatureDirectory(sourceExtractionDirectory, cleanupRoot);
                Directory.CreateDirectory(sourceExtractionDirectory);
                OptionalFeatureSourceMetadata metadata = await ResolveOptionalFeatureSourceMetadataAsync(
                        setupMediaImagePath,
                        appliedImageIndex,
                        workingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RunRequiredProcessAsync(
                    "dism.exe",
                    [
                        "/English",
                        "/Apply-Image",
                        $"/ImageFile:{setupMediaImagePath}",
                        $"/Index:{metadata.SetupMediaIndex}",
                        $"/ApplyDir:{sourceExtractionDirectory}",
                        "/CheckIntegrity",
                        $"/ScratchDir:{scratchDirectory}"
                    ],
                    workingDirectory,
                    $"Failed to extract Windows Setup Media from '{setupMediaImagePath}'",
                    cancellationToken).ConfigureAwait(false);

                sourcePath = ValidateMatchingNetFx3Source(
                    setupMediaImagePath,
                    sourceExtractionDirectory,
                    metadata);
            }

            WindowsOptionalFeatureWorkItem[] orderedPendingItems =
            [
                .. pendingItems
                    .Where(item => item.Action.Enable)
                    .OrderBy(item => item.Depth)
                    .ThenBy(item => item.CatalogEntry.SortOrder),
                .. pendingItems
                    .Where(item => !item.Action.Enable)
                    .OrderByDescending(item => item.Depth)
                    .ThenBy(item => item.CatalogEntry.SortOrder)
            ];

            if (orderedPendingItems.Length > 0)
            {
                onServicingStarted?.Invoke();
            }

            for (int index = 0; index < orderedPendingItems.Length; index++)
            {
                WindowsOptionalFeatureWorkItem item = orderedPendingItems[index];
                List<string> arguments =
                [
                    "/English",
                    $"/Image:{windowsPartitionRoot}",
                    item.Action.Enable ? "/Enable-Feature" : "/Disable-Feature",
                    $"/FeatureName:{item.CatalogEntry.FeatureName}"
                ];
                if (item.Action.Enable)
                {
                    arguments.Add("/All");
                }

                arguments.Add("/NoRestart");
                if (item.Action.Enable)
                {
                    arguments.Add("/LimitAccess");
                    if (item.CatalogEntry.RequiresSetupMediaSxs)
                    {
                        arguments.Add($"/Source:{sourcePath}");
                    }
                }

                arguments.Add($"/ScratchDir:{scratchDirectory}");
                await RunRequiredProcessAsync(
                    "dism.exe",
                    arguments,
                    workingDirectory,
                    $"Failed to {(item.Action.Enable ? "enable" : "disable")} Windows optional feature '{item.CatalogEntry.FeatureName}'",
                    cancellationToken).ConfigureAwait(false);
                progress?.Report((index + 1d) / orderedPendingItems.Length * 100d);
            }

            if (orderedPendingItems.Length > 0)
            {
                IReadOnlyDictionary<string, OfflineWindowsFeatureState> finalStates =
                    await GetOfflineWindowsFeatureStatesAsync(windowsPartitionRoot, workingDirectory, cancellationToken)
                        .ConfigureAwait(false);
                foreach (WindowsOptionalFeatureWorkItem item in orderedPendingItems)
                {
                    if (!finalStates.TryGetValue(item.CatalogEntry.FeatureName, out OfflineWindowsFeatureState finalState) ||
                        !IsRequestedStateSatisfied(item.Action.Enable, finalState))
                    {
                        throw new InvalidOperationException(
                            $"Windows optional feature verification failed for '{item.CatalogEntry.FeatureName}'.");
                    }
                }
            }

            return new WindowsOptionalFeatureServicingResult
            {
                RequestedActionCount = requestedItems.Length,
                ChangedActionCount = orderedPendingItems.Length,
                AlreadySatisfiedActionCount = alreadySatisfiedCount,
                UnavailableEnableActionIds = unavailableEnableActionIds,
                MatchingSourceUsed = matchingSourceUsed
            };
        }
        finally
        {
            TryCleanupOptionalFeatureDirectory(scratchDirectory, cleanupRoot);
            TryCleanupOptionalFeatureDirectory(sourceExtractionDirectory, cleanupRoot);
        }
    }

    private static bool HasAnyAiPolicyOptionEnabled(DeployAiComponentRemovalSettings settings)
    {
        return settings.RemoveCopilot ||
            settings.DisableRecall ||
            settings.DisableClickToDo ||
            settings.DisableAiServiceAutoStart ||
            settings.DisableEdgeAi ||
            settings.DisablePaintAi ||
            settings.DisableNotepadAi;
    }

    private static string? ResolveUnattendTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        string normalizedTimeZoneId = timeZoneId.Trim();
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(normalizedTimeZoneId, out string? windowsTimeZoneId) &&
            !string.IsNullOrWhiteSpace(windowsTimeZoneId))
        {
            return windowsTimeZoneId;
        }

        return normalizedTimeZoneId.Contains('/', StringComparison.Ordinal)
            ? null
            : normalizedTimeZoneId;
    }

    /// <inheritdoc />
    public async Task ConfigureRecoveryEnvironmentAsync(
        string windowsPartitionRoot,
        string recoveryPartitionRoot,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowsPartitionRoot))
        {
            throw new ArgumentException("Windows partition root is required.", nameof(windowsPartitionRoot));
        }

        if (string.IsNullOrWhiteSpace(recoveryPartitionRoot))
        {
            throw new ArgumentException("Recovery partition root is required.", nameof(recoveryPartitionRoot));
        }

        Directory.CreateDirectory(workingDirectory);

        string windowsPath = Path.Combine(windowsPartitionRoot, "Windows");
        string sourceWinRePath = Path.Combine(windowsPath, "System32", "Recovery", WinReImageFileName);
        if (!File.Exists(sourceWinRePath))
        {
            throw new FileNotFoundException("The offline Windows image does not contain winre.wim.", sourceWinRePath);
        }

        string recoveryDirectory = GetRecoveryDirectoryPath(recoveryPartitionRoot);
        Directory.CreateDirectory(recoveryDirectory);

        string targetWinRePath = GetRecoveryImagePath(recoveryPartitionRoot);
        File.Copy(sourceWinRePath, targetWinRePath, overwrite: true);

        _logger.LogInformation(
            "Configuring recovery environment. WindowsPath={WindowsPath}, RecoveryDirectory={RecoveryDirectory}",
            windowsPath,
            recoveryDirectory);

        string winReConfigToolPath = ResolveRequiredWinReConfigToolPath();

        await RunRequiredProcessAsync(
            winReConfigToolPath,
            ["/setreimage", "/path", recoveryDirectory, "/target", windowsPath],
            workingDirectory,
            "Failed to set the Windows RE image location",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Recovery environment configured successfully.");
    }

    /// <inheritdoc />
    public async Task SealRecoveryPartitionAsync(
        string recoveryPartitionRoot,
        char recoveryPartitionLetter,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        DeploymentTargetLayout layout = _preparedLayout
            ?? throw new InvalidOperationException("The prepared target layout is unavailable.");
        if (layout.RecoveryPartitionRoot != recoveryPartitionRoot || layout.RecoveryPartitionLetter != recoveryPartitionLetter)
            throw new InvalidOperationException("The recovery partition locator changed.");
        await RunStorageScriptAsync(TargetDiskPreparationScript.Validate(layout.DiskIdentity!, layout.RecoveryPartition!, removeLetter: true),
            workingDirectory, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task ApplyOfflineDriversAsync(
        string windowsPartitionRoot,
        string driverRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        _logger.LogInformation("Applying offline drivers. DriverRoot={DriverRoot}, WindowsPartitionRoot={WindowsPartitionRoot}",
            driverRoot,
            windowsPartitionRoot);
        Directory.CreateDirectory(scratchDirectory);

        if (progress is null)
        {
            await RunRequiredProcessAsync(
                "dism.exe",
                [
                    $"/Image:{windowsPartitionRoot}",
                    "/Add-Driver",
                    $"/Driver:{driverRoot}",
                    "/Recurse",
                    $"/ScratchDir:{scratchDirectory}"
                ],
                workingDirectory,
                $"Offline driver injection failed for '{driverRoot}'",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            DismProgressReporter progressReporter = new(progress);
            await RunRequiredProcessAsync(
                "dism.exe",
                [
                    $"/Image:{windowsPartitionRoot}",
                    "/Add-Driver",
                    $"/Driver:{driverRoot}",
                    "/Recurse",
                    $"/ScratchDir:{scratchDirectory}"
                ],
                workingDirectory,
                $"Offline driver injection failed for '{driverRoot}'",
                cancellationToken,
                progressReporter.HandleOutput,
                progressReporter.HandleOutput).ConfigureAwait(false);

            if (progressReporter.HasReportedProgress)
            {
                progress.Report(100d);
            }
        }

        _logger.LogInformation("Offline driver injection completed. DriverRoot={DriverRoot}", driverRoot);
    }

    /// <inheritdoc />
    public async Task ApplyRecoveryDriversAsync(
        string recoveryPartitionRoot,
        string driverRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? mountProgress = null,
        IProgress<double>? applyProgress = null,
        IProgress<double>? unmountProgress = null,
        Action? onMountStarted = null,
        Action? onApplyStarted = null,
        Action? onUnmountStarted = null)
    {
        if (string.IsNullOrWhiteSpace(recoveryPartitionRoot))
        {
            throw new ArgumentException("Recovery partition root is required.", nameof(recoveryPartitionRoot));
        }

        if (string.IsNullOrWhiteSpace(driverRoot))
        {
            throw new ArgumentException("Driver root is required.", nameof(driverRoot));
        }

        string winReImagePath = GetRecoveryImagePath(recoveryPartitionRoot);
        if (!File.Exists(winReImagePath))
        {
            throw new FileNotFoundException("The recovery partition does not contain winre.wim.", winReImagePath);
        }

        Directory.CreateDirectory(scratchDirectory);
        Directory.CreateDirectory(workingDirectory);

        string mountPath = Path.Combine(workingDirectory, "Mount-WindowsRE");
        ResetWorkingDirectory(mountPath);

        _logger.LogInformation(
            "Applying recovery drivers. DriverRoot={DriverRoot}, WinReImagePath={WinReImagePath}, MountPath={MountPath}",
            driverRoot,
            winReImagePath,
            mountPath);

        Exception? pendingException = null;
        bool mounted = false;
        bool shouldCommit = false;

        try
        {
            string[] mountArguments =
            [
                "/Mount-Image",
                $"/ImageFile:{winReImagePath}",
                "/Index:1",
                $"/MountDir:{mountPath}",
                $"/ScratchDir:{scratchDirectory}"
            ];

            onMountStarted?.Invoke();
            DismProgressReporter? mountProgressReporter = null;
            if (mountProgress is null)
            {
                await RunRequiredProcessAsync(
                    "dism.exe",
                    mountArguments,
                    workingDirectory,
                    "Failed to mount the Windows RE image",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                mountProgressReporter = new(mountProgress);
                await RunRequiredProcessAsync(
                    "dism.exe",
                    mountArguments,
                    workingDirectory,
                    "Failed to mount the Windows RE image",
                    cancellationToken,
                    mountProgressReporter.HandleOutput,
                    mountProgressReporter.HandleOutput).ConfigureAwait(false);
            }

            mounted = true;
            if (mountProgressReporter is not null && mountProgressReporter.HasReportedProgress)
            {
                mountProgress!.Report(100d);
            }

            onApplyStarted?.Invoke();
            DismProgressReporter? progressReporter = null;
            if (applyProgress is null)
            {
                await RunRequiredProcessAsync(
                    "dism.exe",
                    [
                        $"/Image:{mountPath}",
                        "/Add-Driver",
                        $"/Driver:{driverRoot}",
                        "/Recurse",
                        $"/ScratchDir:{scratchDirectory}"
                    ],
                    workingDirectory,
                    $"Recovery driver injection failed for '{driverRoot}'",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                progressReporter = new(applyProgress);
                await RunRequiredProcessAsync(
                    "dism.exe",
                    [
                        $"/Image:{mountPath}",
                        "/Add-Driver",
                        $"/Driver:{driverRoot}",
                        "/Recurse",
                        $"/ScratchDir:{scratchDirectory}"
                    ],
                    workingDirectory,
                    $"Recovery driver injection failed for '{driverRoot}'",
                    cancellationToken,
                    progressReporter.HandleOutput,
                    progressReporter.HandleOutput).ConfigureAwait(false);
            }

            shouldCommit = true;
            if (progressReporter is not null && progressReporter.HasReportedProgress)
            {
                applyProgress!.Report(100d);
            }
        }
        catch (Exception ex)
        {
            pendingException = ex;
        }
        finally
        {
            if (mounted)
            {
                // Always unmount WinRE even after driver injection failure so the image is not left mounted.
                string[] unmountArguments = shouldCommit
                    ? ["/Unmount-Image", $"/MountDir:{mountPath}", "/Commit"]
                    : ["/Unmount-Image", $"/MountDir:{mountPath}", "/Discard"];

                onUnmountStarted?.Invoke();
                ProcessExecutionResult unmountExecution;
                DismProgressReporter? unmountProgressReporter = null;
                if (unmountProgress is null)
                {
                    unmountExecution = await _processRunner
                        .RunAsync("dism.exe", unmountArguments, workingDirectory, cancellationToken, NativeExecutionTimeout)
                        .ConfigureAwait(false);
                }
                else
                {
                    unmountProgressReporter = new(unmountProgress);
                    unmountExecution = await _processRunner
                        .RunAsync(
                            "dism.exe",
                            unmountArguments,
                            workingDirectory,
                            unmountProgressReporter.HandleOutput,
                            unmountProgressReporter.HandleOutput,
                            cancellationToken,
                            NativeExecutionTimeout)
                        .ConfigureAwait(false);
                }

                if (!unmountExecution.IsSuccess)
                {
                    string diagnostic = VolumePathDiagnostics.Redact(unmountExecution.ToDiagnosticText());
                    _logger.LogError("Failed to unmount the Windows RE image. Diagnostic={Diagnostic}", diagnostic);

                    pendingException = pendingException is null
                        ? new DeploymentProcessException(
                            $"Failed to unmount the Windows RE image.{Environment.NewLine}{diagnostic}",
                            unmountExecution.ExitCode)
                        : new DeploymentProcessException(
                            $"Windows RE servicing failed and the image could not be unmounted cleanly.{Environment.NewLine}{diagnostic}",
                            unmountExecution.ExitCode,
                            pendingException);
                }
                else
                {
                    if (unmountProgressReporter is not null && unmountProgressReporter.HasReportedProgress)
                    {
                        unmountProgress!.Report(100d);
                    }
                }
            }

            TryDeleteDirectory(mountPath);
        }

        if (pendingException is not null)
        {
            throw pendingException;
        }

        _logger.LogInformation("Recovery driver injection completed. DriverRoot={DriverRoot}", driverRoot);
    }

    /// <inheritdoc />
    public async Task ConfigureBootAsync(
        string windowsPartitionRoot,
        string systemPartitionRoot,
        int operatingSystemBuildMajor,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        DeploymentTargetLayout layout = _preparedLayout
            ?? throw new InvalidOperationException("The prepared target layout is unavailable.");
        if (layout.WindowsPartitionRoot != windowsPartitionRoot || layout.SystemPartitionRoot != systemPartitionRoot)
            throw new InvalidOperationException("The boot partition locator changed.");
        await RunStorageScriptAsync(TargetDiskPreparationScript.Validate(layout.DiskIdentity!, layout.WindowsPartition!),
            workingDirectory, cancellationToken).ConfigureAwait(false);
        await RunStorageScriptAsync(TargetDiskPreparationScript.Validate(layout.DiskIdentity!, layout.SystemPartition!, verifyLetter: true),
            workingDirectory, cancellationToken).ConfigureAwait(false);
        await RunBcdBootAsync(windowsPartitionRoot, $"{layout.SystemPartition!.DriveLetter}:", operatingSystemBuildMajor,
            workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RunBcdBootAsync(string windowsPartitionRoot, string systemPartitionRoot,
        int operatingSystemBuildMajor, string workingDirectory, CancellationToken cancellationToken = default)
    {
        string windowsPath = Path.Combine(windowsPartitionRoot, "Windows");
        string bcdBootPath = Path.Combine(windowsPath, "System32", "bcdboot.exe");
        if (!_fileExists(bcdBootPath))
        {
            throw new FileNotFoundException(
                "The applied Windows image does not contain bcdboot.exe.",
                bcdBootPath);
        }

        _logger.LogInformation("Configuring boot files. WindowsPath={WindowsPath}, SystemPartitionRoot={SystemPartitionRoot}", windowsPath, systemPartitionRoot);

        var arguments = new List<string>
        {
            windowsPath, "/s", systemPartitionRoot.TrimEnd('\\', '/'), "/f", "UEFI", "/c"
        };
        if (operatingSystemBuildMajor >= 26200)
        {
            arguments.Add("/bootex");
        }
        arguments.Add("/v");
        await RunRequiredProcessAsync(
            bcdBootPath,
            arguments,
            workingDirectory,
            "BCDBoot configuration failed",
            cancellationToken, MetadataExecutionTimeout).ConfigureAwait(false);

        _logger.LogInformation("BCDBoot configuration completed successfully.");
    }

    private async Task<ProcessExecutionResult> RunRequiredProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string failureSummary,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        return await RunRequiredProcessAsync(
            fileName,
            arguments,
            workingDirectory,
            failureSummary,
            cancellationToken,
            onOutputData: null,
            onErrorData: null,
            executionTimeout).ConfigureAwait(false);
    }

    private async Task<ProcessExecutionResult> RunRequiredProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string failureSummary,
        CancellationToken cancellationToken,
        Action<string>? onOutputData,
        Action<string>? onErrorData,
        TimeSpan? executionTimeout = null)
    {
        ProcessExecutionResult execution = await _processRunner
            .RunAsync(fileName, arguments, workingDirectory, onOutputData, onErrorData, cancellationToken, executionTimeout ?? NativeExecutionTimeout)
            .ConfigureAwait(false);

        if (!execution.IsSuccess)
        {
            _logger.LogError("{FailureSummary}. Diagnostic={Diagnostic}", failureSummary, VolumePathDiagnostics.Redact(execution.ToDiagnosticText()));
            throw new DeploymentProcessException(
                $"{failureSummary}.{Environment.NewLine}{VolumePathDiagnostics.Redact(execution.ToDiagnosticText())}",
                execution.ExitCode);
        }

        return execution;
    }

    private async Task<IReadOnlyDictionary<string, OfflineWindowsFeatureState>> GetOfflineWindowsFeatureStatesAsync(
        string windowsPartitionRoot,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await RunRequiredProcessAsync(
            "dism.exe",
            [
                "/English",
                $"/Image:{windowsPartitionRoot}",
                "/Get-Features",
                "/Format:Table"
            ],
            workingDirectory,
            $"Failed to inspect Windows optional features in '{windowsPartitionRoot}'",
            cancellationToken, MetadataExecutionTimeout).ConfigureAwait(false);
        result.EnsureCompleteOutput();
        IReadOnlyDictionary<string, OfflineWindowsFeatureState> states = ParseOfflineWindowsFeatureStates(result.StandardOutput);
        if (states.Count == 0)
        {
            throw new InvalidOperationException("Failed to parse Windows optional feature states from DISM output.");
        }

        return states;
    }

    private static IReadOnlyDictionary<string, OfflineWindowsFeatureState> ParseOfflineWindowsFeatureStates(string output)
    {
        var states = new Dictionary<string, OfflineWindowsFeatureState>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = line.IndexOf('|');
            if (separatorIndex < 0)
            {
                continue;
            }

            string name = line[..separatorIndex].Trim();
            string stateText = line[(separatorIndex + 1)..].Trim();
            if (name.Equals("Feature Name", StringComparison.OrdinalIgnoreCase) ||
                name.All(character => character is '-' or ' ') ||
                stateText.All(character => character is '-' or ' '))
            {
                continue;
            }

            OfflineWindowsFeatureState state = stateText.ToUpperInvariant() switch
            {
                "ENABLED" => OfflineWindowsFeatureState.Enabled,
                "DISABLED" => OfflineWindowsFeatureState.Disabled,
                "ENABLE PENDING" => OfflineWindowsFeatureState.EnablePending,
                "DISABLE PENDING" => OfflineWindowsFeatureState.DisablePending,
                "DISABLED WITH PAYLOAD REMOVED" => OfflineWindowsFeatureState.PayloadRemoved,
                _ => throw new InvalidOperationException($"Unsupported Windows optional feature state '{stateText}'.")
            };
            states[name] = state;
        }

        return states;
    }

    private static bool IsRequestedStateSatisfied(bool enable, OfflineWindowsFeatureState state)
    {
        return enable
            ? state is OfflineWindowsFeatureState.Enabled or OfflineWindowsFeatureState.EnablePending
            : state is OfflineWindowsFeatureState.Disabled or OfflineWindowsFeatureState.DisablePending or OfflineWindowsFeatureState.PayloadRemoved;
    }

    private async Task<OptionalFeatureSourceMetadata> ResolveOptionalFeatureSourceMetadataAsync(
        string imagePath,
        int appliedImageIndex,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(appliedImageIndex, 1);

        ProcessExecutionResult summary = await RunRequiredProcessAsync(
            "dism.exe",
            ["/English", "/Get-ImageInfo", $"/ImageFile:{imagePath}"],
            workingDirectory,
            $"Failed to inspect setup-media image '{imagePath}'",
            cancellationToken, MetadataExecutionTimeout).ConfigureAwait(false);
        summary.EnsureCompleteOutput();
        (int Index, string Name)[] matches = Regex.Matches(
                summary.StandardOutput ?? string.Empty,
                @"^\s*Index\s*:\s*(?<index>\d+)\s*$\s*^\s*Name\s*:\s*(?<name>.+?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Select(match => (
                int.Parse(match.Groups["index"].Value),
                match.Groups["name"].Value.Trim()))
            .Where(item => string.Equals(item.Item2, "Windows Setup Media", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Setup-media image '{imagePath}' must contain exactly one image named 'Windows Setup Media'.");
        }

        ProcessExecutionResult detail = await RunRequiredProcessAsync(
            "dism.exe",
            ["/English", "/Get-ImageInfo", $"/ImageFile:{imagePath}", $"/Index:{appliedImageIndex}"],
            workingDirectory,
            $"Failed to inspect applied Windows image index {appliedImageIndex}",
            cancellationToken, MetadataExecutionTimeout).ConfigureAwait(false);
        detail.EnsureCompleteOutput();
        return new OptionalFeatureSourceMetadata(
            matches[0].Index,
            appliedImageIndex,
            ParseImageProperty(detail.StandardOutput, "Architecture"),
            ParseImageProperty(detail.StandardOutput, "Version"));
    }

    private static string ValidateMatchingNetFx3Source(
        string imagePath,
        string sourceExtractionDirectory,
        OptionalFeatureSourceMetadata metadata)
    {
        string sourcePath = Path.Combine(sourceExtractionDirectory, "sources", "sxs");
        string architectureToken = metadata.Architecture.ToUpperInvariant() switch
        {
            "X64" or "AMD64" => "amd64",
            "ARM64" => "arm64",
            _ => throw new InvalidOperationException(
                $"Applied Windows image index {metadata.AppliedImageIndex} in '{imagePath}' reports unsupported architecture '{metadata.Architecture}'.")
        };
        bool hasMatchingCab = Directory.Exists(sourcePath) && Directory
            .EnumerateFiles(sourcePath, "*.cab", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Any(fileName =>
                fileName is not null &&
                fileName.Contains("netfx3-ondemand-package", StringComparison.OrdinalIgnoreCase) &&
                fileName.Contains($"~{architectureToken}~", StringComparison.OrdinalIgnoreCase));
        if (!hasMatchingCab)
        {
            throw new InvalidOperationException(
                $"Matching NetFx3 source is unavailable. Media='{imagePath}', Version='{metadata.Version}', Architecture='{metadata.Architecture}', Expected='{architectureToken} NetFx3 OnDemand CAB'.");
        }

        return sourcePath;
    }

    private void TryCleanupOptionalFeatureDirectory(string path, string cleanupRoot)
    {
        try
        {
            string fullRoot = Path.GetFullPath(cleanupRoot);
            string fullPath = Path.GetFullPath(path);
            string relativePath = Path.GetRelativePath(fullRoot, fullPath);
            if (string.IsNullOrWhiteSpace(relativePath) ||
                relativePath == "." ||
                Path.IsPathRooted(relativePath) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                string.Equals(relativePath, "..", StringComparison.Ordinal))
            {
                _logger.LogWarning("Skipped optional-feature cleanup outside the deployment temp root. Path={Path}", fullPath);
                return;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean optional-feature temporary directory. Path={Path}", path);
        }
    }

    private static (char systemLetter, char windowsLetter, char recoveryLetter) GetPartitionLetters()
    {
        HashSet<char> usedLetters = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        char systemLetter = GetAvailableLetter(usedLetters, ['S', 'T', 'U', 'V', 'W']);
        usedLetters.Add(systemLetter);

        char windowsLetter = GetAvailableLetter(usedLetters, ['W', 'V', 'U', 'T', 'Q', 'P']);
        usedLetters.Add(windowsLetter);

        char recoveryLetter = GetAvailableLetter(usedLetters, ['R', 'X', 'Y', 'Z']);
        return (systemLetter, windowsLetter, recoveryLetter);
    }

    private static char GetAvailableLetter(HashSet<char> usedLetters, IReadOnlyList<char> preferred)
    {
        foreach (char preferredLetter in preferred)
        {
            char letter = char.ToUpperInvariant(preferredLetter);
            if (!usedLetters.Contains(letter))
            {
                return letter;
            }
        }

        for (char letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!usedLetters.Contains(letter))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("No drive letter is available for deployment partitions.");
    }

    private static void ResetWorkingDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup; a later DISM failure will surface if the mount path is unusable.
        }
    }

    private static string GetRecoveryDirectoryPath(string recoveryPartitionRoot)
    {
        return Path.Combine(recoveryPartitionRoot, "Recovery", "WindowsRE");
    }

    private static string GetRecoveryImagePath(string recoveryPartitionRoot)
    {
        return Path.Combine(GetRecoveryDirectoryPath(recoveryPartitionRoot), WinReImageFileName);
    }

    private static string ResolveRequiredWinReConfigToolPath()
    {
        string path = Path.Combine(Environment.SystemDirectory, "winrecfg.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Required WinPE executable 'winrecfg.exe' was not found. Add the WinPE-WinReCfg optional component to the WinPE image.",
                path);
        }

        return path;
    }

    private static void SetElementValue(XElement parent, XNamespace elementNamespace, string elementName, string value)
    {
        XElement element = parent.Element(elementNamespace + elementName) ?? new XElement(elementNamespace + elementName);
        if (element.Parent is null)
        {
            parent.Add(element);
        }

        element.Value = value;
    }

    private static void RemoveElement(XElement parent, XNamespace elementNamespace, string elementName)
    {
        parent.Element(elementNamespace + elementName)?.Remove();
    }

    private static IReadOnlyList<int> ParseImageIndexes(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return Regex.Matches(output, @"^\s*Index\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Select(match => int.Parse(match.Groups[1].Value))
            .Distinct()
            .ToArray();
    }

    private static string ParseEditionId(string output)
    {
        string editionId = ParseImageProperty(output, "Edition ID");
        return !string.IsNullOrWhiteSpace(editionId)
            ? editionId
            : ParseImageProperty(output, "Edition");
    }

    private static string ParseImageProperty(string output, string propertyName)
    {
        Match match = Regex.Match(
            output,
            $@"^\s*{Regex.Escape(propertyName)}\s*:\s*(.+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private sealed record ImageIndexMetadata(int Index, string EditionId);

    private sealed record WindowsOptionalFeatureWorkItem(
        DeployWindowsOptionalFeatureAction Action,
        WindowsOptionalFeatureCatalogEntry CatalogEntry,
        int Depth);

    private sealed record OptionalFeatureSourceMetadata(
        int SetupMediaIndex,
        int AppliedImageIndex,
        string Architecture,
        string Version);

    private enum OfflineWindowsFeatureState
    {
        Enabled,
        Disabled,
        EnablePending,
        DisablePending,
        PayloadRemoved
    }
}
