// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text.Json;

namespace Foundry.Deploy.Services.Download;

/// <summary>Records best-effort inventory only; manifests never authorize cached content.</summary>
internal static class ArtifactCacheManifestService
{
    public static async Task WriteAsync(string artifactPath, ArtifactIdentity identity, CancellationToken cancellationToken)
    {
        FileInfo artifact = new(artifactPath);
        var manifest = new ArtifactCacheManifest
        {
            ArtifactKind = identity.Kind,
            SourceUrl = identity.SourceUri.GetLeftPart(UriPartial.Authority),
            HashAlgorithm = identity.Integrity.Digest?.Algorithm.Name ?? string.Empty,
            ExpectedHash = identity.Integrity.Digest?.Hex ?? string.Empty,
            ExpectedSizeBytes = identity.Integrity.SizeBytes,
            FileSizeBytes = artifact.Length,
            FileLastWriteTimeUtc = new DateTimeOffset(artifact.LastWriteTimeUtc, TimeSpan.Zero),
            ValidatedAtUtc = DateTimeOffset.UtcNow
        };
        await using FileStream stream = File.Create(GetManifestPath(artifactPath));
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static string GetManifestPath(string artifactPath) => $"{artifactPath}.manifest.json";
}
