// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using Foundry.Deploy.Models.Configuration;

namespace Foundry.Deploy.ViewModels;

internal static class WindowsCustomizationSummaryBuilder
{
    public static IReadOnlyList<DeploymentSummaryRowViewModel> Build(
        DeployOobeSettings oobe,
        DeployAppxRemovalSettings appxRemoval,
        DeployAiComponentRemovalSettings aiComponentRemoval,
        DeployWindowsOptionalFeatureSettings optionalFeatures,
        Func<string, string> getString,
        CultureInfo culture,
        bool usesCustomUnattend = false)
    {
        var rows = new List<DeploymentSummaryRowViewModel>();

        if (usesCustomUnattend)
        {
            rows.Add(new(getString("Summary.Oobe"), getString("Unattend.Managed")));
        }

        if (!usesCustomUnattend && oobe.IsEnabled)
        {
            AddSection(rows, getString("Summary.Oobe"));
            rows.Add(new(getString("Summary.DiagnosticData"), GetDiagnosticDataText(oobe.DiagnosticDataLevel, getString)));
            rows.Add(new(getString("Summary.LocationAccess"), GetLocationAccessText(oobe.LocationAccess, getString)));
            rows.Add(new(getString("Summary.BuiltInAdministrator"), GetEnabledText(oobe.EnableAdministratorAccount, getString)));
            rows.Add(new(getString("Summary.AdditionalLocalAccounts"), oobe.AdditionalAccounts.Count.ToString(culture)));
            rows.Add(new(getString("Summary.SkipAccountCreation"), GetYesNoText(oobe.AdditionalAccounts.Count > 0, getString)));
            rows.Add(new(getString("Summary.SkipLicenseTerms"), GetYesNoText(oobe.SkipLicenseTerms, getString)));
            rows.Add(new(getString("Summary.HidePrivacySetup"), GetYesNoText(oobe.HidePrivacySetup, getString)));
            rows.Add(new(getString("Summary.TailoredExperiences"), GetEnabledText(oobe.AllowTailoredExperiences, getString)));
            rows.Add(new(getString("Summary.AdvertisingId"), GetEnabledText(oobe.AllowAdvertisingId, getString)));
            rows.Add(new(getString("Summary.OnlineSpeechRecognition"), GetEnabledText(oobe.AllowOnlineSpeechRecognition, getString)));
            rows.Add(new(getString("Summary.InkingAndTypingDiagnostics"), GetEnabledText(oobe.AllowInkingAndTypingDiagnostics, getString)));
        }

        if (appxRemoval.IsEnabled || aiComponentRemoval.IsEnabled)
        {
            AddSection(rows, getString("Summary.ApplicationAndAiRemoval"));
            if (appxRemoval.IsEnabled)
            {
                rows.Add(new(getString("Summary.AppxRemoval"), appxRemoval.PackageNames.Count.ToString(culture)));
            }

            if (aiComponentRemoval.IsEnabled)
            {
                rows.Add(new(
                    getString("Summary.AiComponentRemoval"),
                    CountSelectedAiRemovalActions(aiComponentRemoval).ToString(culture)));
            }
        }

        if (optionalFeatures.IsEnabled)
        {
            DeployWindowsOptionalFeatureAction[] actions = optionalFeatures.Actions?.ToArray() ?? [];
            int enabledCount = actions.Count(action => action.Enable);

            AddSection(rows, getString("Summary.WindowsOptionalFeatures"));
            rows.Add(new(getString("Summary.TotalConfigured"), actions.Length.ToString(culture)));
            rows.Add(new(getString("Summary.FeaturesEnabled"), enabledCount.ToString(culture)));
            rows.Add(new(getString("Summary.FeaturesDisabled"), (actions.Length - enabledCount).ToString(culture)));
        }

        return rows.Count > 0
            ? rows
            : [new(getString("Summary.Status"), getString("Summary.Status.NoChanges"))];
    }

    private static void AddSection(ICollection<DeploymentSummaryRowViewModel> rows, string title)
    {
        if (rows.Count > 0)
        {
            rows.Add(DeploymentSummaryRowViewModel.Separator());
        }

        rows.Add(DeploymentSummaryRowViewModel.Section(title));
    }

    private static string GetDiagnosticDataText(
        DeployOobeDiagnosticDataLevel level,
        Func<string, string> getString)
    {
        return getString(level switch
        {
            DeployOobeDiagnosticDataLevel.Optional => "Summary.DiagnosticData.Optional",
            DeployOobeDiagnosticDataLevel.Off => "Summary.DiagnosticData.Off",
            _ => "Summary.DiagnosticData.Required"
        });
    }

    private static string GetLocationAccessText(
        DeployOobeLocationAccessMode mode,
        Func<string, string> getString)
    {
        return getString(mode == DeployOobeLocationAccessMode.ForceOff
            ? "Summary.LocationAccess.ForceOff"
            : "Summary.LocationAccess.UserControlled");
    }

    private static string GetYesNoText(bool value, Func<string, string> getString)
    {
        return getString(value ? "Common.Yes" : "Common.No");
    }

    private static string GetEnabledText(bool enabled, Func<string, string> getString)
    {
        return getString(enabled ? "Common.Enabled" : "Common.Disabled");
    }

    private static int CountSelectedAiRemovalActions(DeployAiComponentRemovalSettings settings)
    {
        return new[]
        {
            settings.RemoveCopilot,
            settings.RemoveAiHub,
            settings.DisableRecall,
            settings.DisableClickToDo,
            settings.DisableAiServiceAutoStart,
            settings.DisableEdgeAi,
            settings.DisablePaintAi,
            settings.DisableNotepadAi
        }.Count(selected => selected);
    }
}
