using System.Diagnostics;

namespace Glidecast.Render;

public enum VideoEncoder { Auto, Cpu, Nvenc, Qsv, Amf }

/// <summary>
/// H.264 encoder choice. GPU encoders (NVIDIA NVENC, Intel Quick Sync, AMD AMF)
/// are probed once with a tiny real encode, since ffmpeg lists them even when
/// the machine has no such GPU or driver.
/// </summary>
public static class Encoders
{
    private static readonly Dictionary<VideoEncoder, bool> Probed = new();
    private static readonly object Gate = new();

    public static readonly VideoEncoder[] Hardware = { VideoEncoder.Nvenc, VideoEncoder.Qsv, VideoEncoder.Amf };

    public static string Label(VideoEncoder e) => e switch
    {
        VideoEncoder.Nvenc => "NVIDIA NVENC",
        VideoEncoder.Qsv => "Intel Quick Sync",
        VideoEncoder.Amf => "AMD AMF",
        VideoEncoder.Cpu => "CPU (x264)",
        _ => "Auto",
    };

    public static bool TryParse(string s, out VideoEncoder e)
    {
        e = s.Trim().ToLowerInvariant() switch
        {
            "auto" => VideoEncoder.Auto,
            "cpu" or "x264" or "software" => VideoEncoder.Cpu,
            "nvenc" or "nvidia" => VideoEncoder.Nvenc,
            "qsv" or "intel" => VideoEncoder.Qsv,
            "amf" or "amd" => VideoEncoder.Amf,
            _ => (VideoEncoder)(-1),
        };
        return (int)e >= 0;
    }

    private static string Codec(VideoEncoder e) => e switch
    {
        VideoEncoder.Nvenc => "h264_nvenc",
        VideoEncoder.Qsv => "h264_qsv",
        VideoEncoder.Amf => "h264_amf",
        _ => "libx264",
    };

    /// <summary>True if the encoder actually works here (cached after the first probe).</summary>
    public static bool IsAvailable(VideoEncoder e)
    {
        if (e is VideoEncoder.Cpu or VideoEncoder.Auto) return true;
        lock (Gate)
        {
            if (Probed.TryGetValue(e, out bool ok)) return ok;
            ok = Probe(e);
            Probed[e] = ok;
            return ok;
        }
    }

    /// <summary>The working GPU encoders, fastest-first.</summary>
    public static List<VideoEncoder> AvailableHardware() => Hardware.Where(IsAvailable).ToList();

    /// <summary>GPU encoders Auto may pick. Quick Sync is left out: measured
    /// end to end (compositing saturates the CPU), it was no faster than x264
    /// here, while NVENC and AMF run on a separate GPU and offload the CPU.</summary>
    private static readonly VideoEncoder[] AutoOrder = { VideoEncoder.Nvenc, VideoEncoder.Amf };

    /// <summary>Auto → a working discrete-GPU encoder, else CPU; an unavailable explicit choice → CPU.</summary>
    public static VideoEncoder Resolve(VideoEncoder requested)
    {
        if (requested == VideoEncoder.Auto)
            return AutoOrder.FirstOrDefault(IsAvailable, VideoEncoder.Cpu);
        return IsAvailable(requested) ? requested : VideoEncoder.Cpu;
    }

    private static bool Probe(VideoEncoder e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg.Path,
                // NVENC rejects very small frames, so probe at a modest size.
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=30 -frames:v 5 -pix_fmt {PixFmt(e)} -c:v {Codec(e)} -f null -",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string PixFmt(VideoEncoder e) => e is VideoEncoder.Qsv or VideoEncoder.Amf ? "nv12" : "yuv420p";

    /// <summary>ffmpeg output arguments for an encoder at a quality level.</summary>
    public static string Args(VideoEncoder e, ExportQuality q)
    {
        int cq = q switch { ExportQuality.Draft => 26, ExportQuality.High => 16, _ => 19 };
        return e switch
        {
            VideoEncoder.Nvenc => $"-c:v h264_nvenc -preset {q switch { ExportQuality.Draft => "p2", ExportQuality.High => "p7", _ => "p5" }} -tune hq -rc vbr -cq {cq} -b:v 0 -pix_fmt yuv420p",
            VideoEncoder.Qsv => $"-c:v h264_qsv -preset {q switch { ExportQuality.Draft => "veryfast", ExportQuality.High => "veryslow", _ => "medium" }} -global_quality {cq} -pix_fmt nv12",
            VideoEncoder.Amf => $"-c:v h264_amf -quality {q switch { ExportQuality.Draft => "speed", ExportQuality.High => "quality", _ => "balanced" }} -rc cqp -qp_i {cq} -qp_p {cq + 2} -pix_fmt nv12",
            _ => $"-c:v libx264 -preset {q switch { ExportQuality.Draft => "veryfast", ExportQuality.High => "slow", _ => "medium" }} -pix_fmt yuv420p -crf {cq}",
        };
    }
}
