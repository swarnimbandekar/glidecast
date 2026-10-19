using Glidecast.AutoZoom;
using Glidecast.Model;
using SkiaSharp;

namespace Glidecast.Render;

public enum BackgroundKind { Gradient, Solid, Image }

/// <summary>Look of the composite. Lengths are in source-screen pixels, so the
/// same style renders identically at preview and export resolutions. Colors are
/// "#RRGGBB" strings so the settings round-trip through project.json.</summary>
public sealed class StyleSettings
{
    public BackgroundKind Background { get; set; } = BackgroundKind.Gradient;
    public string BgTop { get; set; } = "#6D5BD0";      // gradient start
    public string BgBottom { get; set; } = "#1C1640";   // gradient end
    public string BgSolid { get; set; } = "#1F2430";
    public string? BgImage { get; set; }                 // wallpaper path
    public float BgBlur { get; set; }                    // wallpaper blur (source px)

    public float PaddingFraction { get; set; } = 0.07f;  // border around the screen card
    public float CornerRadius { get; set; } = 18f;        // card rounding
    public float ShadowBlur { get; set; } = 40f;
    public string Accent { get; set; } = "#7C5CFF";       // click effect / dot cursor tint

    public CursorStyle CursorStyle { get; set; } = CursorStyle.Classic;
    public float CursorScale { get; set; } = 1.6f;
    public float CursorSmoothing { get; set; } = 0.5f;    // 0 raw .. 1 floaty
    public bool HideIdleCursor { get; set; }
    public ClickEffect ClickEffect { get; set; } = ClickEffect.Ripple;
    public bool MotionBlur { get; set; }

    public bool DrawCursor { get; set; } = true;
    public bool DrawClicks { get; set; } = true;
    public bool DrawKeystrokes { get; set; } = true;

    public StyleSettings Clone() => (StyleSettings)MemberwiseClone();

    public static SKColor ParseColor(string? hex, SKColor fallback) =>
        hex != null && SKColor.TryParse(hex, out var c) ? c : fallback;
}

/// <summary>
/// Composites one output frame: background, the screen as a rounded, shadowed
/// "card", and synthetic cursor / click / keystroke overlays — all under the
/// spring-driven camera.
/// </summary>
public sealed class Compositor : IDisposable
{
    private readonly Project _p;
    public int OutW { get; }
    public int OutH { get; }
    public int SrcW { get; }
    public int SrcH { get; }

    /// <summary>Camera motion; may be swapped between frames.</summary>
    public CameraTrack Camera { get; set; }

    /// <summary>Style; may be swapped between frames.</summary>
    public StyleSettings Style { get; set; }

    private readonly SKSurface _surface;
    private readonly SKBitmap _srcBitmap;
    // Output pixels per source pixel when the whole screen is shown unzoomed
    // at full size; scales every style length.
    private readonly float _unit;

    private CursorTrack? _cursor;
    private (string? path, float blur) _wallKey;
    private SKBitmap? _wallpaper;

    /// <param name="outW">Output width (default: source width).</param>
    /// <param name="srcW">Pixel width of the frames passed to RenderInto
    /// (default: project width). Lets a preview decode a downscaled source.</param>
    public Compositor(Project p, CameraTrack camera, StyleSettings? style = null,
        int outW = 0, int outH = 0, int srcW = 0, int srcH = 0)
    {
        _p = p;
        Camera = camera;
        Style = style ?? new StyleSettings();
        OutW = outW > 0 ? outW : p.Width;
        OutH = outH > 0 ? outH : p.Height;
        SrcW = srcW > 0 ? srcW : p.Width;
        SrcH = srcH > 0 ? srcH : p.Height;
        _unit = Math.Min(OutW / (float)p.Width, OutH / (float)p.Height);
        _surface = SKSurface.Create(new SKImageInfo(OutW, OutH, SKColorType.Bgra8888, SKAlphaType.Premul));
        _srcBitmap = new SKBitmap(new SKImageInfo(SrcW, SrcH, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    public SKMatrix CameraMatrix(double tMs) => CameraFor(_p, Camera, Style, OutW, OutH, tMs);

    /// <summary>Camera matrix (source px → output px) for any output size, e.g.
    /// to map a click on a preview back to source pixels via its inverse.</summary>
    public static SKMatrix CameraFor(Project p, CameraTrack cam, StyleSettings style, int outW, int outH, double tMs)
    {
        float unit = Math.Min(outW / (float)p.Width, outH / (float)p.Height);
        var (scale, cx, cy) = cam.At(tMs);
        float cardScale = 1f - 2f * style.PaddingFraction;
        float k = (float)(cardScale * scale) * unit;
        var m = SKMatrix.CreateTranslation(outW / 2f, outH / 2f);
        m = m.PreConcat(SKMatrix.CreateScale(k, k));
        m = m.PreConcat(SKMatrix.CreateTranslation(-(float)cx, -(float)cy));
        return m;
    }

    private CursorTrack CursorFor(StyleSettings st)
    {
        if (_cursor == null || Math.Abs(_cursor.Smoothing - st.CursorSmoothing) > 0.001)
            _cursor = new CursorTrack(_p, st.CursorSmoothing);
        return _cursor;
    }

    /// <summary>Composite one frame from raw BGRA source pixels into BGRA output bytes.</summary>
    public void RenderInto(byte[] srcBgra, double tMs, byte[] dst)
    {
        System.Runtime.InteropServices.Marshal.Copy(srcBgra, 0,
            _srcBitmap.GetPixels(), Math.Min(srcBgra.Length, SrcW * SrcH * 4));

        var st = Style;
        var canvas = _surface.Canvas;

        // 1) Background.
        DrawBackground(canvas, st);

        var m = CameraMatrix(tMs);
        var dstRect = m.MapRect(new SKRect(0, 0, _p.Width, _p.Height));
        float radius = st.CornerRadius * _unit;
        var round = new SKRoundRect(dstRect, radius, radius);

        // 2) Layered shadow: a wide soft one plus a tight contact shadow.
        if (st.ShadowBlur > 0)
        {
            float blur = st.ShadowBlur * _unit;
            using (var wide = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true })
            {
                wide.ImageFilter = SKImageFilter.CreateBlur(blur, blur);
                canvas.Save();
                canvas.Translate(0, blur * 0.35f);
                canvas.DrawRoundRect(round, wide);
                canvas.Restore();
            }
            using var tight = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true };
            tight.ImageFilter = SKImageFilter.CreateBlur(blur * 0.15f + 1, blur * 0.15f + 1);
            canvas.DrawRoundRect(round, tight);
        }

        // 3) The screen, clipped to the card; softened slightly while the camera moves fast.
        canvas.Save();
        canvas.ClipRoundRect(round, SKClipOperation.Intersect, antialias: true);
        using (var img = SKImage.FromPixels(_srcBitmap.Info, _srcBitmap.GetPixels()))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            if (st.MotionBlur)
            {
                // Only the fastest moves (long pans) get a touch of softness.
                float sigma = (float)Math.Clamp((Camera.MotionAt(tMs) - 2.0) * 0.5, 0, 1.1) * Math.Max(0.6f, _unit);
                if (sigma > 0.3f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
            }
            canvas.DrawImage(img, dstRect, paint);
        }
        canvas.Restore();

        // Hairline edge gives the card a crisp, glassy finish.
        using (var edge = new SKPaint { Color = new SKColor(255, 255, 255, 28), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, _unit) })
            canvas.DrawRoundRect(round, edge);

        // 4) Overlays in output space.
        DrawOverlays(canvas, st, m, tMs);

        canvas.Flush();
        using var snap = _surface.Snapshot();
        using var pm = snap.PeekPixels();
        System.Runtime.InteropServices.Marshal.Copy(pm.GetPixels(), dst, 0, Math.Min(dst.Length, OutW * OutH * 4));
    }

    private void DrawBackground(SKCanvas canvas, StyleSettings st)
    {
        var full = new SKRect(0, 0, OutW, OutH);
        if (st.Background == BackgroundKind.Image && LoadWallpaper(st.BgImage, st.BgBlur) is { } wp)
        {
            // Aspect-fill (cover) the frame.
            float s = Math.Max(OutW / (float)wp.Width, OutH / (float)wp.Height);
            float w = wp.Width * s, h = wp.Height * s;
            var r = new SKRect((OutW - w) / 2, (OutH - h) / 2, (OutW + w) / 2, (OutH + h) / 2);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(wp, r, paint);
            return;
        }
        if (st.Background == BackgroundKind.Solid)
        {
            canvas.Clear(StyleSettings.ParseColor(st.BgSolid, SKColors.Black));
            return;
        }
        var top = StyleSettings.ParseColor(st.BgTop, SKColors.Black);
        var bottom = StyleSettings.ParseColor(st.BgBottom, SKColors.Black);
        using var bg = new SKPaint();
        bg.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(OutW, OutH), new[] { top, bottom }, null, SKShaderTileMode.Clamp);
        canvas.DrawRect(full, bg);
        // Soft light bloom in the upper-left adds depth to flat gradients.
        using var bloom = new SKPaint();
        bloom.Shader = SKShader.CreateRadialGradient(new SKPoint(OutW * 0.2f, OutH * 0.1f), Math.Max(OutW, OutH) * 0.7f,
            new[] { new SKColor(255, 255, 255, 34), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp);
        canvas.DrawRect(full, bloom);
    }

    private SKBitmap? LoadWallpaper(string? path, float blur)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_wallKey == (path, blur)) return _wallpaper;
        _wallpaper?.Dispose();
        _wallpaper = null;
        _wallKey = (path, blur);
        try
        {
            if (!File.Exists(path)) return null;
            using var decoded = SKBitmap.Decode(path);
            if (decoded == null) return null;
            float s = Math.Min(1f, Math.Max(OutW / (float)decoded.Width, OutH / (float)decoded.Height));
            using var sized = decoded.Resize(new SKImageInfo(
                Math.Max(1, (int)(decoded.Width * s)), Math.Max(1, (int)(decoded.Height * s))), SKFilterQuality.Medium);
            if (blur <= 0.5f)
            {
                _wallpaper = sized.Copy();
                return _wallpaper;
            }
            // Blur once and cache.
            var info = new SKImageInfo(sized.Width, sized.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(blur * _unit, blur * _unit, SKShaderTileMode.Clamp) };
            surf.Canvas.DrawBitmap(sized, 0, 0, paint);
            using var snap = surf.Snapshot();
            _wallpaper = SKBitmap.FromImage(snap);
        }
        catch { _wallpaper = null; }
        return _wallpaper;
    }

    private void DrawOverlays(SKCanvas canvas, StyleSettings st, SKMatrix m, double tMs)
    {
        var accent = StyleSettings.ParseColor(st.Accent, new SKColor(0x7C, 0x5C, 0xFF));
        var (zoom, _, _) = Camera.At(tMs);
        float zoomBoost = (float)Math.Sqrt(zoom); // overlays grow a little with the zoom

        if (st.DrawClicks)
        {
            foreach (var c in _p.Clicks)
            {
                double age = (tMs - c.T) / 520.0;
                if (age < 0 || age > 1) continue;
                var pt = m.MapPoint(new SKPoint(c.X, c.Y));
                CursorPainter.DrawClick(canvas, st.ClickEffect, pt, _unit * zoomBoost, (float)age, accent);
            }
        }

        if (st.DrawCursor)
        {
            var track = CursorFor(st);
            var (sx, sy) = track.At(tMs);
            var pt = m.MapPoint(new SKPoint((float)sx, (float)sy));

            // Press: quick squeeze at each click, spring back.
            float press = 0;
            foreach (var c in _p.Clicks)
            {
                double d = tMs - c.T;
                if (d < -60 || d > 320) continue;
                press = Math.Max(press, d < 0 ? (float)((d + 60) / 60) : (float)Math.Exp(-d / 90.0));
            }

            float opacity = 1;
            if (st.HideIdleCursor)
            {
                double still = track.StillFor(tMs);
                opacity = (float)Math.Clamp(1 - (still - 2500) / 400, 0, 1);
            }
            CursorPainter.Draw(canvas, st.CursorStyle, pt, st.CursorScale * _unit * zoomBoost, press, accent, opacity);
        }

        if (st.DrawKeystrokes)
            DrawKeystrokes(canvas, st, tMs);
    }

    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
        { "Ctrl", "Shift", "Alt", "Win", "Right Ctrl", "Right Shift", "Right Alt", "Left Windows", "Right Windows" };

    private void DrawKeystrokes(SKCanvas canvas, StyleSettings st, double tMs)
    {
        // Key-downs in the last 1.4 s, as individual keycaps, bottom-center.
        const double Window = 1400;
        var recent = new List<(string sym, double t)>();
        for (int i = _p.Keys.Count - 1; i >= 0 && recent.Count < 8; i--)
        {
            var k = _p.Keys[i];
            if (tMs < k.T) continue;
            if (tMs - k.T > Window) break;
            if (!k.Down) continue;
            if (recent.Count > 0 && recent[0].sym == k.Symbol && recent[0].t - k.T < 60) continue; // auto-repeat
            recent.Insert(0, (k.Symbol, k.T));
        }
        if (recent.Count == 0) return;
        double newest = recent[^1].t;
        float alpha = (float)Math.Clamp((Window - (tMs - newest)) / 250.0, 0, 1);
        float appear = (float)Math.Clamp((tMs - recent[0].t) / 120.0, 0, 1);
        alpha *= appear;
        if (alpha <= 0) return;

        float u = _unit * 1.25f;
        using var font = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)(255 * alpha)), IsAntialias = true, TextSize = 22 * u,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
        };
        float capH = 44 * u, gap = 8 * u, padX = 14 * u;
        var widths = recent.Select(r => Math.Max(capH, font.MeasureText(r.sym) + 2 * padX)).ToList();
        float total = widths.Sum() + gap * (widths.Count - 1);
        float x = (OutW - total) / 2;
        float y = OutH - OutH * st.PaddingFraction * 0.5f - capH * 0.5f - 10 * u + (1 - appear) * 10 * u;

        using var tray = new SKPaint { Color = new SKColor(10, 12, 16, (byte)(150 * alpha)), IsAntialias = true };
        tray.ImageFilter = SKImageFilter.CreateDropShadow(0, 4 * u, 10 * u, 10 * u, new SKColor(0, 0, 0, (byte)(90 * alpha)));
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x - 10 * u, y - capH / 2 - 10 * u, x + total + 10 * u, y + capH / 2 + 12 * u), 16 * u), tray);

        for (int i = 0; i < recent.Count; i++)
        {
            float w = widths[i];
            var cap = new SKRect(x, y - capH / 2, x + w, y + capH / 2);
            bool mod = Modifiers.Contains(recent[i].sym);
            using var bottom = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(140 * alpha)), IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(cap.Left, cap.Top + 3 * u, cap.Right, cap.Bottom + 3 * u), 9 * u), bottom);
            using var face = new SKPaint
            {
                Color = (mod ? new SKColor(0x3A, 0x3F, 0x4D) : new SKColor(0x2A, 0x2E, 0x38)).WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
            };
            canvas.DrawRoundRect(new SKRoundRect(cap, 9 * u), face);
            float tw = font.MeasureText(recent[i].sym);
            canvas.DrawText(recent[i].sym, cap.MidX - tw / 2, cap.MidY + 8 * u, font);
            x += w + gap;
        }
    }

    public void Dispose()
    {
        _surface.Dispose();
        _srcBitmap.Dispose();
        _wallpaper?.Dispose();
    }
}
