using System.IO;
using System.Windows;
using System.Windows.Controls;
using Glidecast.App.Services;
using Glidecast.Capture;
using Glidecast.Demo;
using Microsoft.Win32;

namespace Glidecast.App.Views;

/// <summary>Start screen: choose full screen or a region, record, or open a project.</summary>
public partial class HomeView : UserControl
{
    private readonly MainWindow _shell;

    public HomeView(MainWindow shell)
    {
        _shell = shell;
        InitializeComponent();
        ShowRecent();
        SaveFolder.Text = ProjectStore.UsingFallback
            ? $"Documents isn't writable for this app (Controlled Folder Access?), so recordings are saved to {ProjectStore.Root}"
            : $"Recordings are saved to {ProjectStore.Root}";
        _ = RecoverAsync();
        // Keep the hero pinned to the top (focus would otherwise scroll it).
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => HeroScroll.ScrollToTop(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Rebuilds takes that a crash or power loss interrupted, then refreshes the list.</summary>
    private async Task RecoverAsync()
    {
        var dirs = RecordingRecovery.FindUnfinished(ProjectStore.Root);
        if (dirs.Count == 0) return;
        RecoveredBanner.Visibility = Visibility.Visible;
        RecoveredText.Text = dirs.Count == 1 ? "Recovering a recording that was interrupted…" : $"Recovering {dirs.Count} interrupted recordings…";
        int ok = await Task.Run(() => dirs.Count(d =>
        {
            try { return RecordingRecovery.Recover(d) != null; } catch { return false; }
        }));
        RecoveredText.Text = ok == 0
            ? "An interrupted recording was found, but nothing usable had been saved."
            : ok == 1 ? "Recovered a recording that was interrupted by a crash. It's in your recent list."
            : $"Recovered {ok} recordings that were interrupted by a crash. They're in your recent list.";
        ShowRecent();
    }

    private void ShowRecent()
    {
        var recent = ProjectStore.Recent();
        RecentList.ItemsSource = recent;
        NoRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentCount.Text = recent.Count.ToString();
        RecentCountTag.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = ProjectStore.LoadDetailsAsync(recent);
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        System.Drawing.Rectangle? region = null;
        _shell.Hide();
        try
        {
            if (RegionCard.IsChecked == true)
            {
                var picker = new RegionSelectWindow();
                if (picker.ShowDialog() != true || picker.Selection is not { } sel)
                {
                    _shell.Show();
                    return;
                }
                region = sel;
            }

            double fps = Fps60.IsChecked == true ? 60 : 30;
            var bar = new RecordingBar(ProjectStore.NewRecordingDir(), region, fps);
            bar.Show();
            string? projectFile = await bar.Completion;
            _shell.Show();
            _shell.Activate();
            if (projectFile != null) _shell.ShowEditor(projectFile);
        }
        catch (Exception ex)
        {
            _shell.Show();
            MessageBox.Show(_shell, "Recording failed: " + ex.Message, "Glidecast",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
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
        if (RecentList.SelectedItem is ProjectStore.RecentItem item) _shell.ShowEditor(item.ProjectFile);
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
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
