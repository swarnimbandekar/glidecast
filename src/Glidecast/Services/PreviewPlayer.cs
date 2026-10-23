using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glidecast.AutoZoom;
using Glidecast.Model;
using Glidecast.Render;

namespace Glidecast.App.Services;

/// <summary>
/// Drives the editor preview: decodes source frames with ffmpeg on a worker
/// thread, composites them through the engine's <see cref="Compositor"/> at the
/// playhead, and copies the result into a <see cref="WriteableBitmap"/>.
/// Requests (seek / play / restyle) are coalesced: only the latest one counts.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private readonly Project _p;
    private readonly Dispatcher _ui;
    private readonly Thread _worker;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private readonly FrameReader _reader;
    private readonly Compositor _comp;
    private readonly byte[] _src, _dst;
    // Webcam (optional): its own reader on its own capture clock.
    private readonly FrameReader? _wcReader;
    private readonly FrameClock? _wcFrames;
    private readonly byte[]? _wc;
    private bool _haveWc;

    // Pending requests (guarded by _gate).
    private double? _seekTo;
    private bool _wantPlay;
    private CameraTrack? _newCamera;
    private StyleSettings? _newStyle;
    private bool _dirty;

    public WriteableBitmap Bitmap { get; }
    /// <summary>Project time ↔ source frame mapping (capture timestamps).</summary>
    public FrameClock Frames { get; }
    public int OutWidth { get; }
    public int OutHeight { get; }
    public double Position { get; private set; }
    public bool IsPlaying { get; private set; }

    /// <summary>Raised on the UI thread after each presented frame.</summary>
    public event Action<double>? PositionChanged;
    /// <summary>Raised on the UI thread when playback starts or stops.</summary>
    public event Action<bool>? PlayingChanged;

    public PreviewPlayer(Project p, Dispatcher ui, int outWidth)
    {
        _p = p;
        _ui = ui;
        // Preview in the canvas's shape (reframed 9:16 / 1:1 previews as exported),
        // with the long side at most outWidth.
        var (cw, ch) = Canvas.OutputSize(p.Style?.Aspect ?? CanvasAspect.Source, p.Width, p.Height);
        double fit = Math.Min(1.0, outWidth / (double)Math.Max(cw, ch));
        OutWidth = Math.Max(2, (int)Math.Round(cw * fit) & ~1);
        OutHeight = Math.Max(2, (int)Math.Round(ch * fit) & ~1);
        // Decode at up to 1920 wide so zoomed-in previews stay sharp.
        int srcW = Math.Min(p.Width, 1920) & ~1;
        int srcH = (int)Math.Round(srcW * (double)p.Height / p.Width) & ~1;

        Frames = FrameClock.For(p);
        _reader = new FrameReader(p.SourceVideo, Frames, srcW, srcH);
        _comp = new Compositor(p, p.BuildCamera(), p.Style?.Clone(), OutWidth, OutHeight, _reader.Width, _reader.Height);
        _src = new byte[_reader.FrameBytes];
        _dst = new byte[OutWidth * OutHeight * 4];
        if (p.Webcam is { Width: > 0, Height: > 0, FrameTimes.Count: > 0 } wc && System.IO.File.Exists(wc.Video))
        {
            _wcFrames = FrameClock.FromTimes(wc.FrameTimes, wc.Fps);
            _wcReader = new FrameReader(wc.Video, _wcFrames, wc.Width, wc.Height);
            _wc = new byte[_wcReader.FrameBytes];
        }
        Bitmap = new WriteableBitmap(OutWidth, OutHeight, 96, 96, PixelFormats.Pbgra32, null);

        _seekTo = 0;
        _worker = new Thread(Run) { IsBackground = true, Name = "Glidecast-Preview" };
        _worker.Start();
    }

    public void Seek(double tMs)
    {
        lock (_gate) _seekTo = Math.Clamp(tMs, 0, _p.DurationMs);
        _wake.Set();
    }

    public void Play()
    {
        lock (_gate)
        {
            _wantPlay = true;
            if (Position >= _p.DurationMs - 50) _seekTo = 0;
        }
        _wake.Set();
    }

    public void Pause()
    {
        lock (_gate) _wantPlay = false;
        _wake.Set();
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause(); else Play();
    }

    /// <summary>Re-render the current frame with a new zoom timeline / style.</summary>
    public void Update(CameraTrack camera, StyleSettings style)
    {
        lock (_gate)
        {
            _newCamera = camera;
            _newStyle = style.Clone();
            _dirty = true;
        }
        _wake.Set();
    }

    private void Run()
    {
        var ct = _cts.Token;
        bool haveFrame = false;
        var playClock = new Stopwatch();
        double playStart = 0;
        const double TickMs = 1000.0 / 60; // preview refresh while playing

        try
        {
            while (!ct.IsCancellationRequested)
            {
                double? seek;
                bool wantPlay, dirty;
                lock (_gate)
                {
                    seek = _seekTo; _seekTo = null;
                    wantPlay = _wantPlay;
                    dirty = _dirty; _dirty = false;
                    if (_newCamera != null) { _comp.Camera = _newCamera; _newCamera = null; }
                    if (_newStyle != null) { _comp.Style = _newStyle; _newStyle = null; }
                }

                if (seek is { } t0)
                {
                    Position = t0;
                    _reader.EndStream();
                    _wcReader?.EndStream();
                }

                if (wantPlay != IsPlaying)
                {
                    IsPlaying = wantPlay;
                    if (!wantPlay) { _reader.EndStream(); _wcReader?.EndStream(); }
                    bool playing = wantPlay;
                    _ui.BeginInvoke(() => PlayingChanged?.Invoke(playing));
                }

                if (IsPlaying)
                {
                    double tickStart = Environment.TickCount64;
                    if (!_reader.Streaming)
                    {
                        _reader.BeginStream(Frames.FrameAt(Position));
                        playStart = Position;
                        playClock.Restart();
                    }
                    // Wall-clock time drives playback; the picture is whichever
                    // frame was captured at that moment.
                    double t = Math.Min(_p.DurationMs, playStart + playClock.Elapsed.TotalMilliseconds);
                    int want = Frames.FrameAt(t);
                    while (_reader.StreamIndex < want)
                        if (!_reader.ReadNext(_src)) break;
                    if (_reader.StreamIndex >= 0) haveFrame = true;
                    if (WebcamShows(t))
                    {
                        if (!_wcReader!.Streaming) _wcReader.BeginStream(_wcFrames!.FrameAt(t));
                        int wcWant = _wcFrames!.FrameAt(t);
                        while (_wcReader.StreamIndex < wcWant)
                            if (!_wcReader.ReadNext(_wc!)) break;
                        _haveWc = _wcReader.StreamIndex >= 0;
                    }
                    else _haveWc = false;
                    if (haveFrame) Present(t);
                    if (t >= _p.DurationMs)
                    {
                        _reader.EndStream();
                        lock (_gate) _wantPlay = false;
                        continue;
                    }
                    double rest = TickMs - (Environment.TickCount64 - tickStart);
                    if (rest >= 1) _wake.WaitOne((int)rest); // a seek/pause interrupts the wait
                    continue;
                }

                if (seek is { } ts)
                {
                    _haveWc = WebcamShows(ts) && _wcReader!.ReadFrame(_wcFrames!.FrameAt(ts), _wc!);
                    if (_reader.ReadFrame(Frames.FrameAt(ts), _src))
                    {
                        haveFrame = true;
                        Present(ts);
                    }
                    continue;
                }

                if (dirty && haveFrame)
                {
                    Present(Position);
                    continue;
                }

                _wake.WaitOne(250);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                _ui.BeginInvoke(() => MessageBox.Show("Preview stopped: " + ex.Message, "Glidecast"));
        }
        finally
        {
            _reader.Dispose();
            _wcReader?.Dispose();
            _comp.Dispose();
        }
    }

    /// <summary>The bubble shows from the webcam's first frame to just past its last.</summary>
    private bool WebcamShows(double t) =>
        _wcFrames != null && t >= _wcFrames.TimeOf(0) && t <= _wcFrames.TimeOf(_wcFrames.Count - 1) + 500;

    /// <summary>The project has a webcam take that can be previewed.</summary>
    public bool HasWebcam => _wcReader != null;

    private void Present(double t)
    {
        _comp.RenderInto(_src, t, _dst, _haveWc ? _wc : null);
        Position = t;
        try
        {
            _ui.Invoke(() =>
            {
                Bitmap.WritePixels(new Int32Rect(0, 0, OutWidth, OutHeight), _dst, OutWidth * 4, 0);
                PositionChanged?.Invoke(t);
            }, DispatcherPriority.Render, _cts.Token, TimeSpan.FromSeconds(2));
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
    }
}
