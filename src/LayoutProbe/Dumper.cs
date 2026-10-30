using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LayoutProbe;

/// <summary>Dumps the visual tree under a root with window-relative Y coordinates.</summary>
public static class Dumper
{
    public static string Dump(FrameworkElement root, Window window, int maxDepth = 40)
    {
        var sb = new StringBuilder();
        Walk(root, window, sb, 0, maxDepth);
        return sb.ToString();
    }

    private static void Walk(DependencyObject node, Window window, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        if (node is FrameworkElement fe && fe.IsVisible)
        {
            double y = double.NaN;
            try { y = fe.TransformToAncestor(window).Transform(new Point(0, 0)).Y; } catch { }
            sb.Append(new string(' ', depth * 2))
              .Append(fe.GetType().Name)
              .Append(string.IsNullOrEmpty(fe.Name) ? "" : $"#{fe.Name}")
              .Append($" y={y:F0} h={fe.ActualHeight:F0} w={fe.ActualWidth:F0}")
              .Append($" VA={fe.VerticalAlignment}");
            if (fe is Control c) sb.Append($" VCA={c.VerticalContentAlignment} Pad={c.Padding}");
            if (fe.Margin != default) sb.Append($" M={fe.Margin}");
            if (fe.Parent is Grid || VisualTreeHelper.GetParent(fe) is Grid) sb.Append($" Row={Grid.GetRow(fe)}");
            if (fe is Grid g && g.RowDefinitions.Count > 0)
            {
                sb.Append(" Rows=[");
                foreach (var r in g.RowDefinitions) sb.Append($"{r.Height}:{r.ActualHeight:F0} ");
                sb.Append(']');
            }
            if (fe is TextBlock tb) sb.Append($" \"{Trim(tb.Text)}\"");
            sb.AppendLine();
        }
        else if (node is FrameworkElement) return; // collapsed subtree

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Walk(VisualTreeHelper.GetChild(node, i), window, sb, depth + 1, maxDepth);
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] + "…" : s;

    public static void Screenshot(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var rtb = new RenderTargetBitmap(
            (int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Paint a solid backdrop first: Mica is transparent to RenderTargetBitmap.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        rtb.Render(dv);
        rtb.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var r = Find<T>(child);
            if (r != null) return r;
        }
        return null;
    }
}
