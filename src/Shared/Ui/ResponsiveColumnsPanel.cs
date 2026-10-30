using System;
using System.Windows;
using System.Windows.Controls;

namespace RemoteKSP.Shared.Ui;

/// <summary>
/// 按实际可用宽度决定列数的等宽网格：每列至少 <see cref="MinColumnWidth"/>，最多 <see cref="MaxColumns"/> 列。
/// 同一行的子元素高度一致（取该行最高者），不够一整行的最后几个平分整行宽度，避免留下半行空白。
/// 断点由面板拿到的宽度决定，与窗口宽度、导航栏是否展开无关。
/// </summary>
public class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    /// <summary>当前列数（测量后有效），供布局验证使用</summary>
    public int ActualColumns { get; private set; } = 1;

    private int VisibleCount()
    {
        var n = 0;
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed)
                n++;
        return n;
    }

    private int ColumnsFor(double width, int count)
    {
        if (count == 0)
            return 1;
        var max = Math.Max(1, Math.Min(MaxColumns, count));
        if (double.IsInfinity(width))
            return max;
        var fit = (int)Math.Floor((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing));
        return Math.Clamp(fit, 1, max);
    }

    /// <summary>第 index 个可见子元素所在行、列，以及该行实际列数（最后一行可能更少）</summary>
    private static (int Row, int Col, int RowColumns) Slot(int index, int count, int columns)
    {
        var row = index / columns;
        var rowStart = row * columns;
        var rowColumns = Math.Min(columns, count - rowStart);
        return (row, index - rowStart, rowColumns);
    }

    private double CellWidth(double width, int rowColumns) =>
        Math.Max(0, (width - ColumnSpacing * (rowColumns - 1)) / rowColumns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = VisibleCount();
        var columns = ColumnsFor(availableSize.Width, count);
        ActualColumns = columns;
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth * columns + ColumnSpacing * (columns - 1) : availableSize.Width;

        var rows = count == 0 ? 0 : (count + columns - 1) / columns;
        var rowHeights = new double[rows];
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var (row, _, rowColumns) = Slot(i++, count, columns);
            child.Measure(new Size(CellWidth(width, rowColumns), double.PositiveInfinity));
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }

        var height = 0.0;
        foreach (var h in rowHeights)
            height += h;
        height += RowSpacing * Math.Max(0, rows - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = VisibleCount();
        var columns = ColumnsFor(finalSize.Width, count);
        ActualColumns = columns;
        var rows = count == 0 ? 0 : (count + columns - 1) / columns;

        // 再算一遍行高：Arrange 的宽度可能与 Measure 不同（例如滚动条出现）
        var rowHeights = new double[rows];
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var (row, _, rowColumns) = Slot(i++, count, columns);
            child.Measure(new Size(CellWidth(finalSize.Width, rowColumns), double.PositiveInfinity));
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }

        i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var (row, col, rowColumns) = Slot(i++, count, columns);
            var cellWidth = CellWidth(finalSize.Width, rowColumns);
            var y = 0.0;
            for (var r = 0; r < row; r++)
                y += rowHeights[r] + RowSpacing;
            child.Arrange(new Rect(col * (cellWidth + ColumnSpacing), y, cellWidth, rowHeights[row]));
        }
        return finalSize;
    }
}
