// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Hardware;
using Foundry.Deploy.Services.Localization;
using Foundry.Utilities.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class TargetDiskServiceTests
{
    [Fact]
    public async Task GetDisksAsync_MapsFactsAndAppliesTargetSelectionPolicy()
    {
        DiskInfo[] snapshots =
        [
            new(0, "System", "", "NVMe", "GPT", 1024, true, false, false, false, false),
            new(2, "USB media", "USB-2", "USB", "GPT", 2048, false, false, false, false, true),
            new(1, "Target", "SERIAL-1", "SATA", "GPT", 4096, false, false, false, false, false) { UniqueId = "UNIQUE-1" },
            new(3, "Removable target", "SERIAL-3", "SD", "GPT", 8192, false, false, false, false, true)
        ];
        var service = CreateService(getDisks: _ => Task.FromResult<IReadOnlyList<DiskInfo>>(snapshots));

        IReadOnlyList<TargetDiskInfo> disks = await service.GetDisksAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 0], disks.Select(static disk => disk.DiskNumber));
        Assert.True(disks[0].IsSelectable);
        Assert.Equal("Target", disks[0].FriendlyName);
        Assert.Equal("UNIQUE-1", disks[0].UniqueId);
        Assert.DoesNotContain("UNIQUE-1", disks[0].DisplayLabel, StringComparison.Ordinal);
        Assert.Equal("SERIAL-1", disks[0].SerialNumber);
        Assert.True(disks[1].IsSelectable);
        Assert.True(disks[1].IsRemovable);
        Assert.False(disks[2].IsSelectable);
        Assert.Equal(LocalizationText.GetString("Disk.BlockedSystemDisk"), disks[2].SelectionWarning);
        Assert.Empty(disks[2].SerialNumber);
    }

    [Fact]
    public async Task GetDisksAsync_WhenInspectionDataIsInvalid_ReturnsEmptyList()
    {
        var service = CreateService(getDisks: _ => Task.FromException<IReadOnlyList<DiskInfo>>(
            new InvalidDataException()));

        IReadOnlyList<TargetDiskInfo> disks = await service.GetDisksAsync(TestContext.Current.CancellationToken);

        Assert.Empty(disks);
    }

    [Fact]
    public async Task GetDisksAsync_WhenInspectionFailsUnexpectedly_PropagatesFailure()
    {
        var service = CreateService(getDisks: _ => Task.FromException<IReadOnlyList<DiskInfo>>(
            new ApplicationException("Unexpected failure.")));

        await Assert.ThrowsAsync<ApplicationException>(
            () => service.GetDisksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDisksAsync_WhenInspectionIsCanceled_PropagatesCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var service = CreateService(getDisks: cancellationToken =>
            Task.FromCanceled<IReadOnlyList<DiskInfo>>(cancellationToken));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetDisksAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task GetDiskNumberForPathAsync_DelegatesToInspector()
    {
        string? capturedPath = null;
        var service = CreateService(resolveDisk: (path, _) =>
        {
            capturedPath = path;
            return Task.FromResult<int?>(3);
        });

        int? diskNumber = await service.GetDiskNumberForPathAsync(
            "X:\\Foundry",
            TestContext.Current.CancellationToken);

        Assert.Equal(3, diskNumber);
        Assert.Equal("X:\\Foundry", capturedPath);
    }

    [Fact]
    public async Task GetDiskNumberForPathAsync_WhenInspectionDataIsInvalid_ReturnsNull()
    {
        var service = CreateService(resolveDisk: (_, _) => Task.FromException<int?>(new InvalidDataException()));

        int? diskNumber = await service.GetDiskNumberForPathAsync(
            "X:\\Foundry",
            TestContext.Current.CancellationToken);

        Assert.Null(diskNumber);
    }

    private static TargetDiskService CreateService(
        Func<CancellationToken, Task<IReadOnlyList<DiskInfo>>>? getDisks = null,
        Func<string, CancellationToken, Task<int?>>? resolveDisk = null)
    {
        return new TargetDiskService(
            new StubDiskInspector(
                getDisks ?? (_ => Task.FromResult<IReadOnlyList<DiskInfo>>([])),
                resolveDisk ?? ((_, _) => Task.FromResult<int?>(null))),
            NullLogger<TargetDiskService>.Instance);
    }

    private sealed class StubDiskInspector(
        Func<CancellationToken, Task<IReadOnlyList<DiskInfo>>> getDisks,
        Func<string, CancellationToken, Task<int?>> resolveDisk) : IWindowsDiskInspector
    {
        public Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default)
            => getDisks(cancellationToken);

        public Task<int?> ResolveDiskNumberForPathAsync(
            string path,
            CancellationToken cancellationToken = default)
            => resolveDisk(path, cancellationToken);
    }
}
