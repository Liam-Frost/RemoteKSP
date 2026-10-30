using System;
using System.Windows;
using System.Linq;
using RemoteKSP.Server.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IContentDialogService contentDialogService,
        Wpf.Ui.Abstractions.INavigationViewPageProvider pageProvider)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        contentDialogService.SetDialogHost(RootContentDialogHost);

        RootNavigation.SetPageProviderService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);

        // Subscribe to navigation events to update page title
        RootNavigation.Navigated += OnNavigated;

        // Navigate after page provider is set
        Loaded += (s, e) =>
        {
            RootNavigation.Navigate(App.StartPage == Services.UrlScheme.StartPage.PairingRequests
                ? typeof(Pages.PairingRequestsPage)
                : typeof(Pages.OverviewPage));
            // 启动配对请求监控
            ViewModel.StartPairingRequestMonitoring();
        };

        // 窗口关闭时停止监控
        Closed += (s, e) => ViewModel.StopPairingRequestMonitoring();
    }

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        // 页面声明的标题行按钮（例如概览的「刷新状态」）；没有声明的页面清空
        PageActionsHost.Content = RemoteKSP.Shared.Ui.PageHeader.TakeActions(args.Page);

        // Find the NavigationViewItem that matches the current page
        var currentPage = args.Page?.GetType();
        if (currentPage == null) return;

        var menuItem = ViewModel.MenuItems
            .Concat(ViewModel.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => item.TargetPageType == currentPage);

        if (menuItem?.Content is string title)
        {
            ViewModel.CurrentPageTitle = title;
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
    }
}
