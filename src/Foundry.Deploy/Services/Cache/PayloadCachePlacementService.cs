// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Services.Download;

namespace Foundry.Deploy.Services.Cache;

/// <summary>Describes an authenticated existing payload or a destination with observed writable capacity.</summary>
public sealed record PayloadCachePlacement(string Path, bool IsValidatedCacheHit, bool UsesTargetStorage)
{
    /// <summary>Preserves the verified read result so consumers do not rehash a large payload during placement.</summary>
    public ArtifactDownloadResult? CachedArtifact { get; init; }
}

/// <summary>Reuses verified independent storage before checking capacity for an atomic new payload.</summary>
public sealed class PayloadCachePlacementService(IArtifactDownloadService downloads, IVolumeStorageProbe storageProbe)
{
    private const long UnknownSizeInitialCapacity = 64L * 1024 * 1024;

    public async Task<PayloadCachePlacement> ResolveAsync(ArtifactIdentity artifact, string preferredRoot,
        string? targetRoot, CancellationToken cancellationToken = default)
    {
        ArtifactIntegrityPolicy.Validate(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredRoot);
        string[] roots = string.IsNullOrWhiteSpace(targetRoot) || string.Equals(preferredRoot, targetRoot, StringComparison.OrdinalIgnoreCase)
            ? [preferredRoot] : [preferredRoot, targetRoot];
        List<string> failures = [];
        foreach (string root in roots)
        {
            foreach (string path in new[] { Path.Combine(root, artifact.CacheKey, artifact.FileName), Path.Combine(root, artifact.FileName) })
            {
                try
                {
                    ArtifactDownloadResult? hit = await downloads.TryUseCachedAsync(artifact, path, cancellationToken).ConfigureAwait(false);
                    if (hit is not null)
                    {
                        return new(path, true, root != preferredRoot) { CachedArtifact = hit };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"Cache read unavailable ({ex.GetType().Name}).");
                }
            }
        }

        long requiredBytes = artifact.Integrity.SizeBytes ?? UnknownSizeInitialCapacity;
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(root, artifact.CacheKey);
            VolumeStorageStatus status;
            try
            {
                status = storageProbe.Inspect(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"Storage discovery failed ({ex.GetType().Name}).");
                continue;
            }
            string? failure = !status.IsPresent ? "volume is unavailable" : !status.IsWritable ? "volume is not writable" :
                status.FreeBytes is null ? "capacity is unknown" : status.FreeBytes < requiredBytes ? "capacity is insufficient" : null;
            if (failure is null)
            {
                return new(Path.Combine(directory, artifact.FileName), false, root != preferredRoot);
            }
            failures.Add(failure);
        }
        throw new IOException($"No payload storage is available for {artifact.Kind}: {string.Join("; ", failures.Distinct())}.");
    }
}
