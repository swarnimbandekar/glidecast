using Glidecast.Model;

namespace Glidecast.AutoZoom;

/// <summary>One zoom focus: the camera is zoomed to <see cref="Scale"/> on
/// (<see cref="Cx"/>, <see cref="Cy"/>) from <see cref="Start"/> to
/// <see cref="End"/> ms. This is the unit the editor lets the user add, move,
/// resize and delete; <see cref="CameraTrack"/> turns segments into motion.</summary>
public sealed class ZoomSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Scale { get; set; } = 2.0;
    public double Cx { get; set; }
    public double Cy { get; set; }

    public ZoomSegment Clone() => (ZoomSegment)MemberwiseClone();
}

public enum ZoomSpeed { Gentle, Smooth, Snappy }

public sealed class ZoomSettings
{
    public double MaxScale { get; set; } = 2.0;      // how far to zoom in on activity
    public double HoldAfterClickMs { get; set; } = 1400; // keep focus this long after last activity
    public double IdleToZoomOutMs { get; set; } = 1600;  // clicks further apart than this start a new focus
    public double MergeRadiusPx { get; set; } = 320; // clicks within this distance share a focus
    /// <summary>How quickly the camera springs between states.</summary>
    public ZoomSpeed Speed { get; set; } = ZoomSpeed.Smooth;
    /// <summary>While zoomed, pan to keep the cursor in view.</summary>
    public bool FollowCursor { get; set; } = true;
}

/// <summary>
/// Turns the recorded click/key tracks into zoom focuses: focus on clusters of
/// clicks (and the typing that follows them), hold, then let the camera relax
/// during idle gaps.
/// </summary>
public static class ZoomAuthor
{
    public static List<ZoomSegment> BuildSegments(Project p, ZoomSettings? s = null)
    {
        s ??= new ZoomSettings();
        var segs = new List<ZoomSegment>();
        if (p.Clicks.Count == 0) return segs;

        // Key-down times, so typing right after a click keeps the zoom alive.
        var keyTimes = p.Keys.Where(k => k.Down).Select(k => k.T).OrderBy(t => t).ToList();
        double LastActivity(double from)
        {
            double last = from;
            foreach (var t in keyTimes)
            {
                if (t < last) continue;
                if (t - last > s.IdleToZoomOutMs) break;
                last = t;
            }
            return last;
        }

        double fx = p.Clicks[0].X, fy = p.Clicks[0].Y;
        double fStart = p.Clicks[0].T, fEnd = p.Clicks[0].T;
        int n = 1;

        void Flush()
        {
            var (cx, cy) = ClampCenter(fx, fy, p.Width, p.Height, s.MaxScale);
            double end = LastActivity(fEnd) + s.HoldAfterClickMs;
            segs.Add(new ZoomSegment
            {
                Start = fStart,
                End = Math.Min(p.DurationMs, end),
                Scale = s.MaxScale,
                Cx = cx,
                Cy = cy,
            });
        }

        for (int i = 1; i < p.Clicks.Count; i++)
        {
            var c = p.Clicks[i];
            double dist = Math.Sqrt((c.X - fx) * (c.X - fx) + (c.Y - fy) * (c.Y - fy));
            bool sameFocus = (c.T - LastActivity(fEnd)) < s.IdleToZoomOutMs && dist < s.MergeRadiusPx;
            if (sameFocus)
            {
                // Running average keeps the camera centered on the cluster.
                fx = (fx * n + c.X) / (n + 1);
                fy = (fy * n + c.Y) / (n + 1);
                fEnd = c.T;
                n++;
            }
            else
            {
                Flush();
                fx = c.X; fy = c.Y; fStart = c.T; fEnd = c.T; n = 1;
            }
        }
        Flush();

        // Overlaps can appear after typing extension; trim so segments stay ordered.
        for (int i = 1; i < segs.Count; i++)
            if (segs[i - 1].End > segs[i].Start) segs[i - 1].End = segs[i].Start;
        return segs.Where(g => g.End - g.Start >= 200).ToList();
    }

    /// <summary>Keep the zoomed viewport inside the screen so we never show
    /// empty space past an edge.</summary>
    public static (double cx, double cy) ClampCenter(double x, double y, double w, double h, double scale)
    {
        scale = Math.Max(1, scale);
        double halfW = w / (2 * scale);
        double halfH = h / (2 * scale);
        double cx = Math.Clamp(x, halfW, w - halfW);
        double cy = Math.Clamp(y, halfH, h - halfH);
        return (cx, cy);
    }
}
