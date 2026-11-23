using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Glidecast.App.Services;
using Glidecast.Model;

namespace Glidecast.App.Views;

/// <summary>
/// Click-through, capture-excluded overlay shown during recording to give a live
/// preview of annotations (step badges, highlight boxes, captions, and F9 marks)
/// as clicks happen. Since it uses ExcludeFromCapture, nothing drawn here is
/// baked into the raw screen video — all annotations remain editable post-recording.
/// </summary>
public sealed class LiveAnnotationOverlayWindow : Window
{
    private readonly System.Windows.Controls.Canvas _canvas = new();
    private readonly RecordOptions _options;
    private readonly System.Drawing.Rectangle _region;
    private readonly double _k;
    private int _step;
    private ClickEvent? _prev;

    public LiveAnnotationOverlayWindow(System.Drawing.Rectangle? regionPx, RecordOptions options)
    {
        _options = options;
        _k = NativeMethods.PixelsPerDip();
        _region = regionPx ?? NativeMethods.PrimaryScreen();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            NativeMethods.ExcludeFromCapture(this);
            NativeMethods.MakeClickThrough(this);
        };
    }

    public void ShowClick(ClickEvent c, System.Drawing.Rectangle? ignoreArea)
    {
        if (!_options.Annotate || !_options.LiveAnnotationPreview) return;
        if (ignoreArea is { } ig && ig.Contains(c.X, c.Y)) return;
        if (!_region.Contains(c.X, c.Y)) return;

        // Second press of a double-click is the same step.
        if (_prev is { } pc && c.Button == pc.Button && c.T - pc.T < 450 &&
            Math.Abs(c.X - pc.X) < 12 && Math.Abs(c.Y - pc.Y) < 12)
        {
            _prev = c;
            return;
        }
        _prev = c;
        _step++;

        double screenArea = Math.Max(1.0, _region.Width * (double)_region.Height);
        var el = c.Element;
        bool boxable = el is { W: >= 8, H: >= 8 } && el.W * (double)el.H < screenArea * 0.2;

        var accent = (Brush)Application.Current.FindResource("Accent");

        if (_options.AnnotateBoxes)
        {
            const double pad = 6;
            double hw = boxable ? el!.W + 2 * pad : Math.Clamp(_region.Width * 0.08, 72, 160);
            double hh = boxable ? el!.H + 2 * pad : Math.Clamp(_region.Height * 0.06, 40, 88);
            double hx = boxable ? el!.X - pad : c.X - hw / 2;
            double hy = boxable ? el!.Y - pad : c.Y - hh / 2;

            SpawnBox(hx / _k, hy / _k, hw / _k, hh / _k, accent, 1500);
        }

        if (_options.AnnotateSteps)
        {
            double bx = boxable ? el!.X + el.W : c.X + 26;
            double by = boxable ? el!.Y : c.Y - 26;
            SpawnStep(_step, bx / _k, by / _k, accent, 1800);
        }

        if (_options.AnnotateCaptions && boxable && !string.IsNullOrWhiteSpace(el?.Name))
        {
            string verb = c.Button == 1 ? "Right-click" : c.Count >= 2 ? "Double-click" : "Click";
            string name = el!.Name!.Trim();
            if (name.Length > 40) name = name[..39] + "…";
            double cx = (boxable ? el.Cx : c.X) / _k;
            double cy = (boxable ? el.Y + el.H + 8 : c.Y + 22) / _k;
            SpawnCaption($"{verb} “{name}”", cx, cy, accent, 2000);
        }
    }

    public void ShowMark(int screenX, int screenY)
    {
        if (!_options.LiveAnnotationPreview) return;
        double hw = Math.Clamp(_region.Width * 0.14, 120, 240) / _k;
        double hh = Math.Clamp(_region.Height * 0.10, 72, 140) / _k;
        var accent = (Brush)Application.Current.FindResource("Accent");
        SpawnBox(screenX / _k - hw / 2, screenY / _k - hh / 2, hw, hh, accent, 2200);
    }

    private void SpawnBox(double x, double y, double w, double h, Brush accent, int durationMs)
    {
        var box = new Border
        {
            Width = Math.Max(16, w),
            Height = Math.Max(16, h),
            CornerRadius = new CornerRadius(10),
            BorderBrush = accent,
            BorderThickness = new Thickness(2.5),
            Background = new SolidColorBrush(Color.FromArgb(34, 61, 126, 255)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.08, 1.08),
        };
        System.Windows.Controls.Canvas.SetLeft(box, x);
        System.Windows.Controls.Canvas.SetTop(box, y);
        _canvas.Children.Add(box);
        AnimateInAndOut(box, durationMs);
    }

    private void SpawnStep(int num, double cx, double cy, Brush accent, int durationMs)
    {
        const double r = 16;
        var badge = new Border
        {
            Width = r * 2,
            Height = r * 2,
            CornerRadius = new CornerRadius(r),
            Background = accent,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Child = new TextBlock
            {
                Text = num.ToString(),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = num > 9 ? 13 : 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Controls.Canvas.SetLeft(badge, cx - r);
        System.Windows.Controls.Canvas.SetTop(badge, cy - r);
        _canvas.Children.Add(badge);
        AnimateInAndOut(badge, durationMs);
    }

    private void SpawnCaption(string text, double cx, double topY, Brush accent, int durationMs)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new Border { Background = accent, CornerRadius = new CornerRadius(8, 0, 0, 8) };
        var lbl = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(12, 7, 14, 7),
        };
        Grid.SetColumn(lbl, 1);
        row.Children.Add(bar);
        row.Children.Add(lbl);

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 18, 20, 26)),
            CornerRadius = new CornerRadius(8),
            Child = row,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.95, 0.95),
        };
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = pill.DesiredSize.Width;
        System.Windows.Controls.Canvas.SetLeft(pill, Math.Clamp(cx - w / 2, 8, Math.Max(8, Width - w - 8)));
        System.Windows.Controls.Canvas.SetTop(pill, Math.Clamp(topY, 8, Math.Max(8, Height - 48)));
        _canvas.Children.Add(pill);
        AnimateInAndOut(pill, durationMs);
    }

    private void AnimateInAndOut(FrameworkElement el, int durationMs)
    {
        if (el.RenderTransform is ScaleTransform st)
        {
            var scaleIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
        }

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(200, durationMs - 240)))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs))));
        fade.Completed += (_, _) => _canvas.Children.Remove(el);
        el.BeginAnimation(OpacityProperty, fade);
    }
}
