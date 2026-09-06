// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using Foundry.Views;

namespace Foundry.Services.Shell;

public enum NavigationSection
{
    General,
    Network,
    WindowsAutopilot,
    Customization
}

public sealed record NavigationRoute(
    string Id,
    Type PageType,
    string TitleResourceKey,
    string? DescriptionResourceKey = null,
    string? IconGlyph = null,
    NavigationSection? Section = null,
    Type? ParentPageType = null,
    bool IsAvailableWhenAdkBlocked = false);

public static class NavigationRouteCatalog
{
    public static IReadOnlyList<NavigationRoute> PrimaryRoutes { get; } =
    (NavigationRoute[])
    [
        CreatePrimary<HomeLandingPage>("Nav_HomeKey", "E80F", NavigationSection.General, true),
        CreatePrimary<AdkPage>("Nav_AdkKey", "EC7A", NavigationSection.General, true),
        CreatePrimary<GeneralConfigurationPage>("Nav_GeneralConfigurationKey", "E713", NavigationSection.General),
        CreatePrimary<StartPage>("Nav_StartKey", "E768", NavigationSection.General),
        CreatePrimary<EthernetDot1xPage>("Nav_EthernetDot1xKey", "E839", NavigationSection.Network),
        CreatePrimary<WifiPage>("Nav_WifiKey", "E701", NavigationSection.Network),
        CreatePrimary<AutopilotJsonProfilePage>("Nav_AutopilotJsonProfileKey", "E8A5", NavigationSection.WindowsAutopilot),
        CreatePrimary<AutopilotZeroTouchPage>("Nav_AutopilotZeroTouchKey", "E753", NavigationSection.WindowsAutopilot),
        CreatePrimary<AutopilotInteractiveHashUploadPage>("Nav_AutopilotInteractiveHashUploadKey", "E928", NavigationSection.WindowsAutopilot),
        CreatePrimary<OsSelectionPage>("Nav_OsSelectionKey", "EC77", NavigationSection.Customization),
        CreatePrimary<UnattendPage>("Nav_UnattendKey", "E8A5", NavigationSection.Customization),
        CreatePrimary<MachineNamingPage>("Nav_MachineNamingKey", "E8AC", NavigationSection.Customization),
        CreatePrimary<OobePage>("Nav_OobeKey", "F133", NavigationSection.Customization),
        CreatePrimary<OptionalFeaturesPage>("Nav_OptionalFeaturesKey", "E74C", NavigationSection.Customization),
        CreatePrimary<AppRemovalPage>("Nav_AppRemovalKey", "E7B8", NavigationSection.Customization),
        CreatePrimary<AiComponentsPage>("Nav_AiComponentsKey", "F4A5", NavigationSection.Customization)
    ];

    private static IReadOnlyList<NavigationRoute> Routes { get; } =
    (NavigationRoute[])
    [
        .. PrimaryRoutes,
        CreateSettings<SettingsPage>("SettingsPage.PageTitle", null),
        CreateSettings<GeneralSettingPage>("SettingsPage_GeneralCard.Header", typeof(SettingsPage)),
        CreateSettings<ProxySettingPage>("SettingsPage_ProxyCard.Header", typeof(SettingsPage)),
        CreateSettings<ThemeSettingPage>("SettingsPage_ThemeCard.Header", typeof(SettingsPage)),
        CreateSettings<AppUpdateSettingPage>("SettingsPage_UpdateCard.Header", typeof(SettingsPage))
    ];

    public static NavigationRoute? FindById(string id) =>
        Routes.FirstOrDefault(route => string.Equals(route.Id, id, StringComparison.Ordinal));

    public static NavigationRoute? FindByPageType(Type pageType) =>
        Routes.FirstOrDefault(route => route.PageType == pageType);

    public static string GetSectionTitleResourceKey(NavigationSection section) => section switch
    {
        NavigationSection.General => "Nav_GeneralSection.Title",
        NavigationSection.Network => "Nav_NetworkSection.Title",
        NavigationSection.WindowsAutopilot => "Nav_WindowsAutopilotSection.Title",
        NavigationSection.Customization => "Nav_CustomizationSection.Title",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };

    private static NavigationRoute CreatePrimary<TPage>(
        string resourcePrefix,
        string glyph,
        NavigationSection section,
        bool isAvailableWhenAdkBlocked = false) =>
        new(
            typeof(TPage).FullName!,
            typeof(TPage),
            $"{resourcePrefix}.Title",
            $"{resourcePrefix}.Description",
            glyph,
            section,
            IsAvailableWhenAdkBlocked: isAvailableWhenAdkBlocked);

    private static NavigationRoute CreateSettings<TPage>(
        string titleResourceKey,
        Type? parentPageType) =>
        new(
            typeof(TPage).FullName!,
            typeof(TPage),
            titleResourceKey,
            ParentPageType: parentPageType,
            IsAvailableWhenAdkBlocked: true);
}
