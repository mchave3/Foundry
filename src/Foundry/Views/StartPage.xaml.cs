// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using Foundry.Core.Services.Configuration;
using Foundry.Services.Localization;

namespace Foundry.Views;

public sealed partial class StartPage : Page
{
    private readonly IApplicationLocalizationService localizationService;

    public StartMediaViewModel ViewModel { get; }

    public StartPage()
    {
        localizationService = App.GetService<IApplicationLocalizationService>();
        ViewModel = App.GetService<StartMediaViewModel>();
        InitializeComponent();
        ApplyLocalizedText();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        localizationService.LanguageChanged += OnLanguageChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void ApplyLocalizedText()
    {
        GeneralConfigurationOverviewCard.Header = localizationService.GetString("Nav_GeneralConfigurationKey.Title");
        NetworkOverviewCard.Header = localizationService.GetString("Nav_NetworkSection.Title");
        AutopilotOverviewCard.Header = localizationService.GetString("Nav_WindowsAutopilotSection.Title");
        CustomizationOverviewCard.Header = localizationService.GetString("Nav_CustomizationSection.Title");

        IsoPathCard.Header = localizationService.GetString("StartMedia.IsoPath.Header");
        IsoPathCard.Description = localizationService.GetString("StartMedia.IsoPath.Description");
        BrowseIsoButton.Content = localizationService.GetString("Common.Browse");

        UsbTargetCard.Header = localizationService.GetString("StartMedia.UsbTarget.Header");
        RefreshUsbButton.Content = localizationService.GetString("Common.Refresh");
        UsbPartitionStyleCard.Header = localizationService.GetString("StartMedia.Field.PartitionStyle");
        UsbPartitionStyleCard.Description = localizationService.GetString("StartMedia.UsbLayout.PartitionStyle.Description");
        UsbFormatModeCard.Header = localizationService.GetString("StartMedia.Field.FormatMode");
        UsbFormatModeCard.Description = localizationService.GetString("StartMedia.UsbLayout.FormatMode.Description");

        FinalCommandsCard.Header = localizationService.GetString("StartMedia.FinalCommands.Header");
        FinalCommandsCard.Description = localizationService.GetString("StartMedia.FinalCommands.Description");
        CreateIsoButton.Content = localizationService.GetString("StartMedia.CreateIsoButton");
        ApplyUsbActionButtonState();
    }

    private void ApplyUsbActionButtonState()
    {
        CreateUsbButton.Content = localizationService.GetString(ViewModel.IsSelectedUsbFoundryMedia
            ? "StartMedia.UpdateUsbButton"
            : "StartMedia.CreateUsbButton");
        if (ViewModel.IsSelectedUsbFoundryMedia)
        {
            CreateUsbButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
            return;
        }

        CreateUsbButton.ClearValue(Control.StyleProperty);
    }

    private void ReadinessActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ConfigurationNavigationTarget navigationTarget })
        {
            return;
        }

        switch (navigationTarget)
        {
            case ConfigurationNavigationTarget.General:
                App.Current.NavigationService.NavigateTo(typeof(GeneralConfigurationPage));
                break;
            case ConfigurationNavigationTarget.EthernetDot1x:
                App.Current.NavigationService.NavigateTo(typeof(EthernetDot1xPage));
                break;
            case ConfigurationNavigationTarget.Wifi:
                App.Current.NavigationService.NavigateTo(typeof(WifiPage));
                break;
            case ConfigurationNavigationTarget.AutopilotJsonProfile:
                App.Current.NavigationService.NavigateTo(typeof(AutopilotJsonProfilePage));
                break;
            case ConfigurationNavigationTarget.AutopilotHardwareHashUpload:
                App.Current.NavigationService.NavigateTo(typeof(AutopilotZeroTouchPage));
                break;
            case ConfigurationNavigationTarget.AutopilotInteractiveHardwareHashUpload:
                App.Current.NavigationService.NavigateTo(typeof(AutopilotInteractiveHashUploadPage));
                break;
            case ConfigurationNavigationTarget.OperatingSystemSelection:
                App.Current.NavigationService.NavigateTo(typeof(OsSelectionPage));
                break;
            case ConfigurationNavigationTarget.MachineNaming:
                App.Current.NavigationService.NavigateTo(typeof(MachineNamingPage));
                break;
            case ConfigurationNavigationTarget.Unattend:
                App.Current.NavigationService.NavigateTo(typeof(UnattendPage));
                break;
            case ConfigurationNavigationTarget.Oobe:
                App.Current.NavigationService.NavigateTo(typeof(OobePage));
                break;
            case ConfigurationNavigationTarget.WindowsOptionalFeatures:
                App.Current.NavigationService.NavigateTo(typeof(OptionalFeaturesPage));
                break;
            case ConfigurationNavigationTarget.AppxRemoval:
                App.Current.NavigationService.NavigateTo(typeof(AppRemovalPage));
                break;
            case ConfigurationNavigationTarget.AiComponentRemoval:
                App.Current.NavigationService.NavigateTo(typeof(AiComponentsPage));
                break;
        }
    }

    private void OnLanguageChanged(object? sender, ApplicationLanguageChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyLocalizedText();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(ApplyLocalizedText);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StartMediaViewModel.IsSelectedUsbFoundryMedia))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyUsbActionButtonState();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(ApplyUsbActionButtonState);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
        localizationService.LanguageChanged -= OnLanguageChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }
}
