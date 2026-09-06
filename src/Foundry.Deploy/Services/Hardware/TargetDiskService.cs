// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Localization;
using Foundry.Utilities.Storage;
using Microsoft.Extensions.Logging;

namespace Foundry.Deploy.Services.Hardware;

public sealed class TargetDiskService : ITargetDiskService
{
    private readonly IWindowsDiskInspector _diskInspector;
    private readonly ILogger<TargetDiskService> _logger;

    public TargetDiskService(
        IWindowsDiskInspector diskInspector,
        ILogger<TargetDiskService> logger)
    {
        _diskInspector = diskInspector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TargetDiskInfo>> GetDisksAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Querying target disks.");

        try
        {
            IReadOnlyList<DiskInfo> snapshots = await _diskInspector
                .GetDisksAsync(cancellationToken)
                .ConfigureAwait(false);
            var disks = new List<TargetDiskInfo>();

            foreach (DiskInfo snapshot in snapshots)
            {
                TargetDiskInfo disk = MapDisk(snapshot);
                if (ShouldExcludeFromTargets(disk))
                {
                    _logger.LogInformation(
                        "Skipping disk {DiskNumber} from target selection because it is attached over USB. FriendlyName={FriendlyName}",
                        disk.DiskNumber,
                        disk.FriendlyName);
                    continue;
                }

                disks.Add(disk);
            }

            TargetDiskInfo[] orderedDisks = disks
                .OrderByDescending(static disk => disk.IsSelectable)
                .ThenBy(static disk => disk.DiskNumber)
                .ToArray();

            _logger.LogInformation(
                "Resolved {DiskCount} target disks ({SelectableCount} selectable).",
                orderedDisks.Length,
                orderedDisks.Count(static disk => disk.IsSelectable));
            return orderedDisks;
        }
        catch (InvalidDataException exception)
        {
            _logger.LogError(exception, "Failed to inspect target disks.");
            return [];
        }
    }

    public async Task<int?> GetDiskNumberForPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _diskInspector
                .ResolveDiskNumberForPathAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogError(exception, "Failed to resolve the disk number for path {Path}.", path);
            return null;
        }
    }

    private static TargetDiskInfo MapDisk(DiskInfo snapshot)
    {
        string warning = BuildSelectionWarning(
            snapshot.IsSystem,
            snapshot.IsBoot,
            snapshot.IsReadOnly,
            snapshot.IsOffline);

        return new TargetDiskInfo
        {
            DiskNumber = snapshot.Number,
            FriendlyName = NormalizeValue(snapshot.FriendlyName),
            UniqueId = snapshot.UniqueId.Trim(),
            SerialNumber = snapshot.SerialNumber.Trim(),
            BusType = NormalizeValue(snapshot.BusType),
            PartitionStyle = NormalizeValue(snapshot.PartitionStyle),
            SizeBytes = snapshot.SizeBytes,
            IsSystem = snapshot.IsSystem,
            IsBoot = snapshot.IsBoot,
            IsReadOnly = snapshot.IsReadOnly,
            IsOffline = snapshot.IsOffline,
            IsRemovable = snapshot.IsRemovable,
            IsSelectable = string.IsNullOrWhiteSpace(warning),
            SelectionWarning = warning
        };
    }

    private static string BuildSelectionWarning(
        bool isSystem,
        bool isBoot,
        bool isReadOnly,
        bool isOffline)
    {
        if (isSystem)
        {
            return LocalizationText.GetString("Disk.BlockedSystemDisk");
        }

        if (isBoot)
        {
            return LocalizationText.GetString("Disk.BlockedBootDisk");
        }

        if (isReadOnly)
        {
            return LocalizationText.GetString("Disk.BlockedReadOnly");
        }

        if (isOffline)
        {
            return LocalizationText.GetString("Disk.BlockedOffline");
        }

        return string.Empty;
    }

    private static bool ShouldExcludeFromTargets(TargetDiskInfo disk)
        => string.Equals(disk.BusType, "USB", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeValue(string value)
    {
        string normalized = value.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? LocalizationText.GetString("Common.Unknown")
            : normalized;
    }
}
