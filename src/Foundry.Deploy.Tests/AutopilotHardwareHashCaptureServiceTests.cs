// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Xml.Linq;
using Foundry.Deploy.Services.Autopilot;
using Foundry.Deploy.Services.System;
using Foundry.Utilities.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Deploy.Tests;

public sealed class AutopilotHardwareHashCaptureServiceTests
{
    [Fact]
    public async Task CaptureAsync_WhenPcpKspExists_CopiesPcpKspBeforeRunningOa3Tool()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace);
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: "Sales"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("SER123", result.Identity?.SerialNumber);
        Assert.Equal("HASHVALUE", result.Identity?.HardwareHash);
        Assert.True(processRunner.WasPcpKspPresentBeforeOa3Run);
        Assert.Equal(workspace.RuntimeHashRootPath, processRunner.WorkingDirectory);
        Assert.Equal(
            Path.Combine(workspace.WinPeWindowsRootPath, "System32", "PCPKsp.dll"),
            processRunner.CopiedPcpKspPath);
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.xml")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.log")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "AutopilotHWID.csv")));
    }

    [Fact]
    public async Task CaptureAsync_WritesFileBasedOa3ConfigurationWithExpectedPaths()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace);
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string configPath = Path.Combine(workspace.RuntimeHashRootPath, "OA3.cfg");
        XDocument config = XDocument.Load(configPath);
        Assert.Equal("OA3", config.Root?.Name.LocalName);
        Assert.Equal(
            Path.Combine(workspace.RuntimeHashRootPath, "input.xml"),
            config.Root?.Element("FileBased")?.Element("InputKeyXMLFile")?.Value);
        Assert.Equal(
            Path.Combine(workspace.RuntimeHashRootPath, "OA3.bin"),
            config.Root?.Element("OutputData")?.Element("AssembledBinaryFile")?.Value);
        Assert.Equal(
            Path.Combine(workspace.RuntimeHashRootPath, "OA3.xml"),
            config.Root?.Element("OutputData")?.Element("ReportedXMLFile")?.Value);

        XDocument input = XDocument.Load(Path.Combine(workspace.RuntimeHashRootPath, "input.xml"));
        Assert.Equal("XXXXX-XXXXX-XXXXX-XXXXX-XXXXX", input.Root?.Element("ProductKey")?.Value);
        Assert.Equal("0000000000000", input.Root?.Element("ProductKeyID")?.Value);
        Assert.Equal("0", input.Root?.Element("ProductKeyState")?.Value);
    }

    [Fact]
    public async Task CaptureAsync_WhenPcpKspIsMissing_ReturnsSupportLibraryMissingFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace);
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.SupportLibraryMissing, result.FailureCode);
        Assert.False(processRunner.WasCalled);
    }

    [Fact]
    public async Task CaptureAsync_WhenPcpKspCopyFails_ReturnsSupportLibraryCopyFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        workspace.CreateWinPePcpKspDirectory();
        var processRunner = new RecordingProcessRunner(workspace);
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.SupportLibraryCopyFailed, result.FailureCode);
        Assert.False(processRunner.WasCalled);
    }

    [Fact]
    public async Task CaptureAsync_WhenOa3ToolIsMissing_ReturnsToolMissingFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        var processRunner = new RecordingProcessRunner(workspace);
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.ToolMissing, result.FailureCode);
        Assert.False(processRunner.WasCalled);
    }

    [Fact]
    public async Task CaptureAsync_WhenOa3ToolExitsNonZero_ReturnsToolFailedFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace)
        {
            ExitCode = 5,
            StandardError = "oa3 failed"
        };
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.ToolFailed, result.FailureCode);
        Assert.Contains("oa3 failed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsync_WhenOa3ToolCannotLoadPcpKsp_ReturnsSupportLibraryLoadFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace)
        {
            ExitCode = 5,
            StandardError = "Failed to load PCPKsp.dll"
        };
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.SupportLibraryLoadFailed, result.FailureCode);
    }

    [Fact]
    public async Task CaptureAsync_WhenOa3ToolSucceedsWithoutReport_ReturnsReportMissingFailure()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace)
        {
            WriteOa3Xml = false
        };
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.ReportMissing, result.FailureCode);
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.cfg")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "input.xml")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.log")));
    }

    [Fact]
    public async Task CaptureAsync_WhenReportCannotBeParsed_RetainsOa3Diagnostics()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create();
        workspace.WriteTargetPcpKsp("pcp");
        workspace.WriteOa3Tool();
        var processRunner = new RecordingProcessRunner(workspace)
        {
            Oa3LogContent = "<HardwareVerificationData />"
        };
        var service = CreateService(processRunner);

        AutopilotHardwareHashCaptureResult result = await service.CaptureAsync(
            workspace.CreateRequest(groupTag: null),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AutopilotHardwareHashCaptureFailureCode.SerialMissing, result.FailureCode);
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.cfg")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "input.xml")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.xml")));
        Assert.True(File.Exists(Path.Combine(workspace.DiagnosticsRootPath, "OA3.log")));
    }

    private static AutopilotHardwareHashCaptureService CreateService(IProcessRunner processRunner)
    {
        return new AutopilotHardwareHashCaptureService(
            processRunner,
            NullLogger<AutopilotHardwareHashCaptureService>.Instance);
    }

    private sealed class RecordingProcessRunner(TemporaryWorkspace workspace) : IProcessRunner
    {
        public bool WasCalled { get; private set; }
        public bool WasPcpKspPresentBeforeOa3Run { get; private set; }
        public string? CopiedPcpKspPath { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public int ExitCode { get; init; }
        public string StandardError { get; init; } = string.Empty;
        public string Oa3XmlContent { get; init; } = """
            <Key>
              <ProductKeyState>6</ProductKeyState>
              <HardwareHash>HASHVALUE</HardwareHash>
            </Key>
            """;
        public string Oa3LogContent { get; init; } = """
            <HardwareVerificationData>
              <Hardware>
                <SMBIOS>
                  <System>
                    <p name="SerialNumber">SER123</p>
                  </System>
                </SMBIOS>
              </Hardware>
            </HardwareVerificationData>
            """;
        public bool WriteOa3Xml { get; init; } = true;

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
        {
            return RunAsync(fileName, arguments, workingDirectory, null, null, cancellationToken);
        }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            Action<string>? onOutputData,
            Action<string>? onErrorData,
            CancellationToken cancellationToken = default, TimeSpan? executionTimeout = null)
        {
            WasCalled = true;
            WorkingDirectory = workingDirectory;
            CopiedPcpKspPath = Path.Combine(workspace.WinPeWindowsRootPath, "System32", "PCPKsp.dll");
            WasPcpKspPresentBeforeOa3Run = File.Exists(CopiedPcpKspPath);

            if (ExitCode == 0)
            {
                if (WriteOa3Xml)
                {
                    File.WriteAllText(Path.Combine(workingDirectory, "OA3.xml"), Oa3XmlContent);
                }

                File.WriteAllText(Path.Combine(workingDirectory, "OA3.log"), Oa3LogContent);
            }

            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = ExitCode,
                FileName = fileName,
                Arguments = string.Join(' ', arguments),
                WorkingDirectory = workingDirectory,
                StandardError = StandardError
            });
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string rootPath)
        {
            RootPath = rootPath;
            TargetWindowsRootPath = Path.Combine(rootPath, "TargetWindows");
            WinPeWindowsRootPath = Path.Combine(rootPath, "WinPeWindows");
            WorkspaceRootPath = Path.Combine(rootPath, "Foundry");
            DiagnosticsRootPath = Path.Combine(TargetWindowsRootPath, "Windows", "Temp", "Foundry", "Logs", "AutopilotHash");
            RuntimeHashRootPath = Path.Combine(WorkspaceRootPath, "Runtime", "AutopilotHash");
            Directory.CreateDirectory(Path.Combine(TargetWindowsRootPath, "Windows", "System32"));
            Directory.CreateDirectory(Path.Combine(WinPeWindowsRootPath, "System32"));
            Directory.CreateDirectory(Path.Combine(WorkspaceRootPath, "Tools", "OA3"));
            Directory.CreateDirectory(RuntimeHashRootPath);
        }

        public string RootPath { get; }
        public string TargetWindowsRootPath { get; }
        public string WinPeWindowsRootPath { get; }
        public string WorkspaceRootPath { get; }
        public string DiagnosticsRootPath { get; }
        public string RuntimeHashRootPath { get; }

        public static TemporaryWorkspace Create()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"foundry-hash-capture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            return new TemporaryWorkspace(rootPath);
        }

        public void WriteTargetPcpKsp(string content)
        {
            File.WriteAllText(Path.Combine(TargetWindowsRootPath, "Windows", "System32", "PCPKsp.dll"), content);
        }

        public void WriteOa3Tool()
        {
            File.WriteAllText(Path.Combine(WorkspaceRootPath, "Tools", "OA3", "oa3tool.exe"), "oa3");
        }

        public void CreateWinPePcpKspDirectory()
        {
            Directory.CreateDirectory(Path.Combine(WinPeWindowsRootPath, "System32", "PCPKsp.dll"));
        }

        public AutopilotHardwareHashCaptureRequest CreateRequest(string? groupTag)
        {
            return new AutopilotHardwareHashCaptureRequest
            {
                TargetWindowsRootPath = TargetWindowsRootPath,
                WinPeWindowsRootPath = WinPeWindowsRootPath,
                WorkspaceRootPath = WorkspaceRootPath,
                DiagnosticsRootPath = DiagnosticsRootPath,
                GroupTag = groupTag
            };
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
