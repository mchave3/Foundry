// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Autopilot;
using Foundry.Core.Services.Configuration;
using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests;

public sealed class UnattendMediaTests
{
    [Fact]
    public void Generate_RejectsUnprotectedCustomAnswerFiles()
    {
        var configuration = new FoundryConfigurationService().Deserialize("""
            { "unattend": { "isEnabled": true, "files": [{
              "id": "12345678901234567890123456789012", "displayName": "Office",
              "sourcePath": "C:\\answer.xml",
              "contentHash": "1234567890123456789012345678901234567890123456789012345678901234"
            }] } }
            """);

        Assert.Throws<InvalidOperationException>(() => new DeployConfigurationGenerator().Generate(configuration));
    }

    [Fact]
    public void Generate_EmitsOnlyActiveManifestMetadata()
    {
        using var media = new MediaFixture();
        var generator = new DeployConfigurationGenerator();
        var authoring = new FoundryConfigurationDocument { Unattend = media.Options.Unattend };
        FoundryDeployConfigurationDocument generated = generator.Generate(authoring, media.Protection.DeploymentKey, media.Protection.Settings);
        Assert.Equal(MediaFixture.FileId, generated.Unattend.DefaultFileId);
        Assert.Equal(MediaFixture.FileId, Assert.Single(generated.Unattend.Files).Id);
        string json = generator.Serialize(generated);
        Assert.DoesNotContain("sourcePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-test-value", json);

        var disabled = generator.Generate(authoring with { Unattend = authoring.Unattend with { IsEnabled = false } });
        Assert.False(disabled.Unattend.IsEnabled);
        Assert.Empty(disabled.Unattend.Files);
        Assert.Null(disabled.Unattend.DefaultFileId);
    }

    [Fact]
    public async Task Provision_EncryptsExactImportedBytesAndOmitsSourceFromRuntimeConfiguration()
    {
        using var media = new MediaFixture();
        string secretsRoot = Path.Combine(media.ConfigPath, "Secrets");
        Directory.CreateDirectory(secretsRoot);
        File.WriteAllBytes(Path.Combine(secretsRoot, "deployment-secrets.key"), media.Protection.DeploymentKey);
        WinPeResult result = await new WinPeMountedImageAssetProvisioningService()
            .ProvisionAsync(media.Options, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Details);
        string asset = Path.Combine(media.ConfigPath, "Unattend", MediaFixture.FileId + ".xml.encrypted");
        Assert.True(File.Exists(asset));
        var envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(asset), ConfigurationJsonDefaults.SerializerOptions)!;
        byte[] decrypted = MediaSecretEnvelopeProtector.DecryptBytes(envelope, media.Protection.DeploymentKey, MediaSecretEnvelopeProtector.DeploymentKeyId);
        try
        {
            Assert.Equal(media.Content, decrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(media.ConfigPath, "Unattend"), "*.xml"));
        Assert.False(File.Exists(Path.Combine(media.ConfigPath, "Secrets", "deployment-secrets.key")));
        string runtimeJson = File.ReadAllText(Path.Combine(media.ConfigPath, "foundry.deploy.config.json"));
        Assert.DoesNotContain("sourcePath", runtimeJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-test-value", runtimeJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provision_RejectsMissingOrChangedSource(bool removeSource)
    {
        using var media = new MediaFixture();
        if (removeSource)
        {
            File.Delete(media.SourcePath);
        }
        else
        {
            File.AppendAllText(media.SourcePath, "<!-- changed -->");
        }
        WinPeResult result = await new WinPeMountedImageAssetProvisioningService()
            .ProvisionAsync(media.Options, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("private-test-value", result.Error?.Details ?? string.Empty);
    }

    [Fact]
    public async Task Provision_RejectsManifestThatDoesNotMatchSources()
    {
        using var media = new MediaFixture();
        WinPeResult result = await new WinPeMountedImageAssetProvisioningService()
            .ProvisionAsync(media.Options with { DeployConfigurationJson = "{}" }, TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Provision_RejectsNullProtectionMetadataWithoutThrowing()
    {
        using var media = new MediaFixture();
        JsonNode configuration = JsonNode.Parse(media.Options.DeployConfigurationJson)!;
        configuration["protection"] = null;
        WinPeResult result = await new WinPeMountedImageAssetProvisioningService().ProvisionAsync(
            media.Options with { DeployConfigurationJson = configuration.ToJsonString() },
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Provision_DisabledFeatureRemovesItsPreviousAssets()
    {
        using var media = new MediaFixture();
        string customDirectory = Path.Combine(media.ConfigPath, "Unattend");
        Directory.CreateDirectory(customDirectory);
        string previousAsset = Path.Combine(customDirectory, MediaFixture.FileId + ".xml.encrypted");
        File.WriteAllText(previousAsset, "old");
        string unrelatedFile = Path.Combine(customDirectory, "operator-notes.txt");
        File.WriteAllText(unrelatedFile, "retain");
        var disabled = media.Options with
        {
            Unattend = media.Options.Unattend with { IsEnabled = false },
            DeployConfigurationJson = "{}"
        };

        WinPeResult result = await new WinPeMountedImageAssetProvisioningService()
            .ProvisionAsync(disabled, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.False(File.Exists(previousAsset));
        Assert.Equal("retain", File.ReadAllText(unrelatedFile));
    }

    private sealed class MediaFixture : IDisposable
    {
        public const string FileId = "12345678901234567890123456789012";
        private readonly string root = Path.Combine(Path.GetTempPath(), "Foundry-Unattend-Media", Guid.NewGuid().ToString("N"));
        public string ConfigPath { get; }
        public string SourcePath { get; }
        public byte[] Content { get; } = Encoding.Unicode.GetBytes("""
            <?xml version="1.0" encoding="utf-16"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:custom="urn:custom">
              <settings pass="specialize"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64"><ComputerName>Office</ComputerName></component></settings>
              <custom:Payload>private-test-value</custom:Payload>
            </unattend>
            """);
        public DeploymentMediaProtectionMaterial Protection { get; } = DeploymentMediaProtectionService.CreateProtected("test media password");
        public WinPeMountedImageAssetProvisioningOptions Options { get; }

        public MediaFixture()
        {
            string mounted = Path.Combine(root, "mount");
            Directory.CreateDirectory(Path.Combine(mounted, "Windows", "System32"));
            ConfigPath = Path.Combine(mounted, "Foundry", "Config");
            SourcePath = Path.Combine(root, "source.xml");
            File.WriteAllBytes(SourcePath, Content);
            string curl = Path.Combine(root, "curl.exe");
            File.WriteAllText(curl, "test");
            string digest = Convert.ToHexString(SHA256.HashData(Content));
            string deployJson = JsonSerializer.Serialize(new
            {
                protection = Protection.Settings,
                unattend = new { isEnabled = true, defaultFileId = FileId, files = new[] { new { id = FileId, displayName = "Office", contentHash = digest } } }
            }, ConfigurationJsonDefaults.SerializerOptions);
            Options = JsonSerializer.Deserialize<WinPeMountedImageAssetProvisioningOptions>(JsonSerializer.Serialize(new
            {
                mountedImagePath = mounted,
                bootstrapScriptContent = "bootstrap",
                curlExecutableSourcePath = curl,
                ianaWindowsTimeZoneMapJson = "{}",
                deployConfigurationJson = deployJson,
                isDeploymentProtectionEnabled = true,
                deploymentSecretsKey = Protection.DeploymentKey,
                unattend = new { isEnabled = true, defaultFileId = FileId, files = new[] { new { id = FileId, displayName = "Office", sourcePath = SourcePath, contentHash = digest } } }
            }, ConfigurationJsonDefaults.SerializerOptions), ConfigurationJsonDefaults.SerializerOptions)!;
        }

        public void Dispose()
        {
            Protection.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
