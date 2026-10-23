using System.IO;
using System.Windows.Media;
using Glidecast.Model;

namespace Glidecast.App.Services;

/// <summary>
/// Plays the recorded audio tracks along with the editor preview. Every track
/// starts at project time 0, so the preview position is the audio position;
/// a track that drifts more than a few frames is snapped back.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    private readonly List<MediaPlayer> _players = new();
    private IReadOnlyList<AudioTrack> _tracks = Array.Empty<AudioTrack>();
    private bool _playing;

    public AudioPreview(Project p)
    {
        foreach (var a in p.Audio ?? new())
        {
            var mp = new MediaPlayer();
            if (File.Exists(a.File)) mp.Open(new Uri(a.File));
            _players.Add(mp);
        }
        Rebind(p.Audio);
    }

    /// <summary>Points at the project's current track list (undo replaces it).</summary>
    public void Rebind(IReadOnlyList<AudioTrack>? tracks)
    {
        _tracks = tracks ?? Array.Empty<AudioTrack>();
        ApplyVolumes();
    }

    public void ApplyVolumes()
    {
        for (int i = 0; i < _players.Count && i < _tracks.Count; i++)
            _players[i].Volume = _tracks[i].Muted ? 0 : Math.Clamp(_tracks[i].Volume, 0, 1);
    }

    /// <summary>Call on every preview frame and play-state change.</summary>
    public void Sync(double positionMs, bool playing)
    {
        if (_players.Count == 0) return;
        var want = TimeSpan.FromMilliseconds(positionMs);
        foreach (var mp in _players)
        {
            if (!playing)
            {
                if (_playing) mp.Pause();
                continue;
            }
            if (!_playing || Math.Abs((mp.Position - want).TotalMilliseconds) > 250) mp.Position = want;
            if (!_playing) mp.Play();
        }
        _playing = playing;
    }

    public void Dispose()
    {
        foreach (var mp in _players) mp.Close();
        _players.Clear();
    }
}
