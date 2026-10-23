using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>
/// A floating script that scrolls by itself at a chosen speed, so you can read
/// while you record. It stays on top of everything and is kept out of the
/// capture. The script and settings are remembered between sessions.
/// </summary>
public partial class TeleprompterWindow : Window
{
    private static TeleprompterWindow? _open;
    private static string ScriptFile => Path.Combine(Glidecast.Model.LookLibrary.AppDataDir, "teleprompter.txt");

    private bool _playing;
    private TimeSpan _last;

    /// <summary>Shows the teleprompter, or closes it if it is open.</summary>
    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        _open = new TeleprompterWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public TeleprompterWindow()
    {
        InitializeComponent();
        string text = "";
        try { if (File.Exists(ScriptFile)) text = File.ReadAllText(ScriptFile); } catch { }
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Paste or type your script here.\n\nPress play and it scrolls at your reading speed. It stays on top, and it never shows up in the recording.";
            EditToggle.IsChecked = true;
        }
        Editor.Text = text;
        Script.Text = text;
        ApplyMode();
        Setting_Changed(this, new RoutedEventArgs());

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + 90;
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && EditToggle.IsChecked != true) { Play_Click(this, e); e.Handled = true; }
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= Tick;
            Save();
        };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptFile)!);
            File.WriteAllText(ScriptFile, Editor.Text);
        }
        catch { }
    }

    private void ApplyMode()
    {
        bool edit = EditToggle.IsChecked == true;
        Editor.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        Reader.Visibility = Marker.Visibility = edit ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = !edit;
        if (edit) { SetPlaying(false); Editor.Focus(); }
        else { Script.Text = Editor.Text; Save(); }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => ApplyMode();

    private void Play_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

    private void Restart_Click(object sender, RoutedEventArgs e) => Reader.ScrollToVerticalOffset(0);

    private void SetPlaying(bool on)
    {
        if (on == _playing) return;
        _playing = on;
        PlayButton.Content = on ? "" : "";
        _last = TimeSpan.Zero;
        if (on) CompositionTarget.Rendering += Tick;
        else CompositionTarget.Rendering -= Tick;
    }

    // Smooth scrolling at "speed" pixels per second, once per rendered frame.
    private void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last != TimeSpan.Zero)
        {
            double dt = (now - _last).TotalSeconds;
            Reader.ScrollToVerticalOffset(Reader.VerticalOffset + SpeedSlider.Value * dt);
            if (Reader.VerticalOffset >= Reader.ScrollableHeight) SetPlaying(false);
        }
        _last = now;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (Script == null || Fill == null || OpacitySlider == null) return;
        Script.FontSize = SizeSlider.Value;
        Fill.Opacity = OpacitySlider.Value;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }
}
