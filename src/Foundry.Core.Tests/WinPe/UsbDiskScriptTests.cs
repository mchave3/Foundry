// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Foundry.Core.Services.WinPe;
using Foundry.Core.Tests.TestUtilities;

namespace Foundry.Core.Tests.WinPe;

public sealed class UsbDiskScriptTests
{
    [Theory]
    [InlineData("USB-123", true)]
    [InlineData(" USB-123 ", true)]
    [InlineData("USB-123-extra", false)]
    [InlineData("usb-123", false)]
    [InlineData("", false)]
    public void PureGuard_UsesExactStableIdentity(string actualId, bool accepted)
    {
        var expected = new WinPeUsbDiskIdentity
        {
            Number = 9,
            UniqueId = "USB-123",
            SerialNumber = "SERIAL",
            BusType = "USB",
            IsRemovable = true,
            Size = 64000000000
        };
        Assert.Equal(accepted, RunGuard(expected, [expected with { UniqueId = actualId }]));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("capacity")]
    [InlineData("bus")]
    [InlineData("boot")]
    [InlineData("system")]
    [InlineData("offline")]
    [InlineData("readonly")]
    [InlineData("fixed")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-serial")]
    [InlineData("missing")]
    public void PureGuard_RejectsUnsafeOrAmbiguousEnumeration(string change)
    {
        var expected = new WinPeUsbDiskIdentity
        {
            Number = 9,
            UniqueId = "USB-123",
            SerialNumber = "SERIAL",
            BusType = "USB",
            IsRemovable = true,
            Size = 64000000000
        };
        WinPeUsbDiskIdentity actual = change switch
        {
            "number" => expected with { Number = 10 },
            "capacity" => expected with { Size = expected.Size + 1 },
            "bus" => expected with { BusType = "NVMe" },
            "boot" => expected with { IsBoot = true },
            "system" => expected with { IsSystem = true },
            "offline" => expected with { IsOffline = true },
            "readonly" => expected with { IsReadOnly = true },
            "fixed" => expected with { IsRemovable = false },
            "missing" => expected with { UniqueId = "", SerialNumber = "" },
            _ => expected
        };
        if (change == "duplicate-serial") { expected = expected with { UniqueId = "" }; actual = expected; }
        WinPeUsbDiskIdentity[] disks = change.StartsWith("duplicate-", StringComparison.Ordinal)
            ? [actual, actual with { Number = 10 }] : [actual];
        Assert.False(RunGuard(expected, disks));
    }

    [Fact]
    public void GeneratedScripts_ParseInWindowsPowerShell51WithoutExecutingNativeCommands()
    {
        var expected = new WinPeUsbDiskIdentity { Number = 9, UniqueId = "USB-ID", SerialNumber = "SERIAL", BusType = "USB", Size = 64000000000 };
        string[] scripts =
        [
            WinPeEmbeddedAssetService.ReadEmbeddedText("Foundry.Core.WinPe.UsbDiskOperations"),
            WinPeUsbMediaService.BuildPowerShellProvisioningScript(expected, UsbPartitionStyle.Gpt, UsbFormatMode.Quick),
            WinPeUsbMediaService.BuildPowerShellProvisioningScript(expected, UsbPartitionStyle.Mbr, UsbFormatMode.Complete),
            WinPeUsbMediaService.BuildPowerShellBootPartitionUpdateScript(expected, new WinPeUsbProvisionResult(), UsbFormatMode.Quick)
        ];
        foreach (string source in scripts)
        {
            string parserScript = $$"""
                $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(source))}}'))
                $tokens = $null; $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw ($errors -join '; ') }
                [Console]::WriteLine('parsed')
                """;
            Assert.Equal("parsed", RunIsolatedPowerShell(parserScript).Trim());
        }
    }

    private static bool RunGuard(WinPeUsbDiskIdentity expected, WinPeUsbDiskIdentity[] disks)
    {
        string helper = WinPeEmbeddedAssetService.ReadEmbeddedText("Foundry.Core.WinPe.UsbDiskOperations");
        string payload = JsonSerializer.Serialize(new { Expected = expected, Disks = disks });
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Microsoft.PowerShell.Utility
            $PSModuleAutoLoadingPreference = 'None'
            $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(helper))}}'))
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw 'Helper parse failed.' }
            $names = @('ConvertTo-FoundryUsbIdentityText', 'Test-FoundryUsbDiskSelectable', 'Assert-FoundryUsbDiskIdentity')
            foreach ($name in $names) {
                $definitions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false))
                if ($definitions.Count -ne 1) { throw 'Expected exactly one pure guard definition.' }
                $commands = $definitions[0].FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
                foreach ($command in $commands) {
                    if ($command.GetCommandName() -notin $names) { throw 'External command is forbidden in the pure guard harness.' }
                }
                . ([scriptblock]::Create($definitions[0].Extent.Text))
            }
            $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}}')) | Microsoft.PowerShell.Utility\ConvertFrom-Json
            try {
                $disk = Assert-FoundryUsbDiskIdentity -Expected $payload.Expected -Disks $payload.Disks
                if ($disk.Number -ne 9) { throw 'Unexpected disk returned.' }
                [Console]::WriteLine('accepted')
            } catch {
                [Console]::WriteLine('rejected')
            }
            """;
        return RunIsolatedPowerShell(script).Trim() == "accepted";
    }

    private static string RunIsolatedPowerShell(string script)
    {
        using var workspace = new TemporaryDirectory();
        string scriptPath = Path.Combine(workspace.Path, "isolated-usb-guard.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Pure guard harness timed out.");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error.Result), error.Result);
        return output.Result;
    }
}
