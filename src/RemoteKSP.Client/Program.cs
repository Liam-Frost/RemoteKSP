using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteKSP.Client;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 旧版单服务端配置迁移为 server-001（只在第一次运行时生效）
        try { Core.ClientRegistry.MigrateLegacyConfig(); }
        catch { /* 迁移失败不影响启动，用户可以重新配对 */ }

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
}
