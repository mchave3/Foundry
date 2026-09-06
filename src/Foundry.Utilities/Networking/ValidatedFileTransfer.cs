// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security.Cryptography;

namespace Foundry.Utilities.Networking;

/// <summary>
/// Validates a staged download before atomically publishing it under an exclusive destination lock.
/// </summary>
public static class ValidatedFileTransfer
{
    private const int BufferSize = 80 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Returns the published byte count after validating and closing a uniquely owned sibling file.
    /// Caller cancellation and the overall budget include lock acquisition and staged validation.
    /// HTTP handlers own redirect policy and may impose a shorter header timeout.
    /// </summary>
    public static async Task<long> DownloadAsync(
        HttpClient client,
        Uri source,
        string destinationPath,
        FileIntegrity integrity,
        TransferLimits limits,
        Func<string, CancellationToken, Task>? validateStagedFile = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(integrity);
        ArgumentNullException.ThrowIfNull(limits);
        _ = ParseDigest(integrity.Digest);
        ValidateLimits(integrity, limits);
        cancellationToken.ThrowIfCancellationRequested();

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directoryPath = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The destination directory could not be resolved.", nameof(destinationPath));
        string partialPath = Path.Combine(directoryPath,
            $".{Path.GetFileNameWithoutExtension(fullDestinationPath)}.{Guid.NewGuid():N}.partial{Path.GetExtension(fullDestinationPath)}");
        bool ownsPartial = false;
        Exception? operationException = null;
        HttpResponseMessage? response = null;
        FileStream? destination = null;
        using var overall = new CancellationTokenSource(limits.OverallTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overall.Token);

        try
        {
            Directory.CreateDirectory(directoryPath);
            await using FileStream destinationLock = await AcquireDestinationLockAsync(fullDestinationPath, LockTimeout, operation.Token).ConfigureAwait(false);
            response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, operation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpResponseException(response.StatusCode, response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
            }

            long? responseLength = response.Content.Headers.ContentLength;
            ValidateResponseLength(responseLength, integrity.SizeBytes, limits.MaximumBytes);
            destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            ownsPartial = true;
            long written = await CopyBodyAsync(response.Content, destination, integrity, limits, responseLength, progress, operation.Token).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            destination = null;
            response.Dispose();
            response = null;

            if (validateStagedFile is not null)
            {
                await validateStagedFile(partialPath, operation.Token).ConfigureAwait(false);
            }

            operation.Token.ThrowIfCancellationRequested();
            if (File.Exists(fullDestinationPath))
            {
                File.Replace(partialPath, fullDestinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(partialPath, fullDestinationPath);
            }

            ownsPartial = false;
            return written;
        }
        catch (Exception exception)
        {
            operationException = exception;
            if (exception is OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The transfer was canceled.", exception, cancellationToken);
                }

                if (overall.IsCancellationRequested)
                {
                    throw new TransferTimeoutException(TransferTimeoutKind.Overall);
                }
            }

            throw;
        }
        finally
        {
            try
            {
                if (destination is not null)
                {
                    await destination.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch when (operationException is not null)
            {
            }

            try
            {
                response?.Dispose();
            }
            catch when (operationException is not null)
            {
            }

            if (ownsPartial)
            {
                try
                {
                    File.Delete(partialPath);
                }
                catch when (operationException is not null)
                {
                    // Cleanup must not replace the error that prevented publication.
                }
            }
        }
    }

    /// <summary>
    /// Copies and flushes bounded bytes without disposing the content or destination; only body-read failures are transport failures.
    /// </summary>
    internal static async Task<long> CopyBodyAsync(
        HttpContent content,
        Stream destination,
        FileIntegrity integrity,
        TransferLimits limits,
        long? responseLength,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        byte[]? expectedDigest = ParseDigest(integrity.Digest);
        using IncrementalHash? hash = integrity.Digest is null ? null : IncrementalHash.CreateHash(integrity.Digest.Algorithm);
        using var inactivity = new CancellationTokenSource(limits.NoProgressTimeout);
        using var body = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, inactivity.Token);
        var buffer = new byte[BufferSize];
        long written = 0;
        try
        {
            Stream source = await OpenBodyAsync(content, body.Token).ConfigureAwait(false);
            while (true)
            {
                body.Token.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, body.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new TransferReadException(exception);
                }

                if (read == 0)
                {
                    break;
                }

                long nextSize = checked(written + read);
                EnsureWithinLimit(nextSize, integrity.SizeBytes);
                EnsureWithinLimit(nextSize, responseLength);
                EnsureWithinLimit(nextSize, limits.MaximumBytes);
                await destination.WriteAsync(buffer.AsMemory(0, read), body.Token).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, read);
                written = nextSize;
                inactivity.CancelAfter(limits.NoProgressTimeout);
                progress?.Report(written);
            }

            if ((integrity.SizeBytes.HasValue && written != integrity.SizeBytes.Value)
                || (responseLength.HasValue && written != responseLength.Value))
            {
                throw new InvalidDataException("The transferred size does not match its expected length.");
            }

            if (hash is not null && !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedDigest))
            {
                throw new InvalidDataException("The transferred digest does not match its expected value.");
            }

            await destination.FlushAsync(body.Token).ConfigureAwait(false);
            body.Token.ThrowIfCancellationRequested();
            return written;
        }
        catch (OperationCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The transfer was canceled.", exception, cancellationToken);
            }

            if (inactivity.IsCancellationRequested)
            {
                throw new TransferTimeoutException(TransferTimeoutKind.NoProgress);
            }

            throw;
        }
    }

    /// <summary>
    /// Acquires exclusive filesystem ownership across processes within a wait budget; public transfers use 30 seconds.
    /// </summary>
    internal static async Task<FileStream> AcquireDestinationLockAsync(string destinationPath, TimeSpan waitTimeout, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file: deleting it after release could race another owner's acquisition.
                return new FileStream(destinationPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
            {
                TimeSpan remaining = waitTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new IOException("The destination is busy with another transfer.", exception);
                }

                await Task.Delay(remaining < LockRetryDelay ? remaining : LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new TransferReadException(exception);
        }
    }

    private static byte[]? ParseDigest(FileDigest? digest)
    {
        if (digest is null)
        {
            return null;
        }

        int hexLength = digest.Algorithm == HashAlgorithmName.SHA256 ? 64
            : digest.Algorithm == HashAlgorithmName.SHA1 ? 40 : 0;
        if (hexLength == 0 || digest.Hex is null || digest.Hex.Length != hexLength)
        {
            throw new ArgumentException("The digest must specify SHA256 or SHA1 with its exact hexadecimal length.", nameof(digest));
        }

        try
        {
            return Convert.FromHexString(digest.Hex);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The digest contains invalid hexadecimal characters.", nameof(digest), exception);
        }
    }

    private static void ValidateLimits(FileIntegrity integrity, TransferLimits limits)
    {
        if (integrity.SizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(integrity), "A supplied expected size must be positive.");
        }

        if (limits.MaximumBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "A supplied maximum size must be positive.");
        }

        ValidateTimeout(limits.OverallTimeout, nameof(limits.OverallTimeout));
        ValidateTimeout(limits.NoProgressTimeout, nameof(limits.NoProgressTimeout));
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Transfer timeouts must be positive, finite timer durations.");
        }
    }

    private static void ValidateResponseLength(long? responseLength, long? expectedSize, long? maximumBytes)
    {
        if (!responseLength.HasValue)
        {
            return;
        }

        EnsureWithinLimit(responseLength.Value, maximumBytes);
        if (expectedSize.HasValue && responseLength.Value != expectedSize.Value)
        {
            throw new InvalidDataException("The response length does not match the expected file size.");
        }
    }

    private static void EnsureWithinLimit(long size, long? limit)
    {
        if (limit.HasValue && size > limit.Value)
        {
            throw new InvalidDataException("The transfer exceeds its permitted length.");
        }
    }
}
