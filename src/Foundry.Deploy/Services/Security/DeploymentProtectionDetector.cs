// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models.Configuration;
using Foundry.Deploy.Services.Configuration;
using ConfigurationSchemaVersions = Foundry.Core.Models.Configuration.ConfigurationSchemaVersions;

namespace Foundry.Deploy.Services.Security;

internal static class DeploymentProtectionDetector
{
    private const string DeploymentKeyRelativePath = @"Config\Secrets\deployment-secrets.key";
    private const string AutopilotProfilesRelativePath = @"Config\Autopilot";

    public static bool RequiresUnlock(DeployProtectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SecretEnvelope? envelope = settings.ProtectedDeploymentKey;

        return settings.IsEnabled ||
               !string.IsNullOrWhiteSpace(settings.KeyDerivationAlgorithm) ||
               settings.Iterations != 0 ||
               !string.IsNullOrWhiteSpace(settings.Salt) ||
               envelope is null ||
               !string.IsNullOrWhiteSpace(envelope.Kind) ||
               !string.IsNullOrWhiteSpace(envelope.Algorithm) ||
               !string.IsNullOrWhiteSpace(envelope.KeyId) ||
               !string.IsNullOrWhiteSpace(envelope.Nonce) ||
               !string.IsNullOrWhiteSpace(envelope.Tag) ||
               !string.IsNullOrWhiteSpace(envelope.Ciphertext);
    }

    public static bool HasProtectedArtifacts(
        DeployConfigurationLoadResult configuration,
        string? workspaceRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Document is null && configuration.Exists)
        {
            return true;
        }

        string? rootPath = ResolveWorkspaceRootPath(configuration.ConfigurationPath, workspaceRootPath);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            string unattendRootPath = Path.Combine(rootPath, "Config", "Unattend");
            if (configuration.Document?.Unattend.IsEnabled == true ||
                Directory.Exists(unattendRootPath) && Directory.EnumerateFiles(unattendRootPath, "*", SearchOption.AllDirectories).Any(path =>
                    path.EndsWith(".xml.encrypted", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string autopilotRootPath = Path.Combine(rootPath, AutopilotProfilesRelativePath);
            if (Directory.Exists(autopilotRootPath) &&
                Directory.EnumerateFiles(
                    autopilotRootPath,
                    "*.encrypted",
                    SearchOption.AllDirectories).Any())
            {
                return true;
            }

            if (configuration.Document is null)
            {
                return false;
            }

            return configuration.Document.SchemaVersion >= ConfigurationSchemaVersions.DeployCurrent &&
                   !File.Exists(Path.Combine(rootPath, DeploymentKeyRelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static string? ResolveWorkspaceRootPath(string configurationPath, string? workspaceRootPath)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRootPath))
        {
            return workspaceRootPath;
        }

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return null;
        }

        string? configurationDirectoryPath = Path.GetDirectoryName(configurationPath);
        return string.IsNullOrWhiteSpace(configurationDirectoryPath)
            ? null
            : Directory.GetParent(configurationDirectoryPath)?.FullName;
    }
}
