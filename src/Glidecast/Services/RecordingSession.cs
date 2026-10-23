using System.Diagnostics;
using System.IO;
using Glidecast.Capture;
using Glidecast.Model;
using Glidecast.Render;

namespace Glidecast.App.Services;

/// <summary>Screen + input capture for one take, driving the engine's
/// <see cref="ScreenRecorder"/> and <see cref="InputHook"/> on a shared clock,
/// plus the optional webcam, system sound and microphone on the same clock.</summary>
public sealed class RecordingSession
{
    private readonly Stopwatch _clock = new();
    private readonly RecordOptions _options;
    private ScreenRecorder? _screen;
    private InputHook? _input;
    private RecordingJournal? _journal;
    private WebcamRecorder? _webcam;
    private readonly List<AudioRecorder> _audio = new();
    private readonly List<double> _marks = new();

    public string Directory { get; }
    public string ProjectFile => Path.Combine(Directory, "project.json");
    public TimeSpan Elapsed => _clock.Elapsed;
    /// <summary>Sources that were asked for but could not be started.</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>The F9 hotkey is held by this take, so F9 presses are marks, not keystrokes.</summary>
    public bool MarkHotkey { get; set; }

    private readonly CameraFeed? _cameraFeed;

    /// <param name="cameraFeed">The running camera preview to record from, if the camera is on.</param>
    public RecordingSession(string directory, RecordOptions options, CameraFeed? cameraFeed = null)
    {
        Directory = directory;
        _options = options;
        _cameraFeed = cameraFeed;
    }

    /// <param name="region">Capture rectangle in screen pixels, or null for the full primary screen.</param>
    public void Start(System.Drawing.Rectangle? region, double fps)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string src = Path.Combine(Directory, "source.mp4");
        _screen = region is { } r
            ? new ScreenRecorder(_clock, src, fps, r.X, r.Y, r.Width, r.Height)
            : new ScreenRecorder(_clock, src, fps);
        _input = new InputHook(_clock);
        if (_options.SystemAudio)
            _audio.Add(new AudioRecorder(_clock, Path.Combine(Directory, "system.wav"), AudioSource.System, _options.SpeakerId));
        if (_options.Microphone)
            _audio.Add(new AudioRecorder(_clock, Path.Combine(Directory, "mic.wav"), AudioSource.Microphone, _options.MicrophoneId));
        if (_cameraFeed != null)
            _webcam = new WebcamRecorder(_clock, Path.Combine(Directory, "webcam.mp4"), _cameraFeed);
        else if (_options.Camera && !string.IsNullOrEmpty(_options.CameraName))
            _webcam = new WebcamRecorder(_clock, Path.Combine(Directory, "webcam.mp4"), _options.CameraName);

        _clock.Start();
        _input.Start();
        _screen.Start();
        // Extras never stop a take: report what failed and carry on.
        foreach (var a in _audio.ToList())
        {
            try { a.Start(); }
            catch (Exception ex)
            {
                Warnings.Add((a.Source == AudioSource.System ? "System sound" : "Microphone") + ": " + ex.Message);
                _audio.Remove(a);
            }
        }
        if (_webcam != null)
        {
            try { _webcam.Start(); }
            catch (Exception ex) { Warnings.Add("Camera: " + ex.Message); _webcam = null; }
        }
        _journal = new RecordingJournal(Directory, _screen, _input, _clock);
    }

    /// <summary>F9: highlight what's under the cursor right now.</summary>
    public void Mark()
    {
        lock (_marks) _marks.Add(_clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Stops capture and writes project.json. Clicks inside
    /// <paramref name="ignoreArea"/> (the recording toolbar, in screen pixels)
    /// are dropped so pressing Stop doesn't become a zoom focus.</summary>
    public Project Stop(System.Drawing.Rectangle? ignoreArea)
    {
        _clock.Stop();
        _screen!.Stop();
        _input!.Stop();
        _webcam?.Stop();
        foreach (var a in _audio) a.Stop();

        var p = new Project
        {
            Width = _screen.Width,
            Height = _screen.Height,
            Fps = _screen.Fps,
            DurationMs = _clock.Elapsed.TotalMilliseconds,
            SourceVideo = Path.Combine(Directory, "source.mp4"),
            FrameTimes = _screen.FrameTimes,
        };
        if (ignoreArea is { } ig)
        {
            // Filter in screen space before shifting into region space.
            var tmp = new Project();
            _input.CopyInto(tmp);
            var keep = new HashSet<double>(tmp.Clicks.Where(c => !ig.Contains(c.X, c.Y)).Select(c => c.T));
            _input.CopyInto(p, _screen.Left, _screen.Top);
            p.Clicks = p.Clicks.Where(c => keep.Contains(c.T)).ToList();
        }
        else
        {
            _input.CopyInto(p, _screen.Left, _screen.Top);
        }

        AttachMedia(p);
        LookLibrary.ApplyDefault(p);
        p.Style ??= new StyleSettings();
        if (p.Webcam != null) _options.ApplyTo(p.Style);
        AddAnnotations(p);

        p.Save(ProjectFile);
        _journal?.Complete();
        Glidecast.Plugins.Hooks.Fire(Glidecast.Plugins.Hooks.AfterRecord, Directory);
        _screen.Dispose();
        _input.Dispose();
        return p;
    }

    private void AttachMedia(Project p)
    {
        if (_webcam != null)
        {
            var times = _webcam.FrameTimes;
            if (times.Count > 0)
                p.Webcam = new WebcamTrack
                {
                    Video = _webcam.Video, Width = CameraFeed.Width, Height = CameraFeed.Height,
                    Fps = WebcamRecorder.EncodeFps, FrameTimes = times,
                };
            try { File.Delete(Path.Combine(Directory, WebcamRecorder.LogFile)); } catch { }
        }
        foreach (var a in _audio)
        {
            if (!File.Exists(a.File)) continue;
            (p.Audio ??= new()).Add(new AudioTrack { Source = a.Source, File = AudioRecorder.Compress(a.File) });
        }
    }

    /// <summary>The auto walkthrough (if chosen) and a highlight for every F9 mark.</summary>
    private void AddAnnotations(Project p)
    {
        if (_options.Annotate && p.Clicks.Count > 0) AnnotationAuthor.Regenerate(p);

        List<double> marks;
        lock (_marks) marks = _marks.ToList();
        if (MarkHotkey) p.Keys = p.Keys.Where(k => k.Symbol != "F9").ToList();
        foreach (double t in marks)
        {
            var (x, y) = p.CursorAt(t);
            var a = AnnotationAuthor.Create(p, AnnotationKind.Highlight, t, x, y);
            // Box the control under the cursor if a click on it told us its bounds.
            var el = p.Clicks
                .Where(c => c.Element is { W: >= 8, H: >= 8 } e && e.Contains(x, y) && Math.Abs(c.T - t) < 5000 &&
                            e.W * (double)e.H < p.Width * (double)p.Height * 0.2)
                .OrderBy(c => Math.Abs(c.T - t)).Select(c => c.Element).FirstOrDefault();
            if (el != null) { a.X = el.X - 6; a.Y = el.Y - 6; a.W = el.W + 12; a.H = el.H + 12; }
            a.Start = Math.Max(0, t - 150);
            a.End = Math.Min(p.DurationMs, t + 2500);
            (p.Annotations ??= new()).Add(a);
        }
        if (p.Annotations is { Count: > 0 })
        {
            p.Annotations.Sort((m, n) => m.Start.CompareTo(n.Start));
            p.Style!.DrawAnnotations = true;
        }
    }
}
