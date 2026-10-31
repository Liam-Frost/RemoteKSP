using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteKSP.Shared.Networking;
using RemoteKSP.Shared.Ui;
using Wpf.Ui.Controls;

namespace LayoutProbe;

/// <summary>
/// 概览页布局验证：把各种状态通过页面 ViewModel 的真实映射方法（Apply / Show）灌进页面，
/// 在指定窗口尺寸、导航栏展开/折叠下截图，并检查卡片列数、行高一致、按钮对齐、文字与按钮重叠、横向裁剪。
/// 用法：LayoutProbe.exe server|client overview [outDir] [width] [height] [scenario]
/// </summary>
public static class OverviewProbe
{
    public static async Task RunAsync(Window window, string side, string outDir, string scenario)
    {
        var nav = Dumper.Find<NavigationView>(window)!;
        var pageType = side == "server"
            ? typeof(RemoteKSP.Server.Views.Pages.OverviewPage)
            : typeof(RemoteKSP.Client.Views.Pages.OverviewPage);
        nav.Navigate(pageType);
        await Task.Delay(1500); // 等真实刷新完成，再用场景数据覆盖
        var page = (Page)Dumper.Find<Page>(window)!;

        var report = new StringBuilder();
        if (scenario == "interact")
        {
            try { await InteractAsync(window, nav, side, outDir, report); }
            catch (Exception ex) { report.AppendLine("  FAIL exception: " + ex); }
            File.WriteAllText(Path.Combine(outDir, $"{side}_interact_report.txt"), report.ToString());
            Console.Write(report);
            return;
        }
        if (scenario == "real")
        {
            // 不覆盖任何状态：本机真实的服务、管理通道、注册表、网卡
            await Task.Delay(1500);
            window.UpdateLayout();
            var tag = $"{side}_real_{(int)window.ActualWidth}x{(int)window.ActualHeight}";
            report.AppendLine($"== {tag} (elevated={IsElevated()})");
            DumpCards(page, report);
            Check(window, page, report);
            Dumper.Screenshot(window, Path.Combine(outDir, tag + ".png"));
            Console.Write(report);
            return;
        }
        foreach (var name in scenario == "all" ? Scenarios(side) : [scenario])
        {
            Apply(page, side, name);
            foreach (var open in new[] { true, false })
            {
                nav.IsPaneOpen = open;
                await Task.Delay(500);
                window.UpdateLayout();
                var tag = $"{side}_{name}_{(int)window.ActualWidth}x{(int)window.ActualHeight}_{(open ? "open" : "compact")}";
                report.AppendLine($"== {tag}");
                Check(window, page, report);
                Dumper.Screenshot(window, Path.Combine(outDir, tag + ".png"));
            }
        }
        File.WriteAllText(Path.Combine(outDir, $"{side}_{scenario}_{(int)window.ActualWidth}x{(int)window.ActualHeight}_report.txt"), report.ToString());
        Console.Write(report);
    }

    private static string[] Scenarios(string side) => side == "server"
        ? ["running-ready", "stopped", "not-installed", "starting", "channel-down", "not-configured", "query-failed", "many-clients", "expanded"]
        : ["installed", "not-installed", "needs-update", "empty", "one-server", "many-servers", "long-name", "single-nic", "expanded"];

    // ---------- 场景：全部走页面 ViewModel 的公开映射方法 ----------

    private static void Apply(Page page, string side, string name)
    {
        if (side == "server")
        {
            var vm = ((RemoteKSP.Server.Views.Pages.OverviewPage)page).ViewModel;
            vm.Apply(ServerSnapshot(name));
            vm.Network.Show(name == "single-nic" ? SingleNic() : MultiNic());
            vm.Network.ShowOther = name == "expanded";
        }
        else
        {
            var vm = ((RemoteKSP.Client.Views.Pages.OverviewPage)page).ViewModel;
            vm.Apply(ClientSnapshot(name));
            vm.Network.Show(name == "single-nic" ? SingleNic() : MultiNic());
            vm.Network.ShowOther = name == "expanded";
        }
    }

    private static RemoteKSP.Client.ViewModels.ClientOverviewSnapshot ClientSnapshot(string name)
    {
        var ksp = name switch
        {
            "not-installed" or "empty" => RemoteKSP.Client.ViewModels.KspInstallState.NotInstalled,
            "needs-update" => RemoteKSP.Client.ViewModels.KspInstallState.NeedsUpdate,
            _ => RemoteKSP.Client.ViewModels.KspInstallState.Installed
        };
        string[] servers = name switch
        {
            "empty" => [],
            "one-server" => ["sign-01"],
            "many-servers" => ["sign-01", "sign-02", "build-agent-03", "release-signing", "qa-lab"],
            "long-name" => ["contoso-codesign-production-server-east-datacenter-01", "sign-02"],
            _ => ["sign-01", "sign-02"]
        };
        var certs = name switch { "empty" or "one-server" => 0, "many-servers" => 12, _ => 2 };
        return new(ksp, null, servers, certs, Math.Min(certs, servers.Length));
    }

    private static RemoteKSP.Server.ViewModels.ServerOverviewSnapshot ServerSnapshot(string name)
    {
        var running = new RemoteKSP.Server.Services.ServiceState(RemoteKSP.Server.Services.ServiceStatusKind.Running, null);
        var ready = new RemoteKSP.Server.Core.KeySourceStatus("production", "abc",
            string.Format(RemoteKSP.Server.Localization.Strings.KeySrc_DefaultCert,
                string.Format(RemoteKSP.Server.Localization.Strings.KeySrc_Summary, 2, 2),
                "Contoso Code Signing", RemoteKSP.Server.Localization.Strings.KeySrc_Ready));
        return name switch
        {
            "stopped" => new(new(RemoteKSP.Server.Services.ServiceStatusKind.Stopped, null),
                RemoteKSP.Server.ViewModels.KeySourceState.ServiceNotRunning, null, null, null, null),
            "not-installed" => new(new(RemoteKSP.Server.Services.ServiceStatusKind.NotInstalled, null),
                RemoteKSP.Server.ViewModels.KeySourceState.ServiceNotRunning, null, null, null, null),
            "starting" => new(new(RemoteKSP.Server.Services.ServiceStatusKind.Starting, null),
                RemoteKSP.Server.ViewModels.KeySourceState.ServicePending, null, null, null, null),
            "channel-down" => new(running, RemoteKSP.Server.ViewModels.KeySourceState.ChannelUnavailable, null,
                "服务未运行或无法连接: The operation has timed out.", null, "服务未运行或无法连接: The operation has timed out."),
            "not-configured" => new(running, RemoteKSP.Server.ViewModels.KeySourceState.NotConfigured,
                new("test", "", "共 1 个证书，0 个已分享。没有分享的证书，客户端无法获取证书"), null, 0, null),
            "query-failed" => new(running, RemoteKSP.Server.ViewModels.KeySourceState.QueryFailed, null,
                "Unknown command: GetKeySourceStatus", 3, null),
            "many-clients" => new(running, RemoteKSP.Server.ViewModels.KeySourceState.Ready, ready, null, 27, null),
            _ => new(running, RemoteKSP.Server.ViewModels.KeySourceState.Ready, ready, null, 3, null)
        };
    }

    private static NetworkAddressInfo Addr(string nic, string id, NetworkInterfaceType type, string ip,
        bool primary, bool dhcp = false, bool loopback = false, bool linkLocal = false) => new()
    {
        InterfaceId = id, InterfaceName = nic, InterfaceDescription = nic + " adapter", InterfaceType = type,
        OperationalStatus = OperationalStatus.Up, Address = IPAddress.Parse(ip),
        IsPrimary = primary, IsDhcpAssigned = dhcp, IsLoopback = loopback, IsLinkLocal = linkLocal
    };

    private static List<NetworkAddressInfo> SingleNic() =>
    [
        Addr("以太网", "eth", NetworkInterfaceType.Ethernet, "192.168.1.100", true, dhcp: true),
    ];

    /// <summary>主接口 WLAN（IPv4+IPv6），另有环回和一个 VPN；顺序与 NetworkAddressService 一致（主地址在前）</summary>
    private static List<NetworkAddressInfo> MultiNic() =>
    [
        Addr("WLAN", "wlan", NetworkInterfaceType.Wireless80211, "10.0.0.42", true, dhcp: true),
        Addr("Corp VPN", "vpn", (NetworkInterfaceType)53, "172.16.8.21", true),
        Addr("WLAN", "wlan", NetworkInterfaceType.Wireless80211, "2001:db8:85a3::8a2e:370:7334", true),
        Addr("Loopback Pseudo-Interface 1", "lo", NetworkInterfaceType.Loopback, "127.0.0.1", false, loopback: true),
        Addr("WLAN", "wlan", NetworkInterfaceType.Wireless80211, "fe80::1c2d:3e4f:5a6b:7c8d%12", false, linkLocal: true),
        Addr("Loopback Pseudo-Interface 1", "lo", NetworkInterfaceType.Loopback, "::1", false, loopback: true),
    ];

    // ---------- 真实交互：执行页面上按钮绑定的命令（不执行安装/卸载/启动/停止这类会改动本机的操作） ----------

    private static bool IsElevated() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static void DumpCards(Page page, StringBuilder report)
    {
        foreach (var c in All<MetricCard>(page))
            report.AppendLine($"  card 「{c.Title}」 value=「{c.Value}」 status={c.Status} detail=「{c.Detail}」 tooltip=「{c.DetailToolTip}」");
    }

    private static async Task InteractAsync(Window window, NavigationView nav, string side, string outDir, StringBuilder report)
    {
        var overviewType = side == "server"
            ? typeof(RemoteKSP.Server.Views.Pages.OverviewPage)
            : typeof(RemoteKSP.Client.Views.Pages.OverviewPage);
        var problems = new List<string>();
        Page Current() => Dumper.Find<Page>(window)!;
        void Expect(bool ok, string what) { report.AppendLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) problems.Add(what); }
        async Task BackToOverview() { nav.Navigate(overviewType); await Task.Delay(1500); }
        var header = All<ContentPresenter>(window).First(c => c.Name == "PageActionsHost");

        report.AppendLine($"== {side} interact (elevated={IsElevated()})");
        DumpCards(Current(), report);

        // 1. 每个可见按钮都绑定了命令；CanExecute 与当前状态一致（打印出来，不能永远为 false）
        foreach (var b in All<System.Windows.Controls.Primitives.ButtonBase>(Current()).Where(b => b.IsVisible)
                     .Concat(header.Content is FrameworkElement h ? All<System.Windows.Controls.Primitives.ButtonBase>(h).Prepend(h as System.Windows.Controls.Primitives.ButtonBase) : [])
                     .OfType<System.Windows.Controls.Primitives.ButtonBase>().Where(b => b.IsVisible).Distinct())
        {
            if (System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ServiceMoreButton")
                continue; // 「更多操作」用 Click 打开菜单，没有 Command，单独验证
            var label = b.Content as string ?? (b.Content as FrameworkElement)?.GetType().Name ?? "?";
            Expect(b.Command != null, $"按钮「{label}」绑定了命令（CanExecute={b.Command?.CanExecute(b.CommandParameter)}）");
        }
        Expect(header.Content is FrameworkElement, "标题行有页面按钮");

        // 2. 网络：刷新、展开、收起、复制 IP
        var netVm = (NetworkAddressesViewModel)All<NetworkAddressesCard>(Current()).First().DataContext;
        var before = netVm.Groups.Sum(g => g.Addresses.Count);
        await netVm.RefreshCommand.ExecuteAsync(null);
        Expect(!netVm.IsRefreshing && netVm.ErrorText == "", $"刷新网络地址完成（{before} → {netVm.Groups.Sum(g => g.Addresses.Count)} 条可见）");
        if (netVm.HasHidden)
        {
            var collapsed = netVm.Groups.Sum(g => g.Addresses.Count);
            var cardTop = All<NetworkAddressesCard>(Current()).First().TransformToAncestor(window).Transform(new Point()).Y;
            netVm.ToggleOtherCommand.Execute(null);
            await Task.Delay(200);
            window.UpdateLayout();
            var expanded = netVm.Groups.Sum(g => g.Addresses.Count);
            var cardTopAfter = All<NetworkAddressesCard>(Current()).First().TransformToAncestor(window).Transform(new Point()).Y;
            Expect(expanded == collapsed + netVm.HiddenCount && netVm.ToggleText == "收起其他网络地址",
                $"展开其他地址：{collapsed} + {netVm.HiddenCount} = {expanded}");
            Expect(Math.Abs(cardTop - cardTopAfter) < 0.5, "展开时网络卡片上方的内容不移动");
            Expect(netVm.Groups.Select(g => g.Name).Distinct().Count() == netVm.Groups.Count, "展开后每个接口只出现一次");
            Dumper.Screenshot(window, Path.Combine(outDir, $"{side}_interact_expanded.png"));
            netVm.ToggleOtherCommand.Execute(null);
            Expect(netVm.Groups.Sum(g => g.Addresses.Count) == collapsed, "收起其他地址");
        }
        var item = netVm.Groups.SelectMany(g => g.Addresses).FirstOrDefault();
        if (item != null)
        {
            var saved = await ReadClipboardAsync();
            _ = netVm.CopyAddressCommand.ExecuteAsync(item);
            await Task.Delay(600);
            // 本机剪贴板可能不可写（例如在无桌面剪贴板的会话里运行）：这时应报告「复制失败」而不是假装成功
            var clipboardWritable = TryClipboard();
            Expect(CloseDialog(window, out var title) && title == (clipboardWritable ? "复制成功" : "复制失败"),
                $"复制 IP 的反馈与剪贴板实际结果一致：「{title}」（剪贴板可写={clipboardWritable}）");
            if (clipboardWritable)
                Expect(await ReadClipboardAsync() == item.AddressText, $"复制 IP 写入剪贴板：{item.AddressText}");
            if (side == "server")
            {
                var urlItem = netVm.Groups.SelectMany(g => g.Addresses).First(a => a.CanCopyServerUrl);
                _ = netVm.CopyUrlCommand.ExecuteAsync(urlItem);
                await Task.Delay(600);
                Expect(CloseDialog(window, out var urlTitle) && urlTitle == (clipboardWritable ? "复制成功" : "复制失败"),
                    $"复制连接地址的反馈与剪贴板实际结果一致：「{urlTitle}」，URL={urlItem.ServerUrl}");
                if (clipboardWritable)
                    Expect(await ReadClipboardAsync() == urlItem.ServerUrl, $"复制连接地址写入剪贴板：{urlItem.ServerUrl}");
                var linkLocal = netVm.Groups.SelectMany(g => g.Addresses).Concat(Enumerable.Empty<NetworkAddressItem>())
                    .FirstOrDefault(a => a.Info.IsIPv6 && a.Info.IsLinkLocal);
                if (linkLocal != null)
                    Expect(!netVm.CopyUrlCommand.CanExecute(linkLocal), "链路本地 IPv6 不能复制连接地址");
            }
            if (saved != null)
                try { Clipboard.SetText(saved); } catch (System.Runtime.InteropServices.COMException) { }
        }

        // 3. 导航命令跳到正确的页面，离开后标题行按钮清空
        async Task Navigates(string command, Type expected)
        {
            var vm = Current().GetType().GetProperty("ViewModel")!.GetValue(Current())!;
            var cmd = (System.Windows.Input.ICommand)vm.GetType().GetProperty(command)!.GetValue(vm)!;
            cmd.Execute(null);
            await Task.Delay(800);
            Expect(Current().GetType() == expected, $"{command} → {Current().GetType().Name}");
            Expect(header.Content == null, $"离开概览后标题行按钮已清空（{expected.Name}）");
            await BackToOverview();
        }

        if (side == "client")
        {
            await Navigates("OpenServersCommand", typeof(RemoteKSP.Client.Views.Pages.ServersPage));
            await Navigates("OpenCertificatesCommand", typeof(RemoteKSP.Client.Views.Pages.CertificatesPage));
            var vm = ((RemoteKSP.Client.Views.Pages.OverviewPage)Current()).ViewModel;
            if (vm.ServerCount > 0)
                await Navigates("RunDiagnosticsCommand", typeof(RemoteKSP.Client.Views.Pages.DiagnosticsPage));
            else
            {
                _ = vm.RunDiagnosticsCommand.ExecuteAsync(null);
                await Task.Delay(600);
                Expect(CloseDialog(window, out var t) && t == "需要先配对服务端", $"没有服务端时运行诊断给出说明：「{t}」");
            }
        }
        else
        {
            await Navigates("OpenCertificatesCommand", typeof(RemoteKSP.Server.Views.Pages.CertificatesPage));
            var vm = ((RemoteKSP.Server.Views.Pages.OverviewPage)Current()).ViewModel;
            await Navigates("OpenClientsCommand", vm.AuthorizedClients == 0
                ? typeof(RemoteKSP.Server.Views.Pages.PairingRequestsPage)
                : typeof(RemoteKSP.Server.Views.Pages.ClientsPage));

            // 刷新状态：重新查询后卡片仍是真实结果
            vm = ((RemoteKSP.Server.Views.Pages.OverviewPage)Current()).ViewModel;
            await vm.RefreshStatusCommand.ExecuteAsync(null);
            Expect(vm.ServiceValue != "检查中…" && vm.KeySourceValue != "检查中…", $"刷新状态完成：服务「{vm.ServiceValue}」密钥源「{vm.KeySourceValue}」");
            Expect(!(vm.ServiceRunning && vm.KeySourceValue == "服务未运行"), "服务运行中时密钥源不会显示「服务未运行」");

            // 「更多」菜单：打开后菜单项绑定到卸载命令（不执行）
            var more = All<System.Windows.Controls.Button>(Current()).FirstOrDefault(d => d.IsVisible && System.Windows.Automation.AutomationProperties.GetAutomationId(d) == "ServiceMoreButton");
            if (more != null)
            {
                // 走真实点击路径（UI Automation Invoke 等同于鼠标点击/键盘 Enter），不直接改属性
                if (new System.Windows.Automation.Peers.ButtonAutomationPeer(more)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                    is System.Windows.Automation.Provider.IInvokeProvider invoke)
                    invoke.Invoke();
                await Task.Delay(600);
                var menu = more.ContextMenu;
                var uninstall = menu?.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault();
                Expect(menu?.IsOpen == true, "「更多」菜单可以通过点击打开");
                Expect(uninstall?.Command == vm.UninstallServiceCommand,
                    $"「{uninstall?.Header}」绑定到卸载命令（CanExecute={uninstall?.Command?.CanExecute(null)}）");
                Dumper.Screenshot(window, Path.Combine(outDir, $"{side}_interact_more.png"));
                if (menu != null) menu.IsOpen = false;
            }
            else
                report.AppendLine("  note: 服务未安装，「更多」菜单不显示");
        }

        report.AppendLine(problems.Count == 0 ? "  ALL PASS" : $"  {problems.Count} FAILED");
    }

    private static bool TryClipboard()
    {
        try { Clipboard.GetDataObject(); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>剪贴板可能被其他程序（剪贴板管理器等）短暂占用：重试几次</summary>
    private static async Task<string?> ReadClipboardAsync()
    {
        for (var i = 0; ; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.COMException) when (i < 20) { await Task.Delay(100); }
        }
    }

    /// <summary>关闭当前显示的 ContentDialog（复制成功等反馈），返回它的标题</summary>
    private static bool CloseDialog(Window window, out string? title)
    {
        var dialog = All<ContentDialog>(window).FirstOrDefault(d => d.IsVisible);
        title = dialog?.Title as string;
        dialog?.Hide();
        return dialog != null;
    }

    // ---------- 检查 ----------

    private static void Check(Window window, Page page, StringBuilder report)
    {
        Rect B(FrameworkElement e) => e.TransformToAncestor(window).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var problems = new List<string>();

        // 页面内容可用宽度：ScrollViewer 视口
        var scroll = All<ScrollViewer>(page).First();
        report.AppendLine($"  viewport w={scroll.ViewportWidth:F0} extentW={scroll.ExtentWidth:F0} " +
                          $"h={scroll.ViewportHeight:F0} extentH={scroll.ExtentHeight:F0}");
        if (scroll.ExtentWidth > scroll.ViewportWidth + 0.5)
            problems.Add("横向内容超出视口");

        // 标题行按钮与标题同一行
        var header = All<ContentPresenter>(window).FirstOrDefault(c => c.Name == "PageActionsHost");
        if (header?.Content is FrameworkElement action)
            report.AppendLine($"  header action {B(action)}");

        foreach (var panel in All<ResponsiveColumnsPanel>(page))
        {
            var cards = panel.Children.OfType<MetricCard>().Where(c => c.IsVisible).ToList();
            var rects = cards.Select(B).ToList();
            report.AppendLine($"  metrics columns={panel.ActualColumns} panelW={panel.ActualWidth:F0}: " +
                              string.Join(" | ", cards.Select((c, i) => $"{c.Title} x={rects[i].X:F0} y={rects[i].Y:F0} w={rects[i].Width:F0} h={rects[i].Height:F0}")));
            // 量卡片里实际画出来的 ui:Card，不是外层 UserControl：外层总是被面板拉伸，看不出卡片本身是否居中变矮
            var visuals = cards.Select(c => B(All<Card>(c).First())).ToList();
            foreach (var row in visuals.GroupBy(r => Math.Round(r.Y)))
                if (row.Select(r => Math.Round(r.Height)).Distinct().Count() > 1)
                    problems.Add($"同一行卡片高度不一致（y={row.Key}）");
            if (visuals.Select(r => Math.Round(r.Y)).Distinct().Count() != visuals.GroupBy(r => Math.Round(r.Y)).Count()
                || visuals.Zip(rects).Any(p => Math.Abs(p.First.Height - p.Second.Height) > 0.5))
                problems.Add("卡片没有填满所在格子（垂直居中变矮）");
            // 底部操作按钮在同一行上对齐
            var actionTops = cards.Select(c => All<System.Windows.Controls.Primitives.ButtonBase>(c).FirstOrDefault(b => b.IsVisible))
                .Where(b => b != null).Select(b => B(b!)).ToList();
            foreach (var row in actionTops.GroupBy(r => Math.Round(rects.First(c => c.Contains(r.TopLeft)).Y)))
                if (!cards.First().IsWide && row.Select(r => Math.Round(r.Bottom)).Distinct().Count() > 1)
                    problems.Add("同一行卡片的操作按钮没有底部对齐");
        }

        // 每张卡片：主值/说明文字不与操作按钮重叠，不超出卡片
        foreach (var card in All<MetricCard>(page).Where(c => c.IsVisible))
        {
            var cardRect = B(card);
            var texts = All<System.Windows.Controls.TextBlock>(card).Where(t => t.IsVisible && t.ActualWidth > 0 && !IsInside<System.Windows.Controls.Button>(t)).ToList();
            var buttons = All<System.Windows.Controls.Primitives.ButtonBase>(card).Where(b => b.IsVisible).ToList();
            foreach (var t in texts)
            {
                var tr = B(t);
                if (tr.Right > cardRect.Right + 0.5 || tr.Bottom > cardRect.Bottom + 0.5)
                    problems.Add($"「{card.Title}」文字超出卡片：{t.Text}");
                foreach (var b in buttons)
                    if (tr.IntersectsWith(B(b)) && Rect.Intersect(tr, B(b)) is { Width: > 0.5, Height: > 0.5 })
                        problems.Add($"「{card.Title}」文字「{t.Text}」与按钮重叠");
            }
            var value = texts.FirstOrDefault(t => t.Text == card.Value);
            if (value != null && IsTrimmed(value))
                report.AppendLine($"  note: 「{card.Title}」主值被省略：{card.Value}");
            foreach (var b in buttons)
                if (B(b).Right > cardRect.Right + 0.5)
                    problems.Add($"「{card.Title}」按钮超出卡片");
        }

        // 网络卡片：接口名不重复；所有「复制 IP」按钮右边缘对齐
        foreach (var netCard in All<NetworkAddressesCard>(page))
        {
            var vm = (NetworkAddressesViewModel)netCard.DataContext;
            var names = vm.Groups.Select(g => g.Name).ToList();
            if (names.Distinct().Count() != names.Count)
                problems.Add("网络接口名重复出现");
            var copy = All<Wpf.Ui.Controls.Button>(netCard).Where(b => b.IsVisible && (b.Content as string) == "复制 IP").Select(B).ToList();
            var url = All<Wpf.Ui.Controls.Button>(netCard).Where(b => b.IsVisible && (b.Content as string) == "复制连接地址").Select(B).ToList();
            report.AppendLine($"  network groups=[{string.Join(", ", vm.Groups.Select(g => $"{g.Name}:{g.Addresses.Count}"))}] " +
                              $"hidden={vm.HiddenCount} toggle=\"{vm.ToggleText}\" card h={B(netCard).Height:F0}");
            report.AppendLine($"  copy-ip right edges: {string.Join(" ", copy.Select(r => r.Right.ToString("F1")).Distinct())}" +
                              (url.Count > 0 ? $"; copy-url right edges: {string.Join(" ", url.Select(r => r.Right.ToString("F1")).Distinct())}" : ""));
            if (copy.Select(r => Math.Round(r.Right, 1)).Distinct().Count() > 1)
                problems.Add("「复制 IP」按钮没有对齐");
            if (url.Select(r => Math.Round(r.Right, 1)).Distinct().Count() > 1)
                problems.Add("「复制连接地址」按钮没有对齐");
            foreach (var row in All<System.Windows.Controls.TextBlock>(netCard).Where(t => t.IsVisible && t.ToolTip is string))
                foreach (var b in copy)
                    if (Rect.Intersect(B(row), b) is { Width: > 0.5, Height: > 0.5 })
                        problems.Add("地址文字与复制按钮重叠");
            var disabledUrl = All<Wpf.Ui.Controls.Button>(netCard).Count(b => b.IsVisible && (b.Content as string) == "复制连接地址" && !b.IsEnabled);
            if (url.Count > 0)
                report.AppendLine($"  copy-url disabled rows: {disabledUrl}");
        }

        report.AppendLine(problems.Count == 0 ? "  OK" : "  PROBLEMS:\n    " + string.Join("\n    ", problems.Distinct()));
    }

    private static bool IsTrimmed(System.Windows.Controls.TextBlock t)
    {
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = t.DesiredSize.Width;
        t.InvalidateMeasure();
        return full > t.ActualWidth + 0.5;
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T)
                return true;
        return false;
    }

    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in All<T>(child)) yield return d;
        }
    }
}
