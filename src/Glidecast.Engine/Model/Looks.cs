using System.Text.Json;
using System.Text.Json.Serialization;
using Glidecast.AutoZoom;
using Glidecast.Render;

namespace Glidecast.Model;

/// <summary>
/// A saved look (a brand kit): background, frame, cursor, accent, logo and
/// canvas shape, plus optionally the zoom feel. Applying it to a project is
/// one click; one look can be the default for every new recording.
/// </summary>
public sealed class Look
{
    public string Name { get; set; } = "Untitled";
    public StyleSettings Style { get; set; } = new();
    /// <summary>Zoom tuning to apply too; null leaves the project's zoom alone.</summary>
    public ZoomSettings? Zoom { get; set; }
    /// <summary>Also generate step annotations when applied.</summary>
    public bool Steps { get; set; }

    [JsonIgnore] public bool BuiltIn { get; init; }
    [JsonIgnore] public string? FilePath { get; init; }
}

/// <summary>Built-in looks plus the user's own, stored as JSON files in
/// %APPDATA%\Glidecast\looks (GLIDECAST_LOOKS overrides), so they can be
/// shared, versioned or dropped in by hand.</summary>
public static class LookLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glidecast");

    public static string Dir => Environment.GetEnvironmentVariable("GLIDECAST_LOOKS") is { Length: > 0 } d
        ? d : Path.Combine(AppDataDir, "looks");

    private static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    public static IReadOnlyList<Look> BuiltIns { get; } = new[]
    {
        new Look { Name = "Glidecast", BuiltIn = true, Style = new StyleSettings() },
        new Look
        {
            Name = "Clean light", BuiltIn = true,
            Style = new StyleSettings
            {
                Background = BackgroundKind.Solid, BgSolid = "#EEF0F4", PaddingFraction = 0.06f, CornerRadius = 14,
                ShadowBlur = 28, Accent = "#2E5BFF", CursorStyle = CursorStyle.Classic, ClickEffect = ClickEffect.Pulse,
            },
        },
        new Look
        {
            Name = "Midnight pro", BuiltIn = true,
            Style = new StyleSettings
            {
                BgTop = "#1E293B", BgBottom = "#0B1018", PaddingFraction = 0.08f, CornerRadius = 20, ShadowBlur = 60,
                Accent = "#4F8CFF", CursorStyle = CursorStyle.Midnight, MotionBlur = true,
            },
        },
        new Look
        {
            Name = "Vivid", BuiltIn = true,
            Style = new StyleSettings
            {
                BgTop = "#FF8A5B", BgBottom = "#B0306A", PaddingFraction = 0.09f, CornerRadius = 26, ShadowBlur = 50,
                Accent = "#FFB020", CursorStyle = CursorStyle.Soft, CursorScale = 2.0f, ClickEffect = ClickEffect.Burst, CursorTrail = true,
            },
        },
        new Look
        {
            Name = "Tutorial", BuiltIn = true, Steps = true,
            Style = new StyleSettings
            {
                BgTop = "#1A8FE3", BgBottom = "#0B2A5B", Accent = "#2FD195", CursorScale = 2.0f, HideIdleCursor = true,
            },
            Zoom = ZoomSettings.For(ZoomPreset.Tutorial),
        },
        new Look
        {
            Name = "Shorts", BuiltIn = true,
            Style = new StyleSettings
            {
                Aspect = CanvasAspect.Vertical, BgTop = "#DA22FF", BgBottom = "#9733EE", PaddingFraction = 0.04f,
                CornerRadius = 22, Accent = "#FFFFFF", CursorScale = 2.3f, CursorTrail = true, ClickEffect = ClickEffect.Burst,
            },
            Zoom = ZoomSettings.For(ZoomPreset.Dramatic),
        },
    };

    /// <summary>Built-ins first, then saved looks by name. A saved look with a
    /// built-in's name replaces it.</summary>
    public static List<Look> All()
    {
        var saved = Saved();
        var names = new HashSet<string>(saved.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        return BuiltIns.Where(b => !names.Contains(b.Name)).Concat(saved).ToList();
    }

    public static List<Look> Saved()
    {
        var list = new List<Look>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var l = JsonSerializer.Deserialize<Look>(File.ReadAllText(f), Json);
                    if (l != null) list.Add(new Look { Name = l.Name, Style = l.Style, Zoom = l.Zoom, Steps = l.Steps, FilePath = f });
                }
                catch { /* skip a malformed file */ }
            }
        }
        catch { }
        return list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A look by name, or a path to a look .json file.</summary>
    public static Look? Find(string nameOrPath)
    {
        if (nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(nameOrPath))
            return JsonSerializer.Deserialize<Look>(File.ReadAllText(nameOrPath), Json);
        return All().FirstOrDefault(l => string.Equals(l.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
    }

    public static string Save(Look look)
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, SafeFileName(look.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(look, Json));
        return path;
    }

    public static void Delete(string name)
    {
        foreach (var l in Saved().Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            if (l.FilePath != null) File.Delete(l.FilePath);
        if (string.Equals(DefaultName, name, StringComparison.OrdinalIgnoreCase)) DefaultName = null;
    }

    /// <summary>A look made from a project's current style and zoom.</summary>
    public static Look FromProject(Project p, string name, bool includeZoom) => new()
    {
        Name = name,
        Style = (p.Style ?? new StyleSettings()).Clone(),
        Zoom = includeZoom ? p.Zoom?.Clone() : null,
    };

    /// <summary>Applies a look. With zoom settings, an untouched zoom lane is re-authored to match.</summary>
    public static void Apply(Project p, Look look)
    {
        var before = p.Style;
        p.Style = look.Style.Clone();
        // Where the webcam sits belongs to the take, not the look.
        if (before != null)
        {
            p.Style.ShowWebcam = before.ShowWebcam;
            p.Style.WebcamShape = before.WebcamShape;
            p.Style.WebcamSize = before.WebcamSize;
            p.Style.WebcamX = before.WebcamX;
            p.Style.WebcamY = before.WebcamY;
            p.Style.WebcamMirror = before.WebcamMirror;
        }
        if (look.Zoom != null)
        {
            var keep = p.Zoom;
            p.Zoom = look.Zoom.Clone();
            if (keep != null)
            {
                p.Zoom.SmartFraming = keep.SmartFraming;
                p.Zoom.FollowTyping = keep.FollowTyping;
            }
            p.ZoomSegments = ZoomAuthor.BuildSegments(p, p.Zoom);
        }
        if (look.Steps && !(p.Annotations?.Any(a => a.Auto) ?? false))
            AnnotationAuthor.Regenerate(p);
    }

    /// <summary>The look applied to new recordings (null = Glidecast defaults).</summary>
    public static string? DefaultName
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
                return doc.RootElement.TryGetProperty("DefaultLook", out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }
        set
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { DefaultLook = value }, Json));
        }
    }

    /// <summary>Applies the default look, if any, to a freshly recorded project.</summary>
    public static void ApplyDefault(Project p)
    {
        if (DefaultName is { } n && Find(n) is { } look) Apply(p, look);
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string s = new(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(s) ? "look" : s.Trim();
    }
}
