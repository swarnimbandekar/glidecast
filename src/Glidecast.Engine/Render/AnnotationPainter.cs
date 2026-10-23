using Glidecast.Model;
using SkiaSharp;

namespace Glidecast.Render;

/// <summary>
/// Draws step badges, highlight boxes, spotlights, arrows and callouts. Each
/// lives in source-screen space (mapped through the camera, so it stays glued
/// to the UI it points at) and animates in and out over a few frames.
/// </summary>
public static class AnnotationPainter
{
    private const double InMs = 220, OutMs = 240;

    /// <param name="u">Output pixels per style unit (already includes the zoom boost).</param>
    /// <param name="bounds">The visible card, for keeping labels on screen.</param>
    public static void Draw(SKCanvas canvas, IReadOnlyList<Annotation> list, double tMs, SKMatrix m, float u, SKColor accent, SKRect bounds)
    {
        // Spotlights first: they dim everything else, including other annotations' surroundings.
        foreach (var a in list)
            if (a.Kind == AnnotationKind.Spotlight && Visible(a, tMs, out float k)) Spotlight(canvas, a, m, u, k, bounds);
        foreach (var a in list)
        {
            if (a.Kind == AnnotationKind.Spotlight || !Visible(a, tMs, out float k)) continue;
            var color = StyleSettings.ParseColor(a.Color, accent);
            switch (a.Kind)
            {
                case AnnotationKind.Highlight: Highlight(canvas, a, m, u, k, color); break;
                case AnnotationKind.Arrow: Arrow(canvas, a, m, u, k, color); break;
                case AnnotationKind.Step: Step(canvas, a, m, u, k, color); break;
                case AnnotationKind.Callout: Callout(canvas, a, m, u, k, color, bounds); break;
            }
        }
    }

    /// <summary>Animation progress: 0→1 while appearing, 1 while shown, →0 while leaving.</summary>
    private static bool Visible(Annotation a, double t, out float k)
    {
        k = 0;
        if (t < a.Start || t > a.End) return false;
        double fin = Math.Clamp((t - a.Start) / InMs, 0, 1);
        double fout = Math.Clamp((a.End - t) / OutMs, 0, 1);
        k = (float)Math.Min(EaseOut(fin), fout);
        return k > 0.01f;
    }

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    // Overshoots a little, so badges "pop".
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(x - 1, 3) + c1 * MathF.Pow(x - 1, 2);
    }

    private static SKRect Box(Annotation a, SKMatrix m) =>
        m.MapRect(new SKRect((float)a.X, (float)a.Y, (float)(a.X + a.W), (float)(a.Y + a.H)));

    private static SKPaint Shadow(float u, float alpha) => new()
    {
        IsAntialias = true,
        ImageFilter = SKImageFilter.CreateDropShadow(0, 3 * u, 8 * u, 8 * u, new SKColor(0, 0, 0, (byte)(110 * alpha))),
    };

    private static void Highlight(SKCanvas c, Annotation a, SKMatrix m, float u, float k, SKColor color)
    {
        var r = Box(a, m);
        // Settles inward as it appears.
        float grow = (1 - k) * 10 * u;
        r.Inflate(grow, grow);
        float radius = 10 * u;
        using (var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)(34 * k)) })
            c.DrawRoundRect(r, radius, radius, fill);
        using var glow = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7 * u, Color = color.WithAlpha((byte)(60 * k)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4 * u),
        };
        c.DrawRoundRect(r, radius, radius, glow);
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * u, Color = color.WithAlpha((byte)(255 * k)) };
        c.DrawRoundRect(r, radius, radius, edge);
    }

    private static void Spotlight(SKCanvas c, Annotation a, SKMatrix m, float u, float k, SKRect bounds)
    {
        var r = Box(a, m);
        float radius = 14 * u;
        var hole = new SKRoundRect(r, radius, radius);
        c.Save();
        c.ClipRoundRect(hole, SKClipOperation.Difference, antialias: true);
        using (var dim = new SKPaint { Color = new SKColor(5, 6, 10, (byte)(150 * k)) })
            c.DrawRect(bounds, dim);
        c.Restore();
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * u, Color = SKColors.White.WithAlpha((byte)(110 * k)) };
        c.DrawRoundRect(hole, edge);
    }

    private static void Arrow(SKCanvas c, Annotation a, SKMatrix m, float u, float k, SKColor color)
    {
        var tip = m.MapPoint(new SKPoint((float)a.X, (float)a.Y));
        var tail = m.MapPoint(new SKPoint((float)a.X2, (float)a.Y2));
        var d = tip - tail;
        float len = d.Length;
        if (len < 4) return;
        var dir = new SKPoint(d.X / len, d.Y / len);
        var nrm = new SKPoint(-dir.Y, dir.X);
        float head = Math.Min(len * 0.45f, 22 * u);
        // Draws itself from the tail toward the tip.
        var end = tail + new SKPoint(dir.X * (len - head * 0.6f) * k, dir.Y * (len - head * 0.6f) * k);

        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 10 * u, Color = SKColors.White.WithAlpha((byte)(200 * k)) };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 6 * u, Color = color.WithAlpha((byte)(255 * k)) };
        c.DrawLine(tail, end, halo);
        c.DrawLine(tail, end, line);

        if (k < 0.55f) return;
        float hk = (k - 0.55f) / 0.45f;
        float h = head * EaseOutBack(hk);
        var b = tip - new SKPoint(dir.X * h, dir.Y * h);
        using var path = new SKPath();
        path.MoveTo(tip);
        path.LineTo(b + new SKPoint(nrm.X * h * 0.62f, nrm.Y * h * 0.62f));
        path.LineTo(b - new SKPoint(nrm.X * h * 0.62f, nrm.Y * h * 0.62f));
        path.Close();
        using var haloFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 4 * u, Color = SKColors.White.WithAlpha((byte)(200 * k)) };
        using var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)(255 * k)) };
        c.DrawPath(path, haloFill);
        c.DrawPath(path, fill);
    }

    private static void Step(SKCanvas c, Annotation a, SKMatrix m, float u, float k, SKColor color)
    {
        var center = m.MapPoint(new SKPoint((float)a.X, (float)a.Y));
        float r = 17 * u * EaseOutBack(Math.Min(1, k * 1.1f));
        if (r <= 0.5f) return;
        using (var sh = Shadow(u, k))
        {
            sh.Color = color.WithAlpha((byte)(255 * k));
            c.DrawCircle(center, r, sh);
        }
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f * u, Color = SKColors.White.WithAlpha((byte)(240 * k)) })
            c.DrawCircle(center, r, ring);
        string label = a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var font = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * k)), TextSize = r * (label.Length > 1 ? 0.95f : 1.15f),
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), TextAlign = SKTextAlign.Center,
        };
        var bounds = new SKRect();
        font.MeasureText(label, ref bounds);
        c.DrawText(label, center.X, center.Y - bounds.MidY, font);
    }

    private static void Callout(SKCanvas c, Annotation a, SKMatrix m, float u, float k, SKColor color, SKRect visible)
    {
        if (string.IsNullOrWhiteSpace(a.Text)) return;
        var anchor = m.MapPoint(new SKPoint((float)a.X, (float)a.Y));
        using var font = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * k)), TextSize = 19 * u,
            Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
        };
        float padX = 16 * u, padY = 11 * u, ptr = 9 * u, barW = 4 * u;
        float tw = font.MeasureText(a.Text);
        float w = tw + padX * 2 + barW, h = font.TextSize + padY * 2;

        // Below the anchor if it fits, else above; slide horizontally to stay on screen.
        bool below = anchor.Y + ptr + h + 6 * u < visible.Bottom;
        float top = below ? anchor.Y + ptr : anchor.Y - ptr - h;
        float left = Math.Clamp(anchor.X - w / 2, visible.Left + 8 * u, Math.Max(visible.Left + 8 * u, visible.Right - w - 8 * u));
        float rise = (1 - k) * 8 * u * (below ? 1 : -1);
        var rect = new SKRect(left, top + rise, left + w, top + h + rise);
        float radius = 10 * u;

        using var path = new SKPath();
        path.AddRoundRect(rect, radius, radius);
        float px = Math.Clamp(anchor.X, rect.Left + radius + ptr, rect.Right - radius - ptr);
        using (var tri = new SKPath())
        {
            if (below) { tri.MoveTo(px - ptr, rect.Top + 1); tri.LineTo(px, rect.Top - ptr); tri.LineTo(px + ptr, rect.Top + 1); }
            else { tri.MoveTo(px - ptr, rect.Bottom - 1); tri.LineTo(px, rect.Bottom + ptr); tri.LineTo(px + ptr, rect.Bottom - 1); }
            tri.Close();
            path.AddPath(tri);
        }
        using (var sh = Shadow(u, k))
        {
            sh.Color = new SKColor(18, 20, 26, (byte)(238 * k));
            c.DrawPath(path, sh);
        }
        // Accent bar on the left edge.
        c.Save();
        c.ClipRoundRect(new SKRoundRect(rect, radius, radius), antialias: true);
        using (var bar = new SKPaint { Color = color.WithAlpha((byte)(255 * k)) })
            c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + barW, rect.Bottom), bar);
        c.Restore();
        var tb = new SKRect();
        font.MeasureText(a.Text, ref tb);
        c.DrawText(a.Text, rect.Left + barW + padX, rect.MidY - tb.MidY, font);
    }
}
