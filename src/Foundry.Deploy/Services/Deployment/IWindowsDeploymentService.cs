// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Deploy.Models.Configuration;

namespace Foundry.Deploy.Services.Deployment;

/// <summary>
/// Defines the Windows deployment operations that mutate disks, offline images, boot files, and recovery images.
/// </summary>
public interface IWindowsDeploymentService
{
    /// <summary>
    /// Cleans and repartitions the target disk for UEFI Windows deployment.
    /// </summary>
    /// <param name="expectedDisk">The exact device snapshot retained at destructive confirmation.</param>
    /// <param name="workingDirectory">The directory used for temporary scripts.</param>
    /// <param name="cancellationToken">A token used to cancel disk preparation.</param>
    /// <returns>The resulting target partition layout.</returns>
    Task<DeploymentTargetLayout> PrepareTargetDiskAsync(
        TargetDiskIdentity expectedDisk,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the WIM/ESD image index matching a requested edition.
    /// </summary>
    /// <param name="imagePath">Path to the WIM or ESD image.</param>
    /// <param name="requestedEdition">Windows edition name requested by the catalog.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels image inspection.</param>
    /// <returns>The image index matching the requested edition.</returns>
    Task<int> ResolveImageIndexAsync(
        string imagePath,
        string requestedEdition,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a Windows image to the target Windows partition.
    /// </summary>
    /// <param name="imagePath">Path to the WIM or ESD image.</param>
    /// <param name="imageIndex">Image index to apply.</param>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="scratchDirectory">DISM scratch directory used during image apply.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels image application.</param>
    /// <param name="progress">Optional progress sink for DISM percentage updates.</param>
    /// <returns>A task that completes after the image is applied.</returns>
    Task ApplyImageAsync(
        string imagePath,
        int imageIndex,
        string windowsPartitionRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null);

    /// <summary>
    /// Reads the currently applied Windows edition from the offline image.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="workingDirectory">Directory used for temporary command output.</param>
    /// <param name="cancellationToken">Token that cancels edition inspection.</param>
    /// <returns>The applied Windows edition, or <see langword="null"/> when it cannot be resolved.</returns>
    Task<string?> GetAppliedWindowsEditionAsync(
        string windowsPartitionRoot,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes computer name and optional time zone into Windows\Panther\unattend.xml.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="computerName">Computer name written into unattend.xml.</param>
    /// <param name="processorArchitecture">Processor architecture used by unattend components.</param>
    /// <param name="defaultTimeZoneId">Optional Windows time-zone identifier written into unattend.xml.</param>
    /// <param name="cancellationToken">Token that cancels unattend generation.</param>
    /// <returns>A task that completes after unattend.xml is written.</returns>
    Task ConfigureOfflineComputerNameAsync(
        string windowsPartitionRoot,
        string computerName,
        string processorArchitecture,
        string? defaultTimeZoneId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes Windows OOBE unattend and policy settings into an offline Windows installation.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="settings">The OOBE customization settings generated from the Foundry configuration.</param>
    /// <param name="processorArchitecture">Processor architecture used by unattend components.</param>
    /// <param name="workingDirectory">Directory used for temporary command output.</param>
    /// <param name="workspaceRootPath">Deployment workspace root containing encrypted secret material.</param>
    /// <param name="cancellationToken">Token that cancels OOBE configuration.</param>
    /// <returns>A task that completes after OOBE settings are written.</returns>
    Task ConfigureOfflineOobeAsync(
        string windowsPartitionRoot,
        DeployOobeSettings settings,
        string processorArchitecture,
        string workingDirectory,
        string workspaceRootPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes Windows AI component policy settings into an offline Windows installation.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="settings">The AI component removal settings generated from the Foundry configuration.</param>
    /// <param name="workingDirectory">Directory used for temporary command output.</param>
    /// <param name="cancellationToken">Token that cancels AI policy configuration.</param>
    /// <returns>A task that completes after selected AI policy settings are written.</returns>
    Task ConfigureOfflineAiComponentRemovalAsync(
        string windowsPartitionRoot,
        DeployAiComponentRemovalSettings settings,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies Windows optional feature changes to the offline Windows installation.
    /// </summary>
    /// <param name="setupMediaImagePath">Path to the setup-media ESD.</param>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="appliedImageIndex">Index of the Windows image applied from the setup media.</param>
    /// <param name="settings">Optional feature actions generated from the Foundry configuration.</param>
    /// <param name="scratchDirectory">DISM scratch directory.</param>
    /// <param name="sourceExtractionDirectory">Directory used to extract setup-media sources.</param>
    /// <param name="workingDirectory">Directory used for temporary command output.</param>
    /// <param name="cancellationToken">Token that cancels optional feature servicing.</param>
    /// <param name="progress">Optional progress sink for DISM percentage updates.</param>
    /// <param name="onInspectionStarted">Optional callback invoked before feature-state inspection.</param>
    /// <param name="onSourcePreparationStarted">Optional callback invoked before setup-media extraction.</param>
    /// <param name="onServicingStarted">Optional callback invoked before feature servicing.</param>
    /// <returns>The optional feature servicing result.</returns>
    Task<WindowsOptionalFeatureServicingResult> ConfigureOfflineWindowsOptionalFeaturesAsync(
        string setupMediaImagePath,
        string windowsPartitionRoot,
        int appliedImageIndex,
        DeployWindowsOptionalFeatureSettings settings,
        string scratchDirectory,
        string sourceExtractionDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        Action? onInspectionStarted = null,
        Action? onSourcePreparationStarted = null,
        Action? onServicingStarted = null);

    /// <summary>
    /// Copies and configures Windows RE on the recovery partition.
    /// </summary>
    /// <remarks>Requires winre.wim in the applied image and winrecfg.exe in the boot environment.</remarks>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="recoveryPartitionRoot">Root path of the recovery partition.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels recovery configuration.</param>
    /// <returns>A task that completes after WinRE is configured.</returns>
    Task ConfigureRecoveryEnvironmentAsync(
        string windowsPartitionRoot,
        string recoveryPartitionRoot,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the recovery partition drive letter after recovery setup is complete.
    /// </summary>
    /// <param name="recoveryPartitionRoot">Root path of the recovery partition.</param>
    /// <param name="recoveryPartitionLetter">Drive letter assigned to the recovery partition.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels recovery sealing.</param>
    /// <returns>A task that completes after the recovery partition is sealed.</returns>
    Task SealRecoveryPartitionAsync(
        string recoveryPartitionRoot,
        char recoveryPartitionLetter,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Injects INF drivers into the offline Windows image.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="driverRoot">Root directory containing extracted INF drivers.</param>
    /// <param name="scratchDirectory">DISM scratch directory used during driver injection.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels driver injection.</param>
    /// <param name="progress">Optional progress sink for DISM percentage updates.</param>
    /// <returns>A task that completes after offline drivers are applied.</returns>
    Task ApplyOfflineDriversAsync(
        string windowsPartitionRoot,
        string driverRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null);

    /// <summary>
    /// Mounts WinRE, injects INF drivers, and unmounts the image with commit or discard semantics.
    /// </summary>
    /// <param name="recoveryPartitionRoot">Root path of the recovery partition.</param>
    /// <param name="driverRoot">Root directory containing extracted INF drivers.</param>
    /// <param name="scratchDirectory">DISM scratch directory used during mount, injection, and unmount.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels recovery driver injection.</param>
    /// <param name="mountProgress">Optional progress sink for WinRE mount updates.</param>
    /// <param name="applyProgress">Optional progress sink for driver injection updates.</param>
    /// <param name="unmountProgress">Optional progress sink for WinRE unmount updates.</param>
    /// <param name="onMountStarted">Optional callback invoked before mounting WinRE.</param>
    /// <param name="onApplyStarted">Optional callback invoked before applying drivers.</param>
    /// <param name="onUnmountStarted">Optional callback invoked before unmounting WinRE.</param>
    /// <returns>A task that completes after recovery drivers are applied and WinRE is unmounted.</returns>
    Task ApplyRecoveryDriversAsync(
        string recoveryPartitionRoot,
        string driverRoot,
        string scratchDirectory,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        IProgress<double>? mountProgress = null,
        IProgress<double>? applyProgress = null,
        IProgress<double>? unmountProgress = null,
        Action? onMountStarted = null,
        Action? onApplyStarted = null,
        Action? onUnmountStarted = null);

    /// <summary>
    /// Creates UEFI boot files for the applied Windows installation.
    /// </summary>
    /// <param name="windowsPartitionRoot">Root path of the target Windows partition.</param>
    /// <param name="systemPartitionRoot">Root path of the EFI system partition.</param>
    /// <param name="operatingSystemBuildMajor">Major Windows build used to choose boot configuration behavior.</param>
    /// <param name="workingDirectory">Directory used for temporary scripts and command output.</param>
    /// <param name="cancellationToken">Token that cancels boot configuration.</param>
    /// <returns>A task that completes after boot files are configured.</returns>
    Task ConfigureBootAsync(
        string windowsPartitionRoot,
        string systemPartitionRoot,
        int operatingSystemBuildMajor,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
