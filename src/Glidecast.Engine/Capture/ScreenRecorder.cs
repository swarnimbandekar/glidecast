using System.Collections.Concurrent;
using System.Diagnostics;
using Glidecast.Native;
using Glidecast.Render;

namespace Glidecast.Capture;

/// <summary>
/// Grabs the screen with GDI BitBlt and streams raw BGRA frames into ffmpeg,
/// producing a cursor-free source video. The OS cursor is intentionally not
/// captured — the renderer draws a synthetic one from the input track.
///
/// Every frame is stamped with the shared input clock (<see cref="FrameTimes"/>).
/// If the machine can't sustain the target rate, frames are simply fewer, never
/// late: playback maps each moment to the frame captured then, so the picture
/// stays in sync with the cursor, clicks and zooms. Encoding runs on its own
/// thread with a small buffer pool so a slow encoder can't stall capture.
///
/// While recording, the file is a fragmented MP4 (a fragment every ~2 s), so
/// a crash or power loss leaves a playable video up to the last fragment; on a
/// clean stop it is remuxed into a regular, seekable MP4.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    /// <summary>Top-left of the captured area in screen pixels.</summary>
    public int Left { get; }
    public int Top { get; }

    private readonly Stopwatch _clock;
    private readonly string _outPath;
    private Process? _ff;
    private Thread? _capture, _writer;
    private volatile bool _running;
    private readonly List<double> _times = new();
    private readonly BlockingCollection<byte[]> _filled = new(new ConcurrentQueue<byte[]>());
    private readonly ConcurrentBag<byte[]> _free = new();
    private const int PoolSize = 6;

    /// <summary>Records the whole primary screen.</summary>
    public ScreenRecorder(Stopwatch clock, string outPath, double fps = 30)
        : this(clock, outPath, fps, 0, 0,
            Win32.GetSystemMetrics(Win32.SM_CXSCREEN), Win32.GetSystemMetrics(Win32.SM_CYSCREEN))
    {
    }

    /// <summary>Records a rectangle of the desktop, in screen pixels.</summary>
    public ScreenRecorder(Stopwatch clock, string outPath, double fps, int left, int top, int width, int height)
    {
        _clock = clock;
        _outPath = outPath;
        Fps = fps;
        Left = left;
        Top = top;
        // H.264 needs even dimensions.
        Width = Math.Max(2, width & ~1);
        Height = Math.Max(2, height & ~1);
    }

    public long FrameCount { get { lock (_times) return _times.Count; } }

    /// <summary>Input-clock time (ms) at which each written frame was captured.</summary>
    public List<double> FrameTimes { get { lock (_times) return new List<double>(_times); } }

    /// <summary>Timestamps of frames captured since index <paramref name="from"/> (for the journal).</summary>
    public List<double> FrameTimesSince(int from)
    {
        lock (_times) return from >= _times.Count ? new List<double>() : _times.GetRange(from, _times.Count - from);
    }

    public void Start()
    {
        // Intermediate file: favour encode speed; quality stays high via low CRF.
        string args = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"-y -f rawvideo -pixel_format bgra -video_size {Width}x{Height} -framerate {Fps} -i - -an -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf 17 -g {Math.Max(1, (int)Math.Round(Fps * 2))} -movflags +frag_keyframe+empty_moov+default_base_moof \"{_outPath}\"");
        _ff = FFmpeg.Start(args, redirectIn: true, redirectOut: false);
        for (int i = 0; i < PoolSize; i++) _free.Add(new byte[Width * Height * 4]);
        _running = true;
        Win32.timeBeginPeriod(1); // 1 ms sleeps, so 60 fps pacing is possible
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Glidecast-Encode" };
        _writer.Start();
        _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "Glidecast-Screen", Priority = ThreadPriority.AboveNormal };
        _capture.Start();
    }

    private void CaptureLoop()
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr bmp = Win32.CreateCompatibleBitmap(screenDc, Width, Height);
        Win32.SelectObject(memDc, bmp);

        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };

        double interval = 1000.0 / Fps;
        double next = _clock.Elapsed.TotalMilliseconds;
        try
        {
            while (_running)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                if (now < next)
                {
                    int wait = (int)(next - now);
                    if (wait >= 1) Thread.Sleep(wait); else Thread.Yield();
                    continue;
                }
                // Behind schedule: skip ahead instead of bursting to catch up.
                next = Math.Max(next + interval, now + interval * 0.5);

                if (!_free.TryTake(out var buf)) continue; // encoder is behind; drop this tick

                double t0 = _clock.Elapsed.TotalMilliseconds;
                Win32.BitBlt(memDc, 0, 0, Width, Height, screenDc, Left, Top, Win32.SRCCOPY | Win32.CAPTUREBLT);
                double t1 = _clock.Elapsed.TotalMilliseconds;
                Win32.GetDIBits(memDc, bmp, 0, (uint)Height, buf, ref header, Win32.DIB_RGB_COLORS);

                lock (_times) _times.Add(Math.Round((t0 + t1) / 2, 2));
                _filled.Add(buf);
            }
        }
        finally
        {
            _filled.CompleteAdding();
            Win32.DeleteObject(bmp);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void WriteLoop()
    {
        var stream = _ff!.StandardInput.BaseStream;
        try
        {
            foreach (var buf in _filled.GetConsumingEnumerable())
            {
                stream.Write(buf, 0, buf.Length);
                _free.Add(buf);
            }
        }
        catch (IOException) { /* ffmpeg closed the pipe */ }
    }

    public void Stop()
    {
        if (!_running && _capture == null) return;
        _running = false;
        _capture?.Join(3000);
        _writer?.Join(30000); // flush queued frames
        _capture = null;
        Win32.timeEndPeriod(1);
        try { _ff?.StandardInput.BaseStream.Flush(); _ff?.StandardInput.Close(); } catch { }
        _ff?.WaitForExit(30000);
        Finish(_outPath);
    }

    /// <summary>Rewrites a fragmented capture as a regular MP4 with its index up
    /// front (fast seeking for the editor). Stream copy: no quality loss.
    /// Leaves the fragmented file in place if anything goes wrong.</summary>
    public static void Finish(string video)
    {
        if (!File.Exists(video)) return;
        string tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video))!, "source.remux.mp4");
        try
        {
            FFmpeg.Run($"-y -hide_banner -loglevel error -i \"{video}\" -map 0 -c copy -movflags +faststart \"{tmp}\"", 300000);
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                File.Move(tmp, video, overwrite: true);
        }
        catch { }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public void Dispose() => Stop();
}
