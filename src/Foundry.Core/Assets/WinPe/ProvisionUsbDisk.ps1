$ErrorActionPreference = 'Stop'
Import-Module Storage
$expected = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{EXPECTED_DISK}}')) | ConvertFrom-Json
$partitionStyle = '{{PARTITION_STYLE}}'
$fullFormat = {{FULL_FORMAT}}

function Write-FoundryUsbProgress([int]$Percent, [string]$Status) {
    Write-Output ("FOUNDRY_USB_PROGRESS|{0}|{1}" -f $Percent, $Status)
}

function Wait-FoundryUsbVolume($Partition) {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $current = Get-FoundryUsbBoundPartition -Expected $expected -Partition $Partition
        $volumes = @(Get-Volume -Partition $current -ErrorAction Stop)
        if ($volumes.Count -eq 1) { return $volumes[0] }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw 'Timed out waiting for the confirmed USB partition volume.'
}

Write-FoundryUsbProgress 26 'Clearing USB partition table.'
$disk = Assert-FoundryUsbDiskIdentity -Expected $expected -Disks @(Get-Disk -ErrorAction Stop)
Clear-Disk -InputObject $disk -RemoveData -RemoveOEM -Confirm:$false -ErrorAction Stop

Write-FoundryUsbProgress 32 'Initializing USB partition table.'
$disk = Assert-FoundryUsbDiskIdentity -Expected $expected -Disks @(Get-Disk -ErrorAction Stop)
if ($disk.PartitionStyle -eq 'RAW') {
    Initialize-Disk -InputObject $disk -PartitionStyle $partitionStyle -ErrorAction Stop
} elseif ([string]$disk.PartitionStyle -ne $partitionStyle) {
    throw 'USB partition style remained contradictory after Clear-Disk; provisioning stopped.'
}

Write-FoundryUsbProgress 38 'Creating BOOT partition.'
$bootPartitionArguments = @{ Size = 2048MB; AssignDriveLetter = $true; ErrorAction = 'Stop' }
if ($partitionStyle -eq 'GPT') {
    $bootPartitionArguments['GptType'] = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
} else {
    $bootPartitionArguments['MbrType'] = 'FAT32'
    $bootPartitionArguments['IsActive'] = $true
}
$disk = Assert-FoundryUsbDiskIdentity -Expected $expected -Disks @(Get-Disk -ErrorAction Stop)
if ([string]$disk.PartitionStyle -ne $partitionStyle) { throw 'USB partition style changed.' }
$bootPartition = New-Partition -InputObject $disk @bootPartitionArguments
$bootVolume = Wait-FoundryUsbVolume -Partition $bootPartition

Write-FoundryUsbProgress 44 'Formatting BOOT partition.'
$bootPartition = Get-FoundryUsbBoundPartition -Expected $expected -Partition $bootPartition
$currentVolume = Get-FoundryUsbPartitionVolume $bootPartition
if ([string]::IsNullOrWhiteSpace($bootVolume.UniqueId) -or [string]$currentVolume.UniqueId -cne [string]$bootVolume.UniqueId) { throw 'BOOT volume changed before formatting.' }
Format-Volume -InputObject $currentVolume -FileSystem FAT32 -NewFileSystemLabel BOOT -Full:$fullFormat -Force -Confirm:$false -ErrorAction Stop | Out-Null
Write-Output 'FOUNDRY_USB_VERBOSE|BOOT partition formatted.'

Write-FoundryUsbProgress 49 'Creating cache partition.'
$cachePartitionArguments = @{ UseMaximumSize = $true; AssignDriveLetter = $true; ErrorAction = 'Stop' }
if ($partitionStyle -eq 'GPT') {
    $cachePartitionArguments['GptType'] = '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
} else {
    $cachePartitionArguments['MbrType'] = 'IFS'
}
$disk = Assert-FoundryUsbDiskIdentity -Expected $expected -Disks @(Get-Disk -ErrorAction Stop)
$cachePartition = New-Partition -InputObject $disk @cachePartitionArguments
$cacheVolume = Wait-FoundryUsbVolume -Partition $cachePartition

Write-FoundryUsbProgress 53 'Formatting cache partition.'
$cachePartition = Get-FoundryUsbBoundPartition -Expected $expected -Partition $cachePartition
$currentVolume = Get-FoundryUsbPartitionVolume $cachePartition
if ([string]::IsNullOrWhiteSpace($cacheVolume.UniqueId) -or [string]$currentVolume.UniqueId -cne [string]$cacheVolume.UniqueId) { throw 'CACHE volume changed before formatting.' }
Format-Volume -InputObject $currentVolume -FileSystem NTFS -NewFileSystemLabel 'Foundry Cache' -Full:$fullFormat -Force -Confirm:$false -ErrorAction Stop | Out-Null
Write-Output 'FOUNDRY_USB_VERBOSE|CACHE partition formatted.'

$bootPartition = Get-FoundryUsbBoundPartition -Expected $expected -Partition $bootPartition
$cachePartition = Get-FoundryUsbBoundPartition -Expected $expected -Partition $cachePartition
Write-FoundryUsbProgress 55 'USB partitions formatted.'
$layout = Get-FoundryUsbLayout -Expected $expected
foreach ($role in @('Boot', 'Cache')) {
    $partition = if ($role -eq 'Boot') { $bootPartition } else { $cachePartition }
    if ($layout.($role + 'PartitionNumber') -ne $partition.PartitionNumber -or
        $layout.($role + 'PartitionOffset') -ne $partition.Offset -or
        $layout.($role + 'PartitionSize') -ne $partition.Size -or
        $layout.($role + 'PartitionGuid') -cne [string]$partition.Guid) { throw 'Fresh USB partition identity changed.' }
}
$layout | ConvertTo-Json -Depth 5 -Compress
