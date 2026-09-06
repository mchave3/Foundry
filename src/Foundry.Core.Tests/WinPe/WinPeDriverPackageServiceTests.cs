// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Foundry.Utilities.Processes;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeDriverPackageServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task PrepareAsync_NativeInterruption_RetainsPackageAndOriginalFailure(bool rootExited, bool cancellation)
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-retained-{Guid.NewGuid():N}");
        byte[] bytes = "package"u8.ToArray();
        using var cancelled = new CancellationTokenSource();
        Exception interruption = cancellation ? new OperationCanceledException(cancelled.Token) : new TimeoutException("native fixture deadline");
        interruption.Data["ProcessRootExitConfirmed"] = rootExited;
        interruption.Data["ProcessTreeTerminationConfirmed"] = false;
        interruption.Data["ProcessOutputDrainConfirmed"] = !rootExited;
        var nativeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WinPeDriverPackageService(new FakeExtractionRunner(interruption: interruption, beforeInterruption: () =>
        {
            if (cancellation) { cancelled.Cancel(); }
        }), new HttpClient(new StaticPackageHandler(bytes)), "7za.exe");
        string packagePath = Path.Combine(root, "download", "package.cab");
        try
        {
            Task<WinPeResult<WinPePreparedDriverSet>> operation = service.PrepareAsync(
                [CreatePackage() with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }],
                Path.GetDirectoryName(packagePath)!, Path.Combine(root, "extract"), null, cancelled.Token);
            if (cancellation)
            {
                Assert.Same(interruption, await Assert.ThrowsAsync<OperationCanceledException>(() => operation));
            }
            else
            {
                Assert.Same(interruption, (await operation).Error?.Exception);
            }
            Assert.Throws<IOException>(() => File.WriteAllText(packagePath, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(packagePath));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "extract"), "native-input.fixture", SearchOption.AllDirectories));
        }
        finally
        {
            nativeCompleted.SetResult();
            if (interruption.Data[NativeFileLease.RetainedLeaseIdsDataKey] is Guid[] ownershipIds)
            {
                foreach (Guid ownershipId in ownershipIds)
                {
                    await NativeFileLease.ReconcileRetainedAsync(ownershipId, _ => Task.FromResult(nativeCompleted.Task.IsCompleted), CancellationToken.None);
                }
            }
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task PrepareAsync_ValidatedCacheAvoidsNetwork_AndLocalWriteFailureRemainsStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        byte[] bytes = "cached package"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(root, "package.cab"), bytes, TestContext.Current.CancellationToken);
        var handler = new StaticPackageHandler(bytes);
        var service = new WinPeDriverPackageService(new FakeExtractionRunner(), new HttpClient(handler), "7za.exe");
        try
        {
            WinPeDriverCatalogEntry package = CreatePackage() with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            WinPeResult<WinPePreparedDriverSet> cached = await service.PrepareAsync([package], root, Path.Combine(root, "extract"), null, TestContext.Current.CancellationToken);
            Assert.True(cached.IsSuccess);
            Assert.Equal(0, handler.RequestCount);
            string blockedRoot = Path.Combine(root, "blocked");
            await File.WriteAllTextAsync(blockedRoot, "existing file", TestContext.Current.CancellationToken);
            WinPeResult<WinPePreparedDriverSet> failed = await service.PrepareAsync([package], blockedRoot, Path.Combine(root, "extract"), null, TestContext.Current.CancellationToken);
            Assert.False(failed.IsSuccess);
            Assert.Equal(WinPeFailureKinds.FileSystem, failed.Error?.FailureKind);
            Assert.Equal("existing file", await File.ReadAllTextAsync(blockedRoot, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task PrepareAsync_InvalidIntegrity_HasNoFileOrNetworkEffects(string hash)
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-integrity-{Guid.NewGuid():N}");
        var handler = new StaticPackageHandler("payload"u8.ToArray());
        var service = new WinPeDriverPackageService(new FakeExtractionRunner(), new HttpClient(handler), "7za.exe");
        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [CreatePackage() with { Sha256 = hash }], Path.Combine(root, "download"), Path.Combine(root, "extract"), null, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(0, handler.RequestCount);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_WrongReplacementHash_PreservesExistingFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "package.cab");
        await File.WriteAllTextAsync(destination, "previous", TestContext.Current.CancellationToken);
        var service = new WinPeDriverPackageService(new FakeExtractionRunner(), new HttpClient(new StaticPackageHandler("replacement"u8.ToArray())), "7za.exe");
        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [CreatePackage() with { Sha256 = new string('A', 64) }], root, Path.Combine(root, "extract"), null, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal("previous", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PrepareAsync_DownloadsValidatesAndExtractsPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-package-{Guid.NewGuid():N}");
        string downloadRoot = Path.Combine(root, "downloads");
        string extractRoot = Path.Combine(root, "extracted");
        byte[] packageBytes = Encoding.UTF8.GetBytes("driver package");
        string packageHash = Convert.ToHexString(SHA256.HashData(packageBytes));
        var runner = new FakeExtractionRunner();
        var service = new WinPeDriverPackageService(
            runner,
            new HttpClient(new StaticPackageHandler(packageBytes)),
            "7za.exe");

        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [
                    new WinPeDriverCatalogEntry
                    {
                        Id = "dell-package",
                        Vendor = WinPeVendorSelection.Dell,
                        DownloadUri = "https://example.test/dell.cab",
                        FileName = "dell.cab",
                        Format = "cab",
                        Sha256 = packageHash
                    }
                ],
                downloadRoot,
                extractRoot,
                null,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Details);
            Assert.True(File.Exists(Path.Combine(downloadRoot, "dell.cab")));
            string extractionDirectory = Assert.Single(result.Value!.ExtractionDirectories);
            Assert.True(File.Exists(Path.Combine(extractionDirectory, "driver.inf")));
            Assert.Contains(runner.Executions, execution => execution.FileName == "7za.exe");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenExecutableExtractionHasNoInf_ReturnsExtractionFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-package-{Guid.NewGuid():N}");
        byte[] packageBytes = Encoding.UTF8.GetBytes("driver package");
        var service = new WinPeDriverPackageService(
            new FakeExtractionRunner(createInf: false),
            new HttpClient(new StaticPackageHandler(packageBytes)),
            "7za.exe");

        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [
                    new WinPeDriverCatalogEntry
                    {
                        Id = "setup",
                        DownloadUri = "https://example.test/setup.exe",
                        FileName = "setup.exe",
                        Format = "exe",
                        Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes))
                    }
                ],
                Path.Combine(root, "downloads"),
                Path.Combine(root, "extracted"),
                null,
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(WinPeErrorCodes.DriverExtractionFailed, result.Error?.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenDownloadReturnsError_ClassifiesHttpStatus()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-package-{Guid.NewGuid():N}");
        var service = new WinPeDriverPackageService(
            new FakeExtractionRunner(),
            new HttpClient(new StaticPackageHandler([], HttpStatusCode.BadGateway)),
            "7za.exe");

        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [CreatePackage()],
                Path.Combine(root, "downloads"),
                Path.Combine(root, "extracted"),
                null,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(WinPeFailureReasons.HttpStatus, result.Error?.FailureReason);
            Assert.Equal("HTTP 502 Bad Gateway", result.Error?.ErrorSummary);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareAsync_WhenHttpClientTimesOut_PreservesTimeoutException()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-driver-package-{Guid.NewGuid():N}");
        var timeout = new TaskCanceledException("The request timed out.");
        var service = new WinPeDriverPackageService(
            new FakeExtractionRunner(),
            new HttpClient(new StaticPackageHandler([], exception: timeout)),
            "7za.exe");

        try
        {
            WinPeResult<WinPePreparedDriverSet> result = await service.PrepareAsync(
                [CreatePackage()],
                Path.Combine(root, "downloads"),
                Path.Combine(root, "extracted"),
                null,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(WinPeFailureReasons.Timeout, result.Error?.FailureReason);
            Assert.Same(timeout, result.Error?.Exception);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WinPeDriverCatalogEntry CreatePackage() => new()
    {
        Id = "package",
        DownloadUri = "https://example.test/package.cab",
        FileName = "package.cab",
        Format = "cab",
        Sha256 = new string('A', 64)
    };

    private sealed class StaticPackageHandler(
        byte[] content,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Exception? exception = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }

    private sealed class FakeExtractionRunner(bool createInf = true, Exception? interruption = null, Action? beforeInterruption = null) : IWinPeProcessRunner
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
            Executions.Add(new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            });

            if (interruption is not null)
            {
                File.WriteAllText(Path.Combine(workingDirectory, "native-input.fixture"), "native fixture input");
                beforeInterruption?.Invoke();
                throw interruption;
            }

            if (createInf)
            {
                Directory.CreateDirectory(workingDirectory);
                File.WriteAllText(Path.Combine(workingDirectory, "driver.inf"), string.Empty);
            }

            return Task.FromResult(new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            });
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
}
