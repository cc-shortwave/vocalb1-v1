using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace VocalB1.Core.Models;

/// <summary>
/// Decomposition of one voicebank sample (one wav = one phoneme): formants, measured fundamental
/// frequency and aperiodicity. Produced by the Python engine and cached in memory for the session.
/// </summary>
public sealed class PhonemeMeasurement
{
    /// <summary>F1~F7 measured from the sample's median spectral envelope.</summary>
    public double[] Formants { get; set; } = [];

    /// <summary>Median f0 of the voiced frames in Hz; 0 when the sample has no voiced frames.</summary>
    public double F0Hz { get; set; }

    /// <summary>Mean pyworld aperiodicity (0..1) of the voiced frames.</summary>
    public double Aperiodicity { get; set; }

    /// <summary>Aperiodicity relative to the bank median; 1.0 is neutral.</summary>
    public double BreathFactor { get; set; } = 1.0;
}

/// <summary>
/// Whole-voicebank decomposition returned by the engine. Nothing is persisted: the result lives in
/// <see cref="VoicebankAnalysisCache"/> for the current process only.
/// </summary>
public sealed class VoicebankAnalysis
{
    /// <summary>Per-sample measurements keyed by wav stem (for example "_あ").</summary>
    public Dictionary<string, PhonemeMeasurement> Phonemes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Median F1~F7 per vowel row ("a", "i", ...), used by explicit vowel nodes.</summary>
    public Dictionary<string, double[]> Vowels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Breath factor per vowel row, normalised so the bank median is 1.0.</summary>
    public Dictionary<string, double> Breath { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Samples that could not be decomposed; reported but never silently used.</summary>
    public List<string> Failed { get; set; } = [];

    public string Source { get; set; } = "engine";

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Looks up a phoneme measurement. Keys come from sample stems, so "あ", "_あ" and "あ.wav"
    /// variants all resolve to the same entry.
    /// </summary>
    public bool TryGetMeasurement(string? phoneme, [NotNullWhen(true)] out PhonemeMeasurement? measurement)
    {
        foreach (var candidate in PhonemeCandidates(phoneme))
        {
            if (Phonemes.TryGetValue(candidate, out var found))
            {
                measurement = found;
                return true;
            }
        }

        measurement = null;
        return false;
    }

    /// <summary>Candidate dictionary keys for a lyric phoneme or a sample file name.</summary>
    public static IEnumerable<string> PhonemeCandidates(string? phoneme)
    {
        if (string.IsNullOrWhiteSpace(phoneme))
            yield break;

        var trimmed = phoneme.Trim();
        yield return trimmed;

        var bare = trimmed.TrimStart('_');
        if (!string.Equals(bare, trimmed, StringComparison.Ordinal))
            yield return bare;
        if (bare.Length > 0)
            yield return "_" + bare;
    }
}

/// <summary>
/// Session-scoped cache of voicebank decompositions. Analysis runs in the background when a bank is
/// bound; rendering only reads this cache, so no external formants file is ever required.
/// </summary>
public static class VoicebankAnalysisCache
{
    private static readonly ConcurrentDictionary<string, VoicebankAnalysis> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static string NormalizeRoot(string? voicebankRoot)
    {
        if (string.IsNullOrWhiteSpace(voicebankRoot))
            return string.Empty;

        var trimmed = voicebankRoot.Trim();
        try
        {
            return Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            // Invalid characters: keep the raw value so the lookup still fails predictably.
            return trimmed;
        }
    }

    public static bool TryGet(string? voicebankRoot, [NotNullWhen(true)] out VoicebankAnalysis? analysis)
    {
        var key = NormalizeRoot(voicebankRoot);
        if (key.Length > 0 && Entries.TryGetValue(key, out var found))
        {
            analysis = found;
            return true;
        }

        analysis = null;
        return false;
    }

    public static void Set(string? voicebankRoot, VoicebankAnalysis analysis)
    {
        var key = NormalizeRoot(voicebankRoot);
        if (key.Length > 0)
            Entries[key] = analysis;
    }

    public static void Invalidate(string? voicebankRoot)
    {
        var key = NormalizeRoot(voicebankRoot);
        if (key.Length > 0)
            Entries.TryRemove(key, out _);
    }

    /// <summary>Drops every cached decomposition (the UI's "clear cache" action).</summary>
    public static void Clear() => Entries.Clear();
}
