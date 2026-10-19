using Glidecast.AutoZoom;
using Glidecast.Model;

namespace Glidecast.Render;

public enum ExportQuality { Draft, Standard, High }

public sealed class ExportOptions
{
    /// <summary>Output size; 0 = source size. Odd values are rounded down to even.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Output frame rate; 0 = source rate.</summary>
    public double Fps { get; set; }
    public ExportQuality Quality { get; set; } = ExportQuality.Standard;
}

/// <summary>
/// Streams the raw source video through the compositor and auto-zoom camera into
/// a finished MP4. ffmpeg decodes source frames to raw BGRA on one pipe; we
/// composite each one; a second ffmpeg encodes the result.
/// </summary>
public static class VideoRenderer
{
    public static void Render(Project p, string outPath, ZoomSettings? zoom = null, StyleSettings? style = null,
        Action<double>? progress = null, ExportOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ExportOptions();
        // Hand-edited segments win over the auto timeline; an explicit zoom
        // argument (CLI) re-authors from clicks.
        style ??= p.Style;
        var camera = zoom != null
            ? new CameraTrack(p, ZoomAuthor.BuildSegments(p, zoom), zoom, new CursorTrack(p, style?.CursorSmoothing ?? 0.5))
            : p.BuildCamera();

        int w = p.Width, h = p.Height;
        int outW = (options.Width > 0 ? options.Width : w) & ~1;
        int outH = (options.Height > 0 ? options.Height : h) & ~1;
        double fps = options.Fps > 0 ? options.Fps : p.Fps;
        using var comp = new Compositor(p, camera, style, outW, outH);

        var (preset, crf) = options.Quality switch
        {
            ExportQuality.Draft => ("veryfast", 26),
            ExportQuality.High => ("slow", 16),
            _ => ("medium", 19),
        };

        // Decoder: every source frame, in order, at source size. Which frame
        // to show at each output moment comes from the capture timestamps.
        var frames = FrameClock.For(p);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string decArgs = string.Create(inv,
            $"-i \"{p.SourceVideo}\" -fps_mode passthrough -f rawvideo -pix_fmt bgra -s {w}x{h} -");
        var dec = FFmpeg.Start(decArgs, redirectIn: false, redirectOut: true);

        // Encoder.
        string encArgs = string.Create(inv,
            $"-y -f rawvideo -pixel_format bgra -video_size {outW}x{outH} -framerate {fps} -i - -an -c:v libx264 -preset {preset} -pix_fmt yuv420p -crf {crf} -movflags +faststart \"{outPath}\"");
        var enc = FFmpeg.Start(encArgs, redirectIn: true, redirectOut: false);

        var srcBuf = new byte[w * h * 4];
        var dstBuf = new byte[outW * outH * 4];
        var decOut = dec.StandardOutput.BaseStream;
        var encIn = enc.StandardInput.BaseStream;

        int totalFrames = Math.Max(1, (int)Math.Round(p.DurationMs / 1000.0 * fps));
        int srcIndex = -1;
        bool srcEnded = false;
        bool cancelled = false;
        try
        {
            for (int frame = 0; frame < totalFrames; frame++)
            {
                if (cancel.IsCancellationRequested) { cancelled = true; break; }
                double tMs = frame / fps * 1000.0;
                // Advance the decoder to the frame that was on screen at tMs.
                int want = frames.FrameAt(tMs);
                while (!srcEnded && srcIndex < want)
                {
                    if (ReadFull(decOut, srcBuf)) srcIndex++;
                    else srcEnded = true;
                }
                if (srcIndex < 0) break; // empty video
                comp.RenderInto(srcBuf, tMs, dstBuf);
                encIn.Write(dstBuf, 0, dstBuf.Length);
                if ((frame & 7) == 0) progress?.Invoke(Math.Min(1.0, frame / (double)totalFrames));
            }
        }
        finally
        {
            try { encIn.Flush(); encIn.Close(); } catch { }
            TryKill(dec);
            if (cancelled) TryKill(enc);
            enc.WaitForExit(60000);
            dec.WaitForExit(10000);
        }
        if (cancelled)
        {
            try { File.Delete(outPath); } catch { }
            cancel.ThrowIfCancellationRequested();
        }
        if (enc.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg encode failed: " + FFmpeg.LastError);
        progress?.Invoke(1.0);
    }

    private static void TryKill(System.Diagnostics.Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
    }

    internal static bool ReadFull(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n == 0) return false; // EOF
            off += n;
        }
        return true;
    }
}
