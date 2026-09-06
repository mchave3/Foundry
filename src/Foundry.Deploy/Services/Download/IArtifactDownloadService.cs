// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Deploy.Services.Download;

/// <summary>Separates read-only verified cache reuse from payload acquisition.</summary>
public interface IArtifactDownloadService
{
    /// <summary>Rehashes existing bytes without writing; returns null for missing, corrupt, or hashless content, preserving access and cancellation failures.</summary>
    Task<ArtifactDownloadResult?> TryUseCachedAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken = default);

    /// <summary>Reuses verified cache bytes or atomically publishes a bounded acquisition that satisfies artifact policy.</summary>
    Task<ArtifactDownloadResult> DownloadAsync(ArtifactIdentity artifact, string path,
        CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null);
}
