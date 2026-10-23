using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>Floating toolbar shown during a take: 3-2-1 countdown, live elapsed
/// timer, Stop / Discard, and F9 to mark a moment. Excluded from the capture itself.</summary>
public partial class RecordingBar : Window
{
    private readonly RecordingSession _session;
    private readonly System.Drawing.Rectangle? _region;
    private readonly double _fps;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TaskCompletionSource<string?> _done = new();
    private int _countdown = 3;
    private bool _recording;
    private const int MarkHotkeyId = 0x4743;
    private HwndSource? _hwnd;
    private readonly DispatcherTimer _markFlash = new() { Interval = TimeSpan.FromMilliseconds(900) };

    /// <summary>Resolves to the saved project.json, or null if discarded.</summary>
    public Task<string?> Completion => _done.Task;

    private readonly RecordOptions _options;
    private readonly CameraBubbleWindow? _bubble;

    /// <param name="bubble">The floating camera from the pre-record step; its feed is recorded and it closes with the take.</param>
    public RecordingBar(string dir, System.Drawing.Rectangle? region, RecordOptions options, string label, CameraBubbleWindow? bubble)
    {
        InitializeComponent();
        _options = options;
        _bubble = bubble;
        _session = new RecordingSession(dir, options, bubble?.Feed);
        _region = region;
        _fps = options.Fps60 ? 60 : 30;
        _countdown = Math.Max(0, options.Countdown);
        var bits = new List<string> { label };
        if (bubble?.Feed != null) bits.Add("camera");
        if (options.Microphone) bits.Add("mic");
        if (options.SystemAudio) bits.Add("sound");
        Info.Text = string.Join(" · ", bits);
        Clock.Text = _countdown > 0 ? _countdown.ToString() : "0:00";
        StopButton.IsEnabled = false;
        _timer.Tick += Tick;
        _markFlash.Tick += (_, _) => { _markFlash.Stop(); RecLabel.Text = "REC"; };

        SourceInitialized += (_, _) =>
        {
            NativeMethods.ExcludeFromCapture(this);
            _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd.AddHook(WndProc);
            _session.MarkHotkey = NativeMethods.RegisterHotKey(_hwnd.Handle, MarkHotkeyId, 0, 0x78 /* VK_F9 */);
            MarkHint.Visibility = _session.MarkHotkey ? Visibility.Visible : Visibility.Collapsed;
        };
        Closed += (_, _) => { if (_hwnd != null) NativeMethods.UnregisterHotKey(_hwnd.Handle, MarkHotkeyId); };
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 2;
            if (_countdown > 0) ShowCountdown();
            else StartTake();
            _timer.Start();
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == MarkHotkeyId)
        {
            handled = true;
            if (_recording)
            {
                _session.Mark();
                RecLabel.Text = "MARKED";
                _markFlash.Stop();
                _markFlash.Start();
            }
        }
        return IntPtr.Zero;
    }

    private Window? _countdownWindow;
    private TextBlock? _countdownText;

    /// <summary>Big centered 3-2-1 so it's obvious when capture begins (kept out of the capture).</summary>
    private void ShowCountdown()
    {
        _countdownText = new TextBlock
        {
            Text = _countdown.ToString(),
            FontSize = 72,
            FontWeight = FontWeights.SemiBold,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"),
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.ScaleTransform(1, 1),
        };
        var grid = new Grid();
        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEB, 0x1B, 0x1B, 0x1D)),
            Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
        });
        grid.Children.Add(_countdownText);
        _countdownWindow = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Width = 170, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = grid, IsHitTestVisible = false,
        };
        _countdownWindow.SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(_countdownWindow);
        _countdownWindow.Show();
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_countdownText == null) return;
        _countdownText.Text = _countdown.ToString();
        var pop = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var st = (System.Windows.Media.ScaleTransform)_countdownText.RenderTransform;
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!_recording)
        {
            // Countdown in whole seconds (timer ticks at 200 ms).
            if (_timer.Tag is not int ticks) ticks = 0;
            _timer.Tag = ++ticks;
            if (ticks % 5 != 0) return;
            _countdown--;
            if (_countdown > 0) { Clock.Text = _countdown.ToString(); UpdateCountdown(); return; }
            if (!StartTake()) return;
        }
        var t = _session.Elapsed;
        Clock.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private bool StartTake()
    {
        _countdownWindow?.Close();
        _countdownWindow = null;
        try
        {
            _session.Start(_region, _fps);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            MessageBox.Show("Could not start recording: " + ex.Message, "Glidecast");
            Finish(null);
            return false;
        }
        {
            _recording = true;
            StopButton.IsEnabled = true;
            if (_session.Warnings.Count > 0)
            {
                Info.Text += " · " + (_session.Warnings.Count == 1 ? "1 source failed" : $"{_session.Warnings.Count} sources failed");
                Info.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
                Info.ToolTip = string.Join(Environment.NewLine, _session.Warnings);
            }
            RecLabel.Text = "REC";
            var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Dot.BeginAnimation(OpacityProperty, pulse);
        }
        return true;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_recording) return;
        _timer.Stop();
        StopButton.IsEnabled = CancelButton.IsEnabled = false;
        Clock.Text = "Saving…";
        var barRect = NativeMethods.ScreenRect(Chrome);
        barRect.Inflate(4, 4);
        // Wherever the camera was dragged to during the take is where it goes in the video.
        _bubble?.SavePlacement(_options);
        try
        {
            await Task.Run(() => _session.Stop(barRect));
            Finish(_session.ProjectFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Saving the recording failed: " + ex.Message, "Glidecast");
            Finish(null);
        }
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_recording)
        {
            StopButton.IsEnabled = CancelButton.IsEnabled = false;
            await Task.Run(() => _session.Stop(null));
            try { Directory.Delete(_session.Directory, recursive: true); } catch { }
        }
        Finish(null);
    }

    private void Finish(string? projectFile)
    {
        _countdownWindow?.Close();
        _bubble?.Close();
        _done.TrySetResult(projectFile);
        Close();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
