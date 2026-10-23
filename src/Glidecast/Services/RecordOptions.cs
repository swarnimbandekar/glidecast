using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glidecast.Render;

namespace Glidecast.App.Services;

/// <summary>What to capture besides the screen, chosen in the launcher and
/// the pre-record bar. Remembered between launches.</summary>
public sealed class RecordOptions
{
    public bool SystemAudio { get; set; }
    /// <summary>Output device to record (loopback); null = the default one.</summary>
    public string? SpeakerId { get; set; }
    public bool Microphone { get; set; }
    /// <summary>WASAPI endpoint id; null = the default microphone.</summary>
    public string? MicrophoneId { get; set; }
    public bool Camera { get; set; }
    /// <summary>DirectShow device name.</summary>
    public string? CameraName { get; set; }
    public WebcamShape CameraShape { get; set; } = WebcamShape.Circle;
    public float CameraSize { get; set; } = 0.26f;
    public float CameraX { get; set; }
    public float CameraY { get; set; } = 1f;
    /// <summary>Number the clicks, box what was clicked and caption it when the take ends.</summary>
    public bool Annotate { get; set; }
    /// <summary>Seconds of countdown before capture starts (0, 3 or 5).</summary>
    public int Countdown { get; set; } = 3;
    public bool Fps60 { get; set; }

    private static string FilePath => Path.Combine(Glidecast.Model.LookLibrary.AppDataDir, "record.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RecordOptions Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<RecordOptions>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* unreadable: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience */ }
    }

    /// <summary>Applies the webcam placement to a new project's style.</summary>
    public void ApplyTo(StyleSettings st)
    {
        st.ShowWebcam = true;
        st.WebcamShape = CameraShape;
        st.WebcamSize = CameraSize;
        st.WebcamX = CameraX;
        st.WebcamY = CameraY;
    }
}
