using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glidecast.AutoZoom;
using Glidecast.Model;

namespace Glidecast.App.Controls;

/// <summary>
/// Editor timeline: ruler (click/drag to scrub), a film strip of the recording,
/// a zoom lane whose blocks can be selected, moved, resized at either edge and
/// added by double-clicking empty space, and an input lane showing clicks and
/// keystrokes. Segments are edited in place on <see cref="Project.ZoomSegments"/>.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double Gutter = 70;
    private const double PadR = 18;
    private const double RulerH = 28;
    private const double FilmTop = RulerH + 6;
    private const double FilmH = 50;
    private const double ZoomTop = FilmTop + FilmH + 10;
    private const double ZoomH = 42;
    private const double InputTop = ZoomTop + ZoomH + 10;
    private const double InputH = 18;
    private const double TotalH = InputTop + InputH + 12;
    private const double EdgeGrab = 8;
    private const double MinSegMs = 200;

    private static readonly Brush BgBrush = Frozen("#121418");
    private static readonly Brush LaneBrush = Frozen("#1A1D23");
    private static readonly Brush TickBrush = Frozen("#2E333E");
    private static readonly Brush LabelBrush = Frozen("#7E8594");
    private static readonly Brush ClickBrush = Frozen("#F5B94A");
    private static readonly Brush KeyBrush = Frozen("#3DD6B5");
    private static readonly Brush PlayheadBrush = Frozen("#FF4D6A");
    private static readonly Brush SegBrush = Gradient("#7C5CFF", "#4F8CFF", 1);
    private static readonly Brush SegHoverBrush = Gradient("#8E72FF", "#679CFF", 1);
    private static readonly Brush SegSelBrush = Gradient("#9A80FF", "#72A6FF", 1);
    private static readonly Brush FilmShade = Frozen("#66000000");
    private static readonly Pen SelPen = FrozenPen(Brushes.White, 2);
    private static readonly Pen FilmEdge = FrozenPen(Frozen("#2A2F3A"), 1);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Typeface MonoFace = new("Cascadia Mono, Consolas");

    private Project? _project;
    private double _playhead;
    private ZoomSegment? _selected, _hover;
    private IReadOnlyList<BitmapSource>? _film;

    private enum DragMode { None, Scrub, Move, ResizeStart, ResizeEnd }
    private DragMode _drag;
    private double _dragOffsetMs;

    /// <summary>User scrubbed to a time.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Segments were added, moved, resized or deleted.</summary>
    public event Action? SegmentsChanged;
    /// <summary>Selection changed (null = none).</summary>
    public event Action<ZoomSegment?>? SelectionChanged;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
    }

    public Project? Project
    {
        get => _project;
        set { _project = value; _selected = null; InvalidateVisual(); }
    }

    public double Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }

    /// <summary>Evenly spaced frames across the recording for the film strip.</summary>
    public IReadOnlyList<BitmapSource>? FilmStrip
    {
        get => _film;
        set { _film = value; InvalidateVisual(); }
    }

    /// <summary>Maps project time to a 0..1 position in the source video (the
    /// film strip is evenly spaced in video time). Defaults to linear.</summary>
    public Func<double, double>? FilmPosition { get; set; }

    private int FilmIndex(double t, int count) =>
        Math.Clamp((int)((FilmPosition?.Invoke(t) ?? t / Duration) * count), 0, count - 1);

    public ZoomSegment? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            InvalidateVisual();
            SelectionChanged?.Invoke(value);
        }
    }

    private List<ZoomSegment> Segments => _project?.ZoomSegments ?? new List<ZoomSegment>();
    private double Duration => Math.Max(1, _project?.DurationMs ?? 1);
    private double Usable => Math.Max(1, ActualWidth - Gutter - PadR);
    private double X(double ms) => Gutter + ms / Duration * Usable;
    private double Ms(double x) => Math.Clamp((x - Gutter) / Usable * Duration, 0, Duration);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, TotalH);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_project == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        FormattedText Text(string s, double size, Brush b, Typeface? f = null) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, f ?? Face, size, b, dpi);

        // Lane labels.
        void LaneLabel(string glyph, string name, double top, double h)
        {
            var ic = Text(glyph, 12, LabelBrush, IconFace);
            var tx = Text(name, 11, LabelBrush);
            dc.DrawText(ic, new Point(16, top + (h - ic.Height) / 2));
            dc.DrawText(tx, new Point(34, top + (h - tx.Height) / 2));
        }
        LaneLabel("", "Video", FilmTop, FilmH);
        LaneLabel("", "Zoom", ZoomTop, ZoomH);
        LaneLabel("", "Input", InputTop, InputH);

        // Ruler: pick a tick step that keeps labels ~90px apart.
        double[] steps = { 100, 250, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000 };
        double step = steps.FirstOrDefault(s => s / Duration * Usable >= 90, steps[^1]);
        for (double t = 0; t <= Duration + 1; t += step / 4)
        {
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            double x = X(t);
            dc.DrawRectangle(TickBrush, null, new Rect(x, major ? RulerH - 9 : RulerH - 5, 1, major ? 9 : 5));
            if (major)
            {
                var label = Text(FormatTime(t, step < 1000), 10.5, LabelBrush, MonoFace);
                if (x + 4 + label.Width <= ActualWidth) dc.DrawText(label, new Point(x + 4, 5));
            }
        }

        // Film strip.
        var filmRect = new Rect(Gutter, FilmTop, Usable, FilmH);
        dc.DrawRoundedRectangle(LaneBrush, null, filmRect, 8, 8);
        if (_film is { Count: > 0 } film)
        {
            dc.PushClip(new RectangleGeometry(filmRect, 8, 8));
            double tw = FilmH * film[0].PixelWidth / (double)film[0].PixelHeight;
            for (double x = Gutter; x < Gutter + Usable; x += tw)
            {
                int idx = FilmIndex(Ms(x + tw / 2), film.Count);
                dc.DrawImage(film[idx], new Rect(x, FilmTop, tw, FilmH));
            }
            // Dim the parts that play zoomed-out so zooms read at a glance.
            dc.DrawRectangle(FilmShade, null, filmRect);
            foreach (var s in Segments)
            {
                var r = new Rect(X(s.Start), FilmTop, Math.Max(1, X(s.End) - X(s.Start)), FilmH);
                dc.PushClip(new RectangleGeometry(r));
                for (double x = Gutter; x < Gutter + Usable; x += tw)
                {
                    if (x + tw < r.Left || x > r.Right) continue;
                    int idx = FilmIndex(Ms(x + tw / 2), film.Count);
                    dc.DrawImage(film[idx], new Rect(x, FilmTop, tw, FilmH));
                }
                dc.Pop();
            }
            dc.Pop();
        }
        dc.DrawRoundedRectangle(null, FilmEdge, filmRect, 8, 8);

        // Zoom lane.
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(Gutter, ZoomTop, Usable, ZoomH), 10, 10);
        foreach (var s in Segments)
        {
            var r = SegRect(s);
            bool sel = s == _selected;
            var brush = sel ? SegSelBrush : s == _hover ? SegHoverBrush : SegBrush;
            dc.DrawRoundedRectangle(brush, sel ? SelPen : null, r, 8, 8);
            // Edge grips.
            var grip = sel ? Brushes.White : Frozen("#B3FFFFFF");
            dc.DrawRoundedRectangle(grip, null, new Rect(r.Left + 4, r.Top + r.Height / 2 - 7, 3, 14), 1.5, 1.5);
            dc.DrawRoundedRectangle(grip, null, new Rect(r.Right - 7, r.Top + r.Height / 2 - 7, 3, 14), 1.5, 1.5);
            if (r.Width > 64)
            {
                dc.PushClip(new RectangleGeometry(new Rect(r.Left + 12, r.Top, Math.Max(0, r.Width - 24), r.Height)));
                var ic = Text("", 11, Brushes.White, IconFace);
                var txt = Text($"{s.Scale:0.0}×", 12, Brushes.White);
                dc.DrawText(ic, new Point(r.Left + 14, r.Top + (r.Height - ic.Height) / 2));
                dc.DrawText(txt, new Point(r.Left + 31, r.Top + (r.Height - txt.Height) / 2));
                dc.Pop();
            }
        }
        if (Segments.Count == 0)
        {
            var hint = Text("Double-click to add a zoom", 12, LabelBrush);
            dc.DrawText(hint, new Point(Gutter + 14, ZoomTop + (ZoomH - hint.Height) / 2));
        }

        // Input lane: keystrokes as teal ticks, clicks as amber dots.
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(Gutter, InputTop, Usable, InputH), 6, 6);
        foreach (var k in _project.Keys)
            if (k.Down) dc.DrawRoundedRectangle(KeyBrush, null, new Rect(X(k.T) - 1, InputTop + 5, 2, InputH - 10), 1, 1);
        foreach (var c in _project.Clicks)
            dc.DrawEllipse(ClickBrush, null, new Point(X(c.T), InputTop + InputH / 2), 3.6, 3.6);

        // Playhead with time bubble.
        double px = X(_playhead);
        dc.DrawRectangle(PlayheadBrush, null, new Rect(px - 1, RulerH - 2, 2, ActualHeight - RulerH));
        var bubbleText = Text(FormatTime(_playhead), 10.5, Brushes.White, MonoFace);
        double bw = bubbleText.Width + 12;
        double bx = Math.Clamp(px - bw / 2, 0, ActualWidth - bw);
        dc.DrawRoundedRectangle(PlayheadBrush, null, new Rect(bx, 3, bw, 19), 6, 6);
        dc.DrawText(bubbleText, new Point(bx + 6, 3 + (19 - bubbleText.Height) / 2));
    }

    private Rect SegRect(ZoomSegment s)
    {
        double x0 = X(s.Start), x1 = X(s.End);
        return new Rect(x0, ZoomTop + 4, Math.Max(8, x1 - x0), ZoomH - 8);
    }

    public static string FormatTime(double ms, bool tenths = true)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return tenths ? $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}" : $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
    }

    private ZoomSegment? HitSegment(Point p, out DragMode mode)
    {
        mode = DragMode.None;
        if (p.Y < ZoomTop || p.Y > ZoomTop + ZoomH) return null;
        foreach (var s in Segments.OrderBy(s => s == _selected ? 1 : 0).Reverse())
        {
            var r = SegRect(s);
            r.Inflate(EdgeGrab / 2, 4);
            if (!r.Contains(p)) continue;
            if (p.X <= r.Left + EdgeGrab) mode = DragMode.ResizeStart;
            else if (p.X >= r.Right - EdgeGrab) mode = DragMode.ResizeEnd;
            else mode = DragMode.Move;
            return s;
        }
        return null;
    }

    private bool InZoomLane(Point p) => p.Y >= ZoomTop && p.Y <= ZoomTop + ZoomH && p.X >= Gutter;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (_project == null) return;
        var p = e.GetPosition(this);

        if (e.ClickCount == 2 && InZoomLane(p) && HitSegment(p, out _) == null)
        {
            AddSegmentAt(Ms(p.X));
            e.Handled = true;
            return;
        }

        var hit = HitSegment(p, out var mode);
        if (hit != null)
        {
            Selected = hit;
            _drag = mode;
            _dragOffsetMs = Ms(p.X) - hit.Start;
        }
        else
        {
            if (InZoomLane(p)) Selected = null;
            _drag = DragMode.Scrub;
            Playhead = Ms(p.X);
            SeekRequested?.Invoke(_playhead);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            var h = HitSegment(p, out var mode);
            if (h != _hover) { _hover = h; InvalidateVisual(); }
            Cursor = mode is DragMode.ResizeStart or DragMode.ResizeEnd ? Cursors.SizeWE
                : mode == DragMode.Move ? Cursors.Hand : p.Y < FilmTop + FilmH ? Cursors.IBeam : Cursors.Arrow;
            return;
        }

        double t = Ms(p.X);
        var s = _selected;
        switch (_drag)
        {
            case DragMode.Scrub:
                Playhead = t;
                SeekRequested?.Invoke(t);
                return;
            case DragMode.Move when s != null:
            {
                double len = s.End - s.Start;
                double start = Math.Clamp(t - _dragOffsetMs, 0, Duration - len);
                s.Start = start;
                s.End = start + len;
                break;
            }
            case DragMode.ResizeStart when s != null:
                s.Start = Math.Clamp(t, 0, s.End - MinSegMs);
                break;
            case DragMode.ResizeEnd when s != null:
                s.End = Math.Clamp(t, s.Start + MinSegMs, Duration);
                break;
        }
        InvalidateVisual();
        SegmentsChanged?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is DragMode.Move or DragMode.ResizeStart or DragMode.ResizeEnd)
        {
            Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            SegmentsChanged?.Invoke();
        }
        _drag = DragMode.None;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && _selected != null)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    /// <summary>Adds a 2 s zoom at <paramref name="tMs"/>, focused on where the
    /// cursor was at that moment.</summary>
    public ZoomSegment? AddSegmentAt(double tMs)
    {
        if (_project == null) return null;
        _project.ZoomSegments ??= new List<ZoomSegment>();
        double scale = _project.Zoom?.MaxScale ?? 2.0;
        var (x, y) = _project.CursorAt(tMs);
        var (cx, cy) = ZoomAuthor.ClampCenter(x, y, _project.Width, _project.Height, scale);
        double start = Math.Clamp(tMs, 0, Math.Max(0, Duration - MinSegMs));
        var seg = new ZoomSegment { Start = start, End = Math.Min(Duration, start + 2000), Scale = scale, Cx = cx, Cy = cy };
        _project.ZoomSegments.Add(seg);
        _project.ZoomSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
        Selected = seg;
        InvalidateVisual();
        SegmentsChanged?.Invoke();
        return seg;
    }

    public void DeleteSelected()
    {
        if (_selected == null || _project?.ZoomSegments == null) return;
        _project.ZoomSegments.Remove(_selected);
        Selected = null;
        InvalidateVisual();
        SegmentsChanged?.Invoke();
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static Brush Gradient(string a, string b, double opacity)
    {
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 0) { Opacity = opacity };
        g.Freeze();
        return g;
    }

    private static Pen FrozenPen(Brush b, double w)
    {
        var p = new Pen(b, w);
        p.Freeze();
        return p;
    }
}
