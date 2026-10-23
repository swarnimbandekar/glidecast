using Glidecast.Model;

namespace Glidecast.Render;

/// <summary>
/// Maps project time (the input clock that cursor, clicks and zooms use) to
/// source-video frames and back, so the picture always matches the input
/// tracks even when capture dropped below its target frame rate.
/// </summary>
public sealed class FrameClock
{
    private readonly double[] _t;

    /// <summary>Nominal rate the source was encoded at (frame i has PTS i / VideoFps).</summary>
    public double VideoFps { get; }
    public int Count => _t.Length;

    private FrameClock(double[] times, double videoFps)
    {
        _t = times.Length > 0 ? times : new[] { 0.0 };
        VideoFps = videoFps;
    }

    /// <summary>Clock for a project. Older recordings have no per-frame
    /// timestamps: their frames are counted and spread evenly over the
    /// recording, and the estimate is stored on the project.</summary>
    public static FrameClock For(Project p)
    {
        if (p.FrameTimes is not { Count: > 0 })
        {
            int n = 0;
            try { n = FFmpeg.CountFrames(p.SourceVideo); } catch { }
            if (n <= 0) n = Math.Max(1, (int)Math.Round(p.DurationMs / 1000.0 * p.Fps));
            double step = p.DurationMs / n;
            p.FrameTimes = Enumerable.Range(0, n).Select(i => Math.Round(i * step, 2)).ToList();
        }
        return new FrameClock(p.FrameTimes.ToArray(), p.Fps);
    }

    /// <summary>Clock for another stream captured on the input clock (the webcam).</summary>
    public static FrameClock FromTimes(IReadOnlyList<double> times, double videoFps) => new(times.ToArray(), videoFps);

    /// <summary>The frame on screen at project time t (latest frame captured at or before t).</summary>
    public int FrameAt(double tMs)
    {
        if (tMs <= _t[0]) return 0;
        if (tMs >= _t[^1]) return _t.Length - 1;
        int lo = 0, hi = _t.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_t[mid] <= tMs) lo = mid; else hi = mid;
        }
        return lo;
    }

    public double TimeOf(int frame) => _t[Math.Clamp(frame, 0, _t.Length - 1)];

    /// <summary>Seek position (seconds of video) that lands exactly on a frame.</summary>
    public double SeekSeconds(int frame) => Math.Max(0, (frame - 0.25) / VideoFps);
}
