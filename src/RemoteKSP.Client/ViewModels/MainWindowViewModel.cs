using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _applicationTitle = "Remote KSP Client";

    [ObservableProperty]
    private string _currentPageTitle = Strings.Nav_Overview;

    [ObservableProperty]
    private ObservableCollection<object> _menuItems = [];

    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems = [];

    public MainWindowViewModel()
    {
        InitializeMenuItems();
    }

    private void InitializeMenuItems()
    {
        MenuItems =
        [
            new NavigationViewItem
            {
                Content = Strings.Nav_Overview,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.OverviewPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_Servers,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Server24 },
                TargetPageType = typeof(Views.Pages.ServersPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_Certificates,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Certificate24 },
                TargetPageType = typeof(Views.Pages.CertificatesPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_Diagnostics,
                Icon = new SymbolIcon { Symbol = SymbolRegular.CheckmarkStarburst24 },
                TargetPageType = typeof(Views.Pages.DiagnosticsPage)
            }
        ];

        FooterMenuItems =
        [
            new NavigationViewItem
            {
                Content = Strings.Nav_Settings,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage)
            }
        ];
    }
}
