using Glidecast.AutoZoom;
using Glidecast.Model;
using SkiaSharp;

namespace Glidecast.Render;

public enum BackgroundKind { Gradient, Solid, Image }

public enum LogoCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public enum WebcamShape { Circle, Square, Oval }

/// <summary>Look of the composite. Lengths are in source-screen pixels, so the
/// same style renders identically at preview and export resolutions. Colors are
/// "#RRGGBB" strings so the settings round-trip through project.json.</summary>
public sealed class StyleSettings
{
    public BackgroundKind Background { get; set; } = BackgroundKind.Gradient;
    public string BgTop { get; set; } = "#2F5FD0";      // gradient start ("Cobalt")
    public string BgBottom { get; set; } = "#0B1226";   // gradient end
    public string BgSolid { get; set; } = "#1F2430";
    public string? BgImage { get; set; }                 // wallpaper path
    public float BgBlur { get; set; }                    // wallpaper blur (source px)

    public float PaddingFraction { get; set; } = 0.07f;  // border around the screen card
    public float CornerRadius { get; set; } = 18f;        // card rounding
    public float ShadowBlur { get; set; } = 40f;
    public string Accent { get; set; } = "#3D7EFF";       // click effect / dot cursor tint

    public CursorStyle CursorStyle { get; set; } = CursorStyle.Classic;
    public float CursorScale { get; set; } = 1.6f;
    public float CursorSmoothing { get; set; } = 0.5f;    // 0 raw .. 1 floaty
    public bool HideIdleCursor { get; set; }
    public ClickEffect ClickEffect { get; set; } = ClickEffect.Ripple;
    public bool MotionBlur { get; set; }

    public bool DrawCursor { get; set; } = true;
    public bool DrawClicks { get; set; } = true;
    public bool DrawKeystrokes { get; set; } = true;
    public bool DrawAnnotations { get; set; } = true;
    /// <summary>A short fading streak behind the moving cursor.</summary>
    public bool CursorTrail { get; set; }

    /// <summary>Shape of the canvas (reframes 16:9 / 9:16 / 1:1).</summary>
    public CanvasAspect Aspect { get; set; } = CanvasAspect.Source;

    /// <summary>Brand logo drawn in a corner of every frame.</summary>
    public string? LogoImage { get; set; }
    public LogoCorner LogoCorner { get; set; } = LogoCorner.BottomRight;
    public float LogoSize { get; set; } = 0.1f;          // fraction of output width
    public float LogoOpacity { get; set; } = 0.85f;

    /// <summary>Webcam bubble, drawn in output space (it doesn't zoom with the screen).</summary>
    public bool ShowWebcam { get; set; } = true;
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;
    public float WebcamSize { get; set; } = 0.26f;       // height, fraction of the output's short side
    /// <summary>Position in the free space: 0 = left / top edge, 1 = right / bottom edge.</summary>
    public float WebcamX { get; set; }
    public float WebcamY { get; set; } = 1f;
    public bool WebcamMirror { get; set; } = true;

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
    // Output pixels per source pixel when the canvas's view is shown unzoomed
    // at full size; scales every style length. Follows the camera's view.
    private float _unit;
    private SKImage? _bgImage;
    private string? _bgKey;
    private (string? path, int w) _logoKey;
    private SKBitmap? _logo;

    private CursorTrack? _cursor;
    private SKBitmap? _webcam;
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
        _unit = Unit(camera, OutW, OutH);
        _surface = SKSurface.Create(new SKImageInfo(OutW, OutH, SKColorType.Bgra8888, SKAlphaType.Premul));
        _srcBitmap = new SKBitmap(new SKImageInfo(SrcW, SrcH, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    public SKMatrix CameraMatrix(double tMs) => CameraFor(_p, Camera, Style, OutW, OutH, tMs);

    /// <summary>Camera matrix (source px → output px) for any output size, e.g.
    /// to map a click on a preview back to source pixels via its inverse.</summary>
    public static SKMatrix CameraFor(Project p, CameraTrack cam, StyleSettings style, int outW, int outH, double tMs)
    {
        float unit = Unit(cam, outW, outH);
        var (scale, cx, cy) = cam.At(tMs);
        float cardScale = 1f - 2f * style.PaddingFraction;
        float k = (float)(cardScale * scale) * unit;
        var m = SKMatrix.CreateTranslation(outW / 2f, outH / 2f);
        m = m.PreConcat(SKMatrix.CreateScale(k, k));
        m = m.PreConcat(SKMatrix.CreateTranslation(-(float)cx, -(float)cy));
        return m;
    }

    /// <summary>Output pixels per annotation style unit at time t (what <see cref="AnnotationPainter"/> draws with).</summary>
    public static float AnnotationUnit(CameraTrack cam, int outW, int outH, double tMs) =>
        Unit(cam, outW, outH) * (float)Math.Sqrt(cam.At(tMs).scale);

    private static float Unit(CameraTrack cam, int outW, int outH) =>
        (float)Math.Min(outW / cam.ViewW, outH / cam.ViewH);

    private CursorTrack CursorFor(StyleSettings st)
    {
        if (_cursor == null || Math.Abs(_cursor.Smoothing - st.CursorSmoothing) > 0.001)
            _cursor = new CursorTrack(_p, st.CursorSmoothing);
        return _cursor;
    }

    /// <summary>Composite one frame from raw BGRA source pixels into BGRA output bytes.</summary>
    /// <param name="webcam">The webcam frame on screen at tMs (BGRA, project webcam size), or null.</param>
    public void RenderInto(byte[] srcBgra, double tMs, byte[] dst, byte[]? webcam = null)
    {
        System.Runtime.InteropServices.Marshal.Copy(srcBgra, 0,
            _srcBitmap.GetPixels(), Math.Min(srcBgra.Length, SrcW * SrcH * 4));

        var st = Style;
        var canvas = _surface.Canvas;

        _unit = Unit(Camera, OutW, OutH);
        var m = CameraMatrix(tMs);
        // The card is the canvas's view window (the whole screen unless reframed).
        var full = new SKRect(0, 0, _p.Width, _p.Height);
        var (_, vcx, vcy) = Camera.At(tMs);
        var window = new SKRect((float)(vcx - Camera.ViewW / 2), (float)(vcy - Camera.ViewH / 2),
            (float)(vcx + Camera.ViewW / 2), (float)(vcy + Camera.ViewH / 2));
        if (!window.IntersectsWith(full)) window = full;
        else window.Intersect(full);
        var dstRect = m.MapRect(window);
        var screenRect = m.MapRect(full);
        float radius = st.CornerRadius * _unit;
        var round = new SKRoundRect(dstRect, radius, radius);

        // Zoomed in far enough that the card (corners included) fills the frame:
        // background and shadow are invisible, so skip them.
        var inner = dstRect;
        inner.Inflate(-radius, -radius);
        bool covered = inner.Left <= 0 && inner.Top <= 0 && inner.Right >= OutW && inner.Bottom >= OutH;

        // 1) Background.
        if (!covered) canvas.DrawImage(Background(st), 0, 0);
        else canvas.Clear(SKColors.Black);

        // 2) Layered shadow: a wide soft one plus a tight contact shadow.
        if (st.ShadowBlur > 0 && !covered)
        {
            // Mask-filter blurs of a round rect take Skia's analytic fast path;
            // an image-filter blur would blur a whole (possibly huge) layer.
            float blur = st.ShadowBlur * _unit;
            using (var wide = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true })
            {
                wide.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
                canvas.Save();
                canvas.Translate(0, blur * 0.35f);
                canvas.DrawRoundRect(round, wide);
                canvas.Restore();
            }
            using var tight = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true };
            tight.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur * 0.15f + 1);
            canvas.DrawRoundRect(round, tight);
        }

        // 3) The screen, clipped to the card; softened slightly while the camera moves fast.
        // Bicubic only where it shows: magnified text. Near 1:1 or shrinking a
        // little, bilinear is indistinguishable and many times cheaper (High
        // would also rebuild mipmaps of every frame).
        float mag = screenRect.Width / SrcW;
        var quality = mag > 1.08f ? SKFilterQuality.High : mag > 0.6f ? SKFilterQuality.Low : SKFilterQuality.Medium;
        // Fill the round rect with the screen as a shader: one anti-aliased
        // shape, no clip mask over the whole frame.
        var local = SKMatrix.CreateScale(screenRect.Width / SrcW, screenRect.Height / SrcH)
            .PostConcat(SKMatrix.CreateTranslation(screenRect.Left, screenRect.Top));
        using (var img = SKImage.FromPixels(_srcBitmap.Info, _srcBitmap.GetPixels()))
        using (var shader = img.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, local))
        using (var paint = new SKPaint { FilterQuality = quality, IsAntialias = true, Shader = shader })
        {
            if (st.MotionBlur)
            {
                // Only the fastest moves (long pans) get a touch of softness.
                float sigma = (float)Math.Clamp((Camera.MotionAt(tMs) - 2.0) * 0.5, 0, 1.1) * Math.Max(0.6f, _unit);
                if (sigma > 0.3f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
            }
            if (covered) canvas.DrawRect(new SKRect(0, 0, OutW, OutH), paint);
            else canvas.DrawRoundRect(round, paint);
        }

        // Hairline edge gives the card a crisp, glassy finish.
        using (var edge = new SKPaint { Color = new SKColor(255, 255, 255, 28), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, _unit) })
            canvas.DrawRoundRect(round, edge);

        // 4) Overlays in output space. Annotations stay on the card.
        if (st.DrawAnnotations && _p.Annotations is { Count: > 0 })
        {
            canvas.Save();
            canvas.ClipRoundRect(round, SKClipOperation.Intersect, antialias: true);
            AnnotationPainter.Draw(canvas, _p.Annotations, tMs, m, _unit * (float)Math.Sqrt(Camera.At(tMs).scale),
                StyleSettings.ParseColor(st.Accent, new SKColor(0x3D, 0x7E, 0xFF)), round.Rect);
            canvas.Restore();
        }
        DrawOverlays(canvas, st, m, tMs);
        if (webcam != null) DrawWebcam(canvas, st, webcam);
        DrawLogo(canvas, st);

        canvas.Flush();
        using var snap = _surface.Snapshot();
        using var pm = snap.PeekPixels();
        System.Runtime.InteropServices.Marshal.Copy(pm.GetPixels(), dst, 0, Math.Min(dst.Length, OutW * OutH * 4));
    }

    /// <summary>The background never moves, so it's drawn once per style and reused.</summary>
    private SKImage Background(StyleSettings st)
    {
        string key = $"{st.Background}|{st.BgTop}|{st.BgBottom}|{st.BgSolid}|{st.BgImage}|{st.BgBlur}";
        if (_bgImage != null && key == _bgKey) return _bgImage;
        _bgImage?.Dispose();
        using var surf = SKSurface.Create(new SKImageInfo(OutW, OutH, SKColorType.Bgra8888, SKAlphaType.Premul));
        DrawBackground(surf.Canvas, st);
        _bgImage = surf.Snapshot();
        _bgKey = key;
        return _bgImage;
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
        var accent = StyleSettings.ParseColor(st.Accent, new SKColor(0x3D, 0x7E, 0xFF));
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
            if (st.CursorTrail && opacity > 0)
                DrawTrail(canvas, track, m, tMs, st.CursorScale * _unit * zoomBoost, accent, opacity);
            CursorPainter.Draw(canvas, st.CursorStyle, pt, st.CursorScale * _unit * zoomBoost, press, accent, opacity);
        }

        if (st.DrawKeystrokes)
            DrawKeystrokes(canvas, st, tMs);
    }

    /// <summary>A tapering streak along the last ~170 ms of cursor path; invisible when still.</summary>
    private static void DrawTrail(SKCanvas canvas, CursorTrack track, SKMatrix m, double tMs, float size, SKColor accent, float opacity)
    {
        const int N = 12;
        const double Step = 14;
        var prev = m.MapPoint(Pt(track.At(tMs)));
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        for (int i = 1; i <= N; i++)
        {
            var pt = m.MapPoint(Pt(track.At(tMs - i * Step)));
            if (SKPoint.Distance(prev, pt) > 0.75f)
            {
                float f = 1 - (i - 1) / (float)N;
                paint.StrokeWidth = Math.Max(1f, 5.5f * size * f);
                paint.Color = accent.WithAlpha((byte)(150 * f * f * opacity));
                canvas.DrawLine(prev, pt, paint);
            }
            prev = pt;
        }

        static SKPoint Pt((double x, double y) p) => new((float)p.x, (float)p.y);
    }

    private void DrawLogo(SKCanvas canvas, StyleSettings st)
    {
        if (string.IsNullOrEmpty(st.LogoImage) || st.LogoOpacity <= 0) return;
        int w = Math.Max(8, (int)(OutW * Math.Clamp(st.LogoSize, 0.02f, 0.5f)));
        if (_logoKey != (st.LogoImage, w))
        {
            _logo?.Dispose();
            _logo = null;
            _logoKey = (st.LogoImage, w);
            try
            {
                if (File.Exists(st.LogoImage) && SKBitmap.Decode(st.LogoImage) is { } raw)
                {
                    using (raw)
                    {
                        int h = Math.Max(1, (int)Math.Round(raw.Height * (w / (double)raw.Width)));
                        _logo = raw.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                    }
                }
            }
            catch { _logo = null; }
        }
        if (_logo == null) return;
        float margin = Math.Min(OutW, OutH) * 0.035f;
        float x = st.LogoCorner is LogoCorner.TopLeft or LogoCorner.BottomLeft ? margin : OutW - margin - _logo.Width;
        float y = st.LogoCorner is LogoCorner.TopLeft or LogoCorner.TopRight ? margin : OutH - margin - _logo.Height;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(st.LogoOpacity, 0, 1))), FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(_logo, x, y, paint);
    }

    /// <summary>Where the webcam bubble sits in an output frame.</summary>
    public static SKRect WebcamRect(StyleSettings st, int outW, int outH)
    {
        float shortSide = Math.Min(outW, outH);
        float h = shortSide * Math.Clamp(st.WebcamSize, 0.08f, 0.7f);
        float w = st.WebcamShape == WebcamShape.Oval ? h * 4f / 3f : h;
        float margin = shortSide * 0.035f;
        float x = margin + Math.Clamp(st.WebcamX, 0, 1) * Math.Max(0, outW - 2 * margin - w);
        float y = margin + Math.Clamp(st.WebcamY, 0, 1) * Math.Max(0, outH - 2 * margin - h);
        return new SKRect(x, y, x + w, y + h);
    }

    /// <summary>The outline of the bubble (circle, rounded square or oval).</summary>
    public static SKPath WebcamPath(StyleSettings st, SKRect r)
    {
        var path = new SKPath();
        if (st.WebcamShape == WebcamShape.Square) path.AddRoundRect(r, r.Height * 0.16f, r.Height * 0.16f);
        else path.AddOval(r);
        return path;
    }

    private void DrawWebcam(SKCanvas canvas, StyleSettings st, byte[] frame)
    {
        if (!st.ShowWebcam || _p.Webcam is not { Width: > 0, Height: > 0 } wc) return;
        if (_webcam == null || _webcam.Width != wc.Width || _webcam.Height != wc.Height)
        {
            _webcam?.Dispose();
            _webcam = new SKBitmap(new SKImageInfo(wc.Width, wc.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        System.Runtime.InteropServices.Marshal.Copy(frame, 0, _webcam.GetPixels(), Math.Min(frame.Length, wc.Width * wc.Height * 4));

        var r = WebcamRect(st, OutW, OutH);
        using var path = WebcamPath(st, r);
        float u = Math.Min(OutW, OutH) / 1080f;
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110) })
        {
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18 * u);
            canvas.Save();
            canvas.Translate(0, 6 * u);
            canvas.DrawPath(path, shadow);
            canvas.Restore();
        }

        // Aspect-fill the shape with the frame, mirrored like a selfie view if asked.
        float s = Math.Max(r.Width / wc.Width, r.Height / wc.Height);
        float iw = wc.Width * s, ih = wc.Height * s;
        var dest = new SKRect(r.MidX - iw / 2, r.MidY - ih / 2, r.MidX + iw / 2, r.MidY + ih / 2);
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
        if (st.WebcamMirror)
        {
            canvas.Translate(r.MidX, 0);
            canvas.Scale(-1, 1);
            canvas.Translate(-r.MidX, 0);
        }
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            canvas.DrawBitmap(_webcam, dest, paint);
        canvas.Restore();

        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, 3 * u), Color = new SKColor(255, 255, 255, 70) };
        canvas.DrawPath(path, ring);
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
        _logo?.Dispose();
        _bgImage?.Dispose();
        _webcam?.Dispose();
    }
}
