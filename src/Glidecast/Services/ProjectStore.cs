using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glidecast.Render;

namespace Glidecast.App.Services;

/// <summary>Where recordings live and the recent list.</summary>
public static class ProjectStore
{
    private static string? _root;

    /// <summary>Documents\Glidecast, unless that isn't writable (e.g. Windows
    /// Controlled Folder Access blocks unknown apps from Documents), in which
    /// case %LOCALAPPDATA%\Glidecast\Recordings. GLIDECAST_PROJECTS overrides both.</summary>
    public static string Root => _root ??= PickRoot();

    /// <summary>True when Documents was blocked and the fallback folder is in use.</summary>
    public static bool UsingFallback { get; private set; }

    private static string PickRoot()
    {
        if (Environment.GetEnvironmentVariable("GLIDECAST_PROJECTS") is { Length: > 0 } custom)
        {
            Directory.CreateDirectory(custom);
            return custom;
        }
        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glidecast");
        if (IsWritable(docs)) return docs;
        UsingFallback = true;
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glidecast", "Recordings");
        Directory.CreateDirectory(local);
        return local;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static string NewRecordingDir() =>
        Path.Combine(Root, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));

    public sealed class RecentItem : INotifyPropertyChanged
    {
        public required string Name { get; init; }
        public required string ProjectFile { get; init; }
        public required DateTime Modified { get; init; }
        public string When => Modified.ToString("MMM d · HH:mm");

        private ImageSource? _thumb;
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; Changed(nameof(Thumb)); } }

        private string _length = "";
        public string Length { get => _length; set { _length = value; Changed(nameof(Length)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static List<RecentItem> Recent(int max = 12)
    {
        try
        {
            return Directory.EnumerateDirectories(Root)
                .Select(d => Path.Combine(d, "project.json"))
                .Where(File.Exists)
                .Select(f => new RecentItem { Name = Path.GetFileName(Path.GetDirectoryName(f))!, ProjectFile = f, Modified = File.GetLastWriteTime(f) })
                .OrderByDescending(r => r.Modified)
                .Take(max)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Fills in thumbnails (cached as thumb.jpg) and durations in the background.</summary>
    public static async Task LoadDetailsAsync(IEnumerable<RecentItem> items)
    {
        foreach (var item in items)
        {
            var (thumbPath, length) = await Task.Run(() => Details(item.ProjectFile));
            item.Length = length;
            if (thumbPath != null && File.Exists(thumbPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                    bmp.UriSource = new Uri(thumbPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    item.Thumb = bmp;
                }
                catch { }
            }
        }
    }

    private static (string? thumb, string length) Details(string projectFile)
    {
        string dir = Path.GetDirectoryName(projectFile)!;
        string length = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(projectFile));
            if (doc.RootElement.TryGetProperty("DurationMs", out var d))
            {
                var ts = TimeSpan.FromMilliseconds(d.GetDouble());
                length = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
            }
        }
        catch { }

        string thumb = Path.Combine(dir, "thumb.jpg");
        string src = Path.Combine(dir, "source.mp4");
        if (!File.Exists(thumb) && File.Exists(src))
        {
            try
            {
                var p = FFmpeg.Start($"-y -ss 0.8 -i \"{src}\" -frames:v 1 -vf scale=320:-2 \"{thumb}\"", redirectIn: false, redirectOut: false);
                p.WaitForExit(8000);
            }
            catch { }
        }
        return (File.Exists(thumb) ? thumb : null, length);
    }
}
