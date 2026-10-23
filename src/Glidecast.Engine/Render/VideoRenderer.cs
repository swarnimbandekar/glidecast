using Glidecast.AutoZoom;
using Glidecast.Model;

namespace Glidecast.Render;

public enum ExportQuality { Draft, Standard, High }

public sealed class ExportOptions
{
    /// <summary>Output size; 0 = derived from the canvas shape and the
    /// recording. Odd values are rounded down to even.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Output frame rate; 0 = source rate.</summary>
    public double Fps { get; set; }
    public ExportQuality Quality { get; set; } = ExportQuality.Standard;
    /// <summary>Canvas shape; null = the project's (Style.Aspect).</summary>
    public CanvasAspect? Aspect { get; set; }
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
}

/// <summary>
/// Streams the raw source video through the compositor and auto-zoom camera into
/// a finished MP4. ffmpeg decodes source frames to raw BGRA on one pipe; we
/// composite each one; a second ffmpeg encodes the result (on the GPU when one
/// is available, falling back to x264 if the GPU encoder fails).
/// </summary>
public static class VideoRenderer
{
    /// <returns>The encoder that produced the file.</returns>
    public static VideoEncoder Render(Project p, string outPath, ZoomSettings? zoom = null, StyleSettings? style = null,
        Action<double>? progress = null, ExportOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ExportOptions();
        var encoder = Encoders.Resolve(options.Encoder);
        try
        {
            RenderWith(p, outPath, zoom, style, progress, options, encoder, cancel);
            return encoder;
        }
        catch (EncoderFailedException) when (encoder != VideoEncoder.Cpu)
        {
            // Drivers can refuse a session (busy GPU, unsupported size): redo on the CPU.
            progress?.Invoke(0);
            RenderWith(p, outPath, zoom, style, progress, options, VideoEncoder.Cpu, cancel);
            return VideoEncoder.Cpu;
        }
    }

    /// <summary>Exports one file per canvas shape, named "&lt;base&gt; 9x16.mp4" etc.</summary>
    public static List<string> RenderAll(Project p, string outBase, IEnumerable<CanvasAspect> aspects, ExportOptions options,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        var list = aspects.Distinct().ToList();
        var outputs = new List<string>();
        string dir = Path.GetDirectoryName(Path.GetFullPath(outBase))!;
        string stem = Path.GetFileNameWithoutExtension(outBase);
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            string path = Path.Combine(dir, $"{stem} {Canvas.Slug(a)}.mp4");
            var o = new ExportOptions { Fps = options.Fps, Quality = options.Quality, Encoder = options.Encoder, Aspect = a };
            if (options.Height > 0)
                (o.Width, o.Height) = Canvas.OutputSize(a, p.Width, p.Height, Math.Min(options.Width, options.Height));
            int k = i;
            Render(p, path, style: p.Style, options: o, cancel: cancel,
                progress: f => progress?.Invoke((k + f) / list.Count));
            outputs.Add(path);
        }
        return outputs;
    }

    private sealed class EncoderFailedException(string message) : InvalidOperationException(message);

    private static void RenderWith(Project p, string outPath, ZoomSettings? zoom, StyleSettings? style,
        Action<double>? progress, ExportOptions options, VideoEncoder encoder, CancellationToken cancel)
    {
        // Hand-edited segments win over the auto timeline; an explicit zoom
        // argument (CLI) re-authors from clicks.
        style ??= p.Style;
        var aspect = options.Aspect ?? style?.Aspect ?? CanvasAspect.Source;
        var camera = zoom != null
            ? new CameraTrack(p, ZoomAuthor.BuildSegments(p, zoom), zoom, new CursorTrack(p, style?.CursorSmoothing ?? 0.5), aspect)
            : p.BuildCamera(aspect);

        int w = p.Width, h = p.Height;
        var (defW, defH) = Canvas.OutputSize(aspect, w, h);
        int outW = (options.Width > 0 ? options.Width : defW) & ~1;
        int outH = (options.Height > 0 ? options.Height : defH) & ~1;
        double fps = options.Fps > 0 ? options.Fps : p.Fps;

        // Frames are independent, so composite a batch at once, one compositor
        // per worker; leave cores for ffmpeg's decoder and encoder. Fewer
        // workers at large sizes keep memory in check (two buffers each).
        long frameBytes = (long)Math.Max(outW * outH, w * h) * 4;
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, frameBytes > 16_000_000 ? 3 : 6);
        var comps = Enumerable.Range(0, workers).Select(_ => new Compositor(p, camera, style, outW, outH)).ToArray();

        // Decoder: every source frame, in order, at source size. Which frame
        // to show at each output moment comes from the capture timestamps.
        var frames = FrameClock.For(p);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string decArgs = string.Create(inv,
            $"-i \"{p.SourceVideo}\" -fps_mode passthrough -f rawvideo -pix_fmt bgra -s {w}x{h} -");
        var dec = FFmpeg.Start(decArgs, redirectIn: false, redirectOut: true);

        // Webcam: a second forward-only decoder, addressed by its own capture timestamps.
        var wc = p.Webcam is { Width: > 0, Height: > 0, FrameTimes.Count: > 0 } cam && File.Exists(cam.Video) && (style?.ShowWebcam ?? true) ? cam : null;
        var wcFrames = wc != null ? FrameClock.FromTimes(wc.FrameTimes, wc.Fps) : null;
        var wcDec = wc != null
            ? FFmpeg.Start(string.Create(inv, $"-i \"{wc.Video}\" -fps_mode passthrough -f rawvideo -pix_fmt bgra -s {wc.Width}x{wc.Height} -"), redirectIn: false, redirectOut: true)
            : null;

        // Encoder, with the recorded audio mixed in.
        string encArgs = string.Create(inv,
            $"-y -f rawvideo -pixel_format bgra -video_size {outW}x{outH} -framerate {fps} -i - {AudioArgs(p)} {Encoders.Args(encoder, options.Quality)} -movflags +faststart \"{outPath}\"");
        var enc = FFmpeg.Start(encArgs, redirectIn: true, redirectOut: false);

        var srcBuf = new byte[w * h * 4];
        var srcs = Enumerable.Range(0, workers).Select(_ => new byte[w * h * 4]).ToArray();
        var dsts = Enumerable.Range(0, workers).Select(_ => new byte[outW * outH * 4]).ToArray();
        var times = new double[workers];
        var wcBuf = wc != null ? new byte[wc.Width * wc.Height * 4] : null;
        var wcs = Enumerable.Range(0, workers).Select(_ => wcBuf != null ? new byte[wcBuf.Length] : null).ToArray();
        var hasWc = new bool[workers];
        int wcIndex = -1;
        bool wcEnded = false;
        var decOut = dec.StandardOutput.BaseStream;
        var encIn = enc.StandardInput.BaseStream;

        int totalFrames = Math.Max(1, (int)Math.Round(p.DurationMs / 1000.0 * fps));
        int srcIndex = -1;
        bool srcEnded = false;
        bool cancelled = false;
        bool pipeBroken = false;
        try
        {
            for (int frame = 0; frame < totalFrames && !pipeBroken; frame += workers)
            {
                if (cancel.IsCancellationRequested) { cancelled = true; break; }
                // Gather the batch's source frames in order (the decoder only goes forward).
                int n = 0;
                for (; n < workers && frame + n < totalFrames; n++)
                {
                    double tMs = (frame + n) / fps * 1000.0;
                    // Advance the decoder to the frame that was on screen at tMs.
                    int want = frames.FrameAt(tMs);
                    while (!srcEnded && srcIndex < want)
                    {
                        if (ReadFull(decOut, srcBuf)) srcIndex++;
                        else srcEnded = true;
                    }
                    if (srcIndex < 0) break; // empty video
                    Buffer.BlockCopy(srcBuf, 0, srcs[n], 0, srcBuf.Length);
                    times[n] = tMs;

                    // The bubble shows from the webcam's first frame to just past its last.
                    hasWc[n] = false;
                    if (wcFrames != null && tMs >= wcFrames.TimeOf(0) && tMs <= wcFrames.TimeOf(wcFrames.Count - 1) + 500)
                    {
                        int wcWant = wcFrames.FrameAt(tMs);
                        while (!wcEnded && wcIndex < wcWant)
                        {
                            if (ReadFull(wcDec!.StandardOutput.BaseStream, wcBuf!)) wcIndex++;
                            else wcEnded = true;
                        }
                        if (wcIndex >= 0)
                        {
                            Buffer.BlockCopy(wcBuf!, 0, wcs[n]!, 0, wcBuf!.Length);
                            hasWc[n] = true;
                        }
                    }
                }
                if (n == 0) break;
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers },
                    i => comps[i].RenderInto(srcs[i], times[i], dsts[i], hasWc[i] ? wcs[i] : null));
                for (int i = 0; i < n; i++)
                {
                    try { encIn.Write(dsts[i], 0, dsts[i].Length); }
                    catch (IOException) { pipeBroken = true; break; } // encoder died
                }
                progress?.Invoke(Math.Min(1.0, (frame + n) / (double)totalFrames));
            }
        }
        finally
        {
            foreach (var c in comps) c.Dispose();
            try { encIn.Flush(); encIn.Close(); } catch { }
            TryKill(dec);
            if (wcDec != null) TryKill(wcDec);
            if (cancelled) TryKill(enc);
            enc.WaitForExit(60000);
            dec.WaitForExit(10000);
        }
        if (cancelled)
        {
            try { File.Delete(outPath); } catch { }
            cancel.ThrowIfCancellationRequested();
        }
        if (pipeBroken || enc.ExitCode != 0)
        {
            try { File.Delete(outPath); } catch { }
            throw new EncoderFailedException($"ffmpeg encode failed ({Encoders.Label(encoder)}): " + FFmpeg.LastError);
        }
        progress?.Invoke(1.0);
    }

    /// <summary>ffmpeg arguments that mix the project's audio tracks (each
    /// starts at project time 0) into the output, or drop audio if there is none.</summary>
    internal static string AudioArgs(Project p)
    {
        var tracks = (p.Audio ?? new()).Where(a => !a.Muted && a.Volume > 0.001f && File.Exists(a.File)).ToList();
        if (tracks.Count == 0) return "-an";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string inputs = string.Join(" ", tracks.Select(a => $"-i \"{a.File}\""));
        // amix divides every input by the input count; scale back up so levels match the preview.
        string chains = string.Join(";", tracks.Select((a, i) => string.Create(inv, $"[{i + 1}:a]volume={a.Volume * tracks.Count:0.###}[a{i}]")));
        string mix = tracks.Count == 1 ? "[a0]anull[aout]"
            : string.Concat(Enumerable.Range(0, tracks.Count).Select(i => $"[a{i}]")) + $"amix=inputs={tracks.Count}:duration=longest:dropout_transition=0[aout]";
        string t = string.Create(inv, $"{p.DurationMs / 1000.0:0.###}");
        return $"{inputs} -filter_complex \"{chains};{mix}\" -map 0:v -map \"[aout]\" -c:a aac -b:a 192k -t {t}";
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
