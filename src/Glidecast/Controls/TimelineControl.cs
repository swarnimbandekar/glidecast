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
/// added by double-clicking empty space, a notes lane for annotations (move,
/// retime at either edge), and an input lane showing clicks and keystrokes.
/// Ctrl+scroll zooms the time axis around the pointer; scrolling pans it.
/// Segments are edited in place on <see cref="Project.ZoomSegments"/>.
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
    private const double NoteTop = ZoomTop + ZoomH + 8;
    private const double NoteH = 36;
    private const double NoteRowH = 10.5;
    private const double InputTop = NoteTop + NoteH + 8;
    private const double InputH = 18;
    private const double ThumbTop = InputTop + InputH + 8;
    private const double ThumbH = 5;
    private const double TotalH = ThumbTop + ThumbH + 7;
    private const double MinViewMs = 400;
    private const double EdgeGrab = 8;
    private const double MinSegMs = 200;

    // Mirrors Theme.xaml: graphite lanes, blue zoom blocks.
    private static readonly Brush BgBrush = Frozen("#151516");
    private static readonly Brush LaneBrush = Frozen("#1B1B1D");
    private static readonly Brush TickBrush = Frozen("#333336");
    private static readonly Brush LabelBrush = Frozen("#6C6C72");
    private static readonly Brush ClickBrush = Frozen("#FFB224");
    private static readonly Brush KeyBrush = Frozen("#3DD68C");
    private static readonly Brush PlayheadBrush = Frozen("#E5484D");
    private static readonly Brush SegBrush = Frozen("#2F6BE8");
    private static readonly Brush SegHoverBrush = Frozen("#3D7EFF");
    private static readonly Brush SegSelBrush = Frozen("#4D8AFF");
    private static readonly Brush FilmShade = Frozen("#80000000");
    private static readonly Brush NoteBrush = Frozen("#FFB224");
    private static readonly Brush NoteBoxBrush = Frozen("#3DD68C");
    private static readonly Brush NoteTextBrush = Frozen("#E93D82");
    private static readonly Brush ThumbBrush = Frozen("#3A3A3E");
    private static readonly Brush ThumbHoverBrush = Frozen("#55555B");
    private static readonly Pen SelPen = FrozenPen(Brushes.White, 1.5);
    private static readonly Pen FilmEdge = FrozenPen(Frozen("#2B2B2E"), 1);
    private static readonly Typeface Face = new(new FontFamily(new Uri("pack://application:,,,/Glidecast;component/Fonts/"), "./#Inter, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface IconFace = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private Project? _project;
    private double _playhead;
    private ZoomSegment? _selected, _hover;
    private Annotation? _note;
    private IReadOnlyList<BitmapSource>? _film;

    private enum DragMode { None, Scrub, Move, ResizeStart, ResizeEnd, MoveNote, NoteStart, NoteEnd, Pan }
    private DragMode _drag;
    private double _dragOffsetMs;
    private bool _thumbHover;

    // Visible window of the time axis; a span of 0 shows the whole recording.
    private double _viewStart, _viewSpan;

    /// <summary>User scrubbed to a time.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Segments were added, moved, resized or deleted.</summary>
    public event Action? SegmentsChanged;
    /// <summary>Selection changed (null = none).</summary>
    public event Action<ZoomSegment?>? SelectionChanged;
    /// <summary>The selected annotation changed (null = none).</summary>
    public event Action<Annotation?>? NoteSelectionChanged;
    /// <summary>An annotation was moved in time.</summary>
    public event Action? NotesChanged;
    /// <summary>The visible time window changed (zoom or pan).</summary>
    public event Action? ViewChanged;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        FocusVisualStyle = null;
    }

    public Project? Project
    {
        get => _project;
        set
        {
            if (_project != value) { _viewStart = 0; _viewSpan = 0; }
            _project = value;
            _selected = null;
            _note = null;
            InvalidateVisual();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            // Keep the playhead on screen while playing or stepping through a zoomed timeline.
            if (IsZoomed && _drag == DragMode.None && (value < _viewStart || value > _viewStart + ViewSpan))
                SetView(value - ViewSpan * 0.1, ViewSpan);
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- time axis zoom

    private double ViewSpan => _viewSpan <= 0 ? Duration : _viewSpan;
    public bool IsZoomed => ViewSpan < Duration - 1;
    /// <summary>How much of the recording is visible, 1 = all of it.</summary>
    public double ViewFraction => ViewSpan / Duration;

    private void SetView(double start, double span)
    {
        span = Math.Clamp(span, Math.Min(MinViewMs, Duration), Duration);
        start = Math.Clamp(start, 0, Duration - span);
        if (Math.Abs(start - _viewStart) < 1e-6 && Math.Abs(span - ViewSpan) < 1e-6) return;
        _viewStart = start;
        _viewSpan = span >= Duration - 1 ? 0 : span;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zooms the time axis by <paramref name="factor"/> (&gt;1 = closer), keeping <paramref name="anchorMs"/> in place.</summary>
    public void ZoomBy(double factor, double? anchorMs = null)
    {
        double anchor = anchorMs ?? (_playhead >= _viewStart && _playhead <= _viewStart + ViewSpan ? _playhead : _viewStart + ViewSpan / 2);
        double f = (anchor - _viewStart) / ViewSpan;
        double span = Math.Clamp(ViewSpan / factor, Math.Min(MinViewMs, Duration), Duration);
        SetView(anchor - f * span, span);
    }

    public void ZoomToFit() => SetView(0, Duration);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_project == null) return;
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomBy(Math.Pow(1.25, e.Delta / 120.0), p.X >= Gutter ? Ms(p.X) : null);
        else if (IsZoomed)
            SetView(_viewStart - e.Delta / 120.0 * ViewSpan * 0.12, ViewSpan);
        else return;
        e.Handled = true;
    }

    private Rect ThumbRect()
    {
        double x0 = Gutter + _viewStart / Duration * Usable;
        return new Rect(x0, ThumbTop, Math.Max(24, ViewSpan / Duration * Usable), ThumbH);
    }

    private bool OnThumbRow(Point p) => IsZoomed && p.X >= Gutter && p.Y >= ThumbTop - 4 && p.Y <= ThumbTop + ThumbH + 5;

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
            if (value != null && _note != null)
            {
                _note = null;
                NoteSelectionChanged?.Invoke(null);
            }
            InvalidateVisual();
            SelectionChanged?.Invoke(value);
        }
    }

    public Annotation? SelectedNote
    {
        get => _note;
        set
        {
            if (_note == value) return;
            _note = value;
            if (value != null) Selected = null;
            InvalidateVisual();
            NoteSelectionChanged?.Invoke(value);
        }
    }

    // Steps / arrows on top, boxes in the middle, captions at the bottom.
    private static int NoteRow(Annotation a) => a.Kind switch
    {
        AnnotationKind.Step or AnnotationKind.Arrow => 0,
        AnnotationKind.Highlight or AnnotationKind.Spotlight => 1,
        _ => 2,
    };

    private Rect NoteRect(Annotation a)
    {
        double x0 = X(a.Start), x1 = X(a.End);
        return new Rect(x0, NoteTop + 3.5 + NoteRow(a) * NoteRowH, Math.Max(4, x1 - x0), NoteRowH - 2.5);
    }

    /// <summary>The annotation bar under p (the selected one wins ties), and
    /// whether p is on its start or end edge.</summary>
    private Annotation? HitNote(Point p, out DragMode mode)
    {
        mode = DragMode.None;
        if (_project?.Annotations is not { } notes || p.Y < NoteTop || p.Y > NoteTop + NoteH) return null;
        Annotation? best = null;
        double bestD = double.MaxValue;
        foreach (var a in notes)
        {
            var r = NoteRect(a);
            if (p.X < r.Left - 3 || p.X > r.Right + 3) continue;
            double d = Math.Abs(p.Y - (r.Top + r.Height / 2)) - (a == _note ? 2 : 0);
            if (d < bestD) { bestD = d; best = a; }
        }
        if (best == null || bestD > NoteRowH) return null;
        var br = NoteRect(best);
        mode = br.Width < 16 ? DragMode.MoveNote
            : p.X <= br.Left + 5 ? DragMode.NoteStart
            : p.X >= br.Right - 5 ? DragMode.NoteEnd : DragMode.MoveNote;
        return best;
    }

    private List<ZoomSegment> Segments => _project?.ZoomSegments ?? new List<ZoomSegment>();
    private double Duration => Math.Max(1, _project?.DurationMs ?? 1);
    private double Usable => Math.Max(1, ActualWidth - Gutter - PadR);
    private double X(double ms) => Gutter + (ms - _viewStart) / ViewSpan * Usable;
    private double Ms(double x) => Math.Clamp(_viewStart + (x - Gutter) / Usable * ViewSpan, 0, Duration);

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
        LaneLabel("\uE70F", "Notes", NoteTop, NoteH);
        LaneLabel("", "Input", InputTop, InputH);

        // Ruler: pick a tick step that keeps labels ~90px apart.
        double[] steps = { 50, 100, 250, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000 };
        double step = steps.FirstOrDefault(s => s / ViewSpan * Usable >= 90, steps[^1]);
        double viewEnd = Math.Min(Duration, _viewStart + ViewSpan);
        dc.PushClip(new RectangleGeometry(new Rect(Gutter - 1, 0, Usable + PadR, ActualHeight)));
        for (double t = Math.Floor(_viewStart / (step / 4)) * (step / 4); t <= viewEnd + 1; t += step / 4)
        {
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            double x = X(t);
            dc.DrawRectangle(TickBrush, null, new Rect(x, major ? RulerH - 9 : RulerH - 5, 1, major ? 9 : 5));
            if (major)
            {
                var label = Text(FormatTime(t, step < 1000), 10.5, LabelBrush);
                if (x + 4 + label.Width <= ActualWidth) dc.DrawText(label, new Point(x + 4, 5));
            }
        }
        dc.Pop();

        // Lanes scroll with the view; keep them out of the labels.
        dc.PushClip(new RectangleGeometry(new Rect(Gutter, 0, Usable, ActualHeight)));

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
            var grip = sel ? Brushes.White : Frozen("#99FFFFFF");
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

        // Notes lane: annotations as thin bars, colored by kind.
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(Gutter, NoteTop, Usable, NoteH), 6, 6);
        if (_project.Annotations is { Count: > 0 } notes)
        {
            foreach (var a in notes)
            {
                var brush = NoteRow(a) switch { 0 => NoteBrush, 1 => NoteBoxBrush, _ => NoteTextBrush };
                var r = NoteRect(a);
                dc.DrawRoundedRectangle(brush, a == _note ? SelPen : null, r, 2.5, 2.5);
            }
        }
        else
        {
            var hint = Text("Steps, boxes and captions appear here (Annotate tab, or F9 while recording)", 10.5, LabelBrush);
            dc.DrawText(hint, new Point(Gutter + 12, NoteTop + (NoteH - hint.Height) / 2));
        }

        // Input lane: keystrokes as teal ticks, clicks as amber dots.
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(Gutter, InputTop, Usable, InputH), 6, 6);
        foreach (var k in _project.Keys)
            if (k.Down) dc.DrawRoundedRectangle(KeyBrush, null, new Rect(X(k.T) - 1, InputTop + 5, 2, InputH - 10), 1, 1);
        foreach (var c in _project.Clicks)
            dc.DrawEllipse(ClickBrush, null, new Point(X(c.T), InputTop + InputH / 2), 3.6, 3.6);
        dc.Pop();

        // Scroll thumb: where the visible window sits in the whole recording.
        if (IsZoomed)
        {
            dc.DrawRoundedRectangle(LaneBrush, null, new Rect(Gutter, ThumbTop, Usable, ThumbH), ThumbH / 2, ThumbH / 2);
            dc.DrawRoundedRectangle(_thumbHover || _drag == DragMode.Pan ? ThumbHoverBrush : ThumbBrush, null, ThumbRect(), ThumbH / 2, ThumbH / 2);
            var zoomLabel = Text($"{1 / ViewFraction:0.#}×", 10, LabelBrush);
            dc.DrawText(zoomLabel, new Point(Gutter - 8 - zoomLabel.Width, ThumbTop + (ThumbH - zoomLabel.Height) / 2));
        }

        // Playhead with time bubble.
        double px = X(_playhead);
        if (px < Gutter - 1 || px > Gutter + Usable + 1) return;
        dc.DrawRectangle(PlayheadBrush, null, new Rect(px - 1, RulerH - 2, 2, ActualHeight - RulerH));
        var bubbleText = Text(FormatTime(_playhead), 10.5, Brushes.White);
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

        if (OnThumbRow(p))
        {
            // Grab the thumb, or center it on the click and keep dragging.
            var th = ThumbRect();
            if (p.X < th.Left || p.X > th.Right)
                SetView((p.X - Gutter) / Usable * Duration - ViewSpan / 2, ViewSpan);
            _drag = DragMode.Pan;
            _dragOffsetMs = (p.X - Gutter) / Usable * Duration - _viewStart;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (HitNote(p, out var noteMode) is { } note)
        {
            SelectedNote = note;
            _drag = noteMode;
            _dragOffsetMs = Ms(p.X) - note.Start;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var hit = HitSegment(p, out var mode);
        if (hit != null)
        {
            SelectedNote = null;
            Selected = hit;
            _drag = mode;
            _dragOffsetMs = Ms(p.X) - hit.Start;
        }
        else
        {
            if (InZoomLane(p)) Selected = null;
            if (p.Y >= NoteTop && p.Y <= NoteTop + NoteH) SelectedNote = null;
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
            bool onThumb = OnThumbRow(p) && p.X >= ThumbRect().Left && p.X <= ThumbRect().Right;
            if (onThumb != _thumbHover) { _thumbHover = onThumb; InvalidateVisual(); }
            HitNote(p, out var noteMode);
            Cursor = mode is DragMode.ResizeStart or DragMode.ResizeEnd || noteMode is DragMode.NoteStart or DragMode.NoteEnd ? Cursors.SizeWE
                : mode == DragMode.Move || noteMode == DragMode.MoveNote || onThumb ? Cursors.Hand
                : p.Y < FilmTop + FilmH ? Cursors.IBeam : Cursors.Arrow;
            return;
        }

        if (_drag == DragMode.Pan)
        {
            SetView((p.X - Gutter) / Usable * Duration - _dragOffsetMs, ViewSpan);
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
            case DragMode.MoveNote when _note != null:
            {
                double len = _note.End - _note.Start;
                double start = Math.Clamp(t - _dragOffsetMs, 0, Math.Max(0, Duration - len));
                _note.Start = start;
                _note.End = start + len;
                InvalidateVisual();
                NotesChanged?.Invoke();
                return;
            }
            case DragMode.NoteStart when _note != null:
                _note.Start = Math.Clamp(t, 0, _note.End - MinSegMs);
                InvalidateVisual();
                NotesChanged?.Invoke();
                return;
            case DragMode.NoteEnd when _note != null:
                _note.End = Math.Clamp(t, _note.Start + MinSegMs, Duration);
                InvalidateVisual();
                NotesChanged?.Invoke();
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
        if (_hover != null || _thumbHover) { _hover = null; _thumbHover = false; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is DragMode.Move or DragMode.ResizeStart or DragMode.ResizeEnd)
        {
            Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            SegmentsChanged?.Invoke();
        }
        if (_drag is DragMode.MoveNote or DragMode.NoteStart or DragMode.NoteEnd)
            _project?.Annotations?.Sort((a, b) => a.Start.CompareTo(b.Start));
        _drag = DragMode.None;
        InvalidateVisual();
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && _selected != null)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && _note != null)
        {
            DeleteSelectedNote();
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

    public void DeleteSelectedNote()
    {
        if (_note == null || _project?.Annotations == null) return;
        _project.Annotations.Remove(_note);
        SelectedNote = null;
        InvalidateVisual();
        NotesChanged?.Invoke();
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

    private static Pen FrozenPen(Brush b, double w)
    {
        var p = new Pen(b, w);
        p.Freeze();
        return p;
    }
}
