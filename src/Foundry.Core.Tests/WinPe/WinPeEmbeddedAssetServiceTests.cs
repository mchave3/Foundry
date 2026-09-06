// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Services.WinPe;

namespace Foundry.Core.Tests.WinPe;

public sealed class WinPeEmbeddedAssetServiceTests
{
    [Fact]
    public void GetBootstrapScriptContent_ReturnsFoundryBootstrapScript()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("FoundryBootstrap.log", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Foundry.Connect", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Foundry.Deploy", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBootstrapScriptContent_UsesCorrelatedBoundedStructuredLogging()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("FOUNDRY_DIAGNOSTIC_SESSION_ID", content, StringComparison.Ordinal);
        Assert.Contains("[Foundry.Bootstrap] [Session:", content, StringComparison.Ordinal);
        Assert.Contains("$MaximumLogFileSizeBytes", content, StringComparison.Ordinal);
        Assert.Contains("$RetainedLogFileCount", content, StringComparison.Ordinal);
        Assert.Contains("function Invoke-LogRotation", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBootstrapScriptContent_PersistsLogsWithoutInteractiveFailurePrompt()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("function Copy-BootstrapLogsToCache", content, StringComparison.Ordinal);
        Assert.Contains("finally {", content, StringComparison.Ordinal);
        Assert.Contains("Copy-BootstrapLogsToCache", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Press E", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetIanaWindowsTimeZoneMapJson_ReturnsEmbeddedTimeZoneMap()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetIanaWindowsTimeZoneMapJson();

        Assert.Contains("Europe/Paris", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Romance Standard Time", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSevenZipSourceDirectoryPath_ReturnsBundledAssets()
    {
        var service = new WinPeEmbeddedAssetService();

        string sourceDirectoryPath = service.GetSevenZipSourceDirectoryPath();

        Assert.True(Directory.Exists(sourceDirectoryPath), sourceDirectoryPath);
        Assert.True(File.Exists(Path.Combine(sourceDirectoryPath, "x64", "7za.exe")));
        Assert.True(File.Exists(Path.Combine(sourceDirectoryPath, "arm64", "7za.exe")));
        Assert.True(File.Exists(Path.Combine(sourceDirectoryPath, "License.txt")));
        Assert.True(File.Exists(Path.Combine(sourceDirectoryPath, "readme.txt")));
    }

    [Fact]
    public void GetBootstrapScriptContent_KeepsWiredNetworkStartupNonFatal()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("function Ensure-ServiceRunning", content, StringComparison.Ordinal);
        Assert.Contains("Start-Service -Name $ServiceName -ErrorAction Stop", content, StringComparison.Ordinal);
        Assert.Contains("Write-Log \"Failed to start $FriendlyName", content, StringComparison.Ordinal);
        Assert.Contains("[void](Ensure-ServiceRunning -ServiceName 'dot3svc'", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBootstrapScriptContent_GatesWlanSvcStartupOnWinReDependencies()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("function Start-WinPeWirelessServiceIfSupported", content, StringComparison.Ordinal);
        Assert.Contains("Skipping WlanSvc startup because WinRE wireless dependencies are not present", content, StringComparison.Ordinal);
        Assert.Contains("[void](Ensure-ServiceRunning -ServiceName 'WlanSvc'", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBootstrapScriptContent_KeepsInternetTimeSyncNonFatal()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("function Sync-WinPeInternetDateTime", content, StringComparison.Ordinal);
        Assert.Contains("Could not resolve internet time. Continuing without clock synchronization.", content, StringComparison.Ordinal);
        Assert.Contains("Clock update failed. Continuing.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBootstrapScriptContent_KeepsTimezoneFallbackNonFatal()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("function Set-WinPeTimeZone", content, StringComparison.Ordinal);
        Assert.Contains("Timezone map unavailable. Auto-detect skipped.", content, StringComparison.Ordinal);
        Assert.Contains("Timezone update failed. Continuing.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBootstrapScriptContent_UsesNormalizedApplicationRuntimeLayout()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("return Join-Path $BootstrapRoot $ApplicationName", content, StringComparison.Ordinal);
        Assert.Contains("Get-RuntimeCacheRoot -BootstrapRoot $BootstrapRoot -ApplicationName $ApplicationName -RuntimeIdentifier $RuntimeIdentifier", content, StringComparison.Ordinal);
        Assert.DoesNotContain("'Foundry.Deploy' { return $BootstrapRoot }", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBootstrapScriptContent_PreservesRuntimeDownloadOverrides()
    {
        var service = new WinPeEmbeddedAssetService();

        string content = service.GetBootstrapScriptContent();

        Assert.Contains("FOUNDRY_CONNECT_RELEASE_TAG", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_DEPLOY_RELEASE_TAG", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_RELEASE_TAG", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_CONNECT_ARCHIVE", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_DEPLOY_ARCHIVE", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_CONNECT_ARCHIVE_SHA256", content, StringComparison.Ordinal);
        Assert.Contains("FOUNDRY_DEPLOY_ARCHIVE_SHA256", content, StringComparison.Ordinal);
        Assert.Contains("curl.exe", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Net.WebClient", content, StringComparison.Ordinal);
        Assert.Contains("Invoke-WebRequest", content, StringComparison.Ordinal);
    }
}
