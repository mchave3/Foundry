<div align="center">

<h1>
  <img src="Assets/GitHub/readme-logo.png" alt="Foundry OSD">
</h1>

<p>
  <a href="https://github.com/foundry-osd/foundry/releases/latest"><img src="https://img.shields.io/github/v/release/foundry-osd/foundry?display_name=tag&sort=semver&style=flat&label=Latest%20Release&labelColor=24292F&color=2563EB" alt="Latest release"></a>
  <a href="https://github.com/foundry-osd/foundry/releases"><img src="https://img.shields.io/github/downloads/foundry-osd/foundry/total?style=flat&label=Downloads&labelColor=24292F&color=22A06B" alt="Total downloads"></a>
  <a href="https://docs.foundryosd.com/reference/supported-versions"><img src="https://img.shields.io/badge/OS%20Scope-Windows%2011%2023H2%20%7C%2024H2%20%7C%2025H2-2563EB?style=flat&logo=windows11&logoColor=white&labelColor=24292F" alt="Windows 11 23H2, 24H2, and 25H2"></a>
  <img src="https://img.shields.io/badge/Architecture-x64%20%7C%20ARM64-2563EB?style=flat&labelColor=24292F" alt="Architecture x64 and ARM64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/foundry-osd/foundry?style=flat&label=License&labelColor=24292F&color=2563EB" alt="MIT license"></a>
</p>

**Build deployment media. Connect in WinPE. Deploy Windows.**

Foundry is an open-source Windows deployment toolkit that helps IT administrators create bootable ISO or USB media and guides technicians from network readiness through a Windows 11 installation.

<p>
  <a href="https://github.com/foundry-osd/foundry/releases/latest/download/Foundry-win-x64.msi"><img src="https://img.shields.io/badge/Download-x64-0078D4?style=for-the-badge&logo=windows11&logoColor=white" alt="Download Foundry OSD for Windows x64"></a>
  &nbsp;
  <a href="https://github.com/foundry-osd/foundry/releases/latest/download/Foundry-win-arm64.msi"><img src="https://img.shields.io/badge/Download-ARM64-0078D4?style=for-the-badge&logo=windows11&logoColor=white" alt="Download Foundry OSD for Windows ARM64"></a>
</p>

<sub>Install x64 on most administrator workstations or ARM64 on Windows on Arm.</sub>

[Quick start](https://docs.foundryosd.com/start-here/quick-start) · [Documentation](https://docs.foundryosd.com) · [Release notes and asset digests](https://github.com/foundry-osd/foundry/releases/latest)

</div>

---

![Foundry workflow showing deployment media creation, WinPE network readiness, and Windows deployment](Assets/GitHub/foundry-workflow.png)

## From media creation to Windows installation

Foundry separates the deployment into three focused stages:

- 💿 **Create media with Foundry OSD** — configure the deployment and build bootable ISO or USB media from an administrator workstation.
- 🌐 **Connect with Foundry Connect** — start the target device in WinPE and validate Ethernet, Wi-Fi, or enterprise connectivity.
- 🪟 **Install with Foundry Deploy** — choose the Windows content, target disk, drivers, firmware, and provisioning options, then run the deployment.

Operating system, driver pack, firmware, and WinPE metadata come from the maintained [`foundry-osd/catalog`](https://github.com/foundry-osd/catalog).

Online catalog requests validate HTTPS certificates against Windows trust stores. Environments that inspect HTTPS traffic need their trusted corporate root certificates in the administrator workstation and WinPE image. Microsoft ESD content retains its supported HTTP delivery path, with SHA-256 verification against authenticated catalog metadata.

Cached payloads are rechecked before reuse. Downloads replace existing files only after validation, and downloaded driver installers require a valid signature from the expected publisher. A verified cached file can be reused from read-only media without reserving space for another download.

## What you can configure

- **Deployment content** — Windows release, language, edition, licensing channel, drivers, and optional firmware.
- **Windows setup** — localization, OOBE, privacy, optional features, AppX packages, and offline component choices.
- **Custom answer files** — [import and select an Unattend file](https://docs.foundryosd.com/foundry-osd/customization/unattend) to configure Windows beyond the options exposed in Foundry, within the supported setup passes.
- **Network readiness** — Ethernet, Wi-Fi, and enterprise 802.1X settings for WinPE.
- **Provisioning and protection** — Windows Autopilot options and optional protection for deployment media and embedded configuration.

## Get started

1. Install the MSI matching the architecture of your administrator workstation.
2. Open Foundry OSD and validate the Windows ADK and Windows PE prerequisites.
3. Configure the deployment, then create ISO or USB media.
4. Boot a representative test device and continue through Foundry Connect and Foundry Deploy.

[Follow the complete quick start →](https://docs.foundryosd.com/start-here/quick-start)

> [!NOTE]
> Foundry OSD runs on Windows 10 and Windows 11. It creates x64 or ARM64 deployment media using the Windows ADK and matching Windows PE add-on. Available Windows releases are catalog-driven; check [Supported versions](https://docs.foundryosd.com/reference/supported-versions) before deployment.

> [!IMPORTANT]
> Creating USB media erases the selected USB device. Deploying Windows can erase or repartition the selected target disk. Validate your configuration and test it on representative hardware before production use.

## Learn more

- [Configure networking](https://docs.foundryosd.com/foundry-osd/network)
- [Customize Windows](https://docs.foundryosd.com/foundry-osd/customization)
- [Prepare Windows Autopilot](https://docs.foundryosd.com/foundry-osd/autopilot)
- [Protect credentials and deployment media](https://docs.foundryosd.com/reference/security-and-credentials)
- [Troubleshoot a deployment](https://docs.foundryosd.com/troubleshooting)

## Contributing and support

Found a bug or have a feature request? Use the [issue chooser](https://github.com/foundry-osd/foundry/issues/new/choose). For setup questions and troubleshooting, follow the [support guide](SUPPORT.md).

Code contributions are welcome. Read the [contributing guide](CONTRIBUTING.md) before starting substantial work, and report vulnerabilities privately through the [security policy](SECURITY.md).

## Supported by

<p align="center">
  <a href="https://www.gitbook.com/">
    <img src="Assets/GitHub/Sponsors/gitbook.svg" alt="GitBook" height="56">
  </a>
  <br>
  Foundry documentation is hosted with support from <strong>GitBook</strong>.
</p>

---

Foundry is available under the [MIT License](LICENSE). Anonymous usage telemetry and remote error diagnostics are enabled by default and can be disabled independently in Settings. See [Telemetry and privacy](https://docs.foundryosd.com/reference/telemetry-and-privacy), [Third-Party Notices](THIRD_PARTY_NOTICES.md), and the [Code of Conduct](CODE_OF_CONDUCT.md).
