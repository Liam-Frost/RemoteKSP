using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteKSP.Shared;

/// <summary>服务端、客户端共用的窗口外观：顶部标题栏 + Logo + 窗口图标。依赖 ModernTheme.xaml 已加载到 Application.Resources。</summary>
public static class AppHeader
{
    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>统一字体、背景，并把矢量 Logo 渲染成窗口/任务栏图标</summary>
    public static void ApplyWindowChrome(Window window)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI");
        window.Background = Res("BackgroundBrush");
        window.Icon = RenderLogo(64);
    }

    /// <summary>顶部标题栏：Logo | Remote KSP [角色标签] / 副标题</summary>
    public static Border Create(string role, string subtitle)
    {
        var logo = new Image
        {
            Source = (ImageSource)Application.Current.FindResource("LogoImage"),
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Remote KSP",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Res("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new Border
        {
            Background = Res("PrimarySoftBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(10, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = role,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("PrimaryBrush")
            }
        });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(titleRow);
        texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12.5,
            Foreground = Res("TextSecondaryBrush"),
            Margin = new Thickness(0, 3, 0, 0)
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texts, 1);
        row.Children.Add(logo);
        row.Children.Add(texts);

        return new Border
        {
            Background = Res("SurfaceBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(28, 16, 28, 16),
            Child = row
        };
    }

    private static BitmapSource RenderLogo(int size)
    {
        var logo = (ImageSource)Application.Current.FindResource("LogoImage");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(logo, new Rect(0, 0, size, size));

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
