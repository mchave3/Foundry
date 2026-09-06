// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Reflection;

namespace Foundry;

/// <summary>
/// Provides product metadata and public project links used by the shell and about dialogs.
/// </summary>
public static class FoundryApplicationInfo
{
    public const string AppName = Constants.ApplicationDisplayName;

    public static string VersionWithPrefix => $"v{Version}";

    public static string AppNameAndVersion => $"{AppName} {VersionWithPrefix}";

    public static string ExecutablePath { get; } = Environment.ProcessPath
        ?? throw new InvalidOperationException("The Foundry executable path is unavailable.");

    /// <summary>
    /// Gets the documentation entry URL.
    /// </summary>
    public const string DocumentationUrl = "https://docs.foundryosd.com";

    /// <summary>
    /// Gets the ADK documentation URL.
    /// </summary>
    public const string AdkDocumentationUrl = DocumentationUrl + "/foundry-osd/adk";

    /// <summary>
    /// Gets the Autopilot JSON profile documentation URL.
    /// </summary>
    public const string AutopilotJsonProfileDocumentationUrl = DocumentationUrl + "/foundry-osd/autopilot/json-profile";

    /// <summary>
    /// Gets the zero-touch Autopilot hardware hash upload documentation URL.
    /// </summary>
    public const string AutopilotZeroTouchDocumentationUrl = DocumentationUrl + "/foundry-osd/autopilot/zero-touch-hardware-hash";

    /// <summary>
    /// Gets the interactive Autopilot hardware hash upload documentation URL.
    /// </summary>
    public const string AutopilotInteractiveDocumentationUrl = DocumentationUrl + "/foundry-osd/autopilot/interactive-hardware-hash";

    /// <summary>
    /// Gets the general configuration documentation URL.
    /// </summary>
    public const string GeneralConfigurationDocumentationUrl = DocumentationUrl + "/foundry-osd/general";

    /// <summary>
    /// Gets the Wi-Fi configuration documentation URL.
    /// </summary>
    public const string WifiDocumentationUrl = DocumentationUrl + "/foundry-osd/network/wifi";

    /// <summary>
    /// Gets the Ethernet 802.1X configuration documentation URL.
    /// </summary>
    public const string EthernetDot1xDocumentationUrl = DocumentationUrl + "/foundry-osd/network/ethernet-802.1x";

    /// <summary>
    /// Gets the operating system customization documentation URL.
    /// </summary>
    public const string OperatingSystemDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/operating-system";

    /// <summary>
    /// Gets the machine naming documentation URL.
    /// </summary>
    public const string MachineNamingDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/machine-naming";

    /// <summary>
    /// Gets the out-of-box experience documentation URL.
    /// </summary>
    public const string OobeDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/oobe";

    /// <summary>
    /// Gets the custom Windows answer-file documentation URL.
    /// </summary>
    public const string UnattendDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/unattend";

    /// <summary>
    /// Gets the Windows optional features documentation URL.
    /// </summary>
    public const string OptionalFeaturesDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/optional-features";

    /// <summary>
    /// Gets the app removal documentation URL.
    /// </summary>
    public const string AppRemovalDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/appx-removals";

    /// <summary>
    /// Gets the AI components documentation URL.
    /// </summary>
    public const string AiComponentsDocumentationUrl = DocumentationUrl + "/foundry-osd/customization/ai-components";

    /// <summary>
    /// Gets the media creation documentation URL.
    /// </summary>
    public const string StartDocumentationUrl = DocumentationUrl + "/foundry-osd/media";

    public const string RepositoryUrl = Constants.RepositoryUrl;

    /// <summary>
    /// Gets the GitHub contributors API endpoint.
    /// </summary>
    public const string ContributorsApiUrl = "https://api.github.com/repos/foundry-osd/foundry/contributors";

    /// <summary>
    /// Gets the issue tracker URL.
    /// </summary>
    public const string IssuesUrl = Constants.RepositoryUrl + "/issues";

    /// <summary>
    /// Gets the GitHub bug report form URL.
    /// </summary>
    public const string BugReportUrl = IssuesUrl + "/new?template=bug-report.yml";

    /// <summary>
    /// Gets the license document URL.
    /// </summary>
    public const string LicenseUrl = Constants.RepositoryUrl + "/blob/main/LICENSE";

    /// <summary>
    /// Gets the releases page URL.
    /// </summary>
    public const string ReleasesUrl = Constants.RepositoryUrl + "/releases";

    public const string LatestReleaseUrl = Constants.LatestReleaseUrl;

    /// <summary>
    /// Gets the support URL shown by user-facing dialogs.
    /// </summary>
    public const string SupportUrl = IssuesUrl;

    /// <summary>
    /// Gets the application version resolved from assembly metadata.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(FoundryApplicationInfo).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }
}
