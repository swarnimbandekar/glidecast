using System.Windows.Controls;
using Glidecast.Capture;

namespace Glidecast.App.Services;

/// <summary>An entry in a device picker; a null Id means "off".</summary>
public sealed record DeviceChoice(string Label, string? Id)
{
    public override string ToString() => Label;
}

/// <summary>
/// Fills the camera / microphone / system-sound pickers shared by the
/// launcher and the pre-record bar. Each list starts with an "off" entry, so
/// choosing a device is also how a source is turned on.
/// </summary>
public static class DevicePickers
{
    private static List<string>? _cameras;
    private static Task<List<string>>? _loading;

    /// <summary>Webcams (asking ffmpeg takes a moment, so the answer is cached).</summary>
    public static Task<List<string>> CamerasAsync(bool refresh = false)
    {
        if (refresh) { _cameras = null; _loading = null; }
        if (_cameras != null) return Task.FromResult(_cameras);
        return _loading ??= Task.Run(() => _cameras = CameraDevices.List());
    }

    public static void FillCameras(ComboBox box, IReadOnlyList<string> cams, RecordOptions o)
    {
        var items = new List<DeviceChoice> { new(cams.Count == 0 ? "No camera found" : "No camera", null) };
        items.AddRange(cams.Select(c => new DeviceChoice(c, c)));
        box.ItemsSource = items;
        box.SelectedItem = o.Camera ? items.FirstOrDefault(i => i.Id != null && i.Id == o.CameraName) ?? items.ElementAtOrDefault(1) ?? items[0] : items[0];
    }

    public static void FillMicrophones(ComboBox box, RecordOptions o)
    {
        var items = new List<DeviceChoice> { new("No microphone", null) };
        string? def = AudioDevices.DefaultMicrophone();
        items.AddRange(AudioDevices.Microphones().Select(d => new DeviceChoice(d.Id == def ? d.Name + " (default)" : d.Name, d.Id)));
        box.ItemsSource = items;
        string? want = o.MicrophoneId ?? def;
        box.SelectedItem = o.Microphone ? items.FirstOrDefault(i => i.Id != null && i.Id == want) ?? items.ElementAtOrDefault(1) ?? items[0] : items[0];
    }

    public static void FillSpeakers(ComboBox box, RecordOptions o)
    {
        var items = new List<DeviceChoice> { new("No system sound", null) };
        string? def = AudioDevices.DefaultSpeaker();
        items.AddRange(AudioDevices.Speakers().Select(d => new DeviceChoice(d.Id == def ? d.Name + " (default)" : d.Name, d.Id)));
        box.ItemsSource = items;
        string? want = o.SpeakerId ?? def;
        box.SelectedItem = o.SystemAudio ? items.FirstOrDefault(i => i.Id != null && i.Id == want) ?? items.ElementAtOrDefault(1) ?? items[0] : items[0];
    }

    /// <summary>Reads the three pickers back into the options (a disabled, still-loading picker is skipped).</summary>
    public static void Read(ComboBox camera, ComboBox mic, ComboBox speaker, RecordOptions o)
    {
        if (camera.IsEnabled && camera.SelectedItem is DeviceChoice c)
        {
            o.Camera = c.Id != null;
            if (c.Id != null) o.CameraName = c.Id;
        }
        if (mic.IsEnabled && mic.SelectedItem is DeviceChoice m)
        {
            o.Microphone = m.Id != null;
            if (m.Id != null) o.MicrophoneId = m.Id;
        }
        if (speaker.IsEnabled && speaker.SelectedItem is DeviceChoice s)
        {
            o.SystemAudio = s.Id != null;
            if (s.Id != null) o.SpeakerId = s.Id;
        }
    }
}
