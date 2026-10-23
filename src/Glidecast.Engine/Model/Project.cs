using System.Text.Json;
using System.Text.Json.Serialization;
using Glidecast.AutoZoom;
using Glidecast.Render;

namespace Glidecast.Model;

/// <summary>
/// A recording project: the raw screen video plus the separately-captured
/// input tracks. Recording the screen and the input log separately is what
/// lets the editor author a zoom timeline, and redraw the cursor, after the fact.
/// </summary>
public sealed class Project
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; } = 30;
    public double DurationMs { get; set; }

    /// <summary>Capture time (ms on the input clock) of each frame in the
    /// source video. Screen capture can't always hold its target rate, so the
    /// video's own frame rate is not a reliable clock; these timestamps are.
    /// Null for older recordings until <see cref="FrameClock.For"/> estimates them.</summary>
    public List<double>? FrameTimes { get; set; }

    /// <summary>Path to the raw captured screen video (no cursor baked in).</summary>
    public string SourceVideo { get; set; } = "source.mp4";

    public List<MoveEvent> Moves { get; set; } = new();
    public List<ClickEvent> Clicks { get; set; } = new();
    public List<KeyEvent> Keys { get; set; } = new();
    /// <summary>Text-caret samples taken while typing (sparse, ~8 Hz).</summary>
    public List<CaretEvent> Carets { get; set; } = new();

    /// <summary>Step numbers, highlights, arrows and callouts on the timeline.</summary>
    public List<Annotation>? Annotations { get; set; }

    /// <summary>Hand-edited zoom focuses. Null means "use the auto-authored
    /// timeline"; once the user edits zooms in the editor this is filled in and
    /// overrides <see cref="ZoomAuthor"/>.</summary>
    public List<ZoomSegment>? ZoomSegments { get; set; }

    /// <summary>Auto-zoom tuning used when (re)authoring segments.</summary>
    public ZoomSettings? Zoom { get; set; }

    /// <summary>Look of the composite (background, padding, overlays).</summary>
    public StyleSettings? Style { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Saves the project. A source video inside the project folder is
    /// stored relative so the folder can be moved.</summary>
    public void Save(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string dir = System.IO.Path.GetDirectoryName(full)!;
        string original = SourceVideo;
        try
        {
            if (System.IO.Path.IsPathRooted(SourceVideo) &&
                string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(SourceVideo)), dir,
                    StringComparison.OrdinalIgnoreCase))
                SourceVideo = System.IO.Path.GetFileName(SourceVideo);
            File.WriteAllText(full, JsonSerializer.Serialize(this, JsonOpts));
        }
        finally { SourceVideo = original; }
    }

    /// <summary>Loads a project and resolves a relative source path against the
    /// project file's folder.</summary>
    public static Project Load(string path)
    {
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException("Could not parse project file: " + path);
        if (!System.IO.Path.IsPathRooted(p.SourceVideo))
            p.SourceVideo = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, p.SourceVideo);
        return p;
    }

    /// <summary>The camera motion to render: hand-edited segments when present,
    /// otherwise the auto-authored ones.</summary>
    public CameraTrack BuildCamera(CanvasAspect? aspect = null) =>
        new(this, ZoomSegments ?? ZoomAuthor.BuildSegments(this, Zoom), Zoom,
            new CursorTrack(this, Style?.CursorSmoothing ?? 0.5), aspect ?? Style?.Aspect ?? CanvasAspect.Source);

    /// <summary>Linear-interpolated cursor position (screen pixels) at time t.</summary>
    public (double x, double y) CursorAt(double tMs)
    {
        if (Moves.Count == 0) return (Width / 2.0, Height / 2.0);
        if (tMs <= Moves[0].T) return (Moves[0].X, Moves[0].Y);
        if (tMs >= Moves[^1].T) return (Moves[^1].X, Moves[^1].Y);

        // Binary search for the surrounding samples.
        int lo = 0, hi = Moves.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Moves[mid].T <= tMs) lo = mid; else hi = mid;
        }
        var a = Moves[lo];
        var b = Moves[hi];
        double span = b.T - a.T;
        double f = span <= 0 ? 0 : (tMs - a.T) / span;
        return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
    }
}

public struct MoveEvent
{
    public double T { get; set; }   // ms from recording start
    public int X { get; set; }      // screen pixels
    public int Y { get; set; }
}

public struct ClickEvent
{
    public double T { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Button { get; set; } // 0 = left, 1 = right, 2 = middle
    public int Count { get; set; }  // 1 single, 2 double
    /// <summary>The UI element under the click (UI Automation), if it could be read.</summary>
    public UiElement? Element { get; set; }
    /// <summary>A meaningful enclosing panel (menu, list, dialog…) of <see cref="Element"/>.</summary>
    public UiElement? Container { get; set; }
}

/// <summary>Screen-space bounds (project pixels) of a UI element, with its
/// accessible name and role, e.g. ("Save", "button").</summary>
public sealed class UiElement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }

    public double Cx => X + W / 2.0;
    public double Cy => Y + H / 2.0;
    public bool Contains(double x, double y) => x >= X && x <= X + W && y >= Y && y <= Y + H;
    public UiElement Offset(int dx, int dy) => new() { X = X - dx, Y = Y - dy, W = W, H = H, Name = Name, Role = Role };
}

/// <summary>Where text was being typed: the caret (or focused field) at time T.</summary>
public struct CaretEvent
{
    public double T { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public struct KeyEvent
{
    public double T { get; set; }
    public string Symbol { get; set; } // e.g. "A", "Enter", "Ctrl"
    public bool Down { get; set; }
}
