using System.Text.RegularExpressions;
using System.Windows;
using Glidecast.App.Services;

namespace Glidecast.App.Views;

/// <summary>"A new version is available": Update installs it now, Skip ignores
/// this version, the close button asks again on the next launch.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService.Release _release;
    private bool _busy;

    public UpdateWindow(UpdateService.Release release)
    {
        _release = release;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this);
        Heading.Text = $"Glidecast {release.Version.ToString(3)} is available";
        Subheading.Text = $"You have {UpdateService.Current.ToString(3)}. The update keeps your projects and settings.";
        Notes.Text = PlainNotes(release.Notes);
        Closing += (_, e) => e.Cancel = _busy;
        ContentRendered += (_, _) => NotesScroll.ScrollToTop();
    }

    /// <summary>Release notes are Markdown; show them as readable plain text.</summary>
    private static string PlainNotes(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "Bug fixes and improvements.";
        var lines = md.Replace("\r", "").Split('\n')
            .Where(l => !Regex.IsMatch(l, @"^\s*!\[[^\]]*\]\([^)]*\)\s*$"))                      // images (the banner)
            .SkipWhile(l => string.IsNullOrWhiteSpace(l) || Regex.IsMatch(l, @"^#+\s*What's new", RegexOptions.IgnoreCase))
            .Select(l =>
        {
            l = Regex.Replace(l, @"^#+\s*", "");
            l = Regex.Replace(l, @"^\s*[-*]\s+", "•  ");
            l = Regex.Replace(l, @"\*\*(.+?)\*\*", "$1");
            l = Regex.Replace(l, @"`([^`]*)`", "$1");
            l = Regex.Replace(l, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            return l.TrimEnd();
        });
        return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        UpdateService.SkippedVersion = _release.Version.ToString(3);
        Close();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        UpdateButton.IsEnabled = SkipButton.IsEnabled = CloseButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Downloading…";
        var progress = new Progress<double>(f =>
        {
            Progress.Value = f;
            ProgressPct.Text = $"{f * 100:0}%";
        });
        try
        {
            await UpdateService.InstallAsync(_release, progress);
            ProgressText.Text = UpdateService.IsInstalled ? "Installing… Glidecast will reopen." : "Updating… Glidecast will reopen.";
            ProgressPct.Text = "";
            await Task.Delay(600);
            _busy = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _busy = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = "Couldn't update: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            UpdateLabel.Text = "Try again";
            UpdateButton.IsEnabled = SkipButton.IsEnabled = CloseButton.IsEnabled = true;
        }
    }
}
