using SkiaSharp;

namespace Glidecast.Render;

public enum CursorStyle { Classic, Midnight, Soft, Dot, Ring, Spotlight }

public enum ClickEffect { None, Ripple, Pulse, Burst }

/// <summary>
/// Vector cursors and click effects, drawn fresh every frame so they stay sharp
/// at any zoom. All shapes are Glidecast's own designs.
/// </summary>
public static class CursorPainter
{
    public static readonly CursorStyle[] All = Enum.GetValues<CursorStyle>();

    public static string Label(CursorStyle s) => s switch
    {
        CursorStyle.Classic => "Classic",
        CursorStyle.Midnight => "Midnight",
        CursorStyle.Soft => "Soft",
        CursorStyle.Dot => "Dot",
        CursorStyle.Ring => "Ring",
        CursorStyle.Spotlight => "Spotlight",
        _ => s.ToString(),
    };

    // Arrow outline in a 24-unit box, tip at (0,0).
    private static SKPath ArrowPath()
    {
        var p = new SKPath();
        p.MoveTo(0, 0);
        p.LineTo(0, 17.5f);
        p.LineTo(4.4f, 13.6f);
        p.LineTo(7.4f, 20.4f);
        p.LineTo(10.6f, 19.0f);
        p.LineTo(7.7f, 12.4f);
        p.LineTo(13.4f, 12.0f);
        p.Close();
        return p;
    }

    // A rounder, friendlier arrow.
    private static SKPath SoftPath()
    {
        var p = new SKPath();
        p.MoveTo(0.8f, 0.8f);
        p.CubicTo(0.2f, 0.4f, -0.6f, 1.0f, -0.4f, 2.0f);
        p.LineTo(3.6f, 17.6f);
        p.CubicTo(3.9f, 18.6f, 5.2f, 18.8f, 5.7f, 17.9f);
        p.LineTo(8.2f, 12.9f);
        p.LineTo(13.8f, 12.4f);
        p.CubicTo(14.8f, 12.3f, 15.2f, 11.1f, 14.4f, 10.4f);
        p.Close();
        return p;
    }

    /// <param name="size">Overall scale in output pixels per cursor unit.</param>
    /// <param name="press">0..1 click press, squeezes the cursor briefly.</param>
    public static void Draw(SKCanvas c, CursorStyle style, SKPoint at, float size, float press, SKColor accent, float opacity = 1)
    {
        if (opacity <= 0.01f) return;
        byte A(float a) => (byte)Math.Clamp(a * opacity * 255, 0, 255);
        float k = size * (1 - 0.18f * press);

        c.Save();
        c.Translate(at.X, at.Y);
        c.Scale(k);

        switch (style)
        {
            case CursorStyle.Classic:
            case CursorStyle.Midnight:
            case CursorStyle.Spotlight:
            {
                if (style == CursorStyle.Spotlight)
                {
                    using var halo = new SKPaint { Color = accent.WithAlpha(A(0.28f)), IsAntialias = true };
                    c.DrawCircle(2, 4, 20, halo);
                }
                using var path = ArrowPath();
                bool dark = style == CursorStyle.Midnight;
                DrawShadow(c, path, A(0.45f));
                using var fill = new SKPaint { Color = (dark ? new SKColor(0x15, 0x17, 0x1C) : SKColors.White).WithAlpha(A(1)), IsAntialias = true };
                using var edge = new SKPaint
                {
                    Color = (dark ? SKColors.White : new SKColor(0x12, 0x14, 0x18)).WithAlpha(A(1)),
                    IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, StrokeJoin = SKStrokeJoin.Round,
                };
                c.DrawPath(path, fill);
                c.DrawPath(path, edge);
                break;
            }
            case CursorStyle.Soft:
            {
                using var path = SoftPath();
                DrawShadow(c, path, A(0.4f));
                using var rim = new SKPaint
                {
                    Color = SKColors.White.WithAlpha(A(1)), IsAntialias = true, Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3.2f, StrokeJoin = SKStrokeJoin.Round,
                };
                using var fill = new SKPaint { Color = new SKColor(0x1A, 0x1C, 0x22).WithAlpha(A(1)), IsAntialias = true };
                c.DrawPath(path, rim);
                c.DrawPath(path, fill);
                break;
            }
            case CursorStyle.Dot:
            {
                using var sh = new SKPaint { Color = SKColors.Black.WithAlpha(A(0.35f)), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.2f) };
                c.DrawCircle(0, 1.2f, 7.5f, sh);
                using var ring = new SKPaint { Color = SKColors.White.WithAlpha(A(1)), IsAntialias = true };
                c.DrawCircle(0, 0, 7.5f, ring);
                using var dot = new SKPaint { Color = accent.WithAlpha(A(1)), IsAntialias = true };
                c.DrawCircle(0, 0, 5.4f, dot);
                break;
            }
            case CursorStyle.Ring:
            {
                using var glow = new SKPaint { Color = accent.WithAlpha(A(0.18f)), IsAntialias = true };
                c.DrawCircle(0, 0, 15, glow);
                using var ring = new SKPaint { Color = accent.WithAlpha(A(0.95f)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f };
                c.DrawCircle(0, 0, 15, ring);
                using var dot = new SKPaint { Color = SKColors.White.WithAlpha(A(1)), IsAntialias = true };
                c.DrawCircle(0, 0, 3.2f, dot);
                using var dotEdge = new SKPaint { Color = accent.WithAlpha(A(1)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
                c.DrawCircle(0, 0, 3.2f, dotEdge);
                break;
            }
        }
        c.Restore();
    }

    private static void DrawShadow(SKCanvas c, SKPath path, byte alpha)
    {
        using var sh = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(alpha),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f),
        };
        c.Save();
        c.Translate(0.6f, 1.4f);
        c.DrawPath(path, sh);
        c.Restore();
    }

    /// <summary>Hotspot offset for arrow-like styles vs centered ones (for click effects).</summary>
    public static bool IsCentered(CursorStyle s) => s is CursorStyle.Dot or CursorStyle.Ring;

    /// <param name="age">0..1 progress through the effect.</param>
    public static void DrawClick(SKCanvas c, ClickEffect effect, SKPoint at, float unit, float age, SKColor accent)
    {
        if (effect == ClickEffect.None || age < 0 || age > 1) return;
        // Ease-out: fast start, gentle settle.
        float e = 1 - (1 - age) * (1 - age) * (1 - age);
        float fade = 1 - age;
        switch (effect)
        {
            case ClickEffect.Ripple:
            {
                using var ring = new SKPaint
                {
                    Style = SKPaintStyle.Stroke, IsAntialias = true,
                    StrokeWidth = (1 + 3.5f * fade) * unit,
                    Color = accent.WithAlpha((byte)(230 * fade)),
                };
                c.DrawCircle(at, (8 + 34 * e) * unit, ring);
                break;
            }
            case ClickEffect.Pulse:
            {
                using var fill = new SKPaint { IsAntialias = true, Color = accent.WithAlpha((byte)(120 * fade)) };
                c.DrawCircle(at, (6 + 26 * e) * unit, fill);
                using var core = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(170 * fade)) };
                c.DrawCircle(at, 5 * unit * fade, core);
                break;
            }
            case ClickEffect.Burst:
            {
                using var ray = new SKPaint
                {
                    IsAntialias = true, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.6f * unit, Color = accent.WithAlpha((byte)(240 * fade)),
                };
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4 + Math.PI / 8;
                    float r0 = (8 + 14 * e) * unit, r1 = (14 + 22 * e) * unit;
                    c.DrawLine(at.X + (float)Math.Cos(a) * r0, at.Y + (float)Math.Sin(a) * r0,
                               at.X + (float)Math.Cos(a) * r1, at.Y + (float)Math.Sin(a) * r1, ray);
                }
                break;
            }
        }
    }
}
