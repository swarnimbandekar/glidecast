using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Glidecast.App.Services;
using Glidecast.Model;
using Glidecast.Render;
using Canvas = Glidecast.Render.Canvas;
using Microsoft.Win32;

namespace Glidecast.App.Views;

/// <summary>Export options and progress; runs the engine's VideoRenderer.</summary>
public partial class ExportWindow : Window
{
    private readonly Project _p;
    private readonly List<(RadioButton Button, int ShortSide)> _sizes = new();
    private readonly List<(RadioButton Button, CanvasAspect? Aspect)> _aspects = new(); // null = all three
    private VideoEncoder? _gpu;
    private CancellationTokenSource? _cts;
    private bool _running;

    private static readonly CanvasAspect[] AllShapes = { CanvasAspect.Wide, CanvasAspect.Vertical, CanvasAspect.Square };

    public ExportWindow(Project p, string projectDir)
    {
        _p = p;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.StyleWindow(this);
        var len = TimeSpan.FromMilliseconds(p.DurationMs);
        int zooms = p.ZoomSegments?.Count ?? 0;
        Summary.Text = $"{(int)len.TotalMinutes}:{len.Seconds:00} long · {zooms} zoom{(zooms == 1 ? "" : "s")} · H.264 MP4";

        var current = p.Style?.Aspect ?? CanvasAspect.Source;
        foreach (var a in Canvas.All) AddAspect(Canvas.Label(a), a, a == current);
        AddAspect("All 3", null, false);

        // Sizes are the output's short side, so they work for every canvas shape.
        int srcShort = Math.Min(p.Width, p.Height) & ~1;
        AddSize("Source", 0, isDefault: true);
        foreach (int h in new[] { 2160, 1440, 1080, 720 })
        {
            if (h >= srcShort && h != 1080) continue;
            if (h == srcShort) continue;
            AddSize($"{h}p", h, isDefault: false);
        }
        UpdateInfo();

        (p.Fps >= 50 ? Fps60 : Fps30).IsChecked = true;
        OutPath.Text = Path.Combine(projectDir, $"Glidecast {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        Closing += (_, e) =>
        {
            if (!_running) return;
            if (MessageBox.Show(this, "Stop the export in progress?", "Export", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                _cts?.Cancel();
            e.Cancel = true;
        };
        Loaded += async (_, _) => await ProbeEncodersAsync();
    }

    private async Task ProbeEncodersAsync()
    {
        var (hw, auto) = await Task.Run(() =>
        {
            try { return (Encoders.AvailableHardware(), Encoders.Resolve(VideoEncoder.Auto)); }
            catch { return (new List<VideoEncoder>(), VideoEncoder.Cpu); }
        });
        if (hw.Count > 0)
        {
            _gpu = hw[0];
            EncGpu.IsEnabled = true;
            EncGpu.Content = Encoders.Label(hw[0]);
            EncInfo.Text = auto == VideoEncoder.Cpu
                ? $"Auto uses the CPU (x264). {Encoders.Label(hw[0])} is available too, but with Glidecast's compositing it usually isn't faster."
                : $"Auto uses {Encoders.Label(auto)}, falling back to the CPU if the GPU refuses.";
        }
        else
        {
            EncGpu.Visibility = Visibility.Collapsed;
            EncInfo.Text = "No GPU encoder found; exporting on the CPU (x264).";
        }
    }

    private void AddAspect(string label, CanvasAspect? aspect, bool isDefault)
    {
        var rb = new RadioButton { Content = label, GroupName = "aspect", Style = (Style)FindResource("Segment"), IsChecked = isDefault };
        rb.Checked += (_, _) => UpdateInfo();
        AspectPanel.Children.Add(rb);
        _aspects.Add((rb, aspect));
    }

    private void AddSize(string label, int shortSide, bool isDefault)
    {
        var rb = new RadioButton
        {
            Content = label,
            GroupName = "res",
            Style = (Style)FindResource("Segment"),
            IsChecked = isDefault,
        };
        rb.Checked += (_, _) => UpdateInfo();
        ResPanel.Children.Add(rb);
        _sizes.Add((rb, shortSide));
    }

    private List<CanvasAspect> SelectedShapes()
    {
        var a = _aspects.FirstOrDefault(x => x.Button.IsChecked == true);
        if (a.Button == null) return new() { CanvasAspect.Source };
        return a.Aspect is { } one ? new() { one } : AllShapes.ToList();
    }

    private int SelectedShortSide() => _sizes.FirstOrDefault(s => s.Button.IsChecked == true).ShortSide;

    private void UpdateInfo()
    {
        if (ResInfo == null || AspectInfo == null || _sizes.Count == 0) return;
        var shapes = SelectedShapes();
        int side = SelectedShortSide();
        ResInfo.Text = string.Join("   ·   ", shapes.Select(a =>
        {
            var (w, h) = Canvas.OutputSize(a, _p.Width, _p.Height, side);
            return shapes.Count > 1 ? $"{Canvas.Label(a)} {w} × {h}" : $"{w} × {h} px";
        }));
        AspectInfo.Text = shapes.Count > 1
            ? "Three files: 16:9, 9:16 and 1:1. The camera is re-planned for each shape so the action stays in frame."
            : shapes[0] switch
            {
                CanvasAspect.Source => "The recording's own shape.",
                CanvasAspect.Vertical => "Vertical for Shorts, Reels and TikTok. The camera pans to keep the action in frame.",
                _ => $"{Canvas.Label(shapes[0])}. The camera reframes the recording to fit this shape.",
            };
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
        var shapes = SelectedShapes();
        int side = SelectedShortSide();
        var opts = new ExportOptions
        {
            Fps = Fps24.IsChecked == true ? 24 : Fps60.IsChecked == true ? 60 : 30,
            Quality = QDraft.IsChecked == true ? ExportQuality.Draft : QHigh.IsChecked == true ? ExportQuality.High : ExportQuality.Standard,
            Encoder = EncCpu.IsChecked == true ? VideoEncoder.Cpu : EncGpu.IsChecked == true && _gpu is { } g ? g : VideoEncoder.Auto,
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
        ProgressTitle.Text = shapes.Count > 1 ? $"Rendering {shapes.Count} formats" : "Rendering";
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
            void Report(double f) => ((IProgress<double>)progress).Report(f);
            List<string> outputs;
            VideoEncoder used;
            if (shapes.Count > 1)
            {
                if (side > 0) opts.Width = opts.Height = side;
                outputs = await Task.Run(() => VideoRenderer.RenderAll(_p, outPath, shapes, opts, Report, token));
                used = Encoders.Resolve(opts.Encoder);
            }
            else
            {
                opts.Aspect = shapes[0];
                if (side > 0) (opts.Width, opts.Height) = Canvas.OutputSize(shapes[0], _p.Width, _p.Height, side);
                used = await Task.Run(() => VideoRenderer.Render(_p, outPath, style: _p.Style, progress: Report, options: opts, cancel: token));
                outputs = new List<string> { outPath };
            }
            Progress.Value = 1;
            ProgressPct.Text = "✓";
            ProgressPct.Foreground = (System.Windows.Media.Brush)FindResource("Success");
            ProgressTitle.Text = "Export complete";
            double mb = outputs.Sum(o => new FileInfo(o).Length) / 1048576.0;
            ProgressText.Text = outputs.Count > 1
                ? $"Done in {sw.Elapsed:m\\:ss} with {Encoders.Label(used)}: {outputs.Count} files ({mb:0.0} MB)"
                : $"Done in {sw.Elapsed:m\\:ss} with {Encoders.Label(used)}: {Path.GetFileName(outputs[0])} ({mb:0.0} MB)";
            OutPath.Text = outputs[0];
            OpenFolderButton.Visibility = PlayButton.Visibility = Visibility.Visible;
            string? project = Path.GetDirectoryName(_p.SourceVideo);
            foreach (var o in outputs) Glidecast.Plugins.Hooks.Fire(Glidecast.Plugins.Hooks.AfterExport, project, o);
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
