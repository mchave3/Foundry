// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;

namespace Foundry.Utilities.Processes;

/// <summary>Protects an input file until native consumption has completed or been explicitly reconciled.</summary>
public sealed class NativeFileLease : IDisposable
{
    /// <summary>Identifies retained ownership using opaque identifiers, never file paths.</summary>
    public const string RetainedLeaseIdsDataKey = "RetainedNativeFileLeaseIds";

    private static readonly ConcurrentDictionary<Guid, FileStream> Retained = new();
    private readonly object _gate = new();
    private FileStream? _stream;
    private bool _nativeCallActive;
    private bool _disposeRequested;

    private NativeFileLease(string path) => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Opens a read handle that disallows writes and deletion.</summary>
    public static NativeFileLease OpenRead(string path) => new(path);

    /// <summary>Runs only an actual native consumer; verification and other preparation must happen before this call.</summary>
    /// <remarks>
    /// Interrupted consumers retain process-lifetime protection until explicit reconciliation.
    /// Root exit and output draining never substitute for confirmed tree completion.
    /// </remarks>
    public async Task<T> RunAsync<T>(Func<Task<T>> nativeCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeCall);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stream is null || _disposeRequested, this);
            if (_nativeCallActive)
            {
                throw new InvalidOperationException("A file lease can protect only one active native call at a time.");
            }
            _nativeCallActive = true;
        }

        try
        {
            return await nativeCall().ConfigureAwait(false);
        }
        catch (Exception error) when (RequiresRetention(error))
        {
            lock (_gate)
            {
                Guid ownershipId;
                do
                {
                    ownershipId = Guid.NewGuid();
                } while (!Retained.TryAdd(ownershipId, _stream!));
                _stream = null;
                Guid[] previousIds = error.Data[RetainedLeaseIdsDataKey] as Guid[] ?? [];
                error.Data[RetainedLeaseIdsDataKey] = previousIds.Append(ownershipId).ToArray();
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _nativeCallActive = false;
                if (_disposeRequested)
                {
                    _stream?.Dispose();
                    _stream = null;
                }
            }
        }
    }

    /// <summary>Reports whether an interruption still owns protected file bytes.</summary>
    public static bool HasRetainedProtection(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Data[RetainedLeaseIdsDataKey] is Guid[] ownershipIds && ownershipIds.Any(Retained.ContainsKey);
    }

    /// <summary>Releases only the identified retained lease after its owner positively confirms all native consumers have completed.</summary>
    /// <remarks>
    /// The owner must establish completion independently of root exit or pipe drain. Failed or canceled reconciliation retains protection.
    /// This internal seam deliberately leaves application reconciliation and shutdown policy to its future owning lifecycle coordinator.
    /// </remarks>
    internal static async Task<bool> ReconcileRetainedAsync(Guid ownershipId, Func<CancellationToken, Task<bool>> confirmAllNativeConsumersCompleted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmAllNativeConsumersCompleted);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Retained.ContainsKey(ownershipId) || !await confirmAllNativeConsumersCompleted(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!Retained.TryRemove(ownershipId, out FileStream? stream))
        {
            return false;
        }
        stream.Dispose();
        return true;
    }

    private static bool RequiresRetention(Exception error)
    {
        bool hasNativeMetadata = error.Data.Contains("ProcessRootExitConfirmed") ||
            error.Data.Contains("ProcessTreeTerminationConfirmed") || error.Data.Contains("ProcessOutputDrainConfirmed");
        return (hasNativeMetadata || error is TimeoutException or OperationCanceledException) &&
            !(error.Data["ProcessRootExitConfirmed"] is true && error.Data["ProcessTreeTerminationConfirmed"] is true);
    }

    /// <summary>Releases protection after a normally completed native consumer or a failure before native consumption.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposeRequested = true;
            if (!_nativeCallActive)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
