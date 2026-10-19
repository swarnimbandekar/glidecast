using Glidecast.Model;
using Glidecast.Render;

namespace Glidecast.AutoZoom;

/// <summary>
/// The camera's actual motion over the whole recording, precomputed once so any
/// time can be sampled instantly (scrubbing, export). Zoom focuses set targets;
/// critically-damped springs chase them, giving the fluid, never-overshooting
/// motion of a real camera operator:
///  - zoom runs in log space, so 1×→2× feels as fast as 2×→4×;
///  - the zoom-in starts a little before a focus so it's settled on the click;
///  - consecutive focuses pan straight across instead of bouncing out and in;
///  - with follow enabled, the camera keeps the cursor inside a dead-zone.
/// </summary>
public sealed class CameraTrack
{
    private const double Hz = 120;      // stored samples per second
    private const int Substeps = 4;     // integration steps per stored sample

    private readonly double[] _scale, _cx, _cy;
    private readonly double _w, _h;

    public CameraTrack(Project p, IReadOnlyList<ZoomSegment> segments, ZoomSettings? settings = null, CursorTrack? cursor = null)
    {
        var s = settings ?? new ZoomSettings();
        _w = p.Width;
        _h = p.Height;
        cursor ??= new CursorTrack(p, 0.5);

        double omegaZoom = s.Speed switch { ZoomSpeed.Gentle => 5.0, ZoomSpeed.Snappy => 11.0, _ => 7.5 };
        double omegaPan = omegaZoom * 0.85;
        // Start early enough that the zoom has ~90% settled when the click lands.
        double leadMs = 4.0 / omegaZoom * 1000;

        var segs = segments.Where(g => g.End > g.Start).OrderBy(g => g.Start).ToList();
        int n = (int)Math.Ceiling(p.DurationMs / 1000.0 * Hz) + 2;
        _scale = new double[n];
        _cx = new double[n];
        _cy = new double[n];

        double logS = 0, vS = 0;
        double cx = _w / 2, cy = _h / 2, vx = 0, vy = 0;
        double fx = cx, fy = cy;           // follow target inside the current focus
        ZoomSegment? active = null;
        double dt = 1.0 / (Hz * Substeps);

        for (int i = 0; i < n; i++)
        {
            for (int sub = 0; sub < Substeps; sub++)
            {
                double t = (i + sub / (double)Substeps) / Hz * 1000;

                // Active focus: the latest one whose (lead-adjusted) window covers t.
                ZoomSegment? seg = null;
                foreach (var g in segs)
                {
                    if (g.Start - leadMs > t) break;
                    if (t < g.End) seg = g;
                }

                double tgtLog, tx, ty;
                if (seg != null)
                {
                    double scale = Math.Max(1, seg.Scale);
                    if (seg != active)
                    {
                        (fx, fy) = ZoomAuthor.ClampCenter(seg.Cx, seg.Cy, _w, _h, scale);
                        active = seg;
                    }
                    if (s.FollowCursor && t >= seg.Start)
                    {
                        // Dead-zone: the cursor may roam the middle ~60% of the view freely.
                        var (mx, my) = cursor.At(t);
                        double dzx = _w / (2 * scale) * 0.6, dzy = _h / (2 * scale) * 0.6;
                        if (mx > fx + dzx) fx = mx - dzx; else if (mx < fx - dzx) fx = mx + dzx;
                        if (my > fy + dzy) fy = my - dzy; else if (my < fy - dzy) fy = my + dzy;
                        (fx, fy) = ZoomAuthor.ClampCenter(fx, fy, _w, _h, scale);
                    }
                    tgtLog = Math.Log(scale);
                    tx = fx; ty = fy;
                }
                else
                {
                    active = null;
                    tgtLog = 0;
                    tx = _w / 2; ty = _h / 2;
                }

                Spring(ref logS, ref vS, tgtLog, omegaZoom, dt);
                Spring(ref cx, ref vx, tx, omegaPan, dt);
                Spring(ref cy, ref vy, ty, omegaPan, dt);

                // Guarantee: while zoomed and following, the cursor never leaves
                // the frame, however fast it moves (the spring alone can lag).
                double cur = Math.Exp(logS);
                if (s.FollowCursor && cur > 1.02)
                {
                    var (mx, my) = cursor.At(t);
                    double hx = _w / (2 * cur) * 0.88, hy = _h / (2 * cur) * 0.88;
                    if (mx > cx + hx) cx = mx - hx; else if (mx < cx - hx) cx = mx + hx;
                    if (my > cy + hy) cy = my - hy; else if (my < cy - hy) cy = my + hy;
                }
            }

            double sc = Math.Max(1, Math.Exp(logS));
            var (ccx, ccy) = ZoomAuthor.ClampCenter(cx, cy, _w, _h, sc);
            _scale[i] = sc;
            _cx[i] = ccx;
            _cy[i] = ccy;
        }
    }

    /// <summary>A track that never zooms.</summary>
    public static CameraTrack Static(Project p) => new(p, Array.Empty<ZoomSegment>());

    // Critically damped spring, semi-implicit Euler (stable for these step sizes).
    private static void Spring(ref double x, ref double v, double target, double omega, double dt)
    {
        double a = -2 * omega * v - omega * omega * (x - target);
        v += a * dt;
        x += v * dt;
    }

    /// <summary>Camera at time t: zoom factor and the source-pixel point at the center of view.</summary>
    public (double scale, double cx, double cy) At(double tMs)
    {
        double f = Math.Clamp(tMs / 1000.0 * Hz, 0, _scale.Length - 1);
        int i = (int)f;
        int j = Math.Min(i + 1, _scale.Length - 1);
        double u = f - i;
        return (
            _scale[i] + (_scale[j] - _scale[i]) * u,
            _cx[i] + (_cx[j] - _cx[i]) * u,
            _cy[i] + (_cy[j] - _cy[i]) * u);
    }

    /// <summary>Camera speed at t (zoom change + pan in screen widths per second); drives motion blur.</summary>
    public double MotionAt(double tMs)
    {
        var (s0, x0, y0) = At(tMs - 8);
        var (s1, x1, y1) = At(tMs + 8);
        double dz = Math.Abs(Math.Log(s1) - Math.Log(s0));
        double dp = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * s1 / _w;
        return (dz + dp) / 0.016;
    }
}
