using System.Diagnostics;

namespace Glidecast.Render;

/// <summary>Locates an ffmpeg executable and starts piped processes.</summary>
public static class FFmpeg
{
    private static string? _cached;

    public static string Path
    {
        get
        {
            if (_cached != null) return _cached;
            // 1) Explicit override.
            var env = Environment.GetEnvironmentVariable("GLIDECAST_FFMPEG");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return _cached = env;

            // 2) On PATH.
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var cand = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(cand)) return _cached = cand;
                }
                catch { /* malformed PATH entry */ }
            }

            // 3) Next to the app, then winget's install locations (PATH may be
            //    stale right after `winget install` until the user signs out).
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var guesses = new List<string>
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                System.IO.Path.Combine(local, @"Microsoft\WinGet\Links\ffmpeg.exe"),
            };
            try
            {
                string pkgs = System.IO.Path.Combine(local, @"Microsoft\WinGet\Packages");
                if (Directory.Exists(pkgs))
                    foreach (var dir in Directory.EnumerateDirectories(pkgs, "Gyan.FFmpeg*"))
                        guesses.AddRange(Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories));
            }
            catch { /* best effort */ }
            foreach (var g in guesses)
                if (File.Exists(g)) return _cached = g;

            throw new FileNotFoundException(
                "ffmpeg.exe not found. Install it (winget install Gyan.FFmpeg.Essentials), put it on PATH, " +
                "or set GLIDECAST_FFMPEG to its full path.");
        }
    }

    /// <summary>True if an ffmpeg executable can be located.</summary>
    public static bool IsAvailable()
    {
        try { _ = Path; return true; }
        catch { return false; }
    }

    /// <summary>Forget a cached lookup (e.g. after installing ffmpeg).</summary>
    public static void Reset() => _cached = null;

    public static Process Start(string args, bool redirectIn, bool redirectOut)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = redirectIn,
            RedirectStandardOutput = redirectOut,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8, // device names
            CreateNoWindow = true,
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        // Drain stderr so the pipe never blocks; keep the last lines for diagnostics.
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) LastError = e.Data; };
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>Runs ffmpeg to completion and returns everything it printed to stderr.</summary>
    public static string Run(string args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return err.Wait(timeoutMs) ? err.Result : "";
    }

    /// <summary>Exact number of video frames in a file (stream copy, so it's fast).</summary>
    public static int CountFrames(string video)
    {
        string log = Run($"-hide_banner -i \"{video}\" -map 0:v:0 -c copy -stats -f null -");
        var m = System.Text.RegularExpressions.Regex.Matches(log, @"frame=\s*(\d+)");
        return m.Count > 0 ? int.Parse(m[^1].Groups[1].Value) : 0;
    }

    public static volatile string? LastError;
}
