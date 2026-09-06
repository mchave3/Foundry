// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Foundry.Utilities.Processes;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinReBootImagePreparationServiceTests
{
    [Theory]
    [InlineData("/Get-ImageInfo", false, false)]
    [InlineData("/Get-ImageInfo", true, true)]
    [InlineData("/Export-Image", false, true)]
    [InlineData("/Export-Image", true, false)]
    public async Task ReplaceBootWimAsync_UnconfirmedNativeInterruption_RetainsSourceAndWorkingInputs(string command, bool rootExited, bool cancellation)
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-winre-retained-{Guid.NewGuid():N}");
        string working = Path.Combine(root, "workspace");
        string cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(cache);
        string sourcePath = Path.Combine(cache, "source.esd");
        string bootPath = Path.Combine(root, "boot.wim");
        await File.WriteAllTextAsync(sourcePath, "source fixture", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(bootPath, "original boot", TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        Exception interruption = cancellation ? new OperationCanceledException(cancelled.Token) : new TimeoutException("native fixture deadline");
        interruption.Data["ProcessRootExitConfirmed"] = rootExited;
        interruption.Data["ProcessTreeTerminationConfirmed"] = false;
        interruption.Data["ProcessOutputDrainConfirmed"] = !rootExited;
        var nativeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeWinPeProcessRunner
        {
            Interruption = interruption,
            InterruptCommand = command,
            BeforeInterruption = () => { if (cancellation) { cancelled.Cancel(); } }
        };
        string catalog = CreateCatalogXml(Convert.ToHexString(SHA256.HashData("source fixture"u8)));
        var service = new WinReBootImagePreparationService(runner, new HttpClient(new StaticCatalogHandler(catalog)));
        try
        {
            Task<WinPeResult<WinReBootImagePreparationResult>> operation = service.ReplaceBootWimAsync(new WinReBootImagePreparationOptions
            {
                Artifact = new WinPeBuildArtifact { Architecture = WinPeArchitecture.X64, BootWimPath = bootPath, WorkingDirectoryPath = working },
                Tools = new WinPeToolPaths { DismPath = "dism.exe" },
                WinPeLanguage = "en-US",
                CacheDirectoryPath = cache
            }, cancelled.Token);
            if (cancellation)
            {
                Assert.Same(interruption, await Assert.ThrowsAsync<OperationCanceledException>(() => operation));
            }
            else
            {
                Assert.Same(interruption, (await operation).Error?.Exception);
            }
            Assert.Equal(rootExited, interruption.Data["ProcessRootExitConfirmed"]);
            Assert.Equal(false, interruption.Data["ProcessTreeTerminationConfirmed"]);
            Assert.Throws<IOException>(() => File.WriteAllText(sourcePath, "replacement"));
            Assert.Throws<IOException>(() => File.Delete(sourcePath));
            Assert.True(File.Exists(Path.Combine(working, "native-owned.fixture")));
            Assert.Equal("original boot", await File.ReadAllTextAsync(bootPath, TestContext.Current.CancellationToken));
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
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EnsureDownloadedAsync_InvalidReplacementPreservesCache_AndHttpIsScoped()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-winre-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string cached = Path.Combine(root, "source.esd");
        await File.WriteAllTextAsync(cached, "previous cache", TestContext.Current.CancellationToken);
        var handler = new StaticCatalogHandler("wrong replacement");
        var service = new WinReBootImagePreparationService(new FakeWinPeProcessRunner(), new HttpClient(handler));
        WinReCatalogItem source = WinReBootImagePreparationService.SelectCatalogCandidates(
            CreateCatalogXml(new string('A', 64)), WinPeArchitecture.X64, "en-US").Value![0].Source with
        {
            Url = "http://dl.delivery.mp.microsoft.com/source.esd"
        };
        try
        {
            WinPeResult<string> failed = await service.EnsureDownloadedAsync(root, source, null, TestContext.Current.CancellationToken);
            Assert.False(failed.IsSuccess);
            Assert.Equal(WinPeErrorCodes.HashMismatch, failed.Error?.Code);
            Assert.Equal("previous cache", await File.ReadAllTextAsync(cached, TestContext.Current.CancellationToken));
            int requests = handler.RequestCount;
            WinPeResult<string> disallowed = await service.EnsureDownloadedAsync(root, source with { Url = "http://example.test/source.esd" }, null, TestContext.Current.CancellationToken);
            Assert.False(disallowed.IsSuccess);
            Assert.Equal(requests, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public async Task ValidateHashIfRequestedAsync_MissingOrMalformedDigest_FailsBeforeOpeningFile(string? hash)
    {
        WinPeResult result = await WinReBootImagePreparationService.ValidateHashIfRequestedAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.esd"), hash, CancellationToken.None);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void SelectCatalogCandidates_Filters24H2ArchitectureAndLanguage()
    {
        const string catalogXml = """
                                  <Catalog>
                                    <Item>
                                      <WindowsRelease>11</WindowsRelease>
                                      <ReleaseId>24H2</ReleaseId>
                                      <BuildMajor>26100</BuildMajor>
                                      <BuildUbr>2454</BuildUbr>
                                      <Architecture>x64</Architecture>
                                      <LanguageCode>fr-fr</LanguageCode>
                                      <Edition>Professional</Edition>
                                      <ClientType>CLIENTCONSUMER</ClientType>
                                      <LicenseChannel>RET</LicenseChannel>
                                      <FileName>consumer.esd</FileName>
                                      <Url>https://example.test/consumer.esd</Url>
                                      <Sha256>abc</Sha256>
                                    </Item>
                                    <Item>
                                      <WindowsRelease>11</WindowsRelease>
                                      <ReleaseId>24H2</ReleaseId>
                                      <BuildMajor>26100</BuildMajor>
                                      <BuildUbr>2454</BuildUbr>
                                      <Architecture>x64</Architecture>
                                      <LanguageCode>fr-fr</LanguageCode>
                                      <Edition>Enterprise</Edition>
                                      <ClientType>CLIENTBUSINESS</ClientType>
                                      <LicenseChannel>VOL</LicenseChannel>
                                      <FileName>business.esd</FileName>
                                      <Url>https://example.test/business.esd</Url>
                                      <Sha256>def</Sha256>
                                    </Item>
                                    <Item>
                                      <WindowsRelease>11</WindowsRelease>
                                      <ReleaseId>23H2</ReleaseId>
                                      <BuildMajor>22631</BuildMajor>
                                      <BuildUbr>5337</BuildUbr>
                                      <Architecture>x64</Architecture>
                                      <LanguageCode>fr-fr</LanguageCode>
                                      <Edition>Professional</Edition>
                                      <ClientType>CLIENTCONSUMER</ClientType>
                                      <LicenseChannel>RET</LicenseChannel>
                                      <FileName>old.esd</FileName>
                                      <Url>https://example.test/old.esd</Url>
                                      <Sha256>ghi</Sha256>
                                    </Item>
                                  </Catalog>
                                  """;

        WinPeResult<IReadOnlyList<WinReSourceCandidate>> result =
            WinReBootImagePreparationService.SelectCatalogCandidates(catalogXml, WinPeArchitecture.X64, "fr-FR");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Collection(
            result.Value,
            candidate =>
            {
                Assert.Equal("Pro", candidate.RequestedEdition);
                Assert.Equal("consumer.esd", candidate.Source.FileName);
            },
            candidate =>
            {
                Assert.Equal("Enterprise", candidate.RequestedEdition);
                Assert.Equal("business.esd", candidate.Source.FileName);
            });
    }

    [Fact]
    public void ResolveImageIndexFromOutput_MatchesEditionId()
    {
        const string dismOutput = """
                                  Deployment Image Servicing and Management tool

                                  Index : 1
                                  Name : Windows 11 Home
                                  Description : Windows 11 Home
                                  Size : 17,123,456 bytes

                                  Index : 6
                                  Name : Windows 11 Pro
                                  Description : Windows 11 Pro
                                  Edition : Professional
                                  Edition ID : Professional
                                  Size : 18,123,456 bytes
                                  """;

        WinPeResult<int> result = WinReBootImagePreparationService.ResolveImageIndexFromOutput(dismOutput, "Pro");

        Assert.True(result.IsSuccess);
        Assert.Equal(6, result.Value);
    }

    [Fact]
    public async Task ValidateHashIfRequestedAsync_WhenHashMatches_ReturnsSuccess()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"foundry-hash-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "foundry");

        try
        {
            WinPeResult result = await WinReBootImagePreparationService.ValidateHashIfRequestedAsync(
                filePath,
                "DFB316701857783DAC69A14D1FE3FD60CFF21D56E830BAF7F0E3871BD73EEE39",
                CancellationToken.None);

            Assert.True(result.IsSuccess);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void PrepareWirelessDependencyFiles_StagesRequiredFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-winre-{Guid.NewGuid():N}");
        string mountedImagePath = Path.Combine(root, "mounted");
        string sourceSystem32Path = Path.Combine(mountedImagePath, "Windows", "System32");
        string dependencyPath = Path.Combine(root, "wireless-support");
        Directory.CreateDirectory(sourceSystem32Path);
        File.WriteAllText(Path.Combine(sourceSystem32Path, "dmcmnutils.dll"), "dm");
        File.WriteAllText(Path.Combine(sourceSystem32Path, "mdmregistration.dll"), "mdm");

        try
        {
            WinPeResult<WinReBootImagePreparationResult> result =
                WinReBootImagePreparationService.PrepareWirelessDependencyFiles(mountedImagePath, dependencyPath);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.Equal(2, result.Value.DependencyFiles.Count);
            Assert.All(result.Value.DependencyFiles, dependency => Assert.True(File.Exists(dependency.StagedPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceBootWimAsync_RequiresCompleteImageMetadataBeforeExporting(bool metadataTruncated)
    {
        string root = Path.Combine(Path.GetTempPath(), $"foundry-winre-replace-{Guid.NewGuid():N}");
        string workingPath = Path.Combine(root, "workspace");
        string mediaPath = Path.Combine(workingPath, "media");
        string sourcesPath = Path.Combine(mediaPath, "sources");
        string cachePath = Path.Combine(root, "cache");
        Directory.CreateDirectory(sourcesPath);
        Directory.CreateDirectory(cachePath);

        string bootWimPath = Path.Combine(sourcesPath, "boot.wim");
        string cachedSourcePath = Path.Combine(cachePath, "source.esd");
        await File.WriteAllTextAsync(bootWimPath, "original");
        await File.WriteAllTextAsync(cachedSourcePath, "cached source");
        string cachedSourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cached source")));
        string catalogXml = CreateCatalogXml(cachedSourceHash);

        var runner = new FakeWinPeProcessRunner { MetadataTruncated = metadataTruncated };
        var service = new WinReBootImagePreparationService(
            runner,
            new HttpClient(new StaticCatalogHandler(catalogXml)));

        try
        {
            WinPeResult<WinReBootImagePreparationResult> result = await service.ReplaceBootWimAsync(
                new WinReBootImagePreparationOptions
                {
                    Artifact = new WinPeBuildArtifact
                    {
                        Architecture = WinPeArchitecture.X64,
                        BootWimPath = bootWimPath,
                        WorkingDirectoryPath = workingPath
                    },
                    Tools = new WinPeToolPaths
                    {
                        DismPath = "dism.exe"
                    },
                    WinPeLanguage = "en-US",
                    CacheDirectoryPath = cachePath
                },
                CancellationToken.None);

            if (metadataTruncated)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal("original", await File.ReadAllTextAsync(bootWimPath, TestContext.Current.CancellationToken));
                Assert.DoesNotContain(runner.Executions, execution => execution.Arguments.Contains("/Export-Image", StringComparison.Ordinal));
                return;
            }

            Assert.True(result.IsSuccess, result.Error?.Details);
            Assert.Equal("winre", await File.ReadAllTextAsync(bootWimPath));
            Assert.NotNull(result.Value);
            Assert.Equal(2, result.Value.DependencyFiles.Count);
            Assert.Contains(runner.Executions, execution => execution.Arguments.Contains("/Export-Image", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(runner.Executions, execution => execution.Arguments.Contains("/Unmount-Image", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateCatalogXml(string hash)
    {
        return $$"""
                 <Catalog>
                   <Item>
                     <WindowsRelease>11</WindowsRelease>
                     <ReleaseId>24H2</ReleaseId>
                     <BuildMajor>26100</BuildMajor>
                     <BuildUbr>2454</BuildUbr>
                     <Architecture>x64</Architecture>
                     <LanguageCode>en-us</LanguageCode>
                     <Edition>Professional</Edition>
                     <ClientType>CLIENTCONSUMER</ClientType>
                     <LicenseChannel>RET</LicenseChannel>
                     <FileName>source.esd</FileName>
                     <Url>https://example.test/source.esd</Url>
                     <Sha256>{{hash}}</Sha256>
                   </Item>
                 </Catalog>
                 """;
    }

    private sealed class StaticCatalogHandler(string content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/xml")
            });
        }
    }

    private sealed class FakeWinPeProcessRunner : IWinPeProcessRunner
    {
        public Exception? Interruption { get; init; }
        public string? InterruptCommand { get; init; }
        public Action? BeforeInterruption { get; init; }
        public bool MetadataTruncated { get; init; }
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
            if (Interruption is not null && InterruptCommand is not null && argumentList.Contains(InterruptCommand))
            {
                File.WriteAllText(Path.Combine(workingDirectory, "native-owned.fixture"), "native fixture input");
                BeforeInterruption?.Invoke();
                throw Interruption;
            }
            var execution = new WinPeProcessExecution
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                StandardOutput = CreateOutput(arguments),
                StandardOutputTruncated = MetadataTruncated && argumentList.Contains("/Get-ImageInfo")
            };

            Executions.Add(execution);
            HandleSideEffects(argumentList);
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

        private static string CreateOutput(string arguments)
        {
            if (!arguments.Contains("/Get-ImageInfo", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return """
                   Index : 6
                   Name : Windows 11 Pro
                   Edition : Professional
                   Edition ID : Professional
                   """;
        }

        private static void HandleSideEffects(IReadOnlyList<string> arguments)
        {
            if (arguments.Contains("/Export-Image", StringComparer.OrdinalIgnoreCase))
            {
                string destination = ExtractArgumentPath(arguments, "/DestinationImageFile:");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, "install");
                return;
            }

            if (arguments.Contains("/Mount-Image", StringComparer.OrdinalIgnoreCase))
            {
                string mountDirectory = ExtractArgumentPath(arguments, "/MountDir:");
                string recoveryPath = Path.Combine(mountDirectory, "Windows", "System32", "Recovery");
                string system32Path = Path.Combine(mountDirectory, "Windows", "System32");
                Directory.CreateDirectory(recoveryPath);
                Directory.CreateDirectory(system32Path);
                File.WriteAllText(Path.Combine(recoveryPath, "winre.wim"), "winre");
                File.WriteAllText(Path.Combine(system32Path, "dmcmnutils.dll"), "dm");
                File.WriteAllText(Path.Combine(system32Path, "mdmregistration.dll"), "mdm");
            }
        }

        private static string ExtractArgumentPath(IReadOnlyList<string> arguments, string name) =>
            Assert.Single(arguments, argument => argument.StartsWith(name, StringComparison.OrdinalIgnoreCase))[name.Length..];
    }
}
