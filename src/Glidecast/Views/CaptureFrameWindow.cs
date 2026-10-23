using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>
/// Shows what will be recorded while getting ready: the rest of the screen is
/// dimmed and the area gets a dashed outline. Clicks pass straight through, so
/// apps can still be arranged, and it never appears in the capture.
/// </summary>
public sealed class CaptureFrameWindow : Window
{
    public CaptureFrameWindow(System.Drawing.Rectangle? regionPx)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        ShowActivated = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        double k = NativeMethods.PixelsPerDip();
        var full = new Rect(0, 0, Width, Height);
        var area = regionPx is { } r ? new Rect(r.X / k, r.Y / k, r.Width / k, r.Height / k) : full;
        var accent = (Brush)Application.Current.FindResource("Accent");
        var canvas = new System.Windows.Controls.Canvas();
        canvas.Children.Add(new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(regionPx == null ? (byte)0 : (byte)0x66, 0, 0, 0)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(area)),
        });
        var outline = new Rectangle { Width = area.Width, Height = area.Height, Stroke = accent, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 3 } };
        System.Windows.Controls.Canvas.SetLeft(outline, area.X);
        System.Windows.Controls.Canvas.SetTop(outline, area.Y);
        canvas.Children.Add(outline);
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            NativeMethods.ExcludeFromCapture(this);
            NativeMethods.MakeClickThrough(this);
        };
    }
}
