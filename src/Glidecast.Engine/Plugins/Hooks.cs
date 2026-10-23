using System.Diagnostics;
using System.Text.Json;
using Glidecast.Model;

namespace Glidecast.Plugins;

/// <summary>
/// Plugin hooks: commands the user configures to run after Glidecast events,
/// e.g. upload every export, transcode, post to chat, or rename files.
///
/// %APPDATA%\Glidecast\hooks.json (GLIDECAST_HOOKS overrides the path):
/// <code>
/// {
///   "afterRecord": [ "powershell -File C:\\scripts\\backup.ps1 \"{project}\"" ],
///   "afterExport": [ "C:\\tools\\upload.exe \"{output}\"" ]
/// }
/// </code>
/// {project} is the project folder and {output} the exported file. Commands
/// also get GLIDECAST_EVENT, GLIDECAST_PROJECT and GLIDECAST_OUTPUT in their
/// environment. They run in the background through cmd.exe; their output is
/// appended to %LOCALAPPDATA%\Glidecast\hooks.log.
/// </summary>
public static class Hooks
{
    public const string AfterRecord = "afterRecord";
    public const string AfterExport = "afterExport";

    public static string ConfigPath => Environment.GetEnvironmentVariable("GLIDECAST_HOOKS") is { Length: > 0 } p
        ? p : Path.Combine(LookLibrary.AppDataDir, "hooks.json");

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glidecast", "hooks.log");

    /// <summary>The commands configured for an event (empty when there's no config).</summary>
    public static List<string> CommandsFor(string evt)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty(evt, out var arr) || arr.ValueKind != JsonValueKind.Array) return new();
            return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        catch (Exception ex)
        {
            Log($"hooks.json could not be read: {ex.Message}");
            return new();
        }
    }

    /// <summary>Starts every command for <paramref name="evt"/> without waiting.
    /// Returns the started processes (callers may wait, e.g. the CLI).</summary>
    public static List<Process> Fire(string evt, string? project, string? output = null)
    {
        var started = new List<Process>();
        foreach (var raw in CommandsFor(evt))
        {
            string cmd = raw.Replace("{project}", project ?? "").Replace("{output}", output ?? "");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + cmd + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = project != null && Directory.Exists(project) ? project : Environment.CurrentDirectory,
                };
                psi.Environment["GLIDECAST_EVENT"] = evt;
                psi.Environment["GLIDECAST_PROJECT"] = project ?? "";
                psi.Environment["GLIDECAST_OUTPUT"] = output ?? "";
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => { if (e.Data != null) Log($"[{evt}] {e.Data}"); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[{evt}] ! {e.Data}"); };
                p.Exited += (_, _) => { try { Log($"[{evt}] exit {p.ExitCode}: {cmd}"); } catch { } };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                Log($"[{evt}] start: {cmd}");
                started.Add(p);
            }
            catch (Exception ex)
            {
                Log($"[{evt}] failed to start '{cmd}': {ex.Message}");
            }
        }
        return started;
    }

    private static readonly object LogGate = new();

    private static void Log(string line)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
