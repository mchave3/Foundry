// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Autopilot;
using Foundry.Core.Services.Configuration;

namespace Foundry.Core.Services.WinPe;

/// <summary>
/// Packages the validated source bytes as protected assets matching the generated deployment manifest.
/// </summary>
internal static class WinPeUnattendAssetProvisioner
{
    public static async Task ProvisionAsync(
        string configurationRoot,
        WinPeMountedImageAssetProvisioningOptions options,
        string deployConfigurationJson,
        CancellationToken cancellationToken)
    {
        UnattendFileService.ValidateSettings(options.Unattend, options.IsDeploymentProtectionEnabled);
        FoundryDeployConfigurationDocument configuration = JsonSerializer.Deserialize<FoundryDeployConfigurationDocument>(
            deployConfigurationJson, ConfigurationJsonDefaults.SerializerOptions)
            ?? throw new InvalidOperationException("Deployment configuration is required for answer-file provisioning.");
        ValidateManifest(options, configuration);

        string assetRoot = Path.Combine(configurationRoot, "Unattend");
        if (Directory.Exists(assetRoot) && (File.GetAttributes(assetRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The answer-file asset directory must not be a link.");
        }

        HashSet<string> retained = new(StringComparer.OrdinalIgnoreCase);
        if (options.Unattend.IsEnabled)
        {
            Directory.CreateDirectory(assetRoot);
            foreach (UnattendFileSettings file in options.Unattend.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = UnattendFileService.GetAssetFileName(file.Id);
                string destination = Path.Combine(assetRoot, fileName);
                byte[] content = UnattendFileService.ReadValidated(file);
                try
                {
                    SecretEnvelope envelope = MediaSecretEnvelopeProtector.EncryptBytes(
                        content, options.DeploymentSecretsKey!, MediaSecretEnvelopeProtector.DeploymentKeyId);
                    if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("An answer-file asset must not be a link.");
                    }
                    await File.WriteAllTextAsync(destination,
                        JsonSerializer.Serialize(envelope, ConfigurationJsonDefaults.SerializerOptions),
                        cancellationToken).ConfigureAwait(false);
                    retained.Add(fileName);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
        }

        RemoveObsoleteAssets(assetRoot, retained);
    }

    private static void ValidateManifest(
        WinPeMountedImageAssetProvisioningOptions options,
        FoundryDeployConfigurationDocument configuration)
    {
        DeployUnattendSettings manifest = configuration.Unattend
            ?? throw new InvalidOperationException("The answer-file manifest is invalid.");
        if (manifest.IsEnabled != options.Unattend.IsEnabled)
        {
            throw new InvalidOperationException("The answer-file manifest does not match the media sources.");
        }
        if (!manifest.IsEnabled)
        {
            if (manifest.Files is null || manifest.Files.Count != 0 || manifest.DefaultFileId is not null)
            {
                throw new InvalidOperationException("Disabled answer-file media must not contain an active manifest.");
            }
            return;
        }

        if (configuration.Protection?.IsEnabled != true || configuration.Protection.ProtectedDeploymentKey is null ||
            options.DeploymentSecretsKey is not { Length: 32 })
        {
            throw new InvalidOperationException("Custom answer files require password-protected deployment media.");
        }
        if (!string.Equals(manifest.DefaultFileId, options.Unattend.DefaultFileId, StringComparison.OrdinalIgnoreCase) ||
            manifest.Files is null || manifest.Files.Count != options.Unattend.Files.Count ||
            manifest.Files.Any(file => file is null) ||
            manifest.Files.Select(file => file.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
        {
            throw new InvalidOperationException("The answer-file manifest does not match the media sources.");
        }
        foreach (UnattendFileSettings source in options.Unattend.Files)
        {
            DeployUnattendFile? target = manifest.Files.SingleOrDefault(file =>
                string.Equals(file.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            if (target is null || !string.Equals(target.ContentHash, source.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(target.DisplayName, source.DisplayName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The answer-file manifest does not match the media sources.");
            }
        }
    }

    private static void RemoveObsoleteAssets(string assetRoot, HashSet<string> retained)
    {
        if (!Directory.Exists(assetRoot))
        {
            return;
        }
        foreach (string path in Directory.EnumerateFiles(assetRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            bool isManagedFile = name.Length > 32 && Guid.TryParseExact(name[..32], "N", out _) &&
                (name[32..].Equals(".xml.encrypted", StringComparison.OrdinalIgnoreCase) ||
                 name[32..].Equals(".xml", StringComparison.OrdinalIgnoreCase));
            if (isManagedFile && !retained.Contains(name))
            {
                File.Delete(path);
            }
        }
    }
}
