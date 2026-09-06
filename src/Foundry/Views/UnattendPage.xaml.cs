// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

namespace Foundry.Views;

public sealed partial class UnattendPage : Page
{
    public UnattendConfigurationViewModel ViewModel { get; }

    public UnattendPage()
    {
        ViewModel = App.GetService<UnattendConfigurationViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshSourcesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        Loaded -= OnLoaded;
        ViewModel.Dispose();
    }
}
