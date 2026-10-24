namespace VocalB1.Core.Audio;

/// <summary>
/// Waveform peaks (min/max per bucket) used for drawing. Full samples are not kept in memory.
/// </summary>
public sealed record WaveformData(
    int SampleRate,
    int Channels,
    double DurationSeconds,
    float[] Min,
    float[] Max)
{
    public int BucketCount => Min.Length;

    public static WaveformData Empty { get; } = new(0, 0, 0, [], []);

    /// <summary>Largest absolute amplitude across the whole audio.</summary>
    public float PeakAmplitude => Max.Length == 0 ? 0 : Math.Max(Max.Max(), -Min.Min());
}
