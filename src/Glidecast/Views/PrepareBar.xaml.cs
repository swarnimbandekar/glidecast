using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glidecast.App.Services;
using Glidecast.Capture;
using Glidecast.Render;

namespace Glidecast.App.Views;

/// <summary>
/// The step between choosing what to record and recording it: the area is
/// outlined on screen, the camera floats where it will appear, and this bar
/// holds the inputs (camera, microphone with a level meter, system sound),
/// take settings and Start. Nothing here is captured.
/// </summary>
public partial class PrepareBar : Window
{
    private readonly RecordOptions _o;
    private readonly System.Drawing.Rectangle? _region;
    private readonly CaptureFrameWindow _frame;
    private readonly TaskCompletionSource<bool> _done = new();
    private MicMeter? _meter;
    private bool _loading = true;

    /// <summary>True when Start was pressed, false when cancelled.</summary>
    public Task<bool> Completion => _done.Task;

    /// <summary>The floating camera (with its running feed), handed to the take on Start.</summary>
    public CameraBubbleWindow? Bubble { get; private set; }

    public PrepareBar(System.Drawing.Rectangle? region, string icon, string label, RecordOptions options)
    {
        InitializeComponent();
        _o = options;
        _region = region;
        var px = region ?? NativeMethods.PrimaryScreen();
        ModeIcon.Text = icon;
        ModeText.Text = label;
        SizeText.Text = $"{px.Width & ~1} × {px.Height & ~1}";

        Count0.IsChecked = _o.Countdown == 0;
        Count3.IsChecked = _o.Countdown is not (0 or 5);
        Count5.IsChecked = _o.Countdown == 5;
        Fps30.IsChecked = !_o.Fps60;
        Fps60.IsChecked = _o.Fps60;
        AnnotateToggle.IsChecked = _o.Annotate;
        ShapeCircle.IsChecked = _o.CameraShape == WebcamShape.Circle;
        ShapeSquare.IsChecked = _o.CameraShape == WebcamShape.Square;
        ShapeOval.IsChecked = _o.CameraShape == WebcamShape.Oval;

        DevicePickers.FillMicrophones(MicPick, _o);
        DevicePickers.FillSpeakers(SpeakerPick, _o);
        CameraPick.ItemsSource = new[] { new DeviceChoice("Looking for cameras…", null) };
        CameraPick.SelectedIndex = 0;
        CameraPick.IsEnabled = false;
        ShapePanel.Visibility = Visibility.Collapsed;

        _frame = new CaptureFrameWindow(region);
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        Loaded += async (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 8;
            _frame.Show();
            Activate();
            _loading = false;
            RestartMeter();
            var cams = await DevicePickers.CamerasAsync();
            _loading = true;
            DevicePickers.FillCameras(CameraPick, cams, _o);
            CameraPick.IsEnabled = cams.Count > 0;
            _loading = false;
            SyncCamera();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(false);
            else if (e.Key == Key.Enter && !SettingsToggle.IsChecked == true) Finish(true);
        };
    }

    private void Camera_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _o);
        _o.Save();
        SyncCamera();
    }

    /// <summary>Shows the floating camera for the chosen webcam, or removes it for "No camera".</summary>
    private void SyncCamera()
    {
        bool on = CameraPick.SelectedItem is DeviceChoice { Id: not null };
        ShapePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            Bubble?.Close();
            Bubble = null;
            return;
        }
        if (Bubble == null)
        {
            Bubble = new CameraBubbleWindow(_region, _o);
            Bubble.Show();
        }
        Bubble.StartFeed(((DeviceChoice)CameraPick.SelectedItem).Id!);
    }

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<WebcamShape>(tag, out var shape)) return;
        _o.CameraShape = shape;
        Bubble?.SetShape(shape);
        _o.Save();
    }

    private void Mic_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _o);
        _o.Save();
        RestartMeter();
    }

    private void Speaker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _o);
        _o.Save();
    }

    /// <summary>A live level line under the microphone picker, so a dead mic shows before the take.</summary>
    private void RestartMeter()
    {
        _meter?.Dispose();
        _meter = null;
        MicLevel.Width = 0;
        if (MicPick.SelectedItem is not DeviceChoice { Id: { } id }) return;
        try
        {
            var meter = new MicMeter(id);
            meter.Level += level => Dispatcher.BeginInvoke(() =>
            {
                if (_meter == meter) MicLevel.Width = Math.Max(0, (MicPick.ActualWidth - 16) * Math.Min(1, Math.Sqrt(level)));
            });
            _meter = meter;
        }
        catch { /* the level line is a nicety */ }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Count5 == null || AnnotateToggle == null) return;
        _o.Countdown = Count0.IsChecked == true ? 0 : Count5.IsChecked == true ? 5 : 3;
        _o.Fps60 = Fps60.IsChecked == true;
        _o.Annotate = AnnotateToggle.IsChecked == true;
        _o.Save();
    }

    private void Settings_Closed(object sender, EventArgs e) => SettingsToggle.IsChecked = false;

    private void Teleprompter_Click(object sender, RoutedEventArgs e) => TeleprompterWindow.Toggle();

    private void Start_Click(object sender, RoutedEventArgs e) => Finish(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool start)
    {
        if (_done.Task.IsCompleted) return;
        _meter?.Dispose();
        _meter = null;
        _frame.Close();
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _o);
        if (start && Bubble != null) Bubble.SavePlacement(_o);
        if (!start)
        {
            Bubble?.Close();
            Bubble = null;
        }
        _o.Save();
        _done.TrySetResult(start);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_done.Task.IsCompleted) Finish(false);
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border) DragMove();
    }
}
