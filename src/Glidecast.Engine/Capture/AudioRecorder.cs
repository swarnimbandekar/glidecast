using System.Diagnostics;
using Glidecast.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Glidecast.Capture;

public sealed record AudioDevice(string Id, string Name);

public static class AudioDevices
{
    public static List<AudioDevice> Microphones()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Output devices whose sound can be recorded (loopback).</summary>
    public static List<AudioDevice> Speakers()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();
        }
        catch { return new(); }
    }

    public static string? DefaultSpeaker()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch { return null; }
    }

    public static string? DefaultMicrophone()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
        }
        catch { return null; }
    }
}

/// <summary>
/// Records system sound (WASAPI loopback of the default output) or a
/// microphone to a WAV file that starts at input-clock 0, so it lines up with
/// the screen without any offset. Loopback delivers nothing while the PC is
/// silent, and device clocks drift from ours: both are absorbed by padding
/// silence or dropping a few samples whenever the file and the clock disagree.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly Stopwatch _clock;
    private readonly string? _deviceId;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private long _written;
    private double _bytesPerMs;
    private int _block;

    public AudioSource Source { get; }
    public string File { get; }
    public string? Error { get; private set; }

    public AudioRecorder(Stopwatch clock, string file, AudioSource source, string? deviceId = null)
    {
        _clock = clock;
        File = file;
        Source = source;
        _deviceId = deviceId;
    }

    public void Start()
    {
        if (Source == AudioSource.System)
        {
            using var en = new MMDeviceEnumerator();
            _capture = _deviceId != null ? new WasapiLoopbackCapture(en.GetDevice(_deviceId)) : new WasapiLoopbackCapture();
        }
        else
        {
            using var en = new MMDeviceEnumerator();
            var device = _deviceId != null
                ? en.GetDevice(_deviceId)
                : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _capture = new WasapiCapture(device);
        }
        var fmt = _capture.WaveFormat;
        _bytesPerMs = fmt.AverageBytesPerSecond / 1000.0;
        _block = fmt.BlockAlign;
        _writer = new WaveFileWriter(File, fmt);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Error = e.Exception.Message;
            _stopped.Set();
        };
        _capture.StartRecording();
    }

    private long Align(double bytes) => (long)(bytes / _block) * _block;

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_writer == null || e.BytesRecorded == 0) return;
            // This buffer ended about now, so it started bufferLength ago.
            double startMs = _clock.Elapsed.TotalMilliseconds - e.BytesRecorded / _bytesPerMs;
            double writtenMs = _written / _bytesPerMs;
            int offset = 0;
            if (startMs - writtenMs > 40)
            {
                PadTo(startMs);
            }
            else if (writtenMs - startMs > 120)
            {
                // Ahead of the clock: skip the overlap.
                offset = (int)Math.Min(e.BytesRecorded, Align((writtenMs - startMs) * _bytesPerMs));
            }
            int count = e.BytesRecorded - offset;
            if (count <= 0) return;
            _writer.Write(e.Buffer, offset, count);
            _written += count;
        }
    }

    private void PadTo(double ms)
    {
        long gap = Align(ms * _bytesPerMs) - _written;
        if (gap <= 0) return;
        var zeros = new byte[Math.Min(gap, 1 << 16)];
        while (gap > 0)
        {
            int n = (int)Math.Min(gap, zeros.Length);
            _writer!.Write(zeros, 0, n);
            _written += n;
            gap -= n;
        }
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _stopped.Wait(2000);
        lock (_gate)
        {
            if (_writer != null)
            {
                // Silence up to the end of the take.
                PadTo(_clock.Elapsed.TotalMilliseconds);
                _writer.Dispose();
                _writer = null;
            }
        }
        _capture.Dispose();
        _capture = null;
    }

    /// <summary>Compresses a finished WAV to AAC (.m4a) next to it; returns the
    /// file to use (the WAV itself if conversion fails).</summary>
    public static string Compress(string wav)
    {
        string m4a = Path.ChangeExtension(wav, ".m4a");
        try
        {
            Render.FFmpeg.Run($"-y -hide_banner -loglevel error -i \"{wav}\" -c:a aac -b:a 192k \"{m4a}\"", 300000);
            if (System.IO.File.Exists(m4a) && new FileInfo(m4a).Length > 0)
            {
                System.IO.File.Delete(wav);
                return m4a;
            }
        }
        catch { }
        return wav;
    }

    public void Dispose() => Stop();
}

/// <summary>Live input level of a microphone (0..1 peak, ~20 Hz), for the meter next to its picker.</summary>
public sealed class MicMeter : IDisposable
{
    private readonly WasapiCapture _capture;
    private float _peak;
    private DateTime _last = DateTime.UtcNow;

    /// <summary>Raised on a capture thread.</summary>
    public event Action<float>? Level;

    public MicMeter(string? deviceId)
    {
        using var en = new MMDeviceEnumerator();
        var device = deviceId != null ? en.GetDevice(deviceId) : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _capture = new WasapiCapture(device);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture.WaveFormat;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample == 32 && fmt.Encoding == WaveFormatEncoding.Extensible)
            for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) _peak = Math.Max(_peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
        else if (fmt.BitsPerSample == 16)
            for (int i = 0; i + 2 <= e.BytesRecorded; i += 2) _peak = Math.Max(_peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
        if ((DateTime.UtcNow - _last).TotalMilliseconds < 50) return;
        _last = DateTime.UtcNow;
        Level?.Invoke(Math.Min(1, _peak));
        _peak = 0;
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
