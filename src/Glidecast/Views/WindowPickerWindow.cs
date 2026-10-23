using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>
/// Full-screen overlay for "Window" mode: the window under the pointer is
/// outlined with its title and size, and a click picks it. The take records
/// that window's area of the primary screen (the window is brought to front).
/// </summary>
public sealed class WindowPickerWindow : Window
{
    private readonly List<NativeMethods.DesktopWindow> _windows;
    private readonly Rectangle _box;
    private readonly Border _tag;
    private readonly TextBlock _title, _size;
    private readonly double _k;
    private NativeMethods.DesktopWindow? _hover;

    /// <summary>The picked window's visible area in screen pixels, clipped to the primary screen.</summary>
    public System.Drawing.Rectangle? Selection { get; private set; }
    public string? PickedTitle { get; private set; }

    public WindowPickerWindow()
    {
        // Enumerate before this overlay exists so it can't pick itself.
        _windows = NativeMethods.TopLevelWindows();
        _k = NativeMethods.PixelsPerDip();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Cursor = Cursors.Hand;

        var accent = (Brush)Application.Current.FindResource("Accent");
        _box = new Rectangle
        {
            Stroke = accent, StrokeThickness = 3, RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0x7E, 0xFF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _title = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
        _size = new TextBlock { Foreground = (Brush)Application.Current.FindResource("Subtext"), Margin = new Thickness(10, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_title);
        row.Children.Add(_size);
        _tag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1B, 0x1B, 0x1D)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7), Child = row, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        var help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1B, 0x1B, 0x1D)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock { Text = "Click the window you want to record  ·  Esc to cancel", Foreground = Brushes.White },
            IsHitTestVisible = false,
        };
        var canvas = new Canvas();
        canvas.Children.Add(_box);
        canvas.Children.Add(_tag);
        canvas.Children.Add(help);
        Content = canvas;
        Loaded += (_, _) =>
        {
            help.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(help, (ActualWidth - help.DesiredSize.Width) / 2);
            Canvas.SetTop(help, 24);
            NativeMethods.ExcludeFromCapture(this);
            Activate();
            Focus();
        };
        MouseMove += (_, e) => HoverAt(e.GetPosition(this));
        MouseLeftButtonDown += (_, _) => Pick();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private void HoverAt(Point dip)
    {
        int x = (int)(dip.X * _k), y = (int)(dip.Y * _k);
        var hit = _windows.FirstOrDefault(w => w.Bounds.Contains(x, y)); // front-most first
        if (hit == _hover) return;
        _hover = hit;
        if (hit == null)
        {
            _box.Visibility = _tag.Visibility = Visibility.Collapsed;
            return;
        }
        var b = hit.Bounds;
        Canvas.SetLeft(_box, b.Left / _k);
        Canvas.SetTop(_box, b.Top / _k);
        _box.Width = b.Width / _k;
        _box.Height = b.Height / _k;
        _title.Text = hit.Title;
        _size.Text = $"{b.Width & ~1} × {b.Height & ~1}";
        Canvas.SetLeft(_tag, Math.Max(8, b.Left / _k + 8));
        Canvas.SetTop(_tag, Math.Max(8, b.Top / _k + 8));
        _box.Visibility = _tag.Visibility = Visibility.Visible;
    }

    private void Pick()
    {
        if (_hover == null) return;
        var r = System.Drawing.Rectangle.Intersect(_hover.Bounds, NativeMethods.PrimaryScreen());
        if (r.Width < 32 || r.Height < 32) return;
        Selection = r;
        PickedTitle = _hover.Title;
        NativeMethods.SetForegroundWindow(_hover.Handle);
        DialogResult = true;
    }
}
