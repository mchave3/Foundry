// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;

namespace Foundry.Core.Services.WinPe;

public sealed record UsbOutputOptions
{
    public string StagingDirectoryPath { get; init; } = string.Empty;
    public int? TargetDiskNumber { get; init; }
    /// <summary>The immutable disk snapshot shown when the user confirmed the operation.</summary>
    public WinPeUsbDiskIdentity? ExpectedDisk { get; init; }
    public UsbPartitionStyle PartitionStyle { get; init; } = UsbPartitionStyle.Gpt;
    public UsbFormatMode FormatMode { get; init; } = UsbFormatMode.Quick;
    public string? WorkingDirectoryPath { get; init; }
    public string? AdkRootPath { get; init; }
    public WinPeArchitecture Architecture { get; init; } = WinPeArchitecture.X64;
    public WinPeSignatureMode SignatureMode { get; init; } = WinPeSignatureMode.Pca2011;
    public WinPeBootImageSource BootImageSource { get; init; } = WinPeBootImageSource.WinPe;
    public string WinPeLanguage { get; init; } = string.Empty;
    public IReadOnlyList<WinPeVendorSelection> DriverVendors { get; init; } = [];
    public string DriverCatalogUri { get; init; } = string.Empty;
    public string? CustomDriverDirectoryPath { get; init; }
    public string? FoundryConnectConfigurationJson { get; init; }
    public IReadOnlyList<FoundryConnectProvisionedAssetFile> FoundryConnectAssetFiles { get; init; } = [];
    public string? DeployConfigurationJson { get; init; }
    public IReadOnlyList<AutopilotProfileSettings> AutopilotProfiles { get; init; } = [];
    public WinPeRuntimePayloadProvisioningOptions? RuntimePayloadProvisioning { get; init; }
    public IProgress<WinPeDownloadProgress>? DownloadProgress { get; init; }
    public IProgress<WinPeMediaProgress>? Progress { get; init; }
    public bool PreserveBuildWorkspace { get; init; }
}
