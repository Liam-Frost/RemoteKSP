using System;
using Microsoft.Extensions.DependencyInjection;
using RemoteKSP.Server.Services;
using RemoteKSP.Server.ViewModels;
using RemoteKSP.Server.Views;
using RemoteKSP.Server.Views.Pages;
using Wpf.Ui;

namespace RemoteKSP.Server;

public static class App
{
    private static ServiceProvider? _serviceProvider;

    public static IServiceProvider Services => _serviceProvider
        ?? throw new InvalidOperationException("Services not initialized");

    /// <summary>GUI 打开后先显示的页面（--open remoteksp-server:pairing）</summary>
    public static UrlScheme.StartPage StartPage { get; set; }

    public static void InitializeServices()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<ServiceManager>();
        services.AddSingleton<PipeClient>();
        services.AddSingleton<ActivityLogService>();


        // WPF-UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<Wpf.Ui.Abstractions.INavigationViewPageProvider>(sp =>
            new PageProvider(sp));
        services.AddSingleton<RemoteKSP.Shared.Services.DialogService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<OverviewViewModel>(sp =>
        {
            var navigationService = sp.GetRequiredService<INavigationService>();
            return new OverviewViewModel(sp.GetRequiredService<ServiceManager>(), sp.GetRequiredService<PipeClient>(),
                sp.GetRequiredService<RemoteKSP.Shared.Services.DialogService>(), page => navigationService.Navigate(page));
        });
        services.AddTransient<CertificatesViewModel>();
        services.AddTransient<PairingRequestsViewModel>();
        services.AddTransient<ClientsViewModel>();
        services.AddTransient<ActivityLogViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Pages
        services.AddTransient<OverviewPage>();
        services.AddTransient<CertificatesPage>();
        services.AddTransient<PairingRequestsPage>();
        services.AddTransient<ClientsPage>();
        services.AddTransient<ActivityLogPage>();
        services.AddTransient<SettingsPage>();

        // Main window
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
    }
}
