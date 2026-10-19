using System.Diagnostics;
using System.IO;
using Glidecast.Capture;
using Glidecast.Model;

namespace Glidecast.App.Services;

/// <summary>Screen + input capture for one take, driving the engine's
/// <see cref="ScreenRecorder"/> and <see cref="InputHook"/> on a shared clock.</summary>
public sealed class RecordingSession
{
    private readonly Stopwatch _clock = new();
    private ScreenRecorder? _screen;
    private InputHook? _input;

    public string Directory { get; }
    public string ProjectFile => Path.Combine(Directory, "project.json");
    public TimeSpan Elapsed => _clock.Elapsed;

    public RecordingSession(string directory) => Directory = directory;

    /// <param name="region">Capture rectangle in screen pixels, or null for the full primary screen.</param>
    public void Start(System.Drawing.Rectangle? region, double fps)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string src = Path.Combine(Directory, "source.mp4");
        _screen = region is { } r
            ? new ScreenRecorder(_clock, src, fps, r.X, r.Y, r.Width, r.Height)
            : new ScreenRecorder(_clock, src, fps);
        _input = new InputHook(_clock);
        _clock.Start();
        _input.Start();
        _screen.Start();
    }

    /// <summary>Stops capture and writes project.json. Clicks inside
    /// <paramref name="ignoreArea"/> (the recording toolbar, in screen pixels)
    /// are dropped so pressing Stop doesn't become a zoom focus.</summary>
    public Project Stop(System.Drawing.Rectangle? ignoreArea)
    {
        _clock.Stop();
        _screen!.Stop();
        _input!.Stop();

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

        p.Save(ProjectFile);
        _screen.Dispose();
        _input.Dispose();
        return p;
    }
}
