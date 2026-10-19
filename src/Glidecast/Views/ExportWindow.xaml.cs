using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Glidecast.App.Services;
using Glidecast.Model;
using Glidecast.Render;
using Microsoft.Win32;

namespace Glidecast.App.Views;

/// <summary>Export options and progress; runs the engine's VideoRenderer.</summary>
public partial class ExportWindow : Window
{
    private readonly Project _p;
    private readonly List<(RadioButton Button, int W, int H)> _sizes = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public ExportWindow(Project p, string projectDir)
    {
        _p = p;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this);
        var len = TimeSpan.FromMilliseconds(p.DurationMs);
        int zooms = p.ZoomSegments?.Count ?? 0;
        Summary.Text = $"{(int)len.TotalMinutes}:{len.Seconds:00} long · {zooms} zoom{(zooms == 1 ? "" : "s")} · H.264 MP4";

        // Resolutions keep the recording's aspect ratio.
        double aspect = (double)p.Width / p.Height;
        AddSize($"Source", p.Width, p.Height, isDefault: true);
        foreach (int h in new[] { 2160, 1440, 1080, 720 })
        {
            if (h >= p.Height && h != 1080) continue;
            int w = (int)Math.Round(h * aspect) & ~1;
            if (w == (p.Width & ~1) && h == (p.Height & ~1)) continue;
            AddSize($"{h}p", w, h, isDefault: false);
        }

        (p.Fps >= 50 ? Fps60 : Fps30).IsChecked = true;
        OutPath.Text = Path.Combine(projectDir, $"Glidecast {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        Closing += (_, e) =>
        {
            if (!_running) return;
            if (MessageBox.Show(this, "Stop the export in progress?", "Export", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                _cts?.Cancel();
            e.Cancel = true;
        };
    }

    private void AddSize(string label, int w, int h, bool isDefault)
    {
        var rb = new RadioButton
        {
            Content = label,
            GroupName = "res",
            Style = (Style)FindResource("Segment"),
            IsChecked = isDefault,
        };
        rb.Checked += (_, _) => ResInfo.Text = $"{w & ~1} × {h & ~1} px";
        if (isDefault) ResInfo.Text = $"{w & ~1} × {h & ~1} px";
        ResPanel.Children.Add(rb);
        _sizes.Add((rb, w, h));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileName(OutPath.Text),
            InitialDirectory = Path.GetDirectoryName(OutPath.Text),
        };
        if (dlg.ShowDialog(this) == true) OutPath.Text = dlg.FileName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var size = _sizes.First(s => s.Button.IsChecked == true);
        var opts = new ExportOptions
        {
            Width = size.W,
            Height = size.H,
            Fps = Fps24.IsChecked == true ? 24 : Fps60.IsChecked == true ? 60 : 30,
            Quality = QDraft.IsChecked == true ? ExportQuality.Draft : QHigh.IsChecked == true ? ExportQuality.High : ExportQuality.Standard,
        };
        string outPath = OutPath.Text.Trim();
        if (string.IsNullOrEmpty(outPath)) return;
        if (!outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outPath += ".mp4";

        _cts = new CancellationTokenSource();
        _running = true;
        ExportButton.IsEnabled = false;
        OpenFolderButton.Visibility = PlayButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        Options.IsEnabled = false;
        ProgressTitle.Text = "Rendering";
        ProgressPct.Foreground = (System.Windows.Media.Brush)FindResource("Text");
        CancelButton.Content = "Stop";
        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(f =>
        {
            Progress.Value = f;
            ProgressPct.Text = $"{f * 100:0}%";
            ProgressText.Text = f > 0.02 ? $"About {TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - f) / f):m\\:ss} left" : "Starting…";
        });

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            var token = _cts.Token;
            await Task.Run(() => VideoRenderer.Render(_p, outPath, style: _p.Style,
                progress: f => ((IProgress<double>)progress).Report(f), options: opts, cancel: token));
            Progress.Value = 1;
            ProgressPct.Text = "✓";
            ProgressPct.Foreground = (System.Windows.Media.Brush)FindResource("Success");
            ProgressTitle.Text = "Export complete";
            ProgressText.Text = $"Done in {sw.Elapsed:m\\:ss} — {Path.GetFileName(outPath)} ({new FileInfo(outPath).Length / 1048576.0:0.0} MB)";
            OutPath.Text = outPath;
            OpenFolderButton.Visibility = PlayButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            ProgressTitle.Text = "Export stopped";
            ProgressText.Text = "The partial file was removed.";
        }
        catch (Exception ex)
        {
            ProgressTitle.Text = "Export failed";
            ProgressText.Text = ex.Message;
        }
        finally
        {
            _running = false;
            Options.IsEnabled = true;
            ExportButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running) _cts?.Cancel();
        else Close();
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"/select,\"{OutPath.Text}\"");

    private void PlayVideo_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(OutPath.Text) { UseShellExecute = true });
}
