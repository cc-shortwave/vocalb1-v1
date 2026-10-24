using System.Collections.Concurrent;
using VocalB1.Core.Abstractions;
using VocalB1.Core.Audio;

namespace VocalB1.Engine.Audio;

/// <summary>Waveform peak cache: each file/bucket count pair is parsed only once and is invalidated automatically when the file changes.</summary>
public sealed class WaveformProvider : IWaveformProvider
{
    private sealed record CacheKey(string Path, int Buckets, DateTime ModifiedUtc, long Length);

    private readonly ConcurrentDictionary<CacheKey, WaveformData> _cache = new();

    public WaveformData Read(string wavPath, int buckets = 2048)
    {
        if (!File.Exists(wavPath))
            return WaveformData.Empty;

        var info = new FileInfo(wavPath);
        var key = new CacheKey(wavPath, buckets, info.LastWriteTimeUtc, info.Length);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        WaveformData data;
        try
        {
            data = WavReader.ReadPeaks(wavPath, buckets);
        }
        catch
        {
            data = WaveformData.Empty;
        }

        _cache[key] = data;
        return data;
    }

    public void Invalidate(string wavPath)
    {
        foreach (var key in _cache.Keys.Where(k => k.Path == wavPath).ToList())
            _cache.TryRemove(key, out _);
    }
}
