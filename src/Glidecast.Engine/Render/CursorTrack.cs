using Glidecast.Model;

namespace Glidecast.Render;

/// <summary>
/// The rendered cursor path. The raw move log is smoothed with a centred
/// (zero-phase) Gaussian filter: jitter and low-rate sampling become a glide,
/// but unlike a spring/low-pass filter the cursor never lags behind the real
/// one. Around every click the path is pulled back onto the exact click point,
/// so the tip is always on target when the click lands. Also tracks how long
/// the cursor has been still (for auto-hide). Precomputed for instant sampling.
/// </summary>
public sealed class CursorTrack
{
    private const double Hz = 120;

    private readonly double[] _x, _y, _still;
    public double Smoothing { get; }

    /// <param name="smoothing">0 = raw input, 1 = very floaty.</param>
    public CursorTrack(Project p, double smoothing)
    {
        Smoothing = Math.Clamp(smoothing, 0, 1);
        int n = (int)Math.Ceiling(p.DurationMs / 1000.0 * Hz) + 2;
        var rx = new double[n];
        var ry = new double[n];
        _x = new double[n];
        _y = new double[n];
        _still = new double[n];

        double lastX = double.NaN, lastY = 0, lastMoveT = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / Hz * 1000;
            (rx[i], ry[i]) = p.CursorAt(t);
            if (double.IsNaN(lastX) || Math.Abs(rx[i] - lastX) + Math.Abs(ry[i] - lastY) > 2)
            {
                lastMoveT = t; lastX = rx[i]; lastY = ry[i];
            }
            _still[i] = t - lastMoveT;
        }

        if (Smoothing <= 0.001)
        {
            Array.Copy(rx, _x, n);
            Array.Copy(ry, _y, n);
            return;
        }

        // Centred Gaussian: sigma 12 ms (light) .. 90 ms (silky).
        double sigmaMs = 12 + 78 * Smoothing;
        double sigma = sigmaMs / 1000 * Hz;
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[2 * radius + 1];
        for (int k = -radius; k <= radius; k++) kernel[k + radius] = Math.Exp(-0.5 * k * k / (sigma * sigma));
        for (int i = 0; i < n; i++)
        {
            double sx = 0, sy = 0, sw = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int j = Math.Clamp(i + k, 0, n - 1);
                double w = kernel[k + radius];
                sx += rx[j] * w; sy += ry[j] * w; sw += w;
            }
            _x[i] = sx / sw;
            _y[i] = sy / sw;
        }

        // Pin to clicks: blend back to the exact click position near each click.
        double pinMs = Math.Max(40, sigmaMs * 1.2);
        foreach (var c in p.Clicks)
        {
            int i0 = Math.Max(0, (int)((c.T - pinMs * 2.5) / 1000 * Hz));
            int i1 = Math.Min(n - 1, (int)((c.T + pinMs * 2.5) / 1000 * Hz) + 1);
            for (int i = i0; i <= i1; i++)
            {
                double dt = i / Hz * 1000 - c.T;
                double w = Math.Exp(-0.5 * dt * dt / (pinMs * pinMs));
                // The raw path already passes through the click; use it so the
                // approach and departure stay natural.
                _x[i] += (rx[i] - _x[i]) * w;
                _y[i] += (ry[i] - _y[i]) * w;
            }
        }
    }

    public (double x, double y) At(double tMs)
    {
        double f = Math.Clamp(tMs / 1000.0 * Hz, 0, _x.Length - 1);
        int i = (int)f;
        int j = Math.Min(i + 1, _x.Length - 1);
        double u = f - i;
        return (_x[i] + (_x[j] - _x[i]) * u, _y[i] + (_y[j] - _y[i]) * u);
    }

    /// <summary>Milliseconds since the cursor last moved.</summary>
    public double StillFor(double tMs)
    {
        int i = (int)Math.Clamp(tMs / 1000.0 * Hz, 0, _still.Length - 1);
        return _still[i];
    }
}
