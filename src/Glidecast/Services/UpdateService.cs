using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glidecast.App.Services;

/// <summary>Checks GitHub for a newer release and installs it. Installed copies
/// run the new installer silently (it relaunches the app); portable copies get
/// their folder replaced by a helper script once the app has exited. Every
/// download is checked against the release's SHA256SUMS.txt before it runs.</summary>
public static class UpdateService
{
    public const string Repo = "swarnimbandekar/glidecast";

    public sealed record Release(Version Version, string Tag, string Notes, string Url,
        string? InstallerUrl, string? InstallerName, string? PortableUrl, string? PortableName, string? SumsUrl);

    // Declared before Http: static initializers run in order.
    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Glidecast/{Current.ToString(3)}");
        return h;
    }

    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glidecast", "update.json");

    /// <summary>A version the user chose to skip; it isn't offered again.</summary>
    public static string? SkippedVersion
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
                return doc.RootElement.TryGetProperty("SkippedVersion", out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(new { SkippedVersion = value }));
            }
            catch { /* not worth failing over */ }
        }
    }

    /// <summary>Installed by the Inno Setup installer (vs. the portable zip).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The latest release if it's newer than this build and not skipped; otherwise null.
    /// GLIDECAST_NO_UPDATE_CHECK=1 turns checking off; GLIDECAST_UPDATE_URL points it at another feed.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("GLIDECAST_NO_UPDATE_CHECK") == "1") return null;
        string? custom = Environment.GetEnvironmentVariable("GLIDECAST_UPDATE_URL");
        Release? r;
        if (custom is { Length: > 0 })
        {
            r = Parse(custom.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await Http.GetStringAsync(custom, ct) : await File.ReadAllTextAsync(custom, ct));
        }
        else
        {
            try
            {
                r = Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
            }
            catch (HttpRequestException)
            {
                // GitHub's API allows only 60 anonymous requests an hour per
                // address (shared offices and VPNs use it up) and answers 403.
                // The web pages have no such limit, so read the release from those.
                r = await LatestFromWebAsync(ct);
            }
        }
        if (r == null || r.Version <= Current) return null;
        if (string.Equals(SkippedVersion, r.Version.ToString(3), StringComparison.Ordinal)) return null;
        return r;
    }

    // Never follows redirects: the redirect target is the answer.
    private static readonly HttpClient WebHttp = CreateWebClient();

    private static HttpClient CreateWebClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Glidecast/{Current.ToString(3)}");
        return h;
    }

    /// <summary>The latest release without the API: github.com/OWNER/REPO/releases/latest
    /// redirects to the newest tag, and our assets have predictable names.</summary>
    private static async Task<Release?> LatestFromWebAsync(CancellationToken ct)
    {
        using var resp = await WebHttp.GetAsync($"https://github.com/{Repo}/releases/latest", ct);
        string? loc = resp.Headers.Location?.ToString();
        if (loc == null || !loc.Contains("/releases/tag/")) return null;
        string tag = Uri.UnescapeDataString(loc[(loc.LastIndexOf('/') + 1)..]);
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        string ver = v.ToString(3), dl = $"https://github.com/{Repo}/releases/download/{tag}/";
        return new Release(v, tag, "", $"https://github.com/{Repo}/releases/tag/{tag}",
            dl + $"Glidecast-Setup-{ver}-x64.exe", $"Glidecast-Setup-{ver}-x64.exe",
            dl + $"Glidecast-{ver}-win-x64-portable.zip", $"Glidecast-{ver}-win-x64-portable.zip",
            dl + "SHA256SUMS.txt");
    }

    private static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));

        string? inst = null, instName = null, port = null, portName = null, sums = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith("Glidecast-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { inst = dl; instName = name; }
                else if (name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) { port = dl; portName = name; }
                else if (name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase)) sums = dl;
            }
        return new Release(version, tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases",
            inst, instName, port, portName, sums);
    }

    /// <summary>Downloads, verifies and launches the update. When this returns,
    /// the caller must exit the app so the files can be replaced.</summary>
    public static async Task InstallAsync(Release r, IProgress<double> progress, CancellationToken ct = default)
    {
        bool installed = IsInstalled;
        string? url = installed ? r.InstallerUrl : r.PortableUrl;
        string? name = installed ? r.InstallerName : r.PortableName;
        if (url == null || name == null) throw new InvalidOperationException("This release has no download for your copy of Glidecast.");
        if (r.SumsUrl == null) throw new InvalidOperationException("This release has no SHA256SUMS.txt, so the download can't be verified.");

        string dir = Path.Combine(Path.GetTempPath(), "Glidecast-update", r.Version.ToString(3));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, name);

        string sums = await Get(r.SumsUrl, ct);
        string expected = ExpectedHash(sums, name) ?? throw new InvalidOperationException($"SHA256SUMS.txt has no entry for {name}.");
        await Download(url, file, progress, ct);
        string actual;
        using (var fs = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new InvalidOperationException("The download didn't match its published checksum, so it wasn't installed.");
        }

        if (installed)
        {
            // /RELAUNCH is ours: the installer starts Glidecast again when it's done.
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1")
            { UseShellExecute = true });
        }
        else
        {
            string extracted = Path.Combine(dir, "files");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
            ZipFile.ExtractToDirectory(file, extracted);
            StartPortableSwap(extracted);
        }
    }

    private static async Task<string> Get(string url, CancellationToken ct) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await Http.GetStringAsync(url, ct) : await File.ReadAllTextAsync(url, ct);

    private static async Task Download(string url, string file, IProgress<double> progress, CancellationToken ct)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(url, file, true);
            progress.Report(1);
            return;
        }
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress.Report((double)done / total);
        }
        progress.Report(1);
    }

    /// <summary>Reads "hash  filename" lines (sha256sum or Get-FileHash style).</summary>
    private static string? ExpectedHash(string sums, string name)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(name, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                return parts[0];
        }
        return null;
    }

    /// <summary>Waits for this process to exit, copies the new files over the app folder, and relaunches.</summary>
    private static void StartPortableSwap(string extracted)
    {
        string app = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(app, "Glidecast.exe");
        string script = Path.Combine(Path.GetDirectoryName(extracted)!, "apply-update.ps1");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        File.WriteAllLines(script, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"try {{ Wait-Process -Id {Environment.ProcessId} -Timeout 60 }} catch {{ }}",
            "Start-Sleep -Milliseconds 500",
            $"Copy-Item -Path (Join-Path {Q(extracted)} '*') -Destination {Q(app)} -Recurse -Force",
            $"Start-Process {Q(exe)}",
        });
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        { UseShellExecute = false, CreateNoWindow = true });
    }
}
