using System.Diagnostics;
using System.Text.RegularExpressions;
using Glidecast.Render;

namespace Glidecast.Capture;

/// <summary>Webcams, as DirectShow names that ffmpeg can open.</summary>
public static class CameraDevices
{
    public static List<string> List()
    {
        try
        {
            string log = FFmpeg.Run("-hide_banner -list_devices true -f dshow -i dummy", 10000);
            return Regex.Matches(log, "\"(.+?)\" \\(video\\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        }
        catch { return new(); }
    }
}

/// <summary>
/// Live frames from a webcam: ffmpeg opens the device and hands us raw BGRA,
/// center-cropped to a fixed 4:3 size, so every shape (circle, square, oval)
/// can be cut from it. Each frame is stamped on the given clock as it arrives.
/// Only one feed can hold a camera at a time.
/// </summary>
public sealed class CameraFeed : IDisposable
{
    public const int Width = 640, Height = 480;
    public const int FrameBytes = Width * Height * 4;

    private readonly string _device;
    private readonly Stopwatch _clock;
    private Process? _ff;
    private Thread? _reader;
    private volatile bool _running;

    /// <summary>Raised on the reader thread; the buffer is reused after the call returns.</summary>
    public event Action<byte[], double>? Frame;
    /// <summary>Raised once if the device could not be opened or stopped delivering.</summary>
    public event Action<string>? Failed;

    public CameraFeed(string device, Stopwatch? clock = null)
    {
        _device = device;
        _clock = clock ?? Stopwatch.StartNew();
    }

    public void Start()
    {
        string name = _device.Replace("\"", "\\\"");
        _ff = FFmpeg.Start(
            $"-hide_banner -loglevel error -f dshow -rtbufsize 64M -i video=\"{name}\" -an " +
            $"-vf scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height} -pix_fmt bgra -f rawvideo -",
            redirectIn: false, redirectOut: true);
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Glidecast-Camera" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[FrameBytes];
        var stream = _ff!.StandardOutput.BaseStream;
        try
        {
            while (_running && VideoRenderer.ReadFull(stream, buf))
                Frame?.Invoke(buf, _clock.Elapsed.TotalMilliseconds);
        }
        catch (Exception) when (!_running) { }
        catch (Exception ex) { Failed?.Invoke(ex.Message); return; }
        if (_running) Failed?.Invoke(FFmpeg.LastError ?? "The camera stopped.");
    }

    public void Dispose()
    {
        _running = false;
        try { if (_ff is { HasExited: false }) _ff.Kill(); } catch { }
        _reader?.Join(2000);
        _ff?.Dispose();
        _ff = null;
    }
}

/// <summary>
/// Records a webcam next to the screen: frames from a <see cref="CameraFeed"/>
/// are stamped on the shared input clock and encoded to a fragmented MP4
/// (crash-safe like the screen). Frame times are also appended to
/// webcam.log so an interrupted take can be recovered. It can share a feed
/// that is already running (the floating camera preview), so the camera
/// never has to reopen when the take starts.
/// </summary>
public sealed class WebcamRecorder : IDisposable
{
    public const string LogFile = "webcam.log";
    public const double EncodeFps = 30;

    private readonly string _outPath;
    private readonly Stopwatch _clock;
    private readonly CameraFeed _feed;
    private readonly bool _ownsFeed;
    private readonly List<double> _times = new();
    private readonly object _gate = new();
    private Process? _enc;
    private Stream? _encIn;
    private StreamWriter? _log;

    public string Video => _outPath;
    public string? Error { get; private set; }

    public WebcamRecorder(Stopwatch clock, string outPath, string device)
        : this(clock, outPath, new CameraFeed(device, clock), ownsFeed: true)
    {
    }

    /// <param name="feed">A feed to record from; started here if this recorder owns it.</param>
    public WebcamRecorder(Stopwatch clock, string outPath, CameraFeed feed, bool ownsFeed = false)
    {
        _outPath = outPath;
        _clock = clock;
        _feed = feed;
        _ownsFeed = ownsFeed;
        _feed.Frame += OnFrame;
        _feed.Failed += OnFailed;
    }

    private void OnFailed(string e) => Error = e;

    public List<double> FrameTimes { get { lock (_gate) return new List<double>(_times); } }

    public void Start()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _enc = FFmpeg.Start(string.Create(inv,
            $"-y -f rawvideo -pixel_format bgra -video_size {CameraFeed.Width}x{CameraFeed.Height} -framerate {EncodeFps} -i - -an " +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -crf 20 -g 60 -movflags +frag_keyframe+empty_moov+default_base_moof \"{_outPath}\""),
            redirectIn: true, redirectOut: false);
        _encIn = _enc.StandardInput.BaseStream;
        _log = new StreamWriter(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_outPath))!, LogFile)) { AutoFlush = true };
        if (_ownsFeed) _feed.Start();
    }

    private void OnFrame(byte[] buf, double _)
    {
        lock (_gate)
        {
            if (_encIn == null) return;
            // Stamped on the take's clock (a shared feed runs on its own).
            double t = _clock.Elapsed.TotalMilliseconds;
            try
            {
                _encIn.Write(buf, 0, buf.Length);
                _times.Add(Math.Round(t, 2));
                _log?.WriteLine(t.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (IOException) { Error ??= "The webcam encoder stopped."; _encIn = null; }
        }
    }

    public void Stop()
    {
        _feed.Frame -= OnFrame;
        _feed.Failed -= OnFailed;
        if (_ownsFeed) _feed.Dispose();
        lock (_gate)
        {
            try { _encIn?.Flush(); _encIn?.Close(); } catch { }
            _encIn = null;
            _log?.Dispose();
            _log = null;
        }
        _enc?.WaitForExit(30000);
        _enc = null;
        ScreenRecorder.Finish(_outPath);
    }

    public void Dispose() => Stop();
}
