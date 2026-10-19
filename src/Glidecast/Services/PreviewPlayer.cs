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
        OutWidth = Math.Min(outWidth, p.Width) & ~1;
        OutHeight = (int)Math.Round(OutWidth * (double)p.Height / p.Width) & ~1;
        // Decode at up to 1920 wide so zoomed-in previews stay sharp.
        int srcW = Math.Min(p.Width, 1920) & ~1;
        int srcH = (int)Math.Round(srcW * (double)p.Height / p.Width) & ~1;

        Frames = FrameClock.For(p);
        _reader = new FrameReader(p.SourceVideo, Frames, srcW, srcH);
        _comp = new Compositor(p, p.BuildCamera(), p.Style?.Clone(), OutWidth, OutHeight, _reader.Width, _reader.Height);
        _src = new byte[_reader.FrameBytes];
        _dst = new byte[OutWidth * OutHeight * 4];
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
                }

                if (wantPlay != IsPlaying)
                {
                    IsPlaying = wantPlay;
                    if (!wantPlay) _reader.EndStream();
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
            _comp.Dispose();
        }
    }

    private void Present(double t)
    {
        _comp.RenderInto(_src, t, _dst);
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
