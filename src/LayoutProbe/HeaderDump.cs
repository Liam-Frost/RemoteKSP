using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LayoutProbe;

/// <summary>导航栏顶部 Grid 的每个子元素，包括 Hidden/Collapsed 的（它们是否仍占位）</summary>
public static class HeaderDump
{
    public static string Dump(FrameworkElement toggle)
    {
        var grid = (Grid)VisualTreeHelper.GetParent(toggle);
        var sb = new StringBuilder();
        sb.AppendLine("Rows=" + string.Join(" ", grid.RowDefinitions.Select((r, i) => $"[{i}]{r.Height}:{r.ActualHeight:F2}")));
        foreach (FrameworkElement c in grid.Children)
            sb.AppendLine($"  {c.GetType().Name}#{c.Name} Row={Grid.GetRow(c)} Vis={c.Visibility} " +
                          $"Src={DependencyPropertyHelper.GetValueSource(c, UIElement.VisibilityProperty).BaseValueSource} Desired={c.DesiredSize.Height:F2} Actual={c.ActualHeight:F2} M={c.Margin} " +
                          $"Content={(c as ContentPresenter)?.Content?.GetType().Name ?? (c as ContentControl)?.Content?.GetType().Name}");
        return sb.ToString();
    }
}
