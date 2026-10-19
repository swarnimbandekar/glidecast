using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glidecast.Render;
using SkiaSharp;

namespace Glidecast.App.Services;

/// <summary>Small images for the inspector (cursor and click-effect tiles,
/// rendered with the engine's painter so they match the video exactly) and
/// the timeline film strip.</summary>
public static class PreviewImages
{
    private static BitmapSource Render(int w, int h, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        using var img = surface.Snapshot();
        using var pm = img.PeekPixels();
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pm.GetPixels(), w * h * 4, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource Cursor(CursorStyle style, SKColor accent)
    {
        // 2x pixels, shown at half size for crispness on high-DPI screens.
        return Render(112, 88, c =>
        {
            bool centered = CursorPainter.IsCentered(style);
            var at = centered ? new SKPoint(56, 44) : new SKPoint(46, 22);
            CursorPainter.Draw(c, style, at, 2.3f, 0, accent);
        });
    }

    public static BitmapSource Click(ClickEffect effect, SKColor accent)
    {
        return Render(112, 88, c =>
        {
            if (effect == ClickEffect.None)
            {
                using var p = new SKPaint { Color = new SKColor(0x6E, 0x75, 0x84), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
                c.DrawCircle(56, 44, 20, p);
                c.DrawLine(42, 58, 70, 30, p);
                return;
            }
            CursorPainter.DrawClick(c, effect, new SKPoint(56, 44), 0.95f, 0.4f, accent);
            using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawCircle(56, 44, 4, dot);
        });
    }

    /// <summary>Decodes ~<paramref name="count"/> evenly spaced frames, <paramref name="height"/> px tall.</summary>
    public static List<BitmapSource> FilmStrip(string video, int srcW, int srcH, double durationMs, int count = 48, int height = 72)
    {
        int h = height & ~1;
        int w = (int)Math.Round(h * srcW / (double)srcH) & ~1;
        double rate = count / Math.Max(0.5, durationMs / 1000.0);
        string args = string.Create(CultureInfo.InvariantCulture,
            $"-i \"{video}\" -vf fps={rate:0.####},scale={w}:{h} -f rawvideo -pix_fmt bgra -");
        var proc = FFmpeg.Start(args, redirectIn: false, redirectOut: true);
        var frames = new List<BitmapSource>();
        var buf = new byte[w * h * 4];
        var stream = proc.StandardOutput.BaseStream;
        try
        {
            while (frames.Count < count * 2 && ReadFull(stream, buf))
            {
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, w * 4);
                bmp.Freeze();
                frames.Add(bmp);
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }
        return frames;
    }

    private static bool ReadFull(System.IO.Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }
}
