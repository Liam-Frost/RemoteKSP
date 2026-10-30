using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteKSP.Server;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        RemoteKSP.Shared.Localization.UiLanguage.Apply(RemoteKSP.Shared.Localization.UiLanguage.LoadPreference());

        // remoteksp-server: 链接（点击配对通知）。链接里的引号能注入额外参数（%1 加了引号也挡不住），
        // 所以这是独占模式：只打开 GUI，其他参数一律不看，不会进入安装、卸载或服务分支
        if (Services.UrlScheme.LinkActivation(args) is { } startPage)
        {
            App.StartPage = startPage;
            RunGui();
            return;
        }

        // 服务在用户会话里启动的通知进程：显示配对通知后立即退出，不打开 GUI
        if (ArgumentAfter(args, "--notify-pairing") is { } encoded)
        {
            if (Services.PairingToastContent.Decode(encoded) is { } content)
            {
                try { Services.PairingToast.Show(content); }
                catch (Exception) { /* 没有通知功能（如 Server Core）或被策略禁用：服务端 GUI 里仍能看到请求 */ }
            }
            return;
        }

        // Install service mode: install and exit
        if (args.Contains("--install-service"))
        {
            try
            {
                var mgr = new Services.ServiceManager();
                mgr.InstallAsync().GetAwaiter().GetResult();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(string.Format(Strings.Cli_InstallFailed, ex.Message));
                Environment.Exit(1);
            }
            return;
        }

        // Uninstall service mode: uninstall and exit
        if (args.Contains("--uninstall-service"))
        {
            try
            {
                // 命令行卸载默认保留数据目录；加 --remove-data 才删除（证书配置、PIN、客户端配对、TLS 证书）
                var mgr = new Services.ServiceManager();
                var result = mgr.UninstallAsync(removeData: args.Contains("--remove-data")).GetAwaiter().GetResult();
                if (result.ProgramKeptAt != null)
                    Console.WriteLine(string.Format(Strings.Cli_Uninstalled_ProgramKept, result.ProgramKeptAt));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(string.Format(Strings.Cli_UninstallFailed, ex.Message));
                Environment.Exit(1);
            }
            return;
        }

        // Service mode: run as Windows Service
        if (args.Contains("--service"))
        {
            RemoteKSP.Shared.Localization.UiLanguage.Apply(Core.ServerConfig.Load().Language ?? RemoteKSP.Shared.Localization.UiLanguage.SystemLanguage);
            var host = CreateHostBuilder(args).Build();

            // 数据目录里的 PIN、客户端密钥和 TLS 私钥用 DPAPI LocalMachine 加密，本机任何进程都能解密，
            // 只能靠 ACL 挡住普通用户。收紧失败时不启动服务，原因写入事件日志
            try
            {
                Core.ServerPaths.SecureDataDirectory();
            }
            catch (Exception ex)
            {
                host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>()
                    .LogCritical(ex, "Failed to restrict access to {DataDir}; refusing to start", Core.ServerPaths.DataDir);
                throw;
            }

            host.Run();
            return;
        }

        RunGui();
    }

    /// <summary>GUI mode: run WPF application with WPF-UI</summary>
    private static void RunGui()
    {
        App.InitializeServices();

        var app = new Application();

        // Merge WPF-UI resources
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        // Load converters
        var convertersUri = new Uri("pack://application:,,,/Resources/Converters.xaml");
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = convertersUri });

        var mainWindow = App.Services.GetRequiredService<Views.MainWindow>();
        app.Run(mainWindow);
    }

    /// <summary>参数 <paramref name="name"/> 后面紧跟的值；没有时为 null</summary>
    private static string? ArgumentAfter(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Microsoft.Extensions.Hosting.IHostBuilder CreateHostBuilder(string[] args) =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .UseWindowsService(options => options.ServiceName = "RemoteKSP")
            .ConfigureServices((context, services) =>
            {
                // 配置文件损坏时原文件已改名保留，这里把原因写进活动日志，管理员在 GUI 里能看到
                services.AddSingleton<RemoteKSP.Server.Core.ClientStore>(sp =>
                {
                    var store = new RemoteKSP.Server.Core.ClientStore(RemoteKSP.Server.Core.ServerPaths.DataDir);
                    if (store.LoadError is { } error)
                        sp.GetRequiredService<RemoteKSP.Server.Services.ActivityLogService>().Error(Strings.LogCat_Config, error);
                    return store;
                });
                services.AddSingleton(sp =>
                {
                    var registry = new RemoteKSP.Server.Core.CertificateRegistry(RemoteKSP.Server.Core.ServerPaths.DataDir);
                    if (registry.LoadError is { } error)
                        sp.GetRequiredService<RemoteKSP.Server.Services.ActivityLogService>().Error(Strings.LogCat_Config, error);
                    return registry;
                });
                services.AddSingleton<RemoteKSP.Server.Core.KeySourceManager>();
                services.AddSingleton<RemoteKSP.Server.Services.ActivityLogService>();
                services.AddSingleton<RemoteKSP.Server.Core.PairingManager>();
                services.AddSingleton<RemoteKSP.Server.Core.PinAttemptLimiter>(_ => new RemoteKSP.Server.Core.PinAttemptLimiter());
                // 先于签名服务启动：第一个签名请求到达前就已订阅停用事件
                services.AddHostedService(sp => new RemoteKSP.Server.Services.PinAlertService(
                    sp.GetRequiredService<RemoteKSP.Server.Core.KeySourceManager>(),
                    sp.GetRequiredService<RemoteKSP.Server.Services.ActivityLogService>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteKSP.Server.Services.PinAlertService>>(),
                    RemoteKSP.Server.Services.SessionMessenger.Broadcast));
                services.AddHostedService(sp => new RemoteKSP.Server.Services.PairingNotifier(
                    sp.GetRequiredService<RemoteKSP.Server.Core.PairingManager>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteKSP.Server.Services.PairingNotifier>>(),
                    TimeProvider.System,
                    RemoteKSP.Server.Services.UserSessionLauncher.NotifyPairing,
                    () => RemoteKSP.Server.Core.ServerConfig.Load().TlsCertThumbprint));
                services.AddHostedService<SigningHostedService>();
                services.AddHostedService<RemoteKSP.Server.Services.PipeServer>();
                services.AddHostedService(sp => new RemoteKSP.Server.Services.KeySourceRetryService(
                    sp.GetRequiredService<RemoteKSP.Server.Core.KeySourceManager>(),
                    sp.GetRequiredService<RemoteKSP.Server.Services.ActivityLogService>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteKSP.Server.Services.KeySourceRetryService>>(),
                    RemoteKSP.Server.Services.KeySourceRetryService.DefaultInterval,
                    RemoteKSP.Server.Services.KeySourceRetryService.DefaultMaxAttempts));
            });
}
