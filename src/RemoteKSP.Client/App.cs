using System;
using Microsoft.Extensions.DependencyInjection;
using RemoteKSP.Client.ViewModels;
using RemoteKSP.Client.Views;
using RemoteKSP.Client.Views.Pages;
using Wpf.Ui;

namespace RemoteKSP.Client;

public static class App
{
    private static ServiceProvider? _serviceProvider;

    public static IServiceProvider Services => _serviceProvider
        ?? throw new InvalidOperationException("Services not initialized");

    public static void InitializeServices()
    {
        var services = new ServiceCollection();

        // WPF-UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<Wpf.Ui.Abstractions.INavigationViewPageProvider>(sp =>
            new PageProvider(sp));
        services.AddSingleton<RemoteKSP.Shared.Services.DialogService>();
        services.AddSingleton<RemoteKSP.Shared.Services.NotificationService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<OverviewViewModel>(sp =>
        {
            var notificationService = sp.GetRequiredService<RemoteKSP.Shared.Services.NotificationService>();
            var dialogService = sp.GetRequiredService<RemoteKSP.Shared.Services.DialogService>();
            var navigationService = sp.GetRequiredService<INavigationService>();
            return new OverviewViewModel(notificationService, dialogService,
                page => navigationService.Navigate(page));
        });
        services.AddTransient<ServersViewModel>();
        services.AddTransient<CertificatesViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Pages
        services.AddTransient<OverviewPage>();
        services.AddTransient<ServersPage>();
        services.AddTransient<CertificatesPage>();
        services.AddTransient<DiagnosticsPage>();
        services.AddTransient<SettingsPage>();

        // Main window
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();
    }
}
