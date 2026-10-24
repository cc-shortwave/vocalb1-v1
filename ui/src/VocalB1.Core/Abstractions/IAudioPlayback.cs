using VocalB1.Core.Audio;

namespace VocalB1.Core.Abstractions;

/// <summary>WAV playback abstraction: supports reading the current position and starting playback from a given time (used by the transport).</summary>
public interface IAudioPlayback
{
    bool IsPlaying { get; }

    /// <summary>Current playback position; 0 when not playing.</summary>
    TimeSpan Position { get; }

    event EventHandler? PlaybackStopped;

    /// <summary>Plays the whole WAV starting at <paramref name="start"/>.</summary>
    void Play(string wavPath, TimeSpan start);

    void Stop();
}

/// <summary>Waveform data provider (with caching).</summary>
public interface IWaveformProvider
{
    /// <summary>Reads a WAV and resamples it into <paramref name="buckets"/> min/max buckets.</summary>
    WaveformData Read(string wavPath, int buckets = 2048);

    void Invalidate(string wavPath);
}
