// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Core.Services.Configuration;
using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.Deployment.Unattend;

/// <summary>Validates the runtime manifest and resolves only application-owned asset filenames.</summary>
internal static class UnattendCatalog
{
    /// <summary>Rejects invalid defaults and unprotected custom catalogs rather than selecting native mode.</summary>
    public static void Validate(DeployUnattendSettings? settings, bool isProtected)
    {
        if (settings is null || settings.Files is null)
        {
            throw new InvalidDataException("The answer-file catalog is invalid.");
        }
        if (!settings.IsEnabled)
        {
            if (settings.Files.Count > 0 || settings.DefaultFileId is not null)
            {
                throw new InvalidDataException("A disabled answer-file catalog must not contain deployment assets.");
            }
            return;
        }
        if (!isProtected || settings.Files.Count == 0)
        {
            throw new InvalidDataException("Answer files require protected media and a nonempty catalog.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DeployUnattendFile file in settings.Files)
        {
            if (file is null || !ids.Add(file.Id) || string.IsNullOrWhiteSpace(file.DisplayName) ||
                file.DisplayName.Length > 200 || file.DisplayName.Any(char.IsControl) ||
                file.ContentHash is null || file.ContentHash.Length != 64 || !file.ContentHash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The answer-file catalog contains invalid metadata.");
            }
            UnattendFileService.GetAssetFileName(file.Id);
        }
        if (settings.DefaultFileId is not null && !ids.Contains(settings.DefaultFileId))
        {
            throw new InvalidDataException("The default answer file is missing from the catalog.");
        }
    }

    /// <summary>Resolves assets beside the configuration file, independently of the deployment cache location.</summary>
    public static IReadOnlyList<UnattendSelection> Resolve(DeployUnattendSettings settings, string configurationPath)
    {
        if (!settings.IsEnabled)
        {
            return [];
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        return settings.Files.Select(file => new UnattendSelection(file,
            Path.Combine(directory, "Unattend", UnattendFileService.GetAssetFileName(file.Id)))).ToArray();
    }
}
