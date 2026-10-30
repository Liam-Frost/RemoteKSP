using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace IconGenerator;

/// <summary>
/// 从 src/Shared/LOGO/RemoteKSP_LOGO.svg 生成多分辨率 RemoteKSP.ico。
/// 仅开发阶段使用：每个尺寸都直接从矢量源渲染，不会被打包进 Client/Server。
/// 用法（仓库任意子目录下）：dotnet run --project tools/IconGenerator -c Release
/// </summary>
internal static class Program
{
    private const string SvgPath = "src/Shared/LOGO/RemoteKSP_LOGO.svg";
    private const string IcoPath = "src/Shared/LOGO/RemoteKSP.ico";
    // 从大到小：WPF 按 pack URI 加载 .ico 时只解码第一帧，大图缩小比 16px 放大清晰；Windows 选 PE 图标与顺序无关
    private static readonly int[] Sizes = [256, 128, 64, 48, 32, 24, 16];

    [STAThread]
    private static int Main()
    {
        var root = FindRepoRoot();
        var svg = Path.Combine(root, SvgPath);
        var ico = Path.Combine(root, IcoPath);
        Console.WriteLine($"Source SVG: {svg}");

        var drawing = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true })
            .Read(svg) ?? throw new InvalidOperationException("无法解析 SVG");
        var viewBox = ReadViewBox(svg);
        Console.WriteLine($"ViewBox: {viewBox}  Drawing bounds: {drawing.Bounds}");

        var images = Sizes.Select(size => Render(drawing, viewBox, size)).ToList();
        WriteIco(ico, images);

        // 预览图便于人工检查，写到临时目录，不进仓库
        var previewDir = Path.Combine(Path.GetTempPath(), "RemoteKSP-icon-preview");
        Console.WriteLine($"Preview PNGs: {previewDir}");
        Directory.CreateDirectory(previewDir);
        foreach (var img in images)
            File.WriteAllBytes(Path.Combine(previewDir, $"{img.PixelWidth}.png"), EncodePng(img));

        Console.WriteLine($"Output ICO: {ico}");
        Console.WriteLine($"Sizes: {string.Join(", ", Sizes.Select(s => $"{s}x{s}"))}");
        return 0;
    }

    /// <summary>从当前目录向上找仓库根目录（包含 SVG 源文件的目录）</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SvgPath)))
                return dir.FullName;
        throw new FileNotFoundException($"找不到 {SvgPath}，请在仓库目录内运行");
    }

    /// <summary>读取 viewBox，保留设计稿原有画布和留白</summary>
    private static Rect ReadViewBox(string svgPath)
    {
        var el = XDocument.Load(svgPath).Root!;
        var parts = ((string?)el.Attribute("viewBox"))?
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (parts is { Length: 4 })
            return new Rect(parts[0], parts[1], parts[2], parts[3]);
        return new Rect(0, 0, double.Parse((string)el.Attribute("width")!, CultureInfo.InvariantCulture),
            double.Parse((string)el.Attribute("height")!, CultureInfo.InvariantCulture));
    }

    /// <summary>把 viewBox 等比缩放后居中到 size×size 透明画布（非预乘 BGRA32）</summary>
    private static BitmapSource Render(Drawing drawing, Rect viewBox, int size)
    {
        var scale = Math.Min(size / viewBox.Width, size / viewBox.Height);
        var offsetX = (size - viewBox.Width * scale) / 2;
        var offsetY = (size - viewBox.Height * scale) / 2;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(offsetX, offsetY));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-viewBox.X, -viewBox.Y));
            dc.PushClip(new RectangleGeometry(viewBox));
            dc.DrawDrawing(drawing);
        }
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var result = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        result.Freeze();
        return result;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>32 位 DIB：BITMAPINFOHEADER + 自下而上的 BGRA 像素 + 1bpp AND 掩码（高度记为 2 倍）</summary>
    private static byte[] EncodeDib(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        image.CopyPixels(pixels, stride, 0);
        int maskStride = ((w + 31) / 32) * 4;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(stride * h + maskStride * h);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--)
            bw.Write(pixels, y * stride, stride);
        for (int y = h - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < w; x++)
                if (pixels[y * stride + x * 4 + 3] == 0)
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(row);
        }
        return ms.ToArray();
    }

    /// <summary>ICO：6 字节头 + 每帧 16 字节目录 + 图像数据。256 用 PNG，其余用 DIB</summary>
    private static void WriteIco(string path, IReadOnlyList<BitmapSource> images)
    {
        var data = images.Select(img => img.PixelWidth >= 256 ? EncodePng(img) : EncodeDib(img)).ToList();
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)images.Count);

        var offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            var size = images[i].PixelWidth;
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(data[i].Length); bw.Write(offset);
            offset += data[i].Length;
        }
        foreach (var d in data)
            bw.Write(d);
    }
}
