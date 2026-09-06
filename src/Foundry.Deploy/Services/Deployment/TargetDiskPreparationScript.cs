// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>Builds the Deploy-owned Storage boundary using encoded expected data and validated CIM objects.</summary>
internal static class TargetDiskPreparationScript
{
    private const string Guards = """
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version 3
        function Get-ConfirmedDisk {
            $all = @(Get-Disk -ErrorAction Stop)
            $id = ([string]$expected.UniqueId).Trim()
            $serial = ([string]$expected.SerialNumber).Trim()
            if ($id.Length -gt 0) {
                $found = @($all | Where-Object { ([string]$_.UniqueId).Trim() -ceq $id })
            } elseif ($serial.Length -gt 0) {
                $found = @($all | Where-Object { ([string]$_.SerialNumber).Trim() -ceq $serial })
            } else { throw 'missing_confirmed_identity' }
            if ($found.Count -ne 1) { throw 'ambiguous_confirmed_identity' }
            $disk = $found[0]
            if ($disk.Number -ne $expected.DiskNumber -or [uint64]$disk.Size -ne [uint64]$expected.SizeBytes -or
                [uint64]$expected.SizeBytes -eq 0 -or ([string]$expected.BusType).Trim().Length -eq 0 -or
                ([string]$disk.BusType).Trim() -cne ([string]$expected.BusType).Trim() -or
                ([string]$disk.SerialNumber).Trim() -cne $serial -or
                $disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or $disk.IsOffline) {
                throw 'confirmed_disk_changed_or_unsafe'
            }
            return $disk
        }
        function Get-OwnedPartition($retained) {
            $disk = Get-ConfirmedDisk
            $id = [guid]$retained.PartitionId
            if ($id -eq [guid]::Empty) { throw 'missing_partition_identity' }
            $parts = @(Get-Partition -Disk $disk -ErrorAction Stop | Where-Object { [guid]$_.Guid -eq $id })
            if ($parts.Count -ne 1 -or $parts[0].DiskNumber -ne $disk.Number -or
                [uint64]$parts[0].Offset -ne [uint64]$retained.Offset -or
                [uint64]$parts[0].Size -ne [uint64]$retained.Size) { throw 'partition_association_changed' }
            return $parts[0]
        }
        function Get-OwnedVolume($part) {
            $volumes = @(Get-Volume -Partition $part -ErrorAction Stop)
            if ($volumes.Count -ne 1 -or [string]$volumes[0].Path -notmatch '^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$') {
                throw 'missing_volume_identity'
            }
            return $volumes[0]
        }
        function Get-RetainedPartition($part) {
            $retained = [pscustomobject]@{ PartitionId = [string]$part.Guid; Offset = [uint64]$part.Offset; Size = [uint64]$part.Size }
            $current = Get-OwnedPartition $retained
            $volume = Get-OwnedVolume $current
            return [pscustomobject]@{ PartitionId = [string]$current.Guid; Offset = [uint64]$current.Offset;
                Size = [uint64]$current.Size; VolumeRoot = [string]$volume.Path; DriveLetter = [string]$current.DriveLetter }
        }
        function Assert-OwnedVolume($retained) {
            $part = Get-OwnedPartition $retained
            $volume = Get-OwnedVolume $part
            if ([string]$volume.Path -cne [string]$retained.VolumeRoot) { throw 'volume_association_changed' }
            return $part
        }
        """;

    internal static string Create(TargetDiskIdentity expected, char systemLetter, char windowsLetter, char recoveryLetter) =>
        Data("expected", expected) + Guards + "\n" + $$"""
        $disk = Get-ConfirmedDisk
        Clear-Disk -InputObject $disk -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop
        $disk = Get-ConfirmedDisk
        Initialize-Disk -InputObject $disk -PartitionStyle GPT -ErrorAction Stop
        $disk = Get-ConfirmedDisk
        $system = New-Partition -InputObject $disk -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -DriveLetter '{{systemLetter}}' -ErrorAction Stop
        $disk = Get-ConfirmedDisk
        $null = New-Partition -InputObject $disk -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' -ErrorAction Stop
        $disk = Get-ConfirmedDisk
        $recovery = New-Partition -InputObject $disk -Size 5120MB -GptType '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}' -DriveLetter '{{recoveryLetter}}' -ErrorAction Stop
        $disk = Get-ConfirmedDisk
        $windows = New-Partition -InputObject $disk -UseMaximumSize -DriveLetter '{{windowsLetter}}' -ErrorAction Stop
        foreach ($item in @(@($system, 'FAT32', 'System'), @($recovery, 'NTFS', 'Recovery'), @($windows, 'NTFS', 'Windows'))) {
            $part = $item[0]
            $retained = [pscustomobject]@{ PartitionId = [string]$part.Guid; Offset = [uint64]$part.Offset; Size = [uint64]$part.Size }
            $part = Get-OwnedPartition $retained
            $volume = Get-OwnedVolume $part
            $null = Format-Volume -InputObject $volume -FileSystem $item[1] -NewFileSystemLabel $item[2] -Confirm:$false -Force -ErrorAction Stop
        }
        [pscustomobject]@{ System = (Get-RetainedPartition $system); Recovery = (Get-RetainedPartition $recovery); Windows = (Get-RetainedPartition $windows) } | ConvertTo-Json -Compress -Depth 5
        """;

    internal static string Validate(TargetDiskIdentity expected, DeploymentPartitionIdentity partition, bool verifyLetter = false, bool removeLetter = false) =>
        Data("expected", expected) + Data("retained", partition) + Guards + "\n" + """
        $part = Assert-OwnedVolume $retained
        """ + (verifyLetter || removeLetter ? "\n" + """
        $accessPath = ([string]$retained.DriveLetter) + ':\'
        if ($part.AccessPaths -cnotcontains $accessPath) { throw 'partition_access_path_changed' }
        $letterPartitions = @(Get-Partition -DriveLetter ([char]$retained.DriveLetter) -ErrorAction Stop)
        if ($letterPartitions.Count -ne 1 -or [guid]$letterPartitions[0].Guid -ne [guid]$retained.PartitionId -or
            $letterPartitions[0].DiskNumber -ne $expected.DiskNumber) { throw 'partition_access_path_changed' }
        """ : string.Empty) + (removeLetter ? "\n" + """
        Remove-PartitionAccessPath -InputObject $part -AccessPath $accessPath -ErrorAction Stop
        $part = Assert-OwnedVolume $retained
        if ($part.AccessPaths -ccontains $accessPath) { throw 'partition_access_path_removal_failed' }
        """ : string.Empty);

    private static string Data(string name, object value) =>
        $"${name} = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))}')) | ConvertFrom-Json\n";
}
