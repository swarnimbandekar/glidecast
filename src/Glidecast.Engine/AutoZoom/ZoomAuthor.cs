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

/// <summary>Named starting points for the auto-zoom tuning; Custom once edited by hand.</summary>
public enum ZoomPreset { Custom, Subtle, Balanced, Dramatic, Tutorial }

public sealed class ZoomSettings
{
    public ZoomPreset Preset { get; set; } = ZoomPreset.Balanced;
    public double MaxScale { get; set; } = 2.0;      // how far to zoom in on activity
    public double HoldAfterClickMs { get; set; } = 1400; // keep focus this long after last activity
    public double IdleToZoomOutMs { get; set; } = 1600;  // clicks further apart than this start a new focus
    public double MergeRadiusPx { get; set; } = 320; // clicks within this distance share a focus
    /// <summary>How quickly the camera springs between states.</summary>
    public ZoomSpeed Speed { get; set; } = ZoomSpeed.Smooth;
    /// <summary>While zoomed, pan to keep the cursor in view.</summary>
    public bool FollowCursor { get; set; } = true;
    /// <summary>Frame the clicked control (or its menu / panel) instead of just
    /// the click point, choosing the zoom level that fits it.</summary>
    public bool SmartFraming { get; set; } = true;
    /// <summary>Zoom on and follow the text caret while typing.</summary>
    public bool FollowTyping { get; set; } = true;

    public ZoomSettings Clone() => (ZoomSettings)MemberwiseClone();

    /// <summary>Tuning for a preset; framing and follow switches are kept from <paramref name="keep"/>.</summary>
    public static ZoomSettings For(ZoomPreset preset, ZoomSettings? keep = null)
    {
        var s = preset switch
        {
            ZoomPreset.Subtle => new ZoomSettings { MaxScale = 1.5, HoldAfterClickMs = 1600, IdleToZoomOutMs = 1800, MergeRadiusPx = 480, Speed = ZoomSpeed.Gentle },
            ZoomPreset.Dramatic => new ZoomSettings { MaxScale = 2.8, HoldAfterClickMs = 1000, IdleToZoomOutMs = 1200, MergeRadiusPx = 240, Speed = ZoomSpeed.Snappy },
            // Walkthroughs: closer, longer holds so viewers can read each step.
            ZoomPreset.Tutorial => new ZoomSettings { MaxScale = 2.2, HoldAfterClickMs = 2200, IdleToZoomOutMs = 2000, MergeRadiusPx = 360, Speed = ZoomSpeed.Gentle },
            _ => new ZoomSettings(),
        };
        s.Preset = preset == ZoomPreset.Custom ? ZoomPreset.Balanced : preset;
        if (keep != null)
        {
            s.FollowCursor = keep.FollowCursor;
            s.SmartFraming = keep.SmartFraming;
            s.FollowTyping = keep.FollowTyping;
        }
        return s;
    }
}

/// <summary>
/// Turns the recorded click/key/caret tracks into zoom focuses: focus on
/// clusters of clicks (and the typing that follows them, or that happens
/// elsewhere), hold, then let the camera relax during idle gaps. With smart
/// framing the zoom level is chosen to fit the clicked controls, or the whole
/// menu / panel they belong to, instead of a fixed factor.
/// </summary>
public static class ZoomAuthor
{
    private const double MinSmartScale = 1.3;  // smart framing never zooms less than this
    private const double FrameMargin = 1.35;   // breathing room around a framed control

    private readonly record struct Activity(double T, double X, double Y, UiElement? Element, UiElement? Container, bool Typing);

    public static List<ZoomSegment> BuildSegments(Project p, ZoomSettings? s = null)
    {
        s ??= new ZoomSettings();
        var acts = p.Clicks.Select(c => new Activity(c.T, c.X, c.Y, c.Element, c.Container, false)).ToList();
        if (s.FollowTyping)
            acts.AddRange((p.Carets ?? new()).Select(k => new Activity(k.T, k.X + k.W / 2.0, k.Y + k.H / 2.0, null, null, true)));
        acts.Sort((a, b) => a.T.CompareTo(b.T));
        var segs = new List<ZoomSegment>();
        if (acts.Count == 0) return segs;

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

        var cluster = new List<Activity> { acts[0] };
        double fx = acts[0].X, fy = acts[0].Y;

        void Flush()
        {
            // A few stray caret samples (one Enter press) are not worth a zoom.
            if (cluster.All(a => a.Typing) && cluster.Count < 3) return;
            var (scale, cx, cy) = Frame(cluster, fx, fy, p, s);
            (cx, cy) = ClampCenter(cx, cy, p.Width, p.Height, scale);
            double end = LastActivity(cluster[^1].T) + s.HoldAfterClickMs;
            segs.Add(new ZoomSegment
            {
                Start = cluster[0].T,
                End = Math.Min(p.DurationMs, end),
                Scale = Math.Round(scale, 2),
                Cx = cx,
                Cy = cy,
            });
        }

        for (int i = 1; i < acts.Count; i++)
        {
            var a = acts[i];
            double dist = Math.Sqrt((a.X - fx) * (a.X - fx) + (a.Y - fy) * (a.Y - fy));
            // Typing drifts along a line, so it may wander further before it's a new place.
            double radius = a.Typing ? s.MergeRadiusPx * 1.4 : s.MergeRadiusPx;
            bool sameFocus = (a.T - LastActivity(cluster[^1].T)) < s.IdleToZoomOutMs && dist < radius;
            if (sameFocus)
            {
                // Running average keeps the camera centered on the cluster.
                int n = cluster.Count;
                fx = (fx * n + a.X) / (n + 1);
                fy = (fy * n + a.Y) / (n + 1);
                cluster.Add(a);
            }
            else
            {
                Flush();
                cluster = new List<Activity> { a };
                fx = a.X; fy = a.Y;
            }
        }
        Flush();

        // Overlaps can appear after typing extension; trim so segments stay ordered.
        for (int i = 1; i < segs.Count; i++)
            if (segs[i - 1].End > segs[i].Start) segs[i - 1].End = segs[i].Start;
        return segs.Where(g => g.End - g.Start >= 200).ToList();
    }

    /// <summary>Zoom level and center for one cluster of activity.</summary>
    private static (double scale, double cx, double cy) Frame(List<Activity> cluster, double fx, double fy, Project p, ZoomSettings s)
    {
        // Elements too big to frame (a whole document, a sidebar) say nothing
        // about where to look: treat those clicks as plain points.
        UiElement? Framable(UiElement? e) => e != null && Fit(e.W, e.H, p) >= MinSmartScale ? e : null;
        if (!s.SmartFraming || cluster.All(a => Framable(a.Element) == null))
            return (s.MaxScale, fx, fy);

        // Prefer the panel that holds every click (a menu, a dialog), if it fits on screen zoomed.
        var clicks = cluster.Where(a => !a.Typing).ToList();
        foreach (var c in clicks)
        {
            if (c.Container is not { } box) continue;
            if (!clicks.All(o => box.Contains(o.X, o.Y))) continue;
            double fit = Fit(box.W, box.H, p);
            if (fit >= MinSmartScale) return (Math.Min(s.MaxScale, fit), box.Cx, box.Cy);
        }

        // Otherwise the union of the clicked controls (and caret / click points).
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var a in cluster)
        {
            var e = Framable(a.Element);
            l = Math.Min(l, e?.X ?? a.X); t = Math.Min(t, e?.Y ?? a.Y);
            r = Math.Max(r, e != null ? e.X + e.W : a.X); b = Math.Max(b, e != null ? e.Y + e.H : a.Y);
        }
        double scale = Math.Clamp(Fit(r - l, b - t, p), MinSmartScale, s.MaxScale);
        return (scale, (l + r) / 2, (t + b) / 2);
    }

    /// <summary>The zoom at which a w×h box (plus margin) just fills the screen.</summary>
    private static double Fit(double w, double h, Project p) =>
        Math.Min(p.Width / Math.Max(1, w * FrameMargin), p.Height / Math.Max(1, h * FrameMargin));

    /// <summary>Keep the zoomed viewport inside the screen so we never show
    /// empty space past an edge.</summary>
    public static (double cx, double cy) ClampCenter(double x, double y, double w, double h, double scale) =>
        ClampCenter(x, y, w, h, w, h, scale);

    /// <summary>As above, for a view of vw×vh source pixels at 1× (a cropped
    /// canvas such as 9:16 shows less than the whole screen).</summary>
    public static (double cx, double cy) ClampCenter(double x, double y, double vw, double vh, double w, double h, double scale)
    {
        scale = Math.Max(1, scale);
        double halfW = Math.Min(w / 2, vw / (2 * scale));
        double halfH = Math.Min(h / 2, vh / (2 * scale));
        double cx = Math.Clamp(x, halfW, w - halfW);
        double cy = Math.Clamp(y, halfH, h - halfH);
        return (cx, cy);
    }
}
