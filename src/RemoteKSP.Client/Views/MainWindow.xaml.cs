using System;
using System.Windows;
using System.Linq;
using RemoteKSP.Client.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        Wpf.Ui.Abstractions.INavigationViewPageProvider pageProvider)
    {
        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();

        contentDialogService.SetDialogHost(RootContentDialogHost);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);

        RootNavigation.SetPageProviderService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);

        // Subscribe to navigation events to update page title
        RootNavigation.Navigated += OnNavigated;

        // Navigate after page provider is set
        Loaded += (s, e) => RootNavigation.Navigate(typeof(Pages.OverviewPage));
    }

    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        // Find the NavigationViewItem that matches the current page
        // 页面声明的标题行按钮（例如概览的「运行诊断」）；没有声明的页面清空
        PageActionsHost.Content = RemoteKSP.Shared.Ui.PageHeader.TakeActions(args.Page);

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
