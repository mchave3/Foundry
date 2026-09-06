// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Autopilot;
using Foundry.Core.Services.Configuration;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeMountedImageAssetProvisioningService : IWinPeMountedImageAssetProvisioningService
{
    private const string BootstrapFileName = "FoundryBootstrap.ps1";
    private const string BootstrapInvocation = @"powershell.exe -ExecutionPolicy Bypass -NoProfile -File X:\Windows\System32\FoundryBootstrap.ps1";
    private const string Oa3CfgTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <OA3>
          <FileBased>
            <InputKeyXMLFile>input.xml</InputKeyXMLFile>
          </FileBased>
          <OutputData>
            <AssembledBinaryFile>OA3.bin</AssembledBinaryFile>
            <ReportedXMLFile>OA3.xml</ReportedXMLFile>
          </OutputData>
        </OA3>
        """;
    private const string Oa3InputTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <Key>
          <ProductKey>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</ProductKey>
          <ProductKeyID>0000000000000</ProductKeyID>
          <ProductKeyState>0</ProductKeyState>
        </Key>
        """;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public async Task<WinPeResult> ProvisionAsync(
        WinPeMountedImageAssetProvisioningOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WinPeDiagnostic? validationError = ValidateOptions(options);
        if (validationError is not null)
        {
            return WinPeResult.Failure(validationError);
        }

        try
        {
            string mountedImagePath = Path.GetFullPath(options.MountedImagePath);
            string system32Path = Path.Combine(mountedImagePath, "Windows", "System32");
            string foundryRootPath = Path.Combine(mountedImagePath, "Foundry");
            string foundryConfigPath = Path.Combine(foundryRootPath, "Config");

            Directory.CreateDirectory(system32Path);
            Directory.CreateDirectory(foundryConfigPath);

            await File.WriteAllTextAsync(
                Path.Combine(system32Path, BootstrapFileName),
                options.BootstrapScriptContent,
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);

            File.Copy(options.CurlExecutableSourcePath, Path.Combine(system32Path, "curl.exe"), overwrite: true);

            ProvisionBundledSevenZip(mountedImagePath, options);
            await WriteStartnetAsync(system32Path, cancellationToken).ConfigureAwait(false);
            await WriteConfigurationAssetsAsync(mountedImagePath, foundryConfigPath, options, cancellationToken).ConfigureAwait(false);

            return WinPeResult.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException or InvalidDataException)
        {
            return WinPeResult.Failure(
                WinPeErrorCodes.BuildFailed,
                "Failed to provision Foundry boot assets into the mounted WinPE image.",
                ex.Message);
        }
    }

    private static async Task WriteStartnetAsync(string system32Path, CancellationToken cancellationToken)
    {
        string startnetPath = Path.Combine(system32Path, "startnet.cmd");
        List<string> lines = File.Exists(startnetPath)
            ? [.. await File.ReadAllLinesAsync(startnetPath, cancellationToken).ConfigureAwait(false)]
            : [];

        if (!lines.Any(line => line.Trim().Equals("wpeinit", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(0, "wpeinit");
        }

        if (!lines.Any(line => line.Contains(BootstrapFileName, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add(BootstrapInvocation);
        }

        await File.WriteAllLinesAsync(startnetPath, lines, Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteConfigurationAssetsAsync(
        string mountedImagePath,
        string foundryConfigPath,
        WinPeMountedImageAssetProvisioningOptions options,
        CancellationToken cancellationToken)
    {
        string connectConfigurationJson = string.IsNullOrWhiteSpace(options.FoundryConnectConfigurationJson)
            ? CreateFallbackFoundryConnectConfigurationJson()
            : options.FoundryConnectConfigurationJson;

        await File.WriteAllTextAsync(
            Path.Combine(foundryConfigPath, "foundry.connect.config.json"),
            connectConfigurationJson,
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        string deployConfigurationJson = string.IsNullOrWhiteSpace(options.DeployConfigurationJson)
            ? CreateFallbackDeployConfigurationJson()
            : options.DeployConfigurationJson;

        await WinPeUnattendAssetProvisioner.ProvisionAsync(
            foundryConfigPath, options, deployConfigurationJson, cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(foundryConfigPath, "foundry.deploy.config.json"),
            deployConfigurationJson,
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        await WriteNetworkSecretsKeyAsync(
            foundryConfigPath,
            connectConfigurationJson,
            options.NetworkSecretsKey,
            cancellationToken).ConfigureAwait(false);

        await WriteDeploymentSecretsKeyAsync(
            foundryConfigPath,
            options.DeploymentSecretsKey,
            options.IsDeploymentProtectionEnabled,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(foundryConfigPath, "foundry.connect.provisioning-source.txt"),
            FormatProvisioningSource(options.ConnectProvisioningSource),
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(foundryConfigPath, "foundry.deploy.provisioning-source.txt"),
            FormatProvisioningSource(options.DeployProvisioningSource),
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(foundryConfigPath, "iana-windows-timezones.json"),
            options.IanaWindowsTimeZoneMapJson,
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        CopyConnectAssetFiles(mountedImagePath, options.FoundryConnectAssetFiles);
        if (options.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload)
        {
            await WriteAutopilotHardwareHashAssetsAsync(
                mountedImagePath,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteAutopilotProfilesAsync(foundryConfigPath, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteNetworkSecretsKeyAsync(
        string foundryConfigPath,
        string connectConfigurationJson,
        byte[]? networkSecretsKey,
        CancellationToken cancellationToken)
    {
        bool hasEncryptedSecrets = MediaSecretEnvelopeProtector.HasEncryptedSecrets(connectConfigurationJson);
        if (networkSecretsKey is null || networkSecretsKey.Length == 0)
        {
            if (hasEncryptedSecrets)
            {
                throw new ArgumentException("Foundry encrypted media secrets require a media secret key.");
            }

            return;
        }

        if (!hasEncryptedSecrets)
        {
            throw new ArgumentException("A media secret key must not be provisioned without encrypted Foundry media secrets.");
        }

        if (networkSecretsKey.Length != 32)
        {
            throw new ArgumentException("Media secret key must be 32 bytes.");
        }

        string secretsPath = Path.Combine(foundryConfigPath, "Secrets");
        Directory.CreateDirectory(secretsPath);
        await File.WriteAllBytesAsync(
            Path.Combine(secretsPath, "media-secrets.key"),
            networkSecretsKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDeploymentSecretsKeyAsync(
        string foundryConfigPath,
        byte[]? deploymentSecretsKey,
        bool isDeploymentProtectionEnabled,
        CancellationToken cancellationToken)
    {
        if (deploymentSecretsKey is null || deploymentSecretsKey.Length == 0)
        {
            if (isDeploymentProtectionEnabled)
            {
                throw new ArgumentException("Protected deployment media requires a Deploy secret key during provisioning.");
            }

            return;
        }

        if (deploymentSecretsKey.Length != 32)
        {
            throw new ArgumentException("Deploy secret key must be 32 bytes.");
        }

        if (isDeploymentProtectionEnabled)
        {
            string plaintextKeyPath = Path.Combine(foundryConfigPath, "Secrets", "deployment-secrets.key");
            if (File.Exists(plaintextKeyPath))
            {
                File.Delete(plaintextKeyPath);
            }
            return;
        }

        string secretsPath = Path.Combine(foundryConfigPath, "Secrets");
        Directory.CreateDirectory(secretsPath);
        await File.WriteAllBytesAsync(
            Path.Combine(secretsPath, "deployment-secrets.key"),
            deploymentSecretsKey,
            cancellationToken).ConfigureAwait(false);
    }

    private static void CopyConnectAssetFiles(
        string mountedImagePath,
        IReadOnlyList<FoundryConnectProvisionedAssetFile> assetFiles)
    {
        foreach (FoundryConnectProvisionedAssetFile assetFile in assetFiles)
        {
            if (string.IsNullOrWhiteSpace(assetFile.SourcePath))
            {
                throw new ArgumentException("Foundry Connect asset source path is required.");
            }

            if (!File.Exists(assetFile.SourcePath))
            {
                throw new IOException($"Foundry Connect asset source file was not found: '{assetFile.SourcePath}'.");
            }

            string destinationPath = ResolveSafeRelativePath(mountedImagePath, assetFile.RelativeDestinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(assetFile.SourcePath, destinationPath, overwrite: true);
        }
    }

    private static void ProvisionBundledSevenZip(
        string mountedImagePath,
        WinPeMountedImageAssetProvisioningOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SevenZipSourceDirectoryPath))
        {
            return;
        }

        string runtimeFolder = options.Architecture.ToSevenZipRuntimeFolder();
        string sourceRootPath = options.SevenZipSourceDirectoryPath;
        string sourceExecutablePath = Path.Combine(sourceRootPath, runtimeFolder, "7za.exe");
        string sourceLicensePath = Path.Combine(sourceRootPath, "License.txt");
        string sourceReadmePath = Path.Combine(sourceRootPath, "readme.txt");

        if (!File.Exists(sourceExecutablePath) || !File.Exists(sourceLicensePath) || !File.Exists(sourceReadmePath))
        {
            throw new IOException($"Bundled 7-Zip assets are incomplete under '{sourceRootPath}' for runtime '{runtimeFolder}'.");
        }

        string destinationToolsRootPath = Path.Combine(mountedImagePath, "Foundry", "Tools", "7zip");
        string destinationRuntimePath = Path.Combine(destinationToolsRootPath, runtimeFolder);
        Directory.CreateDirectory(destinationRuntimePath);

        File.Copy(sourceExecutablePath, Path.Combine(destinationRuntimePath, "7za.exe"), overwrite: true);
        File.Copy(sourceLicensePath, Path.Combine(destinationToolsRootPath, "License.txt"), overwrite: true);
        File.Copy(sourceReadmePath, Path.Combine(destinationToolsRootPath, "readme.txt"), overwrite: true);
    }

    private static async Task WriteAutopilotHardwareHashAssetsAsync(
        string mountedImagePath,
        WinPeMountedImageAssetProvisioningOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Oa3ToolSourcePath) || !File.Exists(options.Oa3ToolSourcePath))
        {
            throw new IOException($"OA3Tool source file was not found: '{options.Oa3ToolSourcePath}'.");
        }

        string oa3ToolsPath = Path.Combine(mountedImagePath, "Foundry", "Tools", "OA3");
        Directory.CreateDirectory(oa3ToolsPath);
        File.Copy(options.Oa3ToolSourcePath, Path.Combine(oa3ToolsPath, "oa3tool.exe"), overwrite: true);

        string runtimePath = Path.Combine(mountedImagePath, "Foundry", "Runtime", "AutopilotHash");
        Directory.CreateDirectory(runtimePath);
        await File.WriteAllTextAsync(
            Path.Combine(runtimePath, "OA3.cfg"),
            Oa3CfgTemplate,
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(runtimePath, "input.xml"),
            Oa3InputTemplate,
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAutopilotProfilesAsync(
        string foundryConfigPath,
        WinPeMountedImageAssetProvisioningOptions options,
        CancellationToken cancellationToken)
    {
        foreach (AutopilotProfileSettings profile in options.AutopilotProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.FolderName))
            {
                throw new ArgumentException("Autopilot profile folder name is required.");
            }

            if (options.IsDeploymentProtectionEnabled)
            {
                if (options.DeploymentSecretsKey is not { Length: 32 })
                {
                    throw new ArgumentException("Protected Autopilot profiles require a 32-byte Deploy secret key.");
                }

                string encryptedProfilePath = ResolveSafeRelativePath(
                    foundryConfigPath,
                    Path.Combine("Autopilot", profile.FolderName, "AutopilotConfigurationFile.json.encrypted"));
                byte[] plaintext = Utf8NoBom.GetBytes(profile.JsonContent);
                try
                {
                    SecretEnvelope envelope = MediaSecretEnvelopeProtector.EncryptBytes(
                        plaintext,
                        options.DeploymentSecretsKey,
                        MediaSecretEnvelopeProtector.DeploymentKeyId);
                    string envelopeJson = JsonSerializer.Serialize(envelope, ConfigurationJsonDefaults.SerializerOptions);
                    Directory.CreateDirectory(Path.GetDirectoryName(encryptedProfilePath)!);
                    await File.WriteAllTextAsync(
                        encryptedProfilePath,
                        envelopeJson,
                        Utf8NoBom,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            else
            {
                string profilePath = ResolveSafeRelativePath(
                    foundryConfigPath,
                    Path.Combine("Autopilot", profile.FolderName, "AutopilotConfigurationFile.json"));

                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                await File.WriteAllTextAsync(profilePath, profile.JsonContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ResolveSafeRelativePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative destination path is required.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Relative destination path must not be rooted: '{relativePath}'.");
        }

        string fullRoot = Path.GetFullPath(rootPath);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootedPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Relative destination path escapes the mounted image: '{relativePath}'.");
        }

        return fullPath;
    }

    private static string FormatProvisioningSource(WinPeProvisioningSource source)
    {
        return source switch
        {
            WinPeProvisioningSource.Debug => "debug",
            WinPeProvisioningSource.Release => "release",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported provisioning source.")
        };
    }

    private static string CreateFallbackFoundryConnectConfigurationJson()
    {
        return JsonSerializer.Serialize(new FoundryConnectConfigurationDocument(), ConfigurationJsonDefaults.SerializerOptions);
    }

    private static string CreateFallbackDeployConfigurationJson()
    {
        return JsonSerializer.Serialize(new FoundryDeployConfigurationDocument(), ConfigurationJsonDefaults.SerializerOptions);
    }

    private static WinPeDiagnostic? ValidateOptions(WinPeMountedImageAssetProvisioningOptions? options)
    {
        if (options is null)
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Mounted image asset provisioning options are required.",
                "Provide a non-null WinPeMountedImageAssetProvisioningOptions instance.");
        }

        if (string.IsNullOrWhiteSpace(options.MountedImagePath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Mounted image path is required.",
                "Set WinPeMountedImageAssetProvisioningOptions.MountedImagePath.");
        }

        if (!Enum.IsDefined(options.Architecture))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "WinPE architecture value is invalid.",
                $"Value: '{options.Architecture}'.");
        }

        if (string.IsNullOrWhiteSpace(options.BootstrapScriptContent))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Foundry bootstrap script content is required.",
                "Set WinPeMountedImageAssetProvisioningOptions.BootstrapScriptContent.");
        }

        if (string.IsNullOrWhiteSpace(options.CurlExecutableSourcePath) || !File.Exists(options.CurlExecutableSourcePath))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "curl.exe source path is required.",
                $"Expected file: '{options.CurlExecutableSourcePath}'.");
        }

        if (string.IsNullOrWhiteSpace(options.IanaWindowsTimeZoneMapJson))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "IANA Windows time zone map JSON is required.",
                "Set WinPeMountedImageAssetProvisioningOptions.IanaWindowsTimeZoneMapJson.");
        }

        if (!Enum.IsDefined(options.ConnectProvisioningSource) || !Enum.IsDefined(options.DeployProvisioningSource))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Provisioning source value is invalid.",
                $"Connect: '{options.ConnectProvisioningSource}', Deploy: '{options.DeployProvisioningSource}'.");
        }

        if (!Enum.IsDefined(options.AutopilotProvisioningMode))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ValidationFailed,
                "Autopilot provisioning mode value is invalid.",
                $"Value: '{options.AutopilotProvisioningMode}'.");
        }

        if (options.AutopilotProvisioningMode == AutopilotProvisioningMode.HardwareHashUpload &&
            (string.IsNullOrWhiteSpace(options.Oa3ToolSourcePath) || !File.Exists(options.Oa3ToolSourcePath)))
        {
            return new WinPeDiagnostic(
                WinPeErrorCodes.ToolNotFound,
                "OA3Tool source path is required for Autopilot hardware hash upload media.",
                $"Expected file: '{options.Oa3ToolSourcePath}'.");
        }

        return null;
    }
}
