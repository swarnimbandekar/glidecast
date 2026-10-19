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
        ("Violet dusk", "#6D5BD0", "#1C1640"),
        ("Midnight", "#1E293B", "#0B1018"),
        ("Ocean", "#1A8FE3", "#0B2A5B"),
        ("Aurora", "#2BC0A0", "#1B3B6F"),
        ("Sunset", "#FF8A5B", "#B0306A"),
        ("Peach", "#FFD1A9", "#F38FA5"),
        ("Mint", "#D4F5E2", "#7FCFC0"),
        ("Graphite", "#4A4F5C", "#16181D"),
        ("Ember", "#F7B733", "#FC4A1A"),
        ("Lagoon", "#43CEA2", "#185A9D"),
        ("Orchid", "#DA22FF", "#9733EE"),
        ("Slate", "#8E9EAB", "#3B4652"),
    };

    private static readonly string[] Solids =
        { "#0E1013", "#1F2430", "#F4F5F8", "#E8E1D6", "#2E5BFF", "#7C5CFF", "#16A37F", "#FF4D6A", "#FFB020", "#0EA5E9" };

    private static readonly string[] Accents =
        { "#7C5CFF", "#4F8CFF", "#2FD195", "#FFB020", "#FF4D6A", "#F472B6", "#FFFFFF" };

    private readonly MainWindow _shell;
    private readonly Project _p;
    private readonly string _file;
    private readonly PreviewPlayer _player;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _cameraTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private CameraTrack _camera;
    private bool _loading;
    private bool _closed;

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

        _player = new PreviewPlayer(_p, Dispatcher, 1280);
        Preview.Source = _player.Bitmap;
        Preview.SizeChanged += (_, _) =>
            Preview.Clip = new RectangleGeometry(new Rect(0, 0, Preview.ActualWidth, Preview.ActualHeight), 12, 12);
        _player.PositionChanged += t =>
        {
            Timeline.Playhead = t;
            TimeNow.Text = TimelineControl.FormatTime(t);
            ZoomNow.Text = $"{_camera.At(t).scale:0.0}×";
        };
        _player.PlayingChanged += playing =>
        {
            PlayIcon.Text = playing ? "" : "";
            PlayIcon.Margin = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        };

        Timeline.Project = _p;
        Timeline.SeekRequested += t => _player.Seek(t);
        Timeline.SegmentsChanged += OnSegmentsChanged;
        Timeline.SelectionChanged += _ => RefreshSelection();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        // Rebuilding the camera is cheap but not free; coalesce bursts (slider drags).
        _cameraTimer.Tick += (_, _) => { _cameraTimer.Stop(); RebuildCamera(); };

        BuildSwatches();
        BuildGalleries();
        LoadStyleControls();
        RefreshSelection();
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
        if (Timeline.Selected is not { } s || Preview.ActualWidth <= 0) return;
        var pos = e.GetPosition(Preview);
        // Preview pixels -> output pixels -> (inverse camera) -> source pixels.
        float ox = (float)(pos.X / Preview.ActualWidth * _player.OutWidth);
        float oy = (float)(pos.Y / Preview.ActualHeight * _player.OutHeight);
        var cam = Compositor.CameraFor(_p, _camera, St, _player.OutWidth, _player.OutHeight, _player.Position);
        if (!cam.TryInvert(out var inv)) return;
        var src = inv.MapPoint(new SKPoint(ox, oy));
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
        AutoScaleVal.Text = $"{_p.Zoom.MaxScale:0.0}×";
        MarkDirty();
    }

    private void Speed_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _p.Zoom!.Speed = SpeedGentle.IsChecked == true ? ZoomSpeed.Gentle
            : SpeedSnappy.IsChecked == true ? ZoomSpeed.Snappy : ZoomSpeed.Smooth;
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
    }

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (BgPanel == null || FramePanel == null || CursorPanel == null || ZoomPanel == null) return;
        BgPanel.Visibility = TabBg.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FramePanel.Visibility = TabFrame.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CursorPanel.Visibility = TabCursor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ZoomPanel.Visibility = TabZoom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        var accent = StyleSettings.ParseColor(St.Accent, new SKColor(0x7C, 0x5C, 0xFF));
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
        return new RadioButton { Style = (System.Windows.Style)FindResource("Tile"), Content = panel, GroupName = group, Width = 96 };
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
        SmoothVal.Text = St.CursorSmoothing < 0.05 ? "Off" : St.CursorSmoothing < 0.4 ? "Light" : St.CursorSmoothing < 0.75 ? "Smooth" : "Silky";
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || IdleToggle == null) return;
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
}
