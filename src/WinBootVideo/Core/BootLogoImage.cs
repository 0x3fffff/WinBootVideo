using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinBootVideo.Core;

/// <summary>Produces the exact BMP3 format consumed by HackBGRT, without external tools.</summary>
public static class BootLogoImage
{
    public const int MaxInputBytes = 16 * 1024 * 1024;
    public static byte[] Convert(string path, int width = 320, int height = 180)
    {
        if (width < 32 || width > 600 || height < 32 || height > 360)
            throw new InvalidDataException("Logo 画布须在 32–600 × 32–360 像素之间。");
        string full = Path.GetFullPath(path);
        string extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp")
            throw new InvalidDataException("请选择静态 PNG、JPEG 或 BMP 图片。");
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidDataException("请选择本地磁盘图片。");
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > MaxInputBytes) throw new InvalidDataException("图片须非空，且不超过 16 MB。");
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("初版仅支持单帧静态图片。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 8192 || frame.PixelHeight > 8192 ||
            (long)frame.PixelWidth * frame.PixelHeight > 20_000_000)
            throw new InvalidDataException("原图每边最多 8192 像素，总像素不超过 2000 万。");
        // Render in pixels at 96 DPI. Flatten transparency over black and preserve aspect ratio.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            double scale = Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);
            double w = frame.PixelWidth * scale, h = frame.PixelHeight * scale;
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(frame, new Rect((width - w) / 2, (height - h) / 2, w, h));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        int stride = checked((width * 3 + 3) & ~3);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        return EncodeBgr24(width, height, pixels, stride);
    }

    public static byte[] EncodeBgr24(int width, int height, byte[] topDownPixels, int stride)
    {
        if (width < 32 || width > 600 || height < 32 || height > 360 || stride != ((width * 3 + 3) & ~3) || topDownPixels.Length != stride * height)
            throw new InvalidDataException("Logo 像素尺寸或步长无效。");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4d42); writer.Write(checked(54 + stride * height));
        writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)24); writer.Write(0); writer.Write(stride * height);
        writer.Write(3780); writer.Write(3780); writer.Write(0); writer.Write(0);
        for (int y = height - 1; y >= 0; y--)
        {
            writer.Write(topDownPixels, y * stride, width * 3);
            for (int pad = width * 3; pad < stride; pad++) writer.Write((byte)0);
        }
        byte[] result = output.ToArray(); Validate(result); return result;
    }

    public static (int Width, int Height) Validate(byte[] bmp)
    {
        if (bmp.Length < 54 || bmp.Length > 54 + 600 * 360 * 3 || BitConverter.ToUInt16(bmp, 0) != 0x4d42 ||
            BitConverter.ToInt32(bmp, 2) != bmp.Length || BitConverter.ToInt32(bmp, 6) != 0 ||
            BitConverter.ToInt32(bmp, 10) != 54 || BitConverter.ToInt32(bmp, 14) != 40 ||
            BitConverter.ToUInt16(bmp, 26) != 1 || BitConverter.ToUInt16(bmp, 28) != 24 || BitConverter.ToInt32(bmp, 30) != 0 ||
            BitConverter.ToInt32(bmp, 46) != 0 || BitConverter.ToInt32(bmp, 50) != 0)
            throw new InvalidDataException("图片必须是 54 字节头、24 位、未压缩的 BMP3。");
        int w = BitConverter.ToInt32(bmp, 18), h = BitConverter.ToInt32(bmp, 22);
        if (w < 32 || w > 600 || h < 32 || h > 360) throw new InvalidDataException("Logo 尺寸超出安全画布范围。");
        int size = ((w * 3 + 3) & ~3) * h;
        // Windows GDI (used by the official installer) writes zero biSizeImage for BI_RGB.
        // This is valid BMP3; actual file length and dimensions still bound every pixel.
        int declared = BitConverter.ToInt32(bmp, 34);
        if (bmp.Length != 54 + size || (declared != 0 && declared != size)) throw new InvalidDataException("BMP 像素数据不完整。");
        return (w, h);
    }
}
