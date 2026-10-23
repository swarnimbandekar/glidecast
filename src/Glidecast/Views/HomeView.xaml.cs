using System.IO;
using System.Windows;
using System.Windows.Controls;
using Glidecast.App.Services;
using Glidecast.Capture;
using Glidecast.Demo;
using Microsoft.Win32;

namespace Glidecast.App.Views;

/// <summary>
/// The launcher: pick what to record (full screen, an area, or a window) and
/// the inputs (camera, microphone, system sound). Picking a mode leads to the
/// selection, then the pre-record bar with the floating camera, then the take.
/// Recent projects, Open and the demo live in the title bar's Projects menu.
/// </summary>
public partial class HomeView : UserControl
{
    private readonly MainWindow _shell;
    private readonly RecordOptions _options = RecordOptions.Load();
    private bool _loading = true;
    private MicMeter? _meter;

    public HomeView(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        ((Panel)TitleBar.Parent).Children.Remove(TitleBar);
        _shell.SetTitleContent(TitleBar);
        ShowRecent();
        if (ProjectStore.UsingFallback)
            Status.Text = $"Documents isn't writable for this app (Controlled Folder Access?), so recordings go to {ProjectStore.Root}";
        _ = RecoverAsync();
        _ = LoadInputsAsync();
        Unloaded += (_, _) => StopMeter();
    }

    // ---------------------------------------------------------------- inputs

    private async Task LoadInputsAsync()
    {
        _loading = true;
        DevicePickers.FillMicrophones(MicPick, _options);
        DevicePickers.FillSpeakers(SpeakerPick, _options);
        CameraPick.ItemsSource = new[] { new DeviceChoice("Looking for cameras…", null) };
        CameraPick.SelectedIndex = 0;
        CameraPick.IsEnabled = false;
        _loading = false;
        RestartMeter();
        var cams = await DevicePickers.CamerasAsync();
        _loading = true;
        DevicePickers.FillCameras(CameraPick, cams, _options);
        CameraPick.IsEnabled = cams.Count > 0;
        _loading = false;
    }

    private void Inputs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _options);
        _options.Save();
    }

    private void Mic_Changed(object sender, SelectionChangedEventArgs e)
    {
        Inputs_Changed(sender, e);
        if (!_loading) RestartMeter();
    }

    /// <summary>A live level line under the microphone picker.</summary>
    private void RestartMeter()
    {
        StopMeter();
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

    private void StopMeter()
    {
        _meter?.Dispose();
        _meter = null;
        MicLevel.Width = 0;
    }

    private void Teleprompter_Click(object sender, RoutedEventArgs e) => TeleprompterWindow.Toggle();

    // ---------------------------------------------------------------- recording

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        DevicePickers.Read(CameraPick, MicPick, SpeakerPick, _options);
        _options.Save();
        StopMeter();
        _shell.Hide();
        try
        {
            System.Drawing.Rectangle? region = null;
            string label = "Full screen", icon = "";
            if (mode == "area")
            {
                var picker = new RegionSelectWindow();
                if (picker.ShowDialog() != true || picker.Selection is not { } sel) { BackHome(); return; }
                region = sel;
                (label, icon) = ("Custom area", "");
            }
            else if (mode == "window")
            {
                var picker = new WindowPickerWindow();
                if (picker.ShowDialog() != true || picker.Selection is not { } sel) { BackHome(); return; }
                region = sel;
                (label, icon) = (picker.PickedTitle ?? "Window", "");
            }

            // Get ready: outline, floating camera, inputs, Start.
            var prep = new PrepareBar(region, icon, label, _options);
            prep.Show();
            if (!await prep.Completion) { BackHome(); return; }

            var bar = new RecordingBar(ProjectStore.NewRecordingDir(), region, _options, label, prep.Bubble);
            bar.Show();
            string? projectFile = await bar.Completion;
            if (projectFile != null)
            {
                _shell.Show();
                _shell.Activate();
                _shell.ShowEditor(projectFile);
            }
            else BackHome();
        }
        catch (Exception ex)
        {
            BackHome();
            MessageBox.Show(_shell, "Recording failed: " + ex.Message, "Glidecast", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Back to the launcher after a cancelled or discarded take (inputs may have changed meanwhile).</summary>
    private void BackHome()
    {
        _shell.Show();
        _shell.Activate();
        _ = LoadInputsAsync();
    }

    // ---------------------------------------------------------------- projects

    /// <summary>Rebuilds takes that a crash or power loss interrupted, then refreshes the list.</summary>
    private async Task RecoverAsync()
    {
        var dirs = RecordingRecovery.FindUnfinished(ProjectStore.Root);
        if (dirs.Count == 0) return;
        RecoveredBanner.Visibility = Visibility.Visible;
        RecoveredText.Text = dirs.Count == 1 ? "Recovering an interrupted recording…" : $"Recovering {dirs.Count} interrupted recordings…";
        int ok = await Task.Run(() => dirs.Count(d =>
        {
            try { return RecordingRecovery.Recover(d) != null; } catch { return false; }
        }));
        RecoveredText.Text = ok == 0
            ? "An interrupted recording was found, but nothing usable had been saved."
            : ok == 1 ? "Recovered a recording interrupted by a crash (see Projects)."
            : $"Recovered {ok} recordings interrupted by a crash (see Projects).";
        ShowRecent();
    }

    private void ShowRecent()
    {
        var recent = ProjectStore.Recent();
        RecentList.ItemsSource = recent;
        NoRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentCount.Text = recent.Count == 0 ? "" : recent.Count.ToString();
        _ = ProjectStore.LoadDetailsAsync(recent);
    }

    private void Projects_Opened(object sender, EventArgs e) => ShowRecent();
    private void Projects_Closed(object sender, EventArgs e) => ProjectsToggle.IsChecked = false;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        ProjectsToggle.IsChecked = false;
        var dlg = new OpenFileDialog
        {
            Title = "Open Glidecast project",
            Filter = "Glidecast project (project.json)|project.json|JSON files (*.json)|*.json",
            InitialDirectory = ProjectStore.Root,
        };
        if (dlg.ShowDialog(_shell) == true) _shell.ShowEditor(dlg.FileName);
    }

    private void Recent_Open(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not ProjectStore.RecentItem item) return;
        ProjectsToggle.IsChecked = false;
        _shell.ShowEditor(item.ProjectFile);
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        ProjectsToggle.IsChecked = false;
        string dir = Path.Combine(ProjectStore.Root, "Demo project");
        Status.Text = "Generating a synthetic demo recording…";
        IsEnabled = false;
        try
        {
            await Task.Run(() => DemoGenerator.Generate(dir));
            _shell.ShowEditor(Path.Combine(dir, "project.json"));
        }
        catch (Exception ex)
        {
            Status.Text = "Demo failed: " + ex.Message;
            IsEnabled = true;
        }
    }
}
