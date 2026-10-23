using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Glidecast.Model;

namespace Glidecast.Capture;

/// <summary>
/// Crash insurance for a take. Once a second, everything captured so far —
/// frame timestamps and input events — is appended to small journal files
/// next to the (fragmented, always-playable) source video. If the app or PC
/// dies mid-recording, <see cref="RecordingRecovery"/> rebuilds the project
/// from them. A clean stop deletes the journal.
///
/// Files: recording.json (geometry), frames.log (one timestamp per line),
/// input.log (one "m|c|k|r {json}" event per line, screen coordinates).
/// </summary>
public sealed class RecordingJournal : IDisposable
{
    internal const string HeaderFile = "recording.json";
    internal const string FramesFile = "frames.log";
    internal const string InputFile = "input.log";

    private readonly string _dir;
    private readonly ScreenRecorder _screen;
    private readonly InputHook _input;
    private readonly Stopwatch _clock;
    private readonly Timer _timer;
    private readonly InputHook.Cursor _at = new();
    private readonly object _gate = new();
    private int _frames;
    private bool _done;

    internal sealed class Header
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public DateTime Started { get; set; }
    }

    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public RecordingJournal(string dir, ScreenRecorder screen, InputHook input, Stopwatch clock)
    {
        _dir = dir;
        _screen = screen;
        _input = input;
        _clock = clock;
        Directory.CreateDirectory(dir);
        var header = new Header { Width = screen.Width, Height = screen.Height, Fps = screen.Fps, Left = screen.Left, Top = screen.Top, Started = DateTime.Now };
        File.WriteAllText(Path.Combine(dir, HeaderFile), JsonSerializer.Serialize(header, Json));
        File.WriteAllText(Path.Combine(dir, FramesFile), "");
        File.WriteAllText(Path.Combine(dir, InputFile), "");
        _timer = new Timer(_ => Flush(false), null, 1000, 1000);
    }

    private void Flush(bool final)
    {
        lock (_gate)
        {
            if (_done) return;
            try
            {
                var times = _screen.FrameTimesSince(_frames);
                _frames += times.Count;
                if (times.Count > 0)
                    File.AppendAllLines(Path.Combine(_dir, FramesFile), times.Select(t => t.ToString("0.##", CultureInfo.InvariantCulture)));

                // Hold clicks back ~1.5 s so their UI element lookup is included.
                double settled = final ? double.MaxValue : _clock.Elapsed.TotalMilliseconds - 1500;
                var (m, c, k, r) = _input.TakeNew(_at, settled);
                var lines = new List<string>(m.Count + c.Count + k.Count + r.Count);
                lines.AddRange(m.Select(e => "m " + JsonSerializer.Serialize(e, Json)));
                lines.AddRange(c.Select(e => "c " + JsonSerializer.Serialize(e, Json)));
                lines.AddRange(k.Select(e => "k " + JsonSerializer.Serialize(e, Json)));
                lines.AddRange(r.Select(e => "r " + JsonSerializer.Serialize(e, Json)));
                if (lines.Count > 0) File.AppendAllLines(Path.Combine(_dir, InputFile), lines);
            }
            catch { /* disk full / locked: keep recording regardless */ }
        }
    }

    /// <summary>The take finished cleanly and project.json is written: drop the journal.</summary>
    public void Complete()
    {
        _timer.Dispose();
        lock (_gate)
        {
            _done = true;
            foreach (var f in new[] { HeaderFile, FramesFile, InputFile })
                try { File.Delete(Path.Combine(_dir, f)); } catch { }
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Finds takes that never finished (crash, power loss) and rebuilds
/// their project from the journal and the fragmented source video.</summary>
public static class RecordingRecovery
{
    public static bool IsUnfinished(string dir) =>
        File.Exists(Path.Combine(dir, RecordingJournal.HeaderFile)) && !File.Exists(Path.Combine(dir, "project.json"));

    public static List<string> FindUnfinished(string root)
    {
        // A journal written to in the last few seconds belongs to a take still
        // being recorded (e.g. by another Glidecast window): leave it alone.
        static bool Live(string d) =>
            DateTime.Now - File.GetLastWriteTime(Path.Combine(d, RecordingJournal.FramesFile)) < TimeSpan.FromSeconds(6);
        try { return Directory.EnumerateDirectories(root).Where(d => IsUnfinished(d) && !Live(d)).ToList(); }
        catch { return new(); }
    }

    /// <summary>Rebuilds project.json; returns null if nothing usable was captured.</summary>
    public static Project? Recover(string dir)
    {
        var header = JsonSerializer.Deserialize<RecordingJournal.Header>(
            File.ReadAllText(Path.Combine(dir, RecordingJournal.HeaderFile)), RecordingJournal.Json);
        string src = Path.Combine(dir, "source.mp4");
        if (header == null || !File.Exists(src) || new FileInfo(src).Length == 0) return null;

        // Stream-copy what survived into a regular MP4, then count real frames.
        ScreenRecorder.Finish(src);
        int frameCount = 0;
        try { frameCount = Render.FFmpeg.CountFrames(src); } catch { }
        if (frameCount <= 0) return null;

        var times = ReadLines(Path.Combine(dir, RecordingJournal.FramesFile))
            .Select(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN)
            .Where(t => !double.IsNaN(t)).ToList();
        // The video ends at its last complete fragment; the journal may run a little past it, or behind.
        if (times.Count > frameCount) times = times.GetRange(0, frameCount);
        bool estimated = times.Count < frameCount;

        var p = new Project { Width = header.Width, Height = header.Height, Fps = header.Fps, SourceVideo = src };
        foreach (var line in ReadLines(Path.Combine(dir, RecordingJournal.InputFile)))
        {
            if (line.Length < 3) continue;
            string json = line[2..];
            try
            {
                switch (line[0])
                {
                    case 'm':
                        var m = JsonSerializer.Deserialize<MoveEvent>(json, RecordingJournal.Json);
                        p.Moves.Add(new MoveEvent { T = m.T, X = m.X - header.Left, Y = m.Y - header.Top });
                        break;
                    case 'c':
                        var c = JsonSerializer.Deserialize<ClickEvent>(json, RecordingJournal.Json);
                        c.X -= header.Left; c.Y -= header.Top;
                        c.Element = c.Element?.Offset(header.Left, header.Top);
                        c.Container = c.Container?.Offset(header.Left, header.Top);
                        p.Clicks.Add(c);
                        break;
                    case 'k':
                        p.Keys.Add(JsonSerializer.Deserialize<KeyEvent>(json, RecordingJournal.Json));
                        break;
                    case 'r':
                        var r = JsonSerializer.Deserialize<CaretEvent>(json, RecordingJournal.Json);
                        p.Carets.Add(new CaretEvent { T = r.T, X = r.X - header.Left, Y = r.Y - header.Top, W = r.W, H = r.H });
                        break;
                }
            }
            catch { /* a torn last line */ }
        }

        if (estimated)
        {
            // Journal lost its tail: spread the frames we have over the known span.
            double span = Math.Max(times.LastOrDefault(), p.Moves.Count > 0 ? p.Moves[^1].T : 0);
            if (span <= 0) span = frameCount / Math.Max(1, header.Fps) * 1000;
            p.FrameTimes = Enumerable.Range(0, frameCount).Select(i => Math.Round(i * span / frameCount, 2)).ToList();
        }
        else p.FrameTimes = times;
        p.DurationMs = p.FrameTimes[^1] + 1000.0 / Math.Max(1, header.Fps);

        // Drop input after the end of the surviving video.
        p.Moves = p.Moves.Where(e => e.T <= p.DurationMs).ToList();
        p.Clicks = p.Clicks.Where(e => e.T <= p.DurationMs).ToList();
        p.Keys = p.Keys.Where(e => e.T <= p.DurationMs).ToList();
        p.Carets = p.Carets.Where(e => e.T <= p.DurationMs).ToList();

        p.Save(Path.Combine(dir, "project.json"));
        foreach (var f in new[] { RecordingJournal.HeaderFile, RecordingJournal.FramesFile, RecordingJournal.InputFile })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        return p;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();
        // Shared read: tolerate a file still held by a dying process.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } l) lines.Add(l);
        return lines;
    }
}
