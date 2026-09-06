// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Buffers.Binary;
using System.Text.Json;
using Foundry.Deploy.Models;
using Foundry.Deploy.Services.Deployment;
using Foundry.Utilities.Hardware;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class TargetDiskSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_SerialFallbackRequiresUniqueExactEvidence(bool duplicate)
    {
        var expected = new TargetDiskIdentity(9, "", "serial", 1024, "NVMe");
        var disk = new TargetDiskInfo { DiskNumber = 9, SerialNumber = " serial ", SizeBytes = 1024, BusType = "NVMe", IsSelectable = true };
        Assert.Equal(!duplicate, expected.Match(duplicate ? [disk, disk with { DiskNumber = 8 }] : [disk]) is not null);
        Assert.Null(expected.Match([disk with { SerialNumber = "SERIAL" }]));
    }

    [Theory]
    [InlineData(WindowsFirmwareType.Bios)]
    [InlineData(WindowsFirmwareType.Unknown)]
    public async Task PrepareTargetDisk_RejectsCurrentUnsupportedFirmwareBeforeAnyProcess(WindowsFirmwareType firmware)
    {
        var service = new WindowsDeploymentService(new RejectingRunner(), NullLogger<WindowsDeploymentService>.Instance,
            () => firmware, _ => throw new Xunit.Sdk.XunitException("Native writer must not run."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareTargetDiskAsync(
            new TargetDiskIdentity(9, "A", "serial", 1024, "NVMe"), Path.GetTempPath(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("same", 1, 3)]
    [InlineData("replacement", 0, 0)]
    [InlineData("duplicate", 0, 0)]
    [InlineData("serial", 1, 3)]
    [InlineData("serial-duplicate", 0, 0)]
    [InlineData("missing", 0, 0)]
    [InlineData("capacity", 0, 0)]
    [InlineData("bus", 0, 0)]
    [InlineData("number", 0, 0)]
    [InlineData("boot", 0, 0)]
    [InlineData("system", 0, 0)]
    [InlineData("readonly", 0, 0)]
    [InlineData("offline", 0, 0)]
    [InlineData("after-clear", 1, 0)]
    public async Task PreparationScript_UsesOnlyConfirmedObjects(string change, int clears, int formats)
    {
        var expected = new TargetDiskIdentity(9, change.StartsWith("serial", StringComparison.Ordinal) ? "" : "A", "serial", 137438953472, "NVMe");
        string script = MockStorage + $"\n$change = '{change}'\ntry {{\n" + TargetDiskPreparationScript.Create(expected, 'S', 'W', 'R') + "\n} catch { $failure = $_.Exception.Message }\n" +
            "[pscustomobject]@{ Clears=$clears; Formats=$formats; Created=@($created); Failure=$failure } | ConvertTo-Json -Compress -Depth 5";
        ProcessExecutionResult result = await new ProcessRunner().RunAsync(new ProcessExecutionRequest(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", .. PowerShellCommand.CreateEncodedArguments(script)], Path.GetTempPath()), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.ToDiagnosticText());
        string lastLine = result.StandardOutput.Trim().Split('\n')[^1];
        using JsonDocument output = JsonDocument.Parse(lastLine);
        Assert.Equal(clears, output.RootElement.GetProperty("Clears").GetInt32());
        Assert.Equal(formats, output.RootElement.GetProperty("Formats").GetInt32());
        if (formats == 3)
        {
            Assert.Equal("", output.RootElement.GetProperty("Failure").GetString());
            JsonElement[] created = output.RootElement.GetProperty("Created").EnumerateArray().ToArray();
            Assert.Equal([272629760UL, 16777216UL, 5368709120UL, 100000000000UL], created.Select(p => p.GetProperty("Size").GetUInt64()));
        }
    }

    [Fact]
    public void RecoveryAttributes_PreservesNativeMetadataAndWritesExactBits()
    {
        DeploymentPartitionIdentity expected = RecoveryIdentity;
        byte[] before = RecoveryBytes();
        byte[] after = before.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(after.AsSpan(64), 0x8000000000000001UL);
        int reads = 0;
        byte[]? written = null;
        RecoveryPartitionAttributes.Apply(expected, () => reads++ == 0 ? before : after, bytes => written = bytes);
        Assert.Equal(2, reads);
        Assert.NotNull(written);
        Assert.Equal(120, written.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(written));
        Assert.Equal(before[32..64], written[8..40]);
        Assert.Equal(0x8000000000000001UL, BinaryPrimitives.ReadUInt64LittleEndian(written.AsSpan(40)));
        Assert.Equal(before[72..144], written[48..120]);
    }

    [Theory]
    [InlineData("same", 1)]
    [InlineData("wrong-volume", 0)]
    [InlineData("wrong-partition", 0)]
    [InlineData("wrong-letter", 0)]
    public async Task SealScript_RemovesOnlyRetainedPartitionAccessPath(string change, int expectedRemovals)
    {
        string setup = """
            $removals = 0
            $created = @([pscustomobject]@{DiskNumber=9;Guid='11111111-2222-3333-4444-555555555555';Offset=[uint64]1048576;Size=[uint64]5368709120;DriveLetter='R';AccessPaths=@('R:\')})
            if ($change -eq 'wrong-partition') {$created[0].Guid='22222222-2222-3333-4444-555555555555'}
            function Get-Partition {param($Disk,$DriveLetter,$ErrorAction)
                if ($DriveLetter -and $change -eq 'wrong-letter') {[pscustomobject]@{DiskNumber=9;Guid='22222222-2222-3333-4444-555555555555'}} else {$created} }
            function Get-Volume {param($Partition,$ErrorAction)
                $id=$Partition.Guid
                if ($change -eq 'wrong-volume') {$id='22222222-2222-3333-4444-555555555555'}
                [pscustomobject]@{Path=('\\?\Volume{'+$id+'}\')} }
            function Remove-PartitionAccessPath {param($InputObject,$AccessPath,$ErrorAction)
                if ($InputObject.Guid -cne '11111111-2222-3333-4444-555555555555' -or $AccessPath -cne 'R:\') {throw 'wrong_removal_target'}
                $script:removals++; $InputObject.AccessPaths=@() }
            """;
        string script = MockStorage + $"\n$change='{change}'\n" + setup + "\ntry {\n" +
            TargetDiskPreparationScript.Validate(new TargetDiskIdentity(9, "A", "serial", 137438953472, "NVMe"), RecoveryIdentity, removeLetter: true) +
            "\n} catch {$failure=$_.Exception.Message}\n[pscustomobject]@{Removals=$removals;Failure=$failure}|ConvertTo-Json -Compress";
        ProcessExecutionResult result = await new ProcessRunner().RunAsync(new ProcessExecutionRequest(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", .. PowerShellCommand.CreateEncodedArguments(script)], Path.GetTempPath()), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.ToDiagnosticText());
        using JsonDocument output = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(expectedRemovals, output.RootElement.GetProperty("Removals").GetInt32());
        Assert.Equal(expectedRemovals == 1, output.RootElement.GetProperty("Failure").GetString() == "");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("style")]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("offset")]
    [InlineData("size")]
    public void RecoveryAttributes_RejectsMismatchedHandleBeforeWrite(string change)
    {
        byte[] bytes = RecoveryBytes();
        if (change == "short") bytes = bytes[..143];
        else bytes[change switch { "style" => 0, "id" => 48, "type" => 32, "offset" => 8, _ => 16 }] ^= 1;
        Assert.Throws<InvalidOperationException>(() => RecoveryPartitionAttributes.Apply(RecoveryIdentity, () => bytes,
            _ => throw new Xunit.Sdk.XunitException("Unexpected native write.")));
    }

    [Fact]
    public void RecoveryAttributes_RejectsFailedReadback()
    {
        Assert.Throws<InvalidOperationException>(() => RecoveryPartitionAttributes.Apply(RecoveryIdentity, RecoveryBytes, _ => { }));
    }

    private static DeploymentPartitionIdentity RecoveryIdentity => new(new Guid("11111111-2222-3333-4444-555555555555"), 1048576, 5368709120,
        @"\\?\Volume{11111111-2222-3333-4444-555555555555}\", 'R');

    private static byte[] RecoveryBytes()
    {
        byte[] bytes = new byte[144];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 1048576);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 5368709120);
        new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac").TryWriteBytes(bytes.AsSpan(32));
        RecoveryIdentity.PartitionId.TryWriteBytes(bytes.AsSpan(48));
        for (int index = 72; index < 144; index++) bytes[index] = (byte)index;
        return bytes;
    }

    private const string MockStorage = """
        $clears = 0; $formats = 0; $created = @(); $failure = ''; $queries = 0
        function Get-Disk {
            $script:queries++
            $disk = [pscustomobject]@{Number=9;UniqueId='A';SerialNumber='serial';Size=[uint64]137438953472;BusType='NVMe';IsBoot=$false;IsSystem=$false;IsReadOnly=$false;IsOffline=$false}
            switch ($change) {
                'replacement' {$disk.UniqueId='B'}
                'missing' {$disk.UniqueId='';$disk.SerialNumber=''}
                'capacity' {$disk.Size=100}
                'bus' {$disk.BusType='SATA'}
                'number' {$disk.Number=8}
                'boot' {$disk.IsBoot=$true}
                'system' {$disk.IsSystem=$true}
                'readonly' {$disk.IsReadOnly=$true}
                'offline' {$disk.IsOffline=$true}
                'after-clear' {if ($clears -gt 0) {$disk.UniqueId='B'}}
            }
            $disk
            if ($change -in @('duplicate','serial-duplicate')) {$disk}
        }
        function Clear-Disk { param($InputObject,[switch]$RemoveData,[switch]$RemoveOEM,$Confirm,$ErrorAction)
            if ($InputObject.UniqueId -cne 'A') {throw 'wrong_clear_object'}; $script:clears++ }
        function Initialize-Disk { param($InputObject,$PartitionStyle,$ErrorAction)
            if ($InputObject.UniqueId -cne 'A' -or $PartitionStyle -cne 'GPT') {throw 'wrong_initialize_object'} }
        function New-Partition { param($InputObject,[uint64]$Size,[switch]$UseMaximumSize,$GptType,$DriveLetter,$ErrorAction)
            if ($InputObject.UniqueId -cne 'A') {throw 'wrong_partition_object'}
            if ($UseMaximumSize) {$Size=100000000000}
            $part=[pscustomobject]@{DiskNumber=9;Guid=[guid]::NewGuid().ToString();Offset=[uint64](1048576+$created.Count*6000000000);Size=$Size;DriveLetter=$DriveLetter;GptType=$GptType}
            $script:created += $part; $part }
        function Get-Partition { param($Disk,$DriveLetter,$ErrorAction) $created }
        function Get-Volume { param($Partition,$ErrorAction)
            [pscustomobject]@{Path=('\\?\Volume{'+$Partition.Guid+'}\');Partition=$Partition} }
        function Format-Volume { param($InputObject,$FileSystem,$NewFileSystemLabel,$Confirm,[switch]$Force,$ErrorAction)
            if ($InputObject.Partition.DiskNumber -ne 9) {throw 'wrong_format_object'}; $script:formats++ }
        function Remove-PartitionAccessPath {throw 'unexpected_mount_mutation'}
        """;

    private sealed class RejectingRunner : Foundry.Deploy.Services.System.IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) => throw new Xunit.Sdk.XunitException("Unexpected process.");
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) => throw new Xunit.Sdk.XunitException("Unexpected process.");
        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, Action<string>? onOutputData, Action<string>? onErrorData, CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null) => throw new Xunit.Sdk.XunitException("Unexpected process.");
    }
}
