using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glidecast.App.Services;
using Glidecast.Capture;
using Glidecast.Render;

namespace Glidecast.App.Views;

/// <summary>
/// The live camera, floating on the desktop in its bubble shape, placed where
/// it will sit in the video: drag it to move, drag the corner grip or scroll
/// to resize. It stays up during the take as a mirror, but is kept out of the
/// screen capture; the webcam is recorded separately and composited later.
/// It owns the camera feed, which the recording borrows.
/// </summary>
public sealed class CameraBubbleWindow : Window
{
    private readonly Grid _shape;
    private readonly System.Windows.Shapes.Path _outline;
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly Border _grip;
    private readonly WriteableBitmap _bitmap = new(CameraFeed.Width, CameraFeed.Height, 96, 96, PixelFormats.Bgra32, null);
    private readonly System.Drawing.Rectangle _region;
    private readonly double _k;
    private int _pending;
    private WebcamShape _kind;
    private Point? _resizeFrom;
    private double _resizeStartH;

    public CameraFeed? Feed { get; private set; }

    /// <param name="regionPx">The area being recorded, in screen pixels (null = primary screen).</param>
    public CameraBubbleWindow(System.Drawing.Rectangle? regionPx, RecordOptions o)
    {
        _region = regionPx ?? NativeMethods.PrimaryScreen();
        _k = NativeMethods.PixelsPerDip();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        // Fill the shape from the center of the frame, like the video does. The
        // image overflows its cell, so mirror a container the cell's size rather
        // than the image itself (that would slide the picture sideways).
        _image = new Image { Source = _bitmap, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mirror = new Grid { ClipToBounds = true, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(-1, 1) };
        mirror.Children.Add(_image);
        _status = new TextBlock
        {
            Text = "Starting camera…", Foreground = (Brush)Application.Current.FindResource("Subtext"), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(12),
        };
        // The picture, clipped to the shape, with the outline drawn on top.
        _shape = new Grid { Background = (Brush)Application.Current.FindResource("Panel3"), Cursor = Cursors.SizeAll };
        _shape.Children.Add(_status);
        _shape.Children.Add(mirror);
        _outline = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), StrokeThickness = 2, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        _grip = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White,
            BorderBrush = (Brush)Application.Current.FindResource("Accent"), BorderThickness = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize", Opacity = 0,
        };
        var root = new Grid { Margin = new Thickness(4) };
        root.Children.Add(_shape);
        root.Children.Add(_outline);
        root.Children.Add(_grip);
        Content = root;

        _shape.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); KeepOnScreen(); } };
        _grip.MouseLeftButtonDown += (_, e) => { _resizeFrom = PointToScreen(e.GetPosition(this)); _resizeStartH = _shape.ActualHeight; _grip.CaptureMouse(); e.Handled = true; };
        _grip.MouseMove += (_, e) =>
        {
            if (_resizeFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = PointToScreen(e.GetPosition(this));
            SetBubbleHeight(_resizeStartH + Math.Max(now.X - from.X, now.Y - from.Y) / _k);
        };
        _grip.MouseLeftButtonUp += (_, _) => { _resizeFrom = null; _grip.ReleaseMouseCapture(); KeepOnScreen(); };
        MouseEnter += (_, _) => _grip.Opacity = 1;
        MouseLeave += (_, _) => { if (_resizeFrom == null) _grip.Opacity = 0; };
        MouseWheel += (_, e) => { SetBubbleHeight(_shape.ActualHeight * (e.Delta > 0 ? 1.08 : 1 / 1.08)); KeepOnScreen(); };

        // Start where the options say, relative to the recorded area.
        var st = new StyleSettings();
        o.ApplyTo(st);
        var r = Compositor.WebcamRect(st, _region.Width, _region.Height);
        SetShape(o.CameraShape, r.Height / _k);
        Left = (_region.Left + r.Left) / _k - 4;
        Top = (_region.Top + r.Top) / _k - 4;
        KeepOnScreen();

        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        Closed += (_, _) => StopFeed();
    }

    /// <summary>Keeps the whole bubble inside the work area: never under the
    /// taskbar or off the edge, where it would be cut off.</summary>
    private void KeepOnScreen()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return; // not placed yet
        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
    }

    public void SetShape(WebcamShape kind, double? height = null)
    {
        _kind = kind;
        double h = height ?? _shape.Height;
        if (double.IsNaN(h) || h <= 0) h = 180;
        SetBubbleHeight(h);
        KeepOnScreen(); // an oval is wider than a circle
    }

    private void SetBubbleHeight(double h)
    {
        double shortSide = Math.Min(_region.Width, _region.Height) / _k;
        h = Math.Clamp(h, shortSide * 0.12, shortSide * 0.5);
        double w = _kind == WebcamShape.Oval ? h * 4 / 3 : h;
        _shape.Width = w;
        _shape.Height = h;
        double radius = h * 0.16;
        Geometry outline = _kind == WebcamShape.Square
            ? new RectangleGeometry(new Rect(0, 0, w, h), radius, radius)
            : new EllipseGeometry(new Rect(0, 0, w, h));
        _shape.Clip = outline;
        _outline.Data = _kind == WebcamShape.Square
            ? new RectangleGeometry(new Rect(1, 1, w - 2, h - 2), radius - 1, radius - 1)
            : new EllipseGeometry(new Rect(1, 1, w - 2, h - 2));
        Width = w + 8;
        Height = h + 8;
    }

    /// <summary>Opens the camera (replacing any open one).</summary>
    public void StartFeed(string device)
    {
        StopFeed();
        _status.Text = "Starting camera…";
        _image.Visibility = Visibility.Collapsed;
        var feed = new CameraFeed(device);
        feed.Frame += (buf, _) =>
        {
            // Drop frames while the last one is still being drawn.
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            var copy = (byte[])buf.Clone();
            Dispatcher.BeginInvoke(() =>
            {
                if (Feed == feed)
                {
                    _bitmap.WritePixels(new Int32Rect(0, 0, CameraFeed.Width, CameraFeed.Height), copy, CameraFeed.Width * 4, 0);
                    _image.Visibility = Visibility.Visible;
                }
                _pending = 0;
            });
        };
        feed.Failed += msg => Dispatcher.BeginInvoke(() =>
        {
            if (Feed != feed) return;
            _image.Visibility = Visibility.Collapsed;
            _status.Text = "Camera unavailable: " + msg;
        });
        Feed = feed;
        try { feed.Start(); }
        catch (Exception ex) { _status.Text = "Camera unavailable: " + ex.Message; }
    }

    public void StopFeed()
    {
        var f = Feed;
        Feed = null;
        f?.Dispose();
    }

    /// <summary>Writes the bubble's place and size, relative to the recorded area, into the options.</summary>
    public void SavePlacement(RecordOptions o)
    {
        var tl = _shape.PointToScreen(new Point(0, 0)); // physical pixels
        double w = _shape.ActualWidth * _k, h = _shape.ActualHeight * _k;
        double shortSide = Math.Min(_region.Width, _region.Height);
        double margin = shortSide * 0.035;
        o.CameraShape = _kind;
        o.CameraSize = (float)Math.Clamp(h / shortSide, 0.08, 0.7);
        o.CameraX = (float)Math.Clamp((tl.X - _region.Left - margin) / Math.Max(1, _region.Width - 2 * margin - w), 0, 1);
        o.CameraY = (float)Math.Clamp((tl.Y - _region.Top - margin) / Math.Max(1, _region.Height - 2 * margin - h), 0, 1);
    }
}
