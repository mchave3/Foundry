// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Resolves the Start overview state associated with a configuration navigation target.
/// </summary>
public static class ConfigurationOverviewNavigationEvaluator
{
    private static readonly ConfigurationOverviewItem[] GeneralItems =
    [
        ConfigurationOverviewItem.Architecture,
        ConfigurationOverviewItem.SecureBoot,
        ConfigurationOverviewItem.WinPeLanguage,
        ConfigurationOverviewItem.TimeZone,
        ConfigurationOverviewItem.DeploymentCompletion,
        ConfigurationOverviewItem.DeploymentProtection,
        ConfigurationOverviewItem.DriverOptions
    ];

    /// <summary>
    /// Gets the overview state represented by a navigation target.
    /// </summary>
    /// <param name="evaluation">Current configuration overview evaluation.</param>
    /// <param name="target">Navigation target to resolve.</param>
    /// <returns>The state that should be exposed by the corresponding navigation item.</returns>
    public static ConfigurationOverviewState EvaluateTarget(
        ConfigurationOverviewEvaluation evaluation,
        ConfigurationNavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        return target switch
        {
            ConfigurationNavigationTarget.General => Aggregate(evaluation, GeneralItems),
            ConfigurationNavigationTarget.EthernetDot1x => evaluation[ConfigurationOverviewItem.EthernetDot1x],
            ConfigurationNavigationTarget.Wifi => evaluation[ConfigurationOverviewItem.Wifi],
            ConfigurationNavigationTarget.AutopilotJsonProfile => evaluation[ConfigurationOverviewItem.AutopilotJsonProfile],
            ConfigurationNavigationTarget.AutopilotHardwareHashUpload => evaluation[ConfigurationOverviewItem.AutopilotZeroTouch],
            ConfigurationNavigationTarget.AutopilotInteractiveHardwareHashUpload => evaluation[ConfigurationOverviewItem.AutopilotInteractive],
            ConfigurationNavigationTarget.OperatingSystemSelection => evaluation[ConfigurationOverviewItem.OperatingSystemSelection],
            ConfigurationNavigationTarget.Unattend => evaluation[ConfigurationOverviewItem.Unattend],
            ConfigurationNavigationTarget.MachineNaming => evaluation[ConfigurationOverviewItem.MachineNaming],
            ConfigurationNavigationTarget.Oobe => evaluation[ConfigurationOverviewItem.Oobe],
            ConfigurationNavigationTarget.WindowsOptionalFeatures => evaluation[ConfigurationOverviewItem.OptionalFeatures],
            ConfigurationNavigationTarget.AppxRemoval => evaluation[ConfigurationOverviewItem.AppxRemoval],
            ConfigurationNavigationTarget.AiComponentRemoval => evaluation[ConfigurationOverviewItem.AiComponents],
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "The navigation target has no configuration overview state.")
        };
    }

    private static ConfigurationOverviewState Aggregate(
        ConfigurationOverviewEvaluation evaluation,
        IEnumerable<ConfigurationOverviewItem> items)
    {
        ConfigurationOverviewState[] states = items.Select(item => evaluation[item]).ToArray();
        if (states.Contains(ConfigurationOverviewState.NeedsAttention))
        {
            return ConfigurationOverviewState.NeedsAttention;
        }

        if (states.Contains(ConfigurationOverviewState.Configured))
        {
            return ConfigurationOverviewState.Configured;
        }

        return states.Contains(ConfigurationOverviewState.Default)
            ? ConfigurationOverviewState.Default
            : states[0];
    }
}
