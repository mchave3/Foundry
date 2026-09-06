// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Foundry.Core.Services.Security;
using Foundry.Deploy.Services.Http;
using Foundry.Utilities.Networking;
using Foundry.Utilities.Security;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Download;

/// <summary>Verifies persistent bytes and publishes only complete payloads validated against authenticated metadata.</summary>
public sealed class ArtifactDownloadService : IArtifactDownloadService
{
    private readonly HttpClient? _httpClient;
    private readonly ILogger<ArtifactDownloadService> _logger;
    private readonly Func<string, IReadOnlySet<string>, CancellationToken, Task> _verifySignature;

    public ArtifactDownloadService(ILogger<ArtifactDownloadService> logger)
    {
        _logger = logger;
        _verifySignature = AuthenticodeVerifier.VerifyAsync;
    }

    internal ArtifactDownloadService(ILogger<ArtifactDownloadService> logger, HttpClient httpClient,
        Func<string, IReadOnlySet<string>, CancellationToken, Task>? verifySignature = null)
        : this(logger)
    {
        _httpClient = httpClient;
        _verifySignature = verifySignature ?? AuthenticodeVerifier.VerifyAsync;
    }

    /// <summary>Reads and rehashes actual bytes without requiring write access or trusting a writable manifest.</summary>
    public async Task<ArtifactDownloadResult?> TryUseCachedAsync(
        ArtifactIdentity artifact,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArtifactIntegrityPolicy.ValidateIdentity(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (artifact.Integrity.Digest is null)
        {
            return null;
        }
        ArtifactIntegrityPolicy.Validate(artifact);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (artifact.Integrity.SizeBytes is long expectedSize && stream.Length != expectedSize)
            {
                _logger.LogWarning("Cached artifact length is invalid. ArtifactKind={ArtifactKind}", artifact.Kind);
                return null;
            }

            using HashAlgorithm algorithm = artifact.Integrity.Digest.Algorithm == HashAlgorithmName.SHA256 ? SHA256.Create() : SHA1.Create();
            byte[] hash = await algorithm.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(artifact.Integrity.Digest.Hex)))
            {
                _logger.LogWarning("Cached artifact digest is invalid. ArtifactKind={ArtifactKind}", artifact.Kind);
                return null;
            }

            return new ArtifactDownloadResult { DestinationPath = path, Downloaded = false, Method = "cache-hit", SizeBytes = stream.Length };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Acquires a cache miss with bounded retries and atomic verified publication.</summary>
    public async Task<ArtifactDownloadResult> DownloadAsync(
        ArtifactIdentity artifact,
        string path,
        CancellationToken cancellationToken = default,
        IProgress<DownloadProgress>? progress = null)
    {
        ArtifactIntegrityPolicy.Validate(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArtifactDownloadResult? cached = await TryUseCachedAsync(artifact, path, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Report(new DownloadProgress(cached.SizeBytes, cached.SizeBytes));
            return cached;
        }

        Uri effectiveSource = artifact.Kind == "OperatingSystemImage" &&
            Path.GetExtension(artifact.FileName).Equals(".esd", StringComparison.OrdinalIgnoreCase)
            ? new Uri(WindowsUpdateContentUrl.Normalize(artifact.SourceUri.AbsoluteUri)) : artifact.SourceUri;
        ArtifactIntegrityPolicy.ValidateSourceUri(artifact, effectiveSource);
        using HttpClient? ownedClient = _httpClient is null
            ? AcquisitionHttpClientFactory.Create(TimeSpan.FromSeconds(30), uri => ArtifactIntegrityPolicy.ValidateSourceUri(artifact, uri))
            : null;
        HttpClient client = _httpClient ?? ownedClient!;
        _logger.LogInformation("Starting artifact acquisition. SourceHost={SourceHost}, ArtifactKind={ArtifactKind}", artifact.SourceUri.Host, artifact.Kind);
        try
        {
            long bytes = await HttpRetryPolicy.ExecuteAsync(
                ct => ValidatedFileTransfer.DownloadAsync(client, effectiveSource, path, artifact.Integrity,
                    new TransferLimits(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(2)),
                    validateStagedFile: artifact.ExpectedPublisher is null ? null : (stagedPath, token) => VerifyStagedSignatureAsync(artifact, stagedPath, token),
                    progress: progress is null ? null : new DownloadProgressAdapter(progress, artifact.Integrity.SizeBytes),
                    cancellationToken: ct),
                _logger, "Artifact acquisition", cancellationToken, HttpOperationOptions.Payload).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(bytes, bytes));

            try
            {
                await ArtifactCacheManifestService.WriteAsync(path, artifact, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Artifact inventory could not be saved after successful publication. ArtifactKind={ArtifactKind}, FailureType={FailureType}", artifact.Kind, ex.GetType().Name);
            }
            _logger.LogInformation("Artifact acquisition completed. SourceHost={SourceHost}, ArtifactKind={ArtifactKind}, SizeBytes={SizeBytes}", artifact.SourceUri.Host, artifact.Kind, bytes);
            return new ArtifactDownloadResult { DestinationPath = path, Downloaded = true, Method = "httpclient", SizeBytes = bytes };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Artifact acquisition failed. SourceHost={SourceHost}, ArtifactKind={ArtifactKind}, FailureType={FailureType}", artifact.SourceUri.Host, artifact.Kind, ex.GetType().Name);
            throw;
        }
    }

    private async Task VerifyStagedSignatureAsync(ArtifactIdentity artifact, string path, CancellationToken cancellationToken)
    {
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await _verifySignature(path, VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(artifact.PackageFamily!), cancellationToken).ConfigureAwait(false);
    }

    private sealed class DownloadProgressAdapter(IProgress<DownloadProgress> progress, long? totalBytes) : IProgress<long>
    {
        private long _lastReport;
        public void Report(long bytes)
        {
            if (_lastReport == 0 || Stopwatch.GetElapsedTime(_lastReport) >= TimeSpan.FromMilliseconds(100) || bytes == totalBytes)
            {
                _lastReport = Stopwatch.GetTimestamp();
                progress.Report(new DownloadProgress(bytes, totalBytes));
            }
        }
    }
}
