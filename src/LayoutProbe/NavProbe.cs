using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace LayoutProbe;

/// <summary>
/// 侧边导航栏测量：导出 NavigationView 的实际 ControlTemplate，并在展开/折叠两种状态下
/// 记录汉堡按钮、第一个导航项、底部 Settings 的窗口坐标和截图。
/// 用法：LayoutProbe.exe server|client nav [outDir]
/// </summary>
public static class NavProbe
{
    private static string? _outDir;
    private static string _tag = "";

    public static async Task RunAsync(Window window, string side, string outDir)
    {
        var nav = Dumper.Find<NavigationView>(window) ?? throw new InvalidOperationException("NavigationView not found");

        var report = new StringBuilder();
        foreach (var open in new[] { true, false })
        {
            nav.IsPaneOpen = open;
            await Task.Delay(700); // 等折叠动画结束
            window.UpdateLayout();
            var state = open ? "expanded" : "collapsed";
            _outDir = outDir;
            _tag = $"{side}_nav_{state}";
            report.AppendLine($"== {side} {state} (window {window.ActualWidth}x{window.ActualHeight}, " +
                              $"dpi {VisualTreeHelper.GetDpi(window).PixelsPerInchY})");
            Measure(window, nav, report);
            File.WriteAllText(Path.Combine(outDir, $"{side}_nav_{state}_tree.txt"), DumpPane(nav, window));
            Dumper.Screenshot(window, Path.Combine(outDir, $"{side}_nav_{state}.png"));
        }
        File.WriteAllText(Path.Combine(outDir, $"{side}_nav_report.txt"), report.ToString());
        Console.Write(report);
    }

    private static void Measure(Window window, NavigationView nav, StringBuilder report)
    {
        var toggle = FindByName(nav, "PART_ToggleButton") as FrameworkElement
            ?? All<FrameworkElement>(nav).FirstOrDefault(e => e.GetType().Name.Contains("Toggle"));
        var items = All<NavigationViewItem>(nav).Where(i => i.IsVisible).ToList();
        var first = items.FirstOrDefault(i => !nav.FooterMenuItems.Contains(i));
        var footer = items.FirstOrDefault(i => nav.FooterMenuItems.Contains(i));

        Rect? Bounds(FrameworkElement? e) => e == null ? null
            : e.TransformToAncestor(window).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

        var t = Bounds(toggle);
        var f = Bounds(first);
        var s = Bounds(footer);
        if (toggle != null)
            report.Append(HeaderDump.Dump(toggle));
        report.AppendLine($"  toggle   {Fmt(t)}");
        report.AppendLine($"  first    {Fmt(f)} \"{first?.Content}\"");
        report.AppendLine($"  footer   {Fmt(s)} \"{footer?.Content}\" (window bottom {window.ActualHeight:F1})");
        if (t is { } tb && f is { } fb)
            report.AppendLine($"  gap toggle.bottom -> first.top = {fb.Top - tb.Bottom:F1}");
        if (items.Count > 1 && !nav.FooterMenuItems.Contains(items[1]))
        {
            var a = Bounds(items[0])!.Value;
            var b = Bounds(items[1])!.Value;
            report.AppendLine($"  item gap 0->1 = {b.Top - a.Bottom:F1} (item h {a.Height:F1})");
        }

        // 实际画出来的高亮区域（悬停/选中背景）：模板里带 Margin 的 Border 比布局盒小，肉眼看到的间距由它决定
        void Borders(string label, FrameworkElement? root)
        {
            if (root == null) return;
            foreach (var bd in All<System.Windows.Controls.Border>(root).Prepend(root as System.Windows.Controls.Border).OfType<System.Windows.Controls.Border>().Where(x => x.IsVisible))
            {
                var r = Bounds(bd)!.Value;
                report.AppendLine($"    {label} Border#{bd.Name} y={r.Top:F1} h={r.Height:F1} bottom={r.Bottom:F1} M={bd.Margin} BT={bd.BorderThickness} Pad={bd.Padding} CR={bd.CornerRadius} Bg={bd.Background}");
            }
        }
        Borders("toggle", toggle);
        Borders("item0", first);
        if (items.Count > 1) Borders("item1", items[1]);
        if (toggle != null && items.Count > 2)
            PixelGaps(window, toggle, items.Take(3).ToList(), report);
    }

    /// <summary>
    /// 肉眼看到的间距：给汉堡按钮和前三个导航项同样的灰色背景（相当于悬停），渲染后沿一列像素扫描灰色色块的上下边缘。
    /// 只在探针里临时设置，测完恢复
    /// </summary>
    private static void PixelGaps(Window window, FrameworkElement toggle, List<NavigationViewItem> items, StringBuilder report)
    {
        var gray = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
        var targets = new List<System.Windows.Controls.Border>();
        if (All<System.Windows.Controls.Border>(toggle).Prepend(toggle as System.Windows.Controls.Border)
                .OfType<System.Windows.Controls.Border>().FirstOrDefault(b => b.Name == "LayoutRoot") is { } lr)
            targets.Add(lr);
        targets.AddRange(items.Select(i => All<System.Windows.Controls.Border>(i).First(b => b.Name == "MainBorder")));
        var saved = targets.Select(b => b.ReadLocalValue(System.Windows.Controls.Border.BackgroundProperty)).ToList();
        foreach (var b in targets) b.Background = gray;
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var w = (int)(window.ActualWidth * dpi.DpiScaleX);
        var h = (int)(window.ActualHeight * dpi.DpiScaleY);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        rtb.Render(bg);
        rtb.Render(window);
        var pixels = new byte[w * h * 4];
        rtb.CopyPixels(pixels, w * 4, 0);

        // 竖直扫描线：汉堡按钮中间偏左（避开图标）
        var tb = toggle.TransformToAncestor(window).TransformBounds(new Rect(0, 0, toggle.ActualWidth, toggle.ActualHeight));
        var x = (int)((tb.Left + 6) * dpi.DpiScaleX);
        var y0 = (int)((tb.Top - 4) * dpi.DpiScaleY);
        var y1 = (int)((items[^1].TransformToAncestor(window).Transform(new Point(0, items[^1].ActualHeight)).Y + 6) * dpi.DpiScaleY);
        var bands = new List<(int Top, int Bottom)>();
        int? start = null;
        for (var y = y0; y <= y1 && y < h; y++)
        {
            var i = (y * w + x) * 4;
            var isGray = Math.Abs(pixels[i] - 0xD0) < 12 && Math.Abs(pixels[i + 1] - 0xD0) < 12 && Math.Abs(pixels[i + 2] - 0xD0) < 12;
            if (isGray && start == null) start = y;
            if (!isGray && start != null) { bands.Add((start.Value, y - 1)); start = null; }
        }
        report.AppendLine($"  pixel bands (px @ {dpi.PixelsPerInchY} dpi): " + string.Join(" ", bands.Select(b => $"[{b.Top}-{b.Bottom}]")));
        var gaps = bands.Zip(bands.Skip(1), (a, b) => b.Top - a.Bottom - 1).ToList();
        report.AppendLine($"  visible gaps px: toggle->item0={gaps.ElementAtOrDefault(0)} item0->item1={gaps.ElementAtOrDefault(1)} item1->item2={gaps.ElementAtOrDefault(2)}");

        // 截取导航栏顶部（灰色背景下），直观对比间距
        var crop = new System.Windows.Media.Imaging.CroppedBitmap(rtb,
            new Int32Rect(0, Math.Max(0, y0 - 10), Math.Min(w, (int)(70 * dpi.DpiScaleX)), Math.Min(h - Math.Max(0, y0 - 10), y1 - y0 + 20)));
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
        using (var fs = File.Create(Path.Combine(_outDir!, $"{_tag}_gaps.png")))
            enc.Save(fs);

        for (var k = 0; k < targets.Count; k++)
        {
            if (saved[k] == DependencyProperty.UnsetValue) targets[k].ClearValue(System.Windows.Controls.Border.BackgroundProperty);
            else targets[k].Background = (Brush)saved[k];
        }
    }

    private static string Fmt(Rect? r) => r is { } b ? $"x={b.Left:F1} y={b.Top:F1} w={b.Width:F1} h={b.Height:F1} bottom={b.Bottom:F1}" : "(not found)";

    /// <summary>导航栏部分的视觉树（只到第一个导航项内部两层），带名字、Margin、Padding</summary>
    private static string DumpPane(NavigationView nav, Window window)
    {
        var sb = new StringBuilder();
        void Walk(DependencyObject node, int depth, int itemDepth)
        {
            if (node is FrameworkElement fe)
            {
                if (!fe.IsVisible) return;
                if (fe is ContentPresenter { Name: "PART_NavigationViewContentPresenter" } || fe.GetType().Name == "Frame")
                    return; // 页面内容不在本次范围
                var b = fe.TransformToAncestor(window).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                sb.Append(new string(' ', depth * 2)).Append(fe.GetType().Name)
                  .Append(string.IsNullOrEmpty(fe.Name) ? "" : $"#{fe.Name}")
                  .Append($" y={b.Top:F1} h={b.Height:F1} x={b.Left:F1} w={b.Width:F1}");
                if (fe.Margin != default) sb.Append($" M={fe.Margin}");
                if (fe is Control c && c.Padding != default) sb.Append($" Pad={c.Padding}");
                if (fe is Border bd && bd.Padding != default) sb.Append($" Pad={bd.Padding}");
                if (!double.IsNaN(fe.Height)) sb.Append($" H={fe.Height}");
                if (fe.MinHeight > 0) sb.Append($" MinH={fe.MinHeight}");
                if (VisualTreeHelper.GetParent(fe) is Grid) sb.Append($" Row={Grid.GetRow(fe)}");
                if (fe is Grid g && g.RowDefinitions.Count > 0)
                    sb.Append(" Rows=[").Append(string.Join(" ", g.RowDefinitions.Select(r => $"{r.Height}:{r.ActualHeight:F0}"))).Append(']');
                if (fe is System.Windows.Controls.TextBlock tb) sb.Append($" \"{tb.Text}\"");
                sb.AppendLine();
                if (fe is NavigationViewItem) itemDepth++;
                if (itemDepth > 2) return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i), depth + 1, itemDepth > 0 ? itemDepth + 1 : 0);
        }
        Walk(nav, 0, 0);
        return sb.ToString();
    }

    private static DependencyObject? FindByName(DependencyObject root, string name) =>
        All<FrameworkElement>(root).FirstOrDefault(e => e.Name == name);

    private static System.Collections.Generic.IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in All<T>(child)) yield return d;
        }
    }
}
