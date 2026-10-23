namespace Glidecast.Render;

/// <summary>Shape of the output video. Anything but <see cref="Source"/> is a
/// reframe: the camera shows a slice of the screen that matches the shape, and
/// pans to keep the action inside it.</summary>
public enum CanvasAspect { Source, Wide, Vertical, Square }

public static class Canvas
{
    public static readonly CanvasAspect[] All = { CanvasAspect.Source, CanvasAspect.Wide, CanvasAspect.Vertical, CanvasAspect.Square };

    public static string Label(CanvasAspect a) => a switch
    {
        CanvasAspect.Wide => "16:9",
        CanvasAspect.Vertical => "9:16",
        CanvasAspect.Square => "1:1",
        _ => "Source",
    };

    public static string Slug(CanvasAspect a) => a switch
    {
        CanvasAspect.Wide => "16x9",
        CanvasAspect.Vertical => "9x16",
        CanvasAspect.Square => "1x1",
        _ => "source",
    };

    public static bool TryParse(string s, out CanvasAspect a)
    {
        a = s.Trim().ToLowerInvariant() switch
        {
            "16:9" or "16x9" or "wide" or "landscape" => CanvasAspect.Wide,
            "9:16" or "9x16" or "vertical" or "portrait" or "shorts" => CanvasAspect.Vertical,
            "1:1" or "1x1" or "square" => CanvasAspect.Square,
            "source" or "original" => CanvasAspect.Source,
            _ => (CanvasAspect)(-1),
        };
        return (int)a >= 0;
    }

    /// <summary>Width ÷ height, or null for the recording's own shape.</summary>
    public static double? Ratio(CanvasAspect a) => a switch
    {
        CanvasAspect.Wide => 16.0 / 9,
        CanvasAspect.Vertical => 9.0 / 16,
        CanvasAspect.Square => 1.0,
        _ => null,
    };

    /// <summary>The largest view of this shape that fits a w×h screen, in source pixels.</summary>
    public static (double vw, double vh) View(CanvasAspect a, double w, double h)
    {
        if (Ratio(a) is not { } r) return (w, h);
        return r >= w / h ? (w, w / r) : (h * r, h);
    }

    /// <summary>Even output size: the short side is <paramref name="shortSide"/>
    /// (0 = the recording's short side), the long side follows the shape.</summary>
    public static (int w, int h) OutputSize(CanvasAspect a, int srcW, int srcH, int shortSide = 0)
    {
        double r = Ratio(a) ?? srcW / (double)srcH;
        if (shortSide <= 0)
        {
            if (a == CanvasAspect.Source) return (srcW & ~1, srcH & ~1);
            shortSide = Math.Min(srcW, srcH);
        }
        return r >= 1
            ? ((int)Math.Round(shortSide * r) & ~1, shortSide & ~1)
            : (shortSide & ~1, (int)Math.Round(shortSide / r) & ~1);
    }
}
