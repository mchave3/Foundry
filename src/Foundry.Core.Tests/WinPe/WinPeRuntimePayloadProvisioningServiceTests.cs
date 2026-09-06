// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeRuntimePayloadProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_WhenDebugArchivesAreProvided_ExtractsToNormalizedIsoAndUsbRuntimeRoots()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();
        string connectArchivePath = workspace.CreateArchive("connect.zip", "Foundry.Connect.exe");
        string deployArchivePath = workspace.CreateArchive("deploy.zip", "Foundry.Deploy.exe");
        string legacyConnectSeedPath = Path.Combine(workspace.MountedImagePath, "Foundry", "Seed", "Foundry.Connect.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyConnectSeedPath)!);
        File.WriteAllText(legacyConnectSeedPath, "legacy");

        var service = new WinPeRuntimePayloadProvisioningService(new FakeRuntimeProcessRunner());

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.X64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                UsbCacheRootPath = workspace.UsbCacheRootPath,
                Connect = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ArchivePath = connectArchivePath
                },
                Deploy = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ArchivePath = deployArchivePath
                }
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Connect", "win-x64", "Foundry.Connect.exe")));
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Deploy", "win-x64", "Foundry.Deploy.exe")));
        Assert.True(File.Exists(Path.Combine(workspace.UsbCacheRootPath, "Runtime", "Foundry.Connect", "win-x64", "Foundry.Connect.exe")));
        Assert.True(File.Exists(Path.Combine(workspace.UsbCacheRootPath, "Runtime", "Foundry.Deploy", "win-x64", "Foundry.Deploy.exe")));
        Assert.False(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Seed", "Foundry.Connect.zip")));
    }

    [Fact]
    public async Task ProvisionAsync_WhenArchiveAndProjectAreProvided_PrefersArchive()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();
        string connectArchivePath = workspace.CreateArchive("connect.zip", "Foundry.Connect.exe");
        string projectPath = Path.Combine(workspace.RootPath, "src", "Foundry.Connect", "Foundry.Connect.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project />");
        var runner = new FakeRuntimeProcessRunner();

        var service = new WinPeRuntimePayloadProvisioningService(runner);

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.X64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                Connect = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ArchivePath = connectArchivePath,
                    ProjectPath = projectPath
                }
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Empty(runner.Executions);
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Connect", "win-x64", "Foundry.Connect.exe")));
    }

    [Fact]
    public async Task ProvisionAsync_WhenProjectIsProvided_PublishesSingleFileBeforeProvisioning()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();
        string projectPath = Path.Combine(workspace.RootPath, "src", "Foundry.Deploy", "Foundry.Deploy.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project />");
        var runner = new FakeRuntimeProcessRunner();

        var service = new WinPeRuntimePayloadProvisioningService(runner);

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.Arm64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                Deploy = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ProjectPath = projectPath
                }
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeProcessExecution execution = Assert.Single(runner.Executions);
        Assert.Equal("dotnet", execution.FileName);
        Assert.Contains("publish", execution.Arguments);
        Assert.Contains("-r win-arm64", execution.Arguments);
        Assert.Contains("/p:PublishSingleFile=true", execution.Arguments);
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Deploy", "win-arm64", "Foundry.Deploy.exe")));
    }

    [Fact]
    public async Task ProvisionAsync_WhenConnectProjectIsProvided_PublishesSingleFileBeforeProvisioning()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();
        string projectPath = Path.Combine(workspace.RootPath, "src", "Foundry.Connect", "Foundry.Connect.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project />");
        var runner = new FakeRuntimeProcessRunner();

        var service = new WinPeRuntimePayloadProvisioningService(runner);

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.X64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                Connect = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ProjectPath = projectPath
                }
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        WinPeProcessExecution execution = Assert.Single(runner.Executions);
        Assert.Equal("dotnet", execution.FileName);
        Assert.Contains("publish", execution.Arguments);
        Assert.Contains("-r win-x64", execution.Arguments);
        Assert.Contains("/p:PublishSingleFile=true", execution.Arguments);
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Connect", "win-x64", "Foundry.Connect.exe")));
    }

    [Fact]
    public async Task ProvisionAsync_WhenReleaseConnectIsEnabled_DownloadsAndExtractsReleaseAsset()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();
        byte[] archiveBytes = await File.ReadAllBytesAsync(workspace.CreateArchive("connect-release.zip", "Foundry.Connect.exe"));
        var httpHandler = new FakeReleaseHttpMessageHandler("Foundry.Connect-win-x64.zip", archiveBytes);
        var service = new WinPeRuntimePayloadProvisioningService(
            new FakeRuntimeProcessRunner(),
            new HttpClient(httpHandler));
        var progress = new CapturingProgress<WinPeDownloadProgress>();

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.X64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                Connect = new WinPeRuntimePayloadApplicationOptions
                {
                    IsEnabled = true,
                    ProvisioningSource = WinPeProvisioningSource.Release
                }
            },
            progress,
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.Contains(FakeReleaseHttpMessageHandler.LatestReleaseUri, httpHandler.RequestUris);
        Assert.Contains(FakeReleaseHttpMessageHandler.DownloadUri, httpHandler.RequestUris);
        Assert.Contains(progress.Items, item => item is { Percent: 0, Status: "Downloading Foundry.Connect runtime payload." });
        Assert.Contains(progress.Items, item => item.Percent == 100 && item.Status.Contains("Foundry.Connect", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Connect", "win-x64", "Foundry.Connect.exe")));
        Assert.False(Directory.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Deploy")));
    }

    [Fact]
    public async Task ProvisionAsync_WhenApplicationIsDisabled_DoesNotCreateRuntimeRoot()
    {
        using TempRuntimeWorkspace workspace = TempRuntimeWorkspace.Create();

        var service = new WinPeRuntimePayloadProvisioningService(new FakeRuntimeProcessRunner());

        WinPeResult result = await service.ProvisionAsync(
            new WinPeRuntimePayloadProvisioningOptions
            {
                Architecture = WinPeArchitecture.X64,
                WorkingDirectoryPath = workspace.WorkingDirectoryPath,
                MountedImagePath = workspace.MountedImagePath,
                UsbCacheRootPath = workspace.UsbCacheRootPath
            },
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Details);
        Assert.False(Directory.Exists(Path.Combine(workspace.MountedImagePath, "Foundry", "Runtime", "Foundry.Connect")));
        Assert.False(Directory.Exists(Path.Combine(workspace.UsbCacheRootPath, "Runtime", "Foundry.Deploy")));
    }

    private sealed class FakeReleaseHttpMessageHandler(string assetName, byte[] archiveBytes) : HttpMessageHandler
    {
        public const string LatestReleaseUri = "https://api.github.com/repos/foundry-osd/foundry/releases/latest";
        public const string DownloadUri = "https://example.test/Foundry.Connect-win-x64.zip";

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string requestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            RequestUris.Add(requestUri);

            if (string.Equals(requestUri, LatestReleaseUri, StringComparison.Ordinal))
            {
                string digest = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
                string json = $$"""
                    {
                      "assets": [
                        {
                          "name": "{{assetName}}",
                          "browser_download_url": "{{DownloadUri}}",
                          "digest": "sha256:{{digest}}"
                        }
                      ]
                    }
                    """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (string.Equals(requestUri, DownloadUri, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value)
        {
            Items.Add(value);
        }
    }

    private sealed class FakeRuntimeProcessRunner : IWinPeProcessRunner
    {
        public List<WinPeProcessExecution> Executions { get; } = [];

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException("Executable calls must pass argument tokens.");
        }

        public Task<WinPeProcessExecution> RunAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environmentOverrides = null,
            TimeSpan? executionTimeout = null)
        {
            string arguments = string.Join(' ', argumentList);
            string outputDirectory = argumentList[argumentList.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(outputDirectory);
            string executableName = arguments.Contains("Foundry.Connect.csproj", StringComparison.OrdinalIgnoreCase)
                ? "Foundry.Connect.exe"
                : "Foundry.Deploy.exe";
            File.WriteAllText(Path.Combine(outputDirectory, executableName), executableName);

            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            };

            Executions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<WinPeProcessExecution> RunCmdScriptAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

        public Task<WinPeProcessExecution> RunCmdScriptDirectAsync(
            string scriptPath,
            string scriptArguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            TimeSpan? executionTimeout = null)
        {
            throw new NotSupportedException();
        }

    }

    private sealed class TempRuntimeWorkspace : IDisposable
    {
        private TempRuntimeWorkspace(string rootPath)
        {
            RootPath = rootPath;
            WorkingDirectoryPath = Path.Combine(rootPath, "work");
            MountedImagePath = Path.Combine(rootPath, "mount");
            UsbCacheRootPath = Path.Combine(rootPath, "usb-cache");
            Directory.CreateDirectory(WorkingDirectoryPath);
            Directory.CreateDirectory(MountedImagePath);
            Directory.CreateDirectory(UsbCacheRootPath);
        }

        public string RootPath { get; }
        public string WorkingDirectoryPath { get; }
        public string MountedImagePath { get; }
        public string UsbCacheRootPath { get; }

        public static TempRuntimeWorkspace Create()
        {
            return new TempRuntimeWorkspace(Path.Combine(Path.GetTempPath(), $"foundry-runtime-{Guid.NewGuid():N}"));
        }

        public string CreateArchive(string archiveName, string executableName)
        {
            string payloadPath = Path.Combine(RootPath, "payloads", Path.GetFileNameWithoutExtension(archiveName));
            Directory.CreateDirectory(payloadPath);
            File.WriteAllText(Path.Combine(payloadPath, executableName), executableName);

            string archivePath = Path.Combine(RootPath, archiveName);
            ZipFile.CreateFromDirectory(payloadPath, archivePath);
            return archivePath;
        }

        public void Dispose()
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
