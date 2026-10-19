using System.Diagnostics;
using System.Globalization;

namespace Glidecast.Render;

/// <summary>
/// Decodes source-video frames to raw BGRA at a fixed (possibly downscaled)
/// size for the editor preview, addressed by frame index (see
/// <see cref="FrameClock"/>): a one-shot exact seek, or a sequential stream of
/// every frame from an index.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private readonly string _video;
    private readonly FrameClock _clock;
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    private Process? _stream;
    private Stream? _out;

    /// <summary>Index of the frame the last <see cref="ReadNext"/> returned.</summary>
    public int StreamIndex { get; private set; } = -1;

    public FrameReader(string video, FrameClock clock, int width, int height)
    {
        _video = video;
        _clock = clock;
        Width = width & ~1;
        Height = height & ~1;
    }

    private string Args(int frame, string extra) =>
        string.Create(CultureInfo.InvariantCulture,
            $"-ss {_clock.SeekSeconds(frame):0.####} -i \"{_video}\" {extra} -vf scale={Width}:{Height}:flags=bilinear -f rawvideo -pix_fmt bgra -");

    /// <summary>Decodes frame <paramref name="frame"/> into buf.</summary>
    public bool ReadFrame(int frame, byte[] buf)
    {
        var p = FFmpeg.Start(Args(frame, "-frames:v 1"), redirectIn: false, redirectOut: true);
        try { return VideoRenderer.ReadFull(p.StandardOutput.BaseStream, buf); }
        finally
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            p.Dispose();
        }
    }

    /// <summary>Starts sequential decoding; the next ReadNext returns <paramref name="frame"/>.</summary>
    public void BeginStream(int frame)
    {
        EndStream();
        _stream = FFmpeg.Start(Args(frame, "-fps_mode passthrough"), redirectIn: false, redirectOut: true);
        _out = _stream.StandardOutput.BaseStream;
        StreamIndex = frame - 1;
    }

    /// <summary>Reads the next streamed frame. Returns false at end of video.</summary>
    public bool ReadNext(byte[] buf)
    {
        if (_out == null || !VideoRenderer.ReadFull(_out, buf)) return false;
        StreamIndex++;
        return true;
    }

    public bool Streaming => _out != null;

    public void EndStream()
    {
        StreamIndex = -1;
        if (_stream == null) return;
        try { if (!_stream.HasExited) _stream.Kill(); } catch { }
        _stream.Dispose();
        _stream = null;
        _out = null;
    }

    public void Dispose() => EndStream();
}
