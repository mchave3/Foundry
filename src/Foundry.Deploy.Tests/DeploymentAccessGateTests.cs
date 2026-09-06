// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.ApplicationShell;
using Foundry.Deploy.Services.Configuration;
using Foundry.Deploy.Services.Security;

namespace Foundry.Deploy.Tests;

public sealed class DeploymentAccessGateTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 5)]
    public void RetryDelay_IsProgressiveAndCapped(int failedAttemptNumber, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DeploymentAccessRetryDelay.GetDelay(failedAttemptNumber));
    }

    [Fact]
    public async Task AuthorizeAsync_WhenProtectionIsDisabled_DoesNotPrompt()
    {
        var dialog = new FakePasswordDialogService();
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(new FoundryDeployConfigurationDocument()),
            new FakeUnlockService(),
            dialog,
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.True(authorized);
        Assert.Equal(0, dialog.PromptCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenEnabledFlagIsClearedButWrappedKeyRemains_StillPrompts()
    {
        var dialog = new FakePasswordDialogService("correct");
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(new FoundryDeployConfigurationDocument
            {
                Protection = CreateWrappedProtection(isEnabled: false)
            }),
            new FakeUnlockService("correct"),
            dialog,
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.True(authorized);
        Assert.Equal(1, dialog.PromptCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenEncryptedProfileRemainsWithoutProtectionMetadata_DeniesAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-access-gate-{Guid.NewGuid():N}");
        string configurationPath = Path.Combine(root, "Config", "foundry.deploy.config.json");
        string profilePath = Path.Combine(root, "Config", "Autopilot", "Profile", "AutopilotConfigurationFile.json.encrypted");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        await File.WriteAllTextAsync(profilePath, "encrypted", TestContext.Current.CancellationToken);
        var dialog = new FakePasswordDialogService("unused");
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(new FoundryDeployConfigurationDocument(), configurationPath: configurationPath),
            new FakeUnlockService(),
            dialog,
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.False(authorized);
        Assert.Equal(0, dialog.PromptCount);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenConfigurationIsMissingButEncryptedProfileRemains_DeniesAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-access-gate-{Guid.NewGuid():N}");
        string configurationPath = Path.Combine(root, "Config", "foundry.deploy.config.json");
        string profilePath = Path.Combine(root, "Config", "Autopilot", "Profile", "AutopilotConfigurationFile.json.encrypted");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        await File.WriteAllTextAsync(profilePath, "encrypted", TestContext.Current.CancellationToken);
        var dialog = new FakePasswordDialogService("unused");
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(document: null, exists: false, configurationPath: configurationPath),
            new FakeUnlockService(),
            dialog,
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.False(authorized);
        Assert.Equal(0, dialog.PromptCount);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPasswordFails_AllowsRetryUntilSuccessful()
    {
        var dialog = new FakePasswordDialogService("wrong", "correct");
        var unlock = new FakeUnlockService("correct");
        var retryDelay = new ImmediateRetryDelay();
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(new FoundryDeployConfigurationDocument
            {
                Protection = new DeployProtectionSettings { IsEnabled = true }
            }),
            unlock,
            dialog,
            retryDelay);

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.True(authorized);
        Assert.Equal(2, dialog.PromptCount);
        Assert.Equal([false, true], dialog.PreviousAttemptFailedValues);
        Assert.Equal([1], retryDelay.Attempts);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPromptIsCancelled_DeniesAccess()
    {
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(new FoundryDeployConfigurationDocument
            {
                Protection = new DeployProtectionSettings { IsEnabled = true }
            }),
            new FakeUnlockService(),
            new FakePasswordDialogService(),
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.False(authorized);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenExistingConfigurationCannotBeParsed_DeniesAccess()
    {
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(document: null, exists: true),
            new FakeUnlockService(),
            new FakePasswordDialogService("unused"),
            new ImmediateRetryDelay());

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.False(authorized);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenSchemaIsUnsupported_ShowsBlockingVersionDiagnostic()
    {
        const string configurationPath = @"X:\Foundry\Config\foundry.deploy.config.json";
        var shell = new FakeApplicationShellService();
        var gate = new DeploymentAccessGate(
            new FakeConfigurationService(
                document: null,
                exists: true,
                configurationPath: configurationPath,
                isUnsupportedSchemaVersion: true,
                failureMessage: "Foundry.Deploy configuration uses schema version 13, but this application supports up to schema version 12."),
            new FakeUnlockService(),
            new FakePasswordDialogService("unused"),
            new ImmediateRetryDelay(),
            shell);

        bool authorized = await gate.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.False(authorized);
        Assert.Equal(1, shell.BlockingErrorCount);
        Assert.Contains("13", shell.BlockingErrorMessage, StringComparison.Ordinal);
        Assert.Contains("12", shell.BlockingErrorMessage, StringComparison.Ordinal);
        Assert.Contains(configurationPath, shell.BlockingErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Update Foundry", shell.BlockingErrorMessage, StringComparison.Ordinal);
    }

    private sealed class FakeConfigurationService(
        FoundryDeployConfigurationDocument? document,
        bool exists = true,
        string configurationPath = "",
        bool isUnsupportedSchemaVersion = false,
        string? failureMessage = null) : IDeployConfigurationService
    {
        public DeployConfigurationLoadResult LoadOptional() => new()
        {
            ConfigurationPath = configurationPath,
            Exists = exists,
            Document = document,
            IsUnsupportedSchemaVersion = isUnsupportedSchemaVersion,
            FailureMessage = failureMessage
        };
    }

    private sealed class FakeApplicationShellService : IApplicationShellService
    {
        public int BlockingErrorCount { get; private set; }

        public string BlockingErrorMessage { get; private set; } = string.Empty;

        public void ShowAbout()
        {
        }

        public bool ConfirmWarning(string title, string message)
        {
            return false;
        }

        public void ShowBlockingError(string title, string message)
        {
            BlockingErrorCount++;
            BlockingErrorMessage = message;
        }

        public void Shutdown()
        {
        }
    }

    private static DeployProtectionSettings CreateWrappedProtection(bool isEnabled) => new()
    {
        IsEnabled = isEnabled,
        KeyDerivationAlgorithm = "pbkdf2-sha256",
        Iterations = 600_000,
        Salt = "salt",
        ProtectedDeploymentKey = new SecretEnvelope
        {
            Kind = "encrypted",
            Algorithm = "aes-gcm-v1",
            KeyId = "deployment-password",
            Nonce = "nonce",
            Tag = "tag",
            Ciphertext = "ciphertext"
        }
    };

    private sealed class FakeUnlockService(string acceptedPassword = "") : IDeploymentProtectionUnlockService
    {
        public bool TryUnlock(DeployProtectionSettings settings, ReadOnlySpan<char> password)
        {
            return password.SequenceEqual(acceptedPassword);
        }
    }

    private sealed class FakePasswordDialogService(params string[] passwords) : IDeploymentPasswordDialogService
    {
        private readonly Queue<string> remainingPasswords = new(passwords);

        public int PromptCount { get; private set; }

        public List<bool> PreviousAttemptFailedValues { get; } = [];

        public DeploymentPasswordPromptResult Prompt(bool previousAttemptFailed)
        {
            PromptCount++;
            PreviousAttemptFailedValues.Add(previousAttemptFailed);
            return remainingPasswords.Count == 0
                ? DeploymentPasswordPromptResult.Cancelled()
                : DeploymentPasswordPromptResult.Submitted(remainingPasswords.Dequeue().AsSpan());
        }
    }

    private sealed class ImmediateRetryDelay : IDeploymentAccessRetryDelay
    {
        public List<int> Attempts { get; } = [];

        public Task WaitAsync(int failedAttemptNumber, CancellationToken cancellationToken)
        {
            Attempts.Add(failedAttemptNumber);
            return Task.CompletedTask;
        }
    }
}
