function ConvertTo-FoundryUsbIdentityText($Value) {
    return ([string]$Value).Trim()
}

function Test-FoundryUsbDiskSelectable($Disk) {
    return $null -ne $Disk -and
        ([string]$Disk.BusType).Trim().ToUpperInvariant() -ceq 'USB' -and
        $Disk.IsRemovable -ne $false -and -not $Disk.IsSystem -and -not $Disk.IsBoot -and
        -not $Disk.IsOffline -and -not $Disk.IsReadOnly -and [uint64]$Disk.Size -ge 17179869184
}

# This guard accepts only supplied snapshots; it performs no device or filesystem operations.
function Assert-FoundryUsbDiskIdentity($Expected, $Disks) {
    if (-not (Test-FoundryUsbDiskSelectable $Expected)) { throw 'Confirmed USB disk is not selectable.' }
    $matches = @()
    foreach ($disk in $Disks) {
        if ($disk.Number -eq $Expected.Number) { $matches += $disk }
    }
    if ($matches.Count -ne 1) { throw 'Confirmed USB disk is missing or ambiguous.' }
    $disk = $matches[0]
    if (-not (Test-FoundryUsbDiskSelectable $disk)) { throw 'USB target is no longer selectable.' }
    if ([uint64]$disk.Size -ne [uint64]$Expected.Size -or
        ([string]$disk.BusType).Trim().ToUpperInvariant() -cne ([string]$Expected.BusType).Trim().ToUpperInvariant()) {
        throw 'USB target capacity or bus changed.'
    }
    $expectedId = ConvertTo-FoundryUsbIdentityText $Expected.UniqueId
    $actualId = ConvertTo-FoundryUsbIdentityText $disk.UniqueId
    $serial = ConvertTo-FoundryUsbIdentityText $Expected.SerialNumber
    if (-not [string]::Equals($expectedId, $actualId, [StringComparison]::Ordinal) -or
        -not [string]::Equals($serial, (ConvertTo-FoundryUsbIdentityText $disk.SerialNumber), [StringComparison]::Ordinal)) {
        throw 'USB target identity changed.'
    }
    if ($expectedId.Length -eq 0 -and $serial.Length -eq 0) { throw 'USB target has no stable identity.' }
    $identityMatches = 0
    foreach ($candidate in $Disks) {
        if ($expectedId.Length -gt 0) {
            if ([string]::Equals((ConvertTo-FoundryUsbIdentityText $candidate.UniqueId), $expectedId, [StringComparison]::Ordinal)) { $identityMatches++ }
        } elseif ([string]::Equals((ConvertTo-FoundryUsbIdentityText $candidate.SerialNumber), $serial, [StringComparison]::Ordinal)) { $identityMatches++ }
    }
    if ($identityMatches -ne 1) { throw 'USB target identity is ambiguous.' }
    return $disk
}

function Get-FoundryUsbDriveLetter($DriveLetter) {
    $text = ([string]$DriveLetter).Trim().TrimEnd(':')
    if ($text -match '^[A-Za-z]$') { return $text.ToUpperInvariant() }
    return $null
}

function Get-FoundryUsbDriveLetterText($DriveLetter) {
    $letter = Get-FoundryUsbDriveLetter $DriveLetter
    if ($null -eq $letter) { return '' }
    return "$($letter):"
}

function Get-FoundryUsbPartitionVolume($Partition) {
    $volumes = @(Get-Volume -Partition $Partition -ErrorAction Stop)
    if ($volumes.Count -ne 1) { throw 'USB partition volume is missing or ambiguous.' }
    return $volumes[0]
}

function Assert-FoundryUsbPartition($Expected, $Partition) {
    if ($null -eq $Expected -or $null -eq $Partition -or
        $Partition.DiskNumber -ne $Expected.DiskNumber -or
        $Partition.PartitionNumber -ne $Expected.PartitionNumber -or
        [uint64]$Partition.Offset -ne [uint64]$Expected.Offset -or
        [uint64]$Partition.Size -ne [uint64]$Expected.Size -or
        [string]$Partition.Guid -cne [string]$Expected.Guid) {
        throw 'USB partition identity changed.'
    }
}

function Get-FoundryUsbBoundPartition($Expected, $Partition) {
    $disk = Assert-FoundryUsbDiskIdentity -Expected $Expected -Disks @(Get-Disk -ErrorAction Stop)
    $matches = @(Get-Partition -Disk $disk -ErrorAction Stop | Where-Object { $_.PartitionNumber -eq $Partition.PartitionNumber })
    if ($matches.Count -ne 1) { throw 'USB partition is missing or ambiguous.' }
    Assert-FoundryUsbPartition -Expected $Partition -Partition $matches[0]
    return $matches[0]
}

function Get-FoundryUsbLayout($Expected, $Layout = $null) {
    $disk = Assert-FoundryUsbDiskIdentity -Expected $Expected -Disks @(Get-Disk -ErrorAction Stop)
    $partitions = @(Get-Partition -Disk $disk -ErrorAction Stop)
    $boot = @(); $cache = @()
    foreach ($partition in $partitions) {
        if ([string]$partition.GptType -eq '{e3c9e316-0b5c-4db8-817d-f92df00215ae}') { continue }
        $volume = Get-FoundryUsbPartitionVolume $partition
        $entry = @{ Partition = $partition; Volume = $volume }
        if ($volume.FileSystemLabel -eq 'BOOT' -and $volume.FileSystem -eq 'FAT32' -and
            ([string]$partition.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -or
             ([string]$partition.MbrType -eq 'FAT32' -and $partition.IsActive))) { $boot += $entry }
        if ($volume.FileSystemLabel -eq 'Foundry Cache' -and $volume.FileSystem -eq 'NTFS') { $cache += $entry }
    }
    if ($boot.Count -ne 1 -or $cache.Count -ne 1) { throw 'Expected one Foundry BOOT and CACHE volume.' }
    $result = [ordered]@{ ConfirmedDisk = $Expected }
    foreach ($role in @('Boot', 'Cache')) {
        $entry = if ($role -eq 'Boot') { $boot[0] } else { $cache[0] }
        $partition = $entry.Partition; $volume = $entry.Volume
        if ($disk.PartitionStyle -eq 'GPT' -and [string]::IsNullOrWhiteSpace($partition.Guid)) { throw 'GPT partition has no stable GUID.' }
        $path = [string]$volume.Path
        if ($path -notmatch '^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$' -or [string]::IsNullOrWhiteSpace($volume.UniqueId)) {
            throw 'USB volume has no stable GUID root.'
        }
        $result[$role + 'PartitionNumber'] = [int]$partition.PartitionNumber
        $result[$role + 'PartitionOffset'] = [uint64]$partition.Offset
        $result[$role + 'PartitionSize'] = [uint64]$partition.Size
        $result[$role + 'PartitionGuid'] = [string]$partition.Guid
        $result[$role + 'VolumeUniqueId'] = [string]$volume.UniqueId
        $result[$role + 'VolumePath'] = $path
        $result[$role + 'DriveLetter'] = Get-FoundryUsbDriveLetterText $volume.DriveLetter
        if ($null -ne $Layout) {
            foreach ($suffix in @('PartitionNumber', 'PartitionOffset', 'PartitionSize', 'PartitionGuid', 'VolumeUniqueId', 'VolumePath')) {
                $property = $role + $suffix
                if ([string]$result[$property] -cne [string]$Layout.$property) { throw 'USB layout changed.' }
            }
        }
    }
    return [pscustomobject]$result
}

function Get-FoundryUsbLayoutPartition($Expected, $Layout, [string]$Role) {
    $snapshot = [pscustomobject]@{
        DiskNumber = $Expected.Number
        PartitionNumber = $Layout.($Role + 'PartitionNumber')
        Offset = $Layout.($Role + 'PartitionOffset')
        Size = $Layout.($Role + 'PartitionSize')
        Guid = $Layout.($Role + 'PartitionGuid')
    }
    return Get-FoundryUsbBoundPartition -Expected $Expected -Partition $snapshot
}
