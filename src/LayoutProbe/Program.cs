using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace LayoutProbe;

/// <summary>
/// Dev-only probe: hosts the real MainWindow, navigates to a page, optionally injects
/// probe-only sample rows, then dumps layout coordinates and a screenshot.
/// Usage: LayoutProbe.exe server|client [rows] [outDir] [width] [height]
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var side = args.ElementAtOrDefault(0) ?? "server";
        var rows = int.TryParse(args.ElementAtOrDefault(1), out var n) ? n : 1;
        var outDir = args.ElementAtOrDefault(2) ?? ".";
        var width = double.TryParse(args.ElementAtOrDefault(3), out var w) ? w : 0;
        var height = double.TryParse(args.ElementAtOrDefault(4), out var h) ? h : 0;
        // overview 模式第 6 个参数是场景名，不是页面名
        var isOverview = args.ElementAtOrDefault(1) == "overview";
        var pageName = isOverview ? null : args.ElementAtOrDefault(5);
        var asmName = side == "server" ? "RemoteKSP.Server" : "RemoteKSP.Client";

        // 收集 WPF Binding 错误，跑完后一并输出
        var bindingErrors = new BindingErrorListener();
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{asmName};component/Resources/Converters.xaml")
        });

        Window window;
        NavigationView nav;
        Type pageType;
        if (side == "server")
        {
            RemoteKSP.Server.App.InitializeServices();
            var mw = RemoteKSP.Server.App.Services.GetRequiredService<RemoteKSP.Server.Views.MainWindow>();
            window = mw; nav = null!;
            pageType = pageName != null
                ? typeof(RemoteKSP.Server.Views.Pages.OverviewPage).Assembly.GetType($"RemoteKSP.Server.Views.Pages.{pageName}", true)!
                : typeof(RemoteKSP.Server.Views.Pages.ActivityLogPage);
        }
        else
        {
            RemoteKSP.Client.App.InitializeServices();
            var mw = RemoteKSP.Client.App.Services.GetRequiredService<RemoteKSP.Client.Views.MainWindow>();
            window = mw; nav = null!;
            pageType = pageName != null
                ? typeof(RemoteKSP.Client.Views.Pages.OverviewPage).Assembly.GetType($"RemoteKSP.Client.Views.Pages.{pageName}", true)!
                : typeof(RemoteKSP.Client.Views.Pages.DiagnosticsPage);
        }
        if (width > 0) window.Width = width;
        if (height > 0) window.Height = height;

        window.ContentRendered += async (_, _) =>
        {
            if (args.ElementAtOrDefault(1) == "nav")
            {
                try { await NavProbe.RunAsync(window, side, outDir); }
                catch (Exception ex) { Console.WriteLine("probe error: " + ex); }
                finally { app.Shutdown(); }
                return;
            }
            if (isOverview)
            {
                try
                {
                    await OverviewProbe.RunAsync(window, side, outDir, args.ElementAtOrDefault(5) ?? "all");
                    Console.WriteLine($"binding errors: {bindingErrors.Errors.Count}");
                    foreach (var err in bindingErrors.Errors.Distinct())
                        Console.WriteLine("  " + err);
                }
                catch (Exception ex) { Console.WriteLine("probe error: " + ex); }
                finally { app.Shutdown(); }
                return;
            }
            try
            {
                nav = Dumper.Find<NavigationView>(window)!;
                nav.Navigate(pageType);
                await Task.Delay(800);
                var page = Dumper.Find<Page>(window) ?? throw new InvalidOperationException("page not found");
                SampleData.Inject(page, rows);
                await Task.Delay(600);
                // 可选第 7 个参数：执行页面 ViewModel 上的某个命令（如打开对话框、展开折叠区），不等待其完成
                if (args.ElementAtOrDefault(6) is { Length: > 0 } commandPath)
                {
                    object? target = page.GetType().GetProperty("ViewModel")!.GetValue(page);
                    var segments = commandPath.Split('.');
                    foreach (var segment in segments)
                        target = target!.GetType().GetProperty(segment)!.GetValue(target);
                    ((System.Windows.Input.ICommand)target!).Execute(null);
                    await Task.Delay(2500);

                    // SignTool 对话框：可选填入目标文件、执行签名，并把内容滚到底部以检查预览区与 Footer
                    if (Dumper.Find<RemoteKSP.Client.Views.Dialogs.SignToolCommandForm>(window) is { } form)
                    {
                        var vm = (RemoteKSP.Client.ViewModels.SignToolCommandDialogViewModel)form.DataContext;
                        if (Environment.GetEnvironmentVariable("REMOTEKSP_PROBE_TARGET") is { Length: > 0 } t)
                            vm.TargetFile = t;
                        if (Environment.GetEnvironmentVariable("REMOTEKSP_PROBE_EXECUTE") == "1")
                        {
                            await vm.ExecuteAsync();
                            Console.WriteLine($"execute: {vm.RunStatus} | {vm.RunMessage}");
                        }
                        await Task.Delay(300);
                        // 表单自己不再有 ScrollViewer，滚动的是 ContentDialog 模板里的那个
                        var scrollViewers = new System.Collections.Generic.List<ScrollViewer>();
                        for (System.Windows.DependencyObject? d = form; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                            if (d is ScrollViewer sv)
                                scrollViewers.Add(sv);
                        Console.WriteLine($"scrollviewers around form: {scrollViewers.Count}, " +
                            $"scrollable: {scrollViewers.Count(sv => sv.ScrollableHeight > 0)}");
                        scrollViewers.FirstOrDefault()?.ScrollToEnd();
                        await Task.Delay(300);
                    }
                }
                window.UpdateLayout();
                var tag = $"{side}_{rows}_{(int)window.ActualWidth}x{(int)window.ActualHeight}";
                File.WriteAllText(Path.Combine(outDir, tag + ".txt"),
                    $"window {window.ActualWidth}x{window.ActualHeight}\n" + Dumper.Dump(window, window));
                Dumper.Screenshot(window, Path.Combine(outDir, tag + ".png"));
                Console.WriteLine("ok " + tag);
                Console.WriteLine($"binding errors: {bindingErrors.Errors.Count}");
                foreach (var err in bindingErrors.Errors.Distinct())
                    Console.WriteLine("  " + err);
            }
            catch (Exception ex) { Console.WriteLine("probe error: " + ex); }
            finally { app.Shutdown(); }
        };
        app.Run(window);
        return 0;
    }
}

/// <summary>把 WPF 数据绑定的 trace 输出收集成字符串列表</summary>
sealed class BindingErrorListener : System.Diagnostics.TraceListener
{
    private readonly System.Text.StringBuilder _line = new();
    public System.Collections.Generic.List<string> Errors { get; } = [];

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        Errors.Add(_line.ToString());
        _line.Clear();
    }
}
