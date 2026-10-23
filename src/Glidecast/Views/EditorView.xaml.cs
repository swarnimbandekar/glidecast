using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glidecast.App.Controls;
using Glidecast.App.Services;
using Glidecast.AutoZoom;
using Glidecast.Model;
using Glidecast.Render;
using Canvas = Glidecast.Render.Canvas;
using Microsoft.Win32;
using SkiaSharp;

namespace Glidecast.App.Views;

/// <summary>
/// Editor: live preview with transport, zoom timeline editing, and the
/// inspector (background, frame, cursor, zoom). Every change is pushed to the
/// preview and auto-saved to project.json.
/// </summary>
public partial class EditorView : UserControl
{
    // Glidecast's own background presets.
    private static readonly (string Name, string Top, string Bottom)[] Gradients =
    {
        ("Cobalt", "#2F5FD0", "#0B1226"),
        ("Midnight", "#1E293B", "#0B1018"),
        ("Ocean", "#1A8FE3", "#0B2A5B"),
        ("Aurora", "#2BC0A0", "#1B3B6F"),
        ("Sunset", "#FF8A5B", "#B0306A"),
        ("Peach", "#FFD1A9", "#F38FA5"),
        ("Mint", "#D4F5E2", "#7FCFC0"),
        ("Graphite", "#4A4F5C", "#16181D"),
        ("Ember", "#F7B733", "#FC4A1A"),
        ("Lagoon", "#43CEA2", "#185A9D"),
        ("Violet dusk", "#6D5BD0", "#1C1640"),
        ("Slate", "#8E9EAB", "#3B4652"),
    };

    private static readonly string[] Solids =
        { "#0F0F10", "#1F2430", "#F4F5F8", "#E8E1D6", "#3D7EFF", "#7C5CFF", "#16A37F", "#E5484D", "#FFB224", "#0EA5E9" };

    private static readonly string[] Accents =
        { "#3D7EFF", "#7C5CFF", "#3DD68C", "#FFB224", "#E5484D", "#E93D82", "#FFFFFF" };

    private readonly MainWindow _shell;
    private readonly Project _p;
    private readonly string _file;
    private PreviewPlayer _player;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _cameraTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private CameraTrack _camera;
    private bool _loading;
    private bool _closed;
    // The zoom lane was edited by hand since it was last generated.
    private bool _laneEdited;

    private StyleSettings St => _p.Style!;

    public EditorView(MainWindow shell, Project p, string projectFile)
    {
        _shell = shell;
        _p = p;
        _file = projectFile;
        // Fill in defaults; the auto-authored zoom becomes the editable lane.
        _p.Zoom ??= new ZoomSettings();
        _p.Style ??= new StyleSettings();
        _p.ZoomSegments ??= ZoomAuthor.BuildSegments(_p, _p.Zoom);
        _camera = _p.BuildCamera();

        _loading = true; // control initialisation fires change events
        InitializeComponent();
        _loading = false;

        // Our header lives in the window's title bar.
        ((Panel)TitleBar.Parent).Children.Remove(TitleBar);
        _shell.SetTitleContent(TitleBar);

        ProjectName.Text = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(projectFile)));
        SourceInfo.Text = $"{_p.Width}×{_p.Height} · {_p.Fps:0} fps · {_p.Clicks.Count} clicks";
        TimeTotal.Text = "/ " + TimelineControl.FormatTime(_p.DurationMs);

        _player = CreatePlayer(0);
        Preview.SizeChanged += (_, _) =>
            Preview.Clip = new RectangleGeometry(new Rect(0, 0, Preview.ActualWidth, Preview.ActualHeight), 12, 12);

        Timeline.Project = _p;
        Timeline.SeekRequested += t => _player.Seek(t);
        Timeline.SegmentsChanged += OnSegmentsChanged;
        Timeline.SelectionChanged += _ => RefreshSelection();
        Timeline.NoteSelectionChanged += _ => RefreshNote();
        Timeline.NotesChanged += () => { RefreshNote(); PushToPreview(cameraChanged: false); MarkDirty(); };

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        // Rebuilding the camera is cheap but not free; coalesce bursts (slider drags).
        _cameraTimer.Tick += (_, _) => { _cameraTimer.Stop(); RebuildCamera(); };

        BuildSwatches();
        BuildGalleries();
        LoadStyleControls();
        RefreshSelection();
        RefreshNote();
        BuildLooks();
        PreviewKeyDown += OnKeys;
        Loaded += (_, _) => Focus();
        _ = LoadFilmStripAsync();
    }

    private async Task LoadFilmStripAsync()
    {
        try
        {
            var clock = _player.Frames;
            double videoMs = clock.Count / clock.VideoFps * 1000;
            var frames = await Task.Run(() => PreviewImages.FilmStrip(_p.SourceVideo, _p.Width, _p.Height, videoMs));
            if (_closed) return;
            Timeline.FilmPosition = t => clock.FrameAt(t) / (double)Math.Max(1, clock.Count);
            Timeline.FilmStrip = frames;
        }
        catch { /* the strip is decoration */ }
    }

    // ---------------------------------------------------------------- lifecycle

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _saveTimer.Stop();
        _cameraTimer.Stop();
        SaveNow();
        _player.Dispose();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        SaveState.Text = "Editing…";
        SaveDot.Fill = (Brush)FindResource("Accent");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try
        {
            _p.Save(_file);
            SaveState.Text = "Saved";
            SaveDot.Fill = (Brush)FindResource("Success");
        }
        catch (Exception ex)
        {
            SaveState.Text = "Save failed: " + ex.Message;
            SaveDot.Fill = (Brush)FindResource("Danger");
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => _shell.ShowHome();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        _saveTimer.Stop();
        SaveNow();
        new ExportWindow(_p, Path.GetDirectoryName(Path.GetFullPath(_file))!) { Owner = _shell }.ShowDialog();
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        double frame = 1000.0 / _p.Fps;
        bool shift = Keyboard.Modifiers == ModifierKeys.Shift;
        switch (e.Key)
        {
            case Key.Space:
                _player.TogglePlay();
                break;
            case Key.Delete when Timeline.Selected != null:
                Timeline.DeleteSelected();
                break;
            case Key.Delete when Timeline.SelectedNote != null:
                Timeline.DeleteSelectedNote();
                break;
            case Key.Left:
                _player.Seek(_player.Position - (shift ? 5000 : frame));
                break;
            case Key.Right:
                _player.Seek(_player.Position + (shift ? 5000 : frame));
                break;
            case Key.Home:
                _player.Seek(0);
                break;
            case Key.Z when Keyboard.Modifiers == ModifierKeys.None:
                Timeline.AddSegmentAt(_player.Position);
                break;
            case Key.E when Keyboard.Modifiers == ModifierKeys.Control:
                Export_Click(sender, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _player.TogglePlay();
    private void Back5_Click(object sender, RoutedEventArgs e) => _player.Seek(_player.Position - 5000);
    private void Fwd5_Click(object sender, RoutedEventArgs e) => _player.Seek(_player.Position + 5000);

    // ---------------------------------------------------------------- camera / zoom editing

    private void RebuildCamera()
    {
        _camera = _p.BuildCamera();
        _player.Update(_camera, St);
    }

    /// <summary>Push changes: style instantly, camera rebuild coalesced.</summary>
    private void PushToPreview(bool cameraChanged)
    {
        if (cameraChanged)
        {
            _cameraTimer.Stop();
            _cameraTimer.Start();
        }
        else
        {
            _player.Update(_camera, St);
        }
    }

    private void OnSegmentsChanged()
    {
        if (!_loading) _laneEdited = true;
        PushToPreview(cameraChanged: true);
        RefreshSelection();
        MarkDirty();
    }

    private void RefreshSelection()
    {
        _loading = true;
        var s = Timeline.Selected;
        SelectedCard.Visibility = s != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelection.Visibility = s != null ? Visibility.Collapsed : Visibility.Visible;
        if (s != null)
        {
            SelTimes.Text = $"{TimelineControl.FormatTime(s.Start)} → {TimelineControl.FormatTime(s.End)}  ·  {(s.End - s.Start) / 1000:0.0} s";
            SelScale.Value = s.Scale;
            SelScaleVal.Text = $"{s.Scale:0.0}×";
            if (TabZoom.IsChecked != true) TabZoom.IsChecked = true;
        }
        var z = _p.Zoom!;
        AutoScale.Value = z.MaxScale;
        AutoScaleVal.Text = $"{z.MaxScale:0.0}×";
        SpeedGentle.IsChecked = z.Speed == ZoomSpeed.Gentle;
        SpeedSmooth.IsChecked = z.Speed == ZoomSpeed.Smooth;
        SpeedSnappy.IsChecked = z.Speed == ZoomSpeed.Snappy;
        FollowToggle.IsChecked = z.FollowCursor;
        SmartToggle.IsChecked = z.SmartFraming;
        TypingToggle.IsChecked = z.FollowTyping;
        PresetSubtle.IsChecked = z.Preset == ZoomPreset.Subtle;
        PresetBalanced.IsChecked = z.Preset == ZoomPreset.Balanced;
        PresetDramatic.IsChecked = z.Preset == ZoomPreset.Dramatic;
        PresetTutorial.IsChecked = z.Preset == ZoomPreset.Tutorial;
        PresetHint.Text = z.Preset switch
        {
            ZoomPreset.Subtle => "Gentle, wide zooms that never feel jumpy.",
            ZoomPreset.Dramatic => "Close, snappy zooms for energetic demos.",
            ZoomPreset.Tutorial => "Close zooms that hold long enough to read each step.",
            ZoomPreset.Custom => "Custom tuning. Pick a preset to start over.",
            _ => "The default: clear zooms with smooth motion.",
        };
        int withUi = _p.Clicks.Count(c => c.Element != null);
        SmartHint.Text = _p.Clicks.Count == 0 ? "Fit the zoom to the clicked button, menu or panel."
            : withUi == 0 ? "No UI details were recorded for these clicks (older recording, or the app exposes none)."
            : $"Fit the zoom to the clicked button, menu or panel ({withUi} of {_p.Clicks.Count} clicks have UI details).";
        int carets = _p.Carets?.Count ?? 0;
        TypingHint.Text = carets == 0 ? "Zoom on the text caret and follow the words as you type."
            : $"Zoom on the text caret and follow the words as you type ({carets} caret samples).";
        int n = _p.ZoomSegments!.Count;
        ZoomStats.Text = n == 1 ? "1 zoom" : $"{n} zooms";
        _loading = false;
    }

    private void SelScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || Timeline == null || Timeline.Selected is not { } s) return;
        s.Scale = Math.Round(e.NewValue, 2);
        var (cx, cy) = ZoomAuthor.ClampCenter(s.Cx, s.Cy, _p.Width, _p.Height, s.Scale);
        s.Cx = cx; s.Cy = cy;
        SelScaleVal.Text = $"{s.Scale:0.0}×";
        Timeline.InvalidateVisual();
        PushToPreview(cameraChanged: true);
        MarkDirty();
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (Preview.ActualWidth <= 0 || PreviewToSource(e.GetPosition(Preview)) is not { } src) return;
        if (Timeline.SelectedNote is { } note)
        {
            MoveNote(note, src.X, src.Y);
            return;
        }
        if (Timeline.Selected is not { } s) return;
        var (cx, cy) = ZoomAuthor.ClampCenter(src.X, src.Y, _p.Width, _p.Height, s.Scale);
        s.Cx = cx; s.Cy = cy;
        OnSegmentsChanged();
    }

    private void JumpSel_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.Selected is not { } s) return;
        _player.Seek(Math.Max(0, s.Start - 800));
        _player.Play();
    }

    private void DeleteSel_Click(object sender, RoutedEventArgs e) => Timeline.DeleteSelected();

    private void AddZoom_Click(object sender, RoutedEventArgs e) => Timeline.AddSegmentAt(_player.Position);

    private void AutoScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _p.Zoom!.MaxScale = Math.Round(e.NewValue, 2);
        _p.Zoom.Preset = ZoomPreset.Custom;
        AutoScaleVal.Text = $"{_p.Zoom.MaxScale:0.0}×";
        MarkDirty();
    }

    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _p.Zoom!.Speed = SpeedGentle.IsChecked == true ? ZoomSpeed.Gentle
            : SpeedSnappy.IsChecked == true ? ZoomSpeed.Snappy : ZoomSpeed.Smooth;
        _p.Zoom.Preset = ZoomPreset.Custom;
        RefreshSelection();
        PushToPreview(cameraChanged: true);
        MarkDirty();
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _p.Zoom!.FollowCursor = FollowToggle.IsChecked == true;
        PushToPreview(cameraChanged: true);
        MarkDirty();
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_p.ZoomSegments!.Count > 0 &&
            MessageBox.Show(_shell, "Replace the current zoom lane with one generated from your clicks and typing?",
                "Regenerate zooms", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _p.ZoomSegments = ZoomAuthor.BuildSegments(_p, _p.Zoom);
        Timeline.Project = _p;
        OnSegmentsChanged();
        _laneEdited = false;
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (BgPanel == null || FramePanel == null || CursorPanel == null || ZoomPanel == null || NotesPanel == null || LooksPanel == null) return;
        NotesPanel.Visibility = TabNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LooksPanel.Visibility = TabLooks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BgPanel.Visibility = TabBg.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FramePanel.Visibility = TabFrame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CursorPanel.Visibility = TabCursor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ZoomPanel.Visibility = TabZoom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (sender is RadioButton { IsChecked: true } tab)
            InspectorTitle.Text = tab == TabBg ? "Background" : tab == TabNotes ? "Annotations" : tab.Content as string;
    }

    // ---------------------------------------------------------------- style

    private readonly List<(Button Button, Func<bool> IsCurrent)> _swatches = new();
    private readonly List<(RadioButton Tile, CursorStyle Style)> _cursorTiles = new();
    private readonly List<(RadioButton Tile, ClickEffect Effect)> _clickTiles = new();

    private Button Swatch(Brush fill, string tip, Func<bool> isCurrent, Action apply)
    {
        var b = new Button { Style = (System.Windows.Style)FindResource("Swatch"), Background = fill, ToolTip = tip };
        b.Click += (_, _) => { apply(); LoadStyleControls(); StyleApplied(); };
        _swatches.Add((b, isCurrent));
        return b;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private void BuildSwatches()
    {
        foreach (var (name, top, bottom) in Gradients)
            GradientSwatches.Children.Add(Swatch(new LinearGradientBrush(C(top), C(bottom), 45), name,
                () => St.Background == BackgroundKind.Gradient && St.BgTop == top && St.BgBottom == bottom,
                () => { St.Background = BackgroundKind.Gradient; St.BgTop = top; St.BgBottom = bottom; }));
        foreach (var hex in Solids)
            SolidSwatches.Children.Add(Swatch(new SolidColorBrush(C(hex)), hex,
                () => St.Background == BackgroundKind.Solid && string.Equals(St.BgSolid, hex, StringComparison.OrdinalIgnoreCase),
                () => { St.Background = BackgroundKind.Solid; St.BgSolid = hex; }));
        foreach (var hex in Accents)
        {
            var b = Swatch(new SolidColorBrush(C(hex)), hex,
                () => string.Equals(St.Accent, hex, StringComparison.OrdinalIgnoreCase),
                () => { St.Accent = hex; BuildGalleries(); });
            b.Width = b.Height = 36;
            AccentSwatches.Children.Add(b);
        }
    }

    /// <summary>Cursor style and click effect tiles, rendered by the engine in the current accent.</summary>
    private void BuildGalleries()
    {
        var accent = StyleSettings.ParseColor(St.Accent, new SKColor(0x3D, 0x7E, 0xFF));
        CursorStyles.Children.Clear();
        _cursorTiles.Clear();
        foreach (var style in CursorPainter.All)
        {
            var tile = GalleryTile(PreviewImages.Cursor(style, accent), CursorPainter.Label(style), "cursor");
            var s = style;
            tile.Checked += (_, _) => { if (_loading) return; St.CursorStyle = s; St.DrawCursor = true; CursorToggle.IsChecked = true; StyleApplied(); };
            CursorStyles.Children.Add(tile);
            _cursorTiles.Add((tile, style));
        }
        ClickEffects.Children.Clear();
        _clickTiles.Clear();
        foreach (var effect in Enum.GetValues<ClickEffect>())
        {
            var tile = GalleryTile(PreviewImages.Click(effect, accent), effect.ToString(), "click");
            tile.Width = 68;
            var fx = effect;
            tile.Checked += (_, _) => { if (_loading) return; St.ClickEffect = fx; St.DrawClicks = fx != ClickEffect.None; StyleApplied(); };
            ClickEffects.Children.Add(tile);
            _clickTiles.Add((tile, effect));
        }
        bool was = _loading;
        _loading = true;
        foreach (var (t, s) in _cursorTiles) t.IsChecked = St.CursorStyle == s;
        foreach (var (t, f) in _clickTiles) t.IsChecked = (St.DrawClicks ? St.ClickEffect : ClickEffect.None) == f;
        _loading = was;
    }

    private RadioButton GalleryTile(ImageSource img, string label, string group)
    {
        var panel = new StackPanel();
        panel.Children.Add(new Image { Source = img, Width = 56, Height = 44 });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        return new RadioButton { Style = (System.Windows.Style)FindResource("Tile"), Content = panel, GroupName = group, Width = 86 };
    }

    private void LoadStyleControls()
    {
        bool was = _loading;
        _loading = true;
        var st = St;
        BgGradient.IsChecked = st.Background == BackgroundKind.Gradient;
        BgSolid.IsChecked = st.Background == BackgroundKind.Solid;
        BgImage.IsChecked = st.Background == BackgroundKind.Image;
        GradientPanel.Visibility = st.Background == BackgroundKind.Gradient ? Visibility.Visible : Visibility.Collapsed;
        SolidPanel.Visibility = st.Background == BackgroundKind.Solid ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = st.Background == BackgroundKind.Image ? Visibility.Visible : Visibility.Collapsed;
        SolidHex.Text = st.BgSolid;
        var sc = StyleSettings.ParseColor(st.BgSolid, SKColors.Black);
        SolidPreview.Background = new SolidColorBrush(Color.FromRgb(sc.Red, sc.Green, sc.Blue));
        LoadWallpaperPreview();
        BlurSlider.Value = st.BgBlur;
        PaddingSlider.Value = st.PaddingFraction;
        RadiusSlider.Value = st.CornerRadius;
        ShadowSlider.Value = st.ShadowBlur;
        CursorSlider.Value = st.CursorScale;
        SmoothSlider.Value = st.CursorSmoothing;
        CursorToggle.IsChecked = st.DrawCursor;
        KeysToggle.IsChecked = st.DrawKeystrokes;
        MotionBlurToggle.IsChecked = st.MotionBlur;
        IdleToggle.IsChecked = st.HideIdleCursor;
        TrailToggle.IsChecked = st.CursorTrail;
        NotesToggle.IsChecked = st.DrawAnnotations;
        foreach (var rb in new[] { AspSource, AspWide, AspVertical, AspSquare })
            rb.IsChecked = (string)rb.Tag == st.Aspect.ToString();
        foreach (var rb in new[] { LogoTL, LogoTR, LogoBL, LogoBR })
            rb.IsChecked = (string)rb.Tag == st.LogoCorner.ToString();
        LogoSizeSlider.Value = st.LogoSize;
        LogoOpacitySlider.Value = st.LogoOpacity;
        LoadLogoPreview();
        foreach (var (t, s) in _cursorTiles) t.IsChecked = st.CursorStyle == s;
        foreach (var (t, f) in _clickTiles) t.IsChecked = (st.DrawClicks ? st.ClickEffect : ClickEffect.None) == f;
        foreach (var (b, isCurrent) in _swatches) b.Tag = isCurrent() ? "selected" : null;
        SyncSizePreset();
        UpdateStyleLabels();
        _loading = was;
    }

    private void LoadWallpaperPreview()
    {
        string? path = St.BgImage;
        ImageName.Text = string.IsNullOrEmpty(path) ? "No image chosen" : "";
        WallPreview.Background = null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 480;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            WallPreview.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { ImageName.Text = Path.GetFileName(path); }
    }

    private void SyncSizePreset()
    {
        foreach (var rb in new[] { SizeS, SizeM, SizeL, SizeXL })
            rb.IsChecked = Math.Abs(double.Parse((string)rb.Tag, System.Globalization.CultureInfo.InvariantCulture) - St.CursorScale) < 0.05;
    }

    private void UpdateStyleLabels()
    {
        PaddingVal.Text = $"{St.PaddingFraction * 100:0}%";
        RadiusVal.Text = $"{St.CornerRadius:0} px";
        ShadowVal.Text = $"{St.ShadowBlur:0}";
        CursorVal.Text = $"{St.CursorScale:0.0}×";
        BlurVal.Text = $"{St.BgBlur:0}";
        LogoSizeVal.Text = $"{St.LogoSize * 100:0}%";
        LogoOpacityVal.Text = $"{St.LogoOpacity * 100:0}%";
        SmoothVal.Text = St.CursorSmoothing < 0.05 ? "Off" : St.CursorSmoothing < 0.4 ? "Light" : St.CursorSmoothing < 0.75 ? "Smooth" : "Silky";
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || IdleToggle == null || TrailToggle == null || NotesToggle == null || LogoOpacitySlider == null) return;
        var st = St;
        bool smoothingChanged = Math.Abs(st.CursorSmoothing - (float)SmoothSlider.Value) > 0.001;
        st.BgBlur = (float)BlurSlider.Value;
        st.PaddingFraction = (float)PaddingSlider.Value;
        st.CornerRadius = (float)RadiusSlider.Value;
        st.ShadowBlur = (float)ShadowSlider.Value;
        st.CursorScale = (float)CursorSlider.Value;
        st.CursorSmoothing = (float)SmoothSlider.Value;
        st.DrawCursor = CursorToggle.IsChecked == true;
        st.DrawKeystrokes = KeysToggle.IsChecked == true;
        st.MotionBlur = MotionBlurToggle.IsChecked == true;
        st.HideIdleCursor = IdleToggle.IsChecked == true;
        st.CursorTrail = TrailToggle.IsChecked == true;
        st.DrawAnnotations = NotesToggle.IsChecked == true;
        st.LogoSize = (float)LogoSizeSlider.Value;
        st.LogoOpacity = (float)LogoOpacitySlider.Value;
        _loading = true;
        SyncSizePreset();
        _loading = false;
        UpdateStyleLabels();
        // Follow-cursor framing uses the smoothed cursor path.
        PushToPreview(cameraChanged: smoothingChanged && _p.Zoom!.FollowCursor);
        MarkDirty();
    }

    private void SizePreset_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb) return;
        CursorSlider.Value = double.Parse((string)rb.Tag, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void StyleApplied()
    {
        foreach (var (b, isCurrent) in _swatches) b.Tag = isCurrent() ? "selected" : null;
        PushToPreview(cameraChanged: false);
        MarkDirty();
    }

    private void BgKind_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        St.Background = BgSolid.IsChecked == true ? BackgroundKind.Solid
            : BgImage.IsChecked == true ? BackgroundKind.Image : BackgroundKind.Gradient;
        if (St.Background == BackgroundKind.Image && string.IsNullOrEmpty(St.BgImage))
            ChooseImage_Click(sender, e);
        LoadStyleControls();
        StyleApplied();
    }

    private void SolidHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        string hex = SolidHex.Text.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (hex.Length != 7 || !SKColor.TryParse(hex, out var c)) return;
        St.Background = BackgroundKind.Solid;
        St.BgSolid = hex.ToUpperInvariant();
        SolidPreview.Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
        StyleApplied();
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a wallpaper",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(_shell) != true) return;
        St.Background = BackgroundKind.Image;
        St.BgImage = dlg.FileName;
        LoadStyleControls();
        StyleApplied();
    }

    // ---------------------------------------------------------------- preview player

    /// <summary>(Re)creates the preview in the canvas's current shape, at time t.</summary>
    private PreviewPlayer CreatePlayer(double t)
    {
        var player = new PreviewPlayer(_p, Dispatcher, 1280);
        Preview.Source = player.Bitmap;
        player.PositionChanged += pos =>
        {
            Timeline.Playhead = pos;
            TimeNow.Text = TimelineControl.FormatTime(pos);
            ZoomNow.Text = $"{_camera.At(pos).scale:0.0}×";
        };
        player.PlayingChanged += playing =>
        {
            PlayIcon.Text = playing ? "" : "";
            PlayIcon.Margin = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        };
        if (t > 0) player.Seek(t);
        return player;
    }

    /// <summary>Replaces the preview (new output shape), keeping position and play state.</summary>
    private void SwapPlayer()
    {
        double t = _player.Position;
        bool playing = _player.IsPlaying;
        _player.Dispose();
        _player = CreatePlayer(t);
        _player.Update(_camera, St);
        PlayIcon.Text = "";
        PlayIcon.Margin = new Thickness(2, 0, 0, 0);
        if (playing) _player.Play();
    }

    /// <summary>Preview pixels → output pixels → (inverse camera) → source pixels.</summary>
    private SKPoint? PreviewToSource(Point pos)
    {
        float ox = (float)(pos.X / Preview.ActualWidth * _player.OutWidth);
        float oy = (float)(pos.Y / Preview.ActualHeight * _player.OutHeight);
        var cam = Compositor.CameraFor(_p, _camera, St, _player.OutWidth, _player.OutHeight, _player.Position);
        if (!cam.TryInvert(out var inv)) return null;
        return inv.MapPoint(new SKPoint(ox, oy));
    }

    private void Aspect_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb || !Enum.TryParse<CanvasAspect>((string)rb.Tag, out var a) || St.Aspect == a) return;
        St.Aspect = a;
        _camera = _p.BuildCamera();
        SwapPlayer();
        MarkDirty();
    }

    // ---------------------------------------------------------------- brand logo

    private void LoadLogoPreview()
    {
        string? path = St.LogoImage;
        bool has = !string.IsNullOrEmpty(path) && File.Exists(path);
        LogoOptions.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        LogoRemove.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        LogoButtonText.Text = has ? Path.GetFileName(path) : "Choose logo…";
        LogoPreview.Background = null;
        if (!has) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160;
            bmp.UriSource = new Uri(path!);
            bmp.EndInit();
            LogoPreview.Background = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
        }
        catch { }
    }

    private void ChooseLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a logo (PNG with transparency works best)",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(_shell) != true) return;
        St.LogoImage = dlg.FileName;
        LoadStyleControls();
        StyleApplied();
    }

    private void LogoRemove_Click(object sender, RoutedEventArgs e)
    {
        St.LogoImage = null;
        LoadStyleControls();
        StyleApplied();
    }

    private void LogoCorner_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb || !Enum.TryParse<LogoCorner>((string)rb.Tag, out var c)) return;
        St.LogoCorner = c;
        StyleApplied();
    }

    // ---------------------------------------------------------------- smart zoom

    /// <summary>Replacing hand edits needs a yes; an untouched lane is just rebuilt.</summary>
    private bool ConfirmReplaceLane() =>
        !_laneEdited || _p.ZoomSegments!.Count == 0 ||
        MessageBox.Show(_shell, "This rebuilds the zoom lane and replaces the zooms you edited by hand. Continue?",
            "Rebuild zooms", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private void RebuildLane()
    {
        _p.ZoomSegments = ZoomAuthor.BuildSegments(_p, _p.Zoom);
        Timeline.Project = _p;
        OnSegmentsChanged();
        _laneEdited = false;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton rb || !Enum.TryParse<ZoomPreset>((string)rb.Tag, out var preset)) return;
        if (!ConfirmReplaceLane()) { RefreshSelection(); return; }
        _p.Zoom = ZoomSettings.For(preset, _p.Zoom);
        RebuildLane();
    }

    private void ZoomSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!ConfirmReplaceLane()) { RefreshSelection(); return; }
        _p.Zoom!.SmartFraming = SmartToggle.IsChecked == true;
        _p.Zoom.FollowTyping = TypingToggle.IsChecked == true;
        // Following the caret also changes the camera between zooms, not just the lane.
        RebuildLane();
    }

    // ---------------------------------------------------------------- annotations

    private void RefreshNote()
    {
        bool was = _loading;
        _loading = true;
        var n = Timeline.SelectedNote;
        NoteCard.Visibility = n != null ? Visibility.Visible : Visibility.Collapsed;
        int count = _p.Annotations?.Count ?? 0;
        int named = _p.Clicks.Count(c => !string.IsNullOrWhiteSpace(c.Element?.Name));
        NotesInfo.Text = (count == 0 ? "No annotations yet. " : $"{count} annotation{(count == 1 ? "" : "s")}. ") +
            (named > 0 ? $"{named} of {_p.Clicks.Count} clicks have a control name for captions." :
             _p.Clicks.Count > 0 ? "No control names were recorded, so captions will be skipped." : "");
        if (n != null)
        {
            if (TabNotes.IsChecked != true) TabNotes.IsChecked = true;
            NoteTitle.Text = n.Kind == AnnotationKind.Step ? $"Step {n.Number}" : n.Kind.ToString();
            NoteTimes.Text = $"{TimelineControl.FormatTime(n.Start)} → {TimelineControl.FormatTime(n.End)}  ·  {(n.End - n.Start) / 1000:0.0} s";
            NoteText.Visibility = n.Kind == AnnotationKind.Callout ? Visibility.Visible : Visibility.Collapsed;
            NoteText.Text = n.Text ?? "";
            NoteNumberRow.Visibility = n.Kind == AnnotationKind.Step ? Visibility.Visible : Visibility.Collapsed;
            NoteNumber.Text = n.Number.ToString();
        }
        _loading = was;
    }

    private void NotesEdited()
    {
        Timeline.InvalidateVisual();
        RefreshNote();
        PushToPreview(cameraChanged: false);
        MarkDirty();
    }

    private void GenerateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_p.Clicks.Count == 0)
        {
            NotesInfo.Text = "This recording has no clicks to build steps from.";
            return;
        }
        Timeline.SelectedNote = null;
        AnnotationAuthor.Regenerate(_p, new AnnotateOptions
        {
            Steps = AutoSteps.IsChecked == true,
            Highlights = AutoBoxes.IsChecked == true,
            Captions = AutoCaptions.IsChecked == true,
        });
        St.DrawAnnotations = true;
        NotesToggle.IsChecked = true;
        NotesEdited();
    }

    private void AddNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !Enum.TryParse<AnnotationKind>((string)b.Tag, out var kind)) return;
        double t = _player.Position;
        // Aim at where the cursor was, which is usually what's being talked about.
        var (x, y) = _p.CursorAt(t);
        var a = AnnotationAuthor.Create(_p, kind, t, x, y);
        _p.Annotations ??= new List<Annotation>();
        _p.Annotations.Add(a);
        _p.Annotations.Sort((m, n) => m.Start.CompareTo(n.Start));
        St.DrawAnnotations = true;
        NotesToggle.IsChecked = true;
        Timeline.SelectedNote = a;
        NotesEdited();
        if (kind == AnnotationKind.Callout) { NoteText.Focus(); NoteText.SelectAll(); }
    }

    private void MoveNote(Annotation a, double x, double y)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Highlight:
            case AnnotationKind.Spotlight:
                a.X = Math.Clamp(x - a.W / 2, -a.W / 2, _p.Width - a.W / 2);
                a.Y = Math.Clamp(y - a.H / 2, -a.H / 2, _p.Height - a.H / 2);
                break;
            case AnnotationKind.Arrow:
                // Keep the arrow's direction and length; move its tip.
                double dx = a.X2 - a.X, dy = a.Y2 - a.Y;
                a.X = x; a.Y = y; a.X2 = x + dx; a.Y2 = y + dy;
                break;
            default:
                a.X = x; a.Y = y;
                break;
        }
        a.Auto = false; // hand-placed: survives regeneration
        NotesEdited();
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e) => Timeline.DeleteSelectedNote();

    private void NoteText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || Timeline.SelectedNote is not { } n) return;
        n.Text = NoteText.Text;
        n.Auto = false;
        PushToPreview(cameraChanged: false);
        MarkDirty();
    }

    private void NoteNumber_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedNote is not { } n || sender is not Button b) return;
        n.Number = Math.Max(1, n.Number + int.Parse((string)b.Tag));
        n.Auto = false;
        NotesEdited();
    }

    private void NoteStart_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedNote is not { } n) return;
        n.Start = Math.Min(_player.Position, n.End - 200);
        NotesEdited();
    }

    private void NoteEnd_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedNote is not { } n) return;
        n.End = Math.Max(_player.Position, n.Start + 200);
        NotesEdited();
    }

    // ---------------------------------------------------------------- looks

    private void BuildLooks()
    {
        LookList.Children.Clear();
        string? def = LookLibrary.DefaultName;
        foreach (var look in LookLibrary.All())
        {
            var st = look.Style;
            Brush swatch = st.Background switch
            {
                BackgroundKind.Solid => new SolidColorBrush(C(st.BgSolid)),
                _ => new LinearGradientBrush(C(st.BgTop), C(st.BgBottom), 45),
            };
            bool isDefault = string.Equals(look.Name, def, StringComparison.OrdinalIgnoreCase);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            if (!look.BuiltIn)
            {
                var del = new Button { Style = (System.Windows.Style)FindResource("IconButton"), Content = "", Width = 30, Height = 30, ToolTip = "Delete this look" };
                DockPanel.SetDock(del, Dock.Right);
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show(_shell, $"Delete the look “{look.Name}”?", "Looks", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                    LookLibrary.Delete(look.Name);
                    BuildLooks();
                };
                row.Children.Add(del);
            }
            var star = new Button
            {
                Style = (System.Windows.Style)FindResource("IconButton"), Width = 30, Height = 30,
                Content = isDefault ? "" : "",
                Foreground = isDefault ? (Brush)FindResource("Accent") : (Brush)FindResource("Subtext"),
                ToolTip = isDefault ? "Default for new recordings (click to clear)" : "Use for new recordings",
            };
            DockPanel.SetDock(star, Dock.Right);
            star.Click += (_, _) =>
            {
                LookLibrary.DefaultName = isDefault ? null : look.Name;
                BuildLooks();
            };
            row.Children.Add(star);

            var content = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var chip = new Border { Width = 44, Height = 30, CornerRadius = new CornerRadius(7), Background = swatch, Margin = new Thickness(0, 0, 10, 0) };
            chip.Child = new Border
            {
                Width = 26, Height = 17, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(220, 245, 246, 250)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(chip);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = look.Name, FontWeight = FontWeights.SemiBold });
            var bits = new List<string> { look.BuiltIn ? "Built-in" : "Saved" };
            if (st.Aspect != CanvasAspect.Source) bits.Add(Canvas.Label(st.Aspect));
            if (look.Zoom != null) bits.Add($"{look.Zoom.Preset} zoom");
            if (look.Steps) bits.Add("steps");
            if (!string.IsNullOrEmpty(st.LogoImage)) bits.Add("logo");
            text.Children.Add(new TextBlock { Text = string.Join(" · ", bits), Style = (System.Windows.Style)FindResource("Hint") });
            content.Children.Add(text);
            var apply = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6), ToolTip = "Apply this look" };
            apply.Click += (_, _) => ApplyLook(look);
            row.Children.Add(apply);
            LookList.Children.Add(row);
        }
        LooksInfo.Text = $"Saved looks are JSON files in {LookLibrary.Dir}. Share them with your team or use them from the CLI (--look).";
    }

    private void ApplyLook(Look look)
    {
        if (look.Zoom != null && !ConfirmReplaceLane()) return;
        var aspectBefore = St.Aspect;
        LookLibrary.Apply(_p, look);
        if (look.Zoom != null) { Timeline.Project = _p; _laneEdited = false; }
        BuildGalleries();
        LoadStyleControls();
        RefreshSelection();
        RefreshNote();
        _camera = _p.BuildCamera();
        if (St.Aspect != aspectBefore) SwapPlayer();
        else _player.Update(_camera, St);
        Timeline.InvalidateVisual();
        MarkDirty();
        LooksInfo.Text = $"Applied “{look.Name}”.";
    }

    private void SaveLook_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NameDialog("Save look", "Name this look (your brand kit):", "My look",
            "Also save the zoom feel (preset, speed, follow)") { Owner = _shell };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        string name = dlg.Value.Trim();
        if (LookLibrary.BuiltIns.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            MessageBox.Show(_shell, $"“{name}” is a built-in look. Save yours over it?", "Looks", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        var look = LookLibrary.FromProject(_p, name, dlg.Option);
        look.Steps = _p.Annotations?.Any(a => a.Auto) ?? false;
        LookLibrary.Save(look);
        BuildLooks();
        LooksInfo.Text = $"Saved “{name}”.";
    }
}
