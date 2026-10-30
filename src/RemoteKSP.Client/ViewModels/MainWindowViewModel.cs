using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _applicationTitle = "Remote KSP Client";

    [ObservableProperty]
    private string _currentPageTitle = "概览";

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
                Content = "概览",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.OverviewPage)
            },
            new NavigationViewItem
            {
                Content = "服务端管理",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Server24 },
                TargetPageType = typeof(Views.Pages.ServersPage)
            },
            new NavigationViewItem
            {
                Content = "证书管理",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Certificate24 },
                TargetPageType = typeof(Views.Pages.CertificatesPage)
            },
            new NavigationViewItem
            {
                Content = "连接诊断",
                Icon = new SymbolIcon { Symbol = SymbolRegular.CheckmarkStarburst24 },
                TargetPageType = typeof(Views.Pages.DiagnosticsPage)
            }
        ];

        FooterMenuItems =
        [
            new NavigationViewItem
            {
                Content = "设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage)
            }
        ];
    }
}
