param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('LenovoExecutable', 'SurfaceMsi')]
    [string]$CommandKind,

    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$SuccessExitCodes = @(0, 3010)
$ResolvedPackagePath = [Environment]::ExpandEnvironmentVariables($PackagePath)
$LogDirectory = Join-Path $env:SystemRoot 'Temp\Foundry\Logs\PreOobe'
$TranscriptPath = Join-Path $LogDirectory 'Install-DriverPack.transcript.log'
$TranscriptStarted = $false
$ScriptStartedAt = [DateTimeOffset]::Now
$DriverPathRegistryKey = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\UnattendSettings\PnPUnattend\DriverPaths\1'

function Start-FoundryTranscript {
    New-Item -Path $LogDirectory -ItemType Directory -Force | Out-Null
    Start-Transcript -Path $TranscriptPath -Force | Out-Null
    $script:TranscriptStarted = $true
}

function Stop-FoundryTranscript {
    if ($script:TranscriptStarted) {
        Stop-Transcript | Out-Null
    }
}

function Write-FoundryLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $now = [DateTimeOffset]::Now
    $elapsed = $now - $script:ScriptStartedAt
    Write-Host ("[{0}] [+{1:c}] {2}" -f $now.ToString('yyyy-MM-ddTHH:mm:ss'), $elapsed, $Message)
}

function ConvertTo-ProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-ProcessAndWait {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory = $true)]
        [string]$OperationName
    )

    Write-FoundryLog "Starting ${OperationName}: $FilePath $($ArgumentList -join ' ')"
    $operationStartedAt = [DateTimeOffset]::Now
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WindowStyle Hidden -Wait -PassThru
    $operationDuration = [DateTimeOffset]::Now - $operationStartedAt
    Write-FoundryLog "$OperationName exited with code $($process.ExitCode) after $($operationDuration.ToString('c'))."

    if ($SuccessExitCodes -notcontains $process.ExitCode) {
        throw "$OperationName failed with exit code $($process.ExitCode)."
    }
}

$DriverPathRegistered = $false
$PackageLock = $null
$InstallationCompleted = $false

function Assert-DriverPackageTrust {
    param([string]$Path, [string]$Kind)

    # Exact subjects qualified from official driver-package signature tables; never accept a substring match.
    $expectedSubject = switch ($Kind) {
        'LenovoExecutable' { 'CN=Lenovo, OU=G10, O=Lenovo, L=Morrisville, S=North Carolina, C=US' }
        'SurfaceMsi' { 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' }
        default { throw 'Trusted publisher policy is unavailable for this driver package family.' }
    }

    $job = Start-Job -ScriptBlock {
        param($FilePath)
        $ErrorActionPreference = 'Stop'
        $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $FilePath
        [pscustomobject]@{ Status = $signature.Status.ToString(); Subject = $signature.SignerCertificate.Subject }
    } -ArgumentList $Path

    try {
        if (-not (Wait-Job -Job $job -Timeout 120)) {
            throw 'Driver package signature verification exceeded its two-minute deadline.'
        }
        if ($job.State -ne 'Completed') {
            throw 'Windows could not complete the driver package signature check.'
        }
        $result = @(Receive-Job -Job $job -ErrorAction Stop)
        if ($result.Count -ne 1 -or $result[0].Status -cne 'Valid' -or $result[0].Subject -cne $expectedSubject) {
            throw 'The driver package does not have a valid signature from the expected publisher.'
        }
    }
    finally {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
}

try {
    Start-FoundryTranscript
    Write-FoundryLog "Foundry driver pack installation started."
    Write-FoundryLog "CommandKind=$CommandKind"
    Write-FoundryLog "PackagePath=$ResolvedPackagePath"

    if (-not (Test-Path -LiteralPath $ResolvedPackagePath -PathType Leaf)) {
        throw "Driver package was not found: $ResolvedPackagePath"
    }

    $ResolvedPackagePath = [IO.Path]::GetFullPath($ResolvedPackagePath)
    $PackageLock = [IO.File]::Open($ResolvedPackagePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    Assert-DriverPackageTrust -Path $ResolvedPackagePath -Kind $CommandKind

    switch ($CommandKind) {
        'LenovoExecutable' {
            Invoke-ProcessAndWait `
                -FilePath $ResolvedPackagePath `
                -ArgumentList @('/SILENT', '/SUPPRESSMSGBOXES') `
                -OperationName 'Lenovo driver package'

            Invoke-ProcessAndWait `
                -FilePath 'reg.exe' `
                -ArgumentList @('add', (ConvertTo-ProcessArgument -Value $DriverPathRegistryKey), '/v', 'Path', '/t', 'REG_SZ', '/d', 'C:\Drivers', '/f') `
                -OperationName 'Register PnPUnattend driver path'
            $DriverPathRegistered = $true
            Invoke-ProcessAndWait `
                -FilePath 'pnpunattend.exe' `
                -ArgumentList @('AuditSystem', '/L') `
                -OperationName 'pnpunattend.exe'
        }
        'SurfaceMsi' {
            $logDirectory = Join-Path $env:SystemRoot 'Temp\Foundry\DriverPack'
            New-Item -Path $logDirectory -ItemType Directory -Force | Out-Null

            $logPath = Join-Path $logDirectory 'surface-driverpack.log'
            Invoke-ProcessAndWait `
                -FilePath 'msiexec.exe' `
                -ArgumentList @('/i', (ConvertTo-ProcessArgument -Value $ResolvedPackagePath), '/qn', '/norestart', '/l*v', (ConvertTo-ProcessArgument -Value $logPath)) `
                -OperationName 'Surface driver package'
        }
    }

    Write-FoundryLog "Foundry driver pack installation completed."
    $InstallationCompleted = $true
}
finally {
    if ($DriverPathRegistered) {
        try {
            Invoke-ProcessAndWait `
                -FilePath 'reg.exe' `
                -ArgumentList @('delete', (ConvertTo-ProcessArgument -Value $DriverPathRegistryKey), '/v', 'Path', '/f') `
                -OperationName 'Remove PnPUnattend driver path'
        }
        catch {
            Write-FoundryLog "WARNING: $($_.Exception.Message)"
        }
    }

    if ($null -ne $PackageLock) {
        $PackageLock.Dispose()
    }

    if ($InstallationCompleted -and (Test-Path -LiteralPath $ResolvedPackagePath -PathType Leaf)) {
        Write-FoundryLog "Removing staged package: $ResolvedPackagePath"
        Remove-Item -LiteralPath $ResolvedPackagePath -Force
    }

    Stop-FoundryTranscript
}
