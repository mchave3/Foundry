// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Core.Models.Configuration;
using Foundry.Core.Models.Configuration.Deploy;
using Foundry.Utilities.Globalization;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Normalizes administrator-authored OS catalog selection policy before it is persisted or emitted to deploy media.
/// </summary>
public static class OperatingSystemSelectionSettingsNormalizer
{
    /// <summary>
    /// Normalizes a user-facing OS selection policy and forces singleton allowed lists as their defaults.
    /// </summary>
    /// <param name="settings">The user-facing OS selection policy.</param>
    /// <returns>A normalized policy that is safe to persist.</returns>
    public static OperatingSystemSelectionSettings Normalize(OperatingSystemSelectionSettings? settings)
    {
        settings ??= new OperatingSystemSelectionSettings();

        string[] allowedLanguages = CanonicalizeLanguageCodes(settings.AllowedLanguageCodes);
        string? defaultLanguage = NormalizeDefault(
            CanonicalizeOptionalLanguageCode(settings.DefaultLanguageCode),
            allowedLanguages);
        string[] allowedReleaseIds = CanonicalizeKnownValues(
            settings.AllowedReleaseIds,
            OperatingSystemSelectionCatalog.SupportedReleaseIds,
            static value => value.Trim());
        string? defaultReleaseId = NormalizeDefault(
            CanonicalizeKnownValue(
                settings.DefaultReleaseId,
                OperatingSystemSelectionCatalog.SupportedReleaseIds,
                static value => value.Trim()),
            allowedReleaseIds);
        string[] configuredLicenseChannels = CanonicalizeKnownValues(
            settings.AllowedLicenseChannels,
            OperatingSystemSelectionCatalog.SupportedLicenseChannels,
            NormalizeLicenseChannel);
        string[] allowedEditions = CanonicalizeKnownValues(
            settings.AllowedEditions,
            OperatingSystemSelectionCatalog.SupportedEditions,
            static value => value.Trim());
        string? defaultEdition = NormalizeDefault(
            CanonicalizeKnownValue(
                settings.DefaultEdition,
                OperatingSystemSelectionCatalog.SupportedEditions,
                static value => value.Trim()),
            allowedEditions);
        string[] allowedLicenseChannels = EnsureCompatibleLicenseChannels(configuredLicenseChannels, allowedEditions);
        defaultEdition = EnsureCompatibleDefaultEdition(defaultEdition, allowedEditions, allowedLicenseChannels);
        string? defaultLicenseChannel = ResolveDefaultLicenseChannel(
            settings.DefaultLicenseChannel,
            allowedLicenseChannels,
            defaultEdition);

        return new OperatingSystemSelectionSettings
        {
            IsEnabled = settings.IsEnabled,
            AllowedLanguageCodes = allowedLanguages,
            DefaultLanguageCode = defaultLanguage,
            AllowedReleaseIds = allowedReleaseIds,
            DefaultReleaseId = defaultReleaseId,
            DefaultMediaOffset = Math.Clamp(settings.DefaultMediaOffset, 0, 11),
            AllowedLicenseChannels = allowedLicenseChannels,
            DefaultLicenseChannel = defaultLicenseChannel,
            AllowedEditions = allowedEditions,
            DefaultEdition = defaultEdition
        };
    }

    /// <summary>
    /// Converts a normalized user-facing OS selection policy into the deploy runtime contract.
    /// </summary>
    /// <param name="settings">The user-facing OS selection policy.</param>
    /// <returns>The deploy runtime OS selection policy.</returns>
    public static DeployOperatingSystemSelectionSettings ToDeploySettings(OperatingSystemSelectionSettings settings)
    {
        OperatingSystemSelectionSettings normalized = Normalize(settings);
        if (!normalized.IsEnabled)
        {
            return new DeployOperatingSystemSelectionSettings();
        }

        return new DeployOperatingSystemSelectionSettings
        {
            IsEnabled = true,
            AllowedLanguageCodes = normalized.AllowedLanguageCodes,
            DefaultLanguageCode = normalized.DefaultLanguageCode,
            AllowedReleaseIds = normalized.AllowedReleaseIds,
            DefaultReleaseId = normalized.DefaultReleaseId,
            DefaultMediaOffset = normalized.DefaultMediaOffset,
            AllowedLicenseChannels = normalized.AllowedLicenseChannels,
            DefaultLicenseChannel = normalized.DefaultLicenseChannel,
            AllowedEditions = normalized.AllowedEditions,
            DefaultEdition = normalized.DefaultEdition
        };
    }

    /// <summary>
    /// Normalizes a license channel token or known English label into its invariant catalog token.
    /// </summary>
    /// <param name="value">The license channel token or label.</param>
    /// <returns>The invariant license channel token, or an empty string when the value is blank.</returns>
    public static string NormalizeLicenseChannel(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized switch
        {
            "RETAIL" => "RET",
            "VOLUME" => "VOL",
            _ => normalized
        };
    }

    private static string[] EnsureCompatibleLicenseChannels(
        IReadOnlyList<string> configuredChannels,
        IReadOnlyList<string> allowedEditions)
    {
        if (configuredChannels.Count == 0 || allowedEditions.Count == 0)
        {
            return configuredChannels.ToArray();
        }

        IReadOnlyList<string> compatibleChannels = WindowsEditionCatalog.GetCompatibleLicenseChannels(allowedEditions);
        IReadOnlyList<string> requiredChannels = WindowsEditionCatalog.GetRequiredLicenseChannels(allowedEditions);

        return OperatingSystemSelectionCatalog.SupportedLicenseChannels
            .Where(channel =>
                requiredChannels.Contains(channel) ||
                (compatibleChannels.Contains(channel) && configuredChannels.Contains(channel, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string? ResolveDefaultLicenseChannel(
        string? configuredDefault,
        IReadOnlyList<string> allowedChannels,
        string? defaultEdition)
    {
        string? canonicalDefault = CanonicalizeKnownValue(
            configuredDefault,
            OperatingSystemSelectionCatalog.SupportedLicenseChannels,
            NormalizeLicenseChannel);
        WindowsEditionDefinition? edition = WindowsEditionCatalog.Find(defaultEdition);

        if (edition is not null)
        {
            string[] compatibleAllowedChannels = edition.LicenseChannels
                .Where(channel => allowedChannels.Count == 0 || allowedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (canonicalDefault is not null && compatibleAllowedChannels.Contains(canonicalDefault, StringComparer.OrdinalIgnoreCase))
            {
                return canonicalDefault;
            }

            return compatibleAllowedChannels.FirstOrDefault() ?? edition.LicenseChannels[0];
        }

        return NormalizeDefault(canonicalDefault, allowedChannels);
    }

    private static string? EnsureCompatibleDefaultEdition(
        string? defaultEdition,
        IReadOnlyList<string> allowedEditions,
        IReadOnlyList<string> allowedChannels)
    {
        if (defaultEdition is null || allowedEditions.Count > 0 || allowedChannels.Count == 0)
        {
            return defaultEdition;
        }

        WindowsEditionDefinition? definition = WindowsEditionCatalog.Find(defaultEdition);
        return definition is not null &&
               definition.LicenseChannels.Any(channel => allowedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase))
            ? defaultEdition
            : null;
    }

    private static string[] CanonicalizeLanguageCodes(IEnumerable<string> languageCodes)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string languageCode in languageCodes ?? [])
        {
            string canonicalCode = CultureCode.Canonicalize(languageCode);
            if (string.IsNullOrWhiteSpace(canonicalCode) ||
                !seen.Add(CultureCode.NormalizeForComparison(canonicalCode)))
            {
                continue;
            }

            result.Add(canonicalCode);
        }

        return result.ToArray();
    }

    private static string? CanonicalizeOptionalLanguageCode(string? languageCode)
    {
        string canonicalCode = CultureCode.Canonicalize(languageCode);
        return string.IsNullOrWhiteSpace(canonicalCode) ? null : canonicalCode;
    }

    private static string[] CanonicalizeKnownValues(
        IEnumerable<string> values,
        IEnumerable<string> supportedValues,
        Func<string, string> normalize)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string value in values ?? [])
        {
            string? canonical = CanonicalizeKnownValue(value, supportedValues, normalize);
            if (!string.IsNullOrWhiteSpace(canonical) && seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        return result.ToArray();
    }

    private static string? CanonicalizeKnownValue(
        string? value,
        IEnumerable<string> supportedValues,
        Func<string, string> normalize)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = normalize(value);
        return supportedValues.FirstOrDefault(supported =>
            string.Equals(supported, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeDefault(string? defaultValue, IReadOnlyList<string> allowedValues)
    {
        if (allowedValues.Count == 1)
        {
            return allowedValues[0];
        }

        if (string.IsNullOrWhiteSpace(defaultValue))
        {
            return null;
        }

        return allowedValues.Count == 0 ||
               allowedValues.Contains(defaultValue, StringComparer.OrdinalIgnoreCase)
            ? defaultValue
            : null;
    }
}
