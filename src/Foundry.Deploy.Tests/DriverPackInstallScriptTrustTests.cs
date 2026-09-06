// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text;
using Foundry.Core.Services.Security;
using Foundry.Deploy.Services.Deployment.PreOobe;
using Foundry.Utilities.Processes;

namespace Foundry.Deploy.Tests;

public sealed class DriverPackInstallScriptTrustTests
{
    [Theory]
    [InlineData("LenovoExecutable", "LenovoDriverPack", "NotSigned", false, false)]
    [InlineData("LenovoExecutable", "LenovoDriverPack", "Valid", false, false)]
    [InlineData("LenovoExecutable", "LenovoDriverPack", "Valid", true, true)]
    [InlineData("SurfaceMsi", "SurfaceDriverPack", "Valid", false, false)]
    [InlineData("SurfaceMsi", "SurfaceDriverPack", "Valid", true, true)]
    public async Task Script_RequiresExactTrustedPublisher_AndLocksThroughExecution(string command, string family, string status, bool expectedPublisher, bool allowed)
    {
        string root = Path.Combine(Path.GetTempPath(), $"FoundryScriptTrust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "Install-DriverPack.ps1");
        string packagePath = Path.Combine(root, "fixture.exe");
        await using (Stream script = typeof(PreOobeScriptResources).Assembly.GetManifestResourceStream(PreOobeScriptResources.InstallDriverPack)!)
        await using (FileStream destination = File.Create(scriptPath))
        {
            await script.CopyToAsync(destination, TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(packagePath, "fixture", TestContext.Current.CancellationToken);
        const string harness = """
            $ErrorActionPreference = 'Stop'
            $launches = [Collections.Generic.List[string]]::new()
            $locks = [Collections.Generic.List[bool]]::new()
            function Test-PackageLock {
                try { $writer = [IO.File]::Open($env:FOUNDRY_TEST_PACKAGE, 'Open', 'Write', 'None'); $writer.Dispose(); $locks.Add($false) }
                catch [IO.IOException] { $locks.Add($true) }
            }
            function Start-Process {
                param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, $WindowStyle)
                if (-not $Wait -or -not $PassThru -or $WindowStyle -ne 'Hidden') { throw 'Native calls must wait and use a hidden window.' }
                Test-PackageLock; $launches.Add($FilePath); [pscustomobject]@{ ExitCode = 0 }
            }
            function New-Item { param($Path, $ItemType, [switch]$Force) }
            function Start-Transcript { param($Path, [switch]$Force) }
            function Stop-Transcript { }
            function Start-Job { param($ScriptBlock, $ArgumentList); Test-PackageLock; [pscustomobject]@{ State = 'Completed' } }
            function Wait-Job { param($Job, $Timeout); $Job }
            function Receive-Job { param($Job); [pscustomobject]@{ Status = $env:FOUNDRY_TEST_STATUS; Subject = $env:FOUNDRY_TEST_SUBJECT } }
            function Stop-Job { param($Job) }
            function Remove-Job { param($Job, [switch]$Force) }
            try { & $env:FOUNDRY_TEST_SCRIPT -CommandKind $env:FOUNDRY_TEST_COMMAND -PackagePath $env:FOUNDRY_TEST_PACKAGE; $accepted = $true }
            catch { $accepted = $false }
            $expected = $env:FOUNDRY_TEST_ALLOWED -eq 'true'
            if ($accepted -ne $expected) { throw 'Unexpected trust acceptance.' }
            if (($launches.Count -gt 0) -ne $expected) { throw 'Untrusted package reached process execution.' }
            if ($locks.Count -eq 0 -or $locks.Contains($false)) { throw 'The package was writable during validation or execution.' }
            if (-not $expected -and -not [IO.File]::Exists($env:FOUNDRY_TEST_PACKAGE)) { throw 'Rejected package was deleted.' }
            """;
        try
        {
            var request = new ProcessExecutionRequest(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(harness))], root)
            {
                ExecutionTimeout = TimeSpan.FromSeconds(30),
                EnvironmentOverrides = new Dictionary<string, string?>
                {
                    ["FOUNDRY_TEST_SCRIPT"] = scriptPath,
                    ["FOUNDRY_TEST_PACKAGE"] = packagePath,
                    ["FOUNDRY_TEST_COMMAND"] = command,
                    ["FOUNDRY_TEST_STATUS"] = status,
                    ["FOUNDRY_TEST_SUBJECT"] = expectedPublisher ? VendorExecutableTrustPolicy.GetExpectedPublisherSubjects(family).Single() : "CN=Wrong Fixture Publisher",
                    ["FOUNDRY_TEST_ALLOWED"] = allowed ? "true" : "false"
                }
            };
            ProcessExecutionResult result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess, result.ToDiagnosticText());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
