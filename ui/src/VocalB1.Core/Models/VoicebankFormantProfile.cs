using VocalB1.Core.Diagnostics;

namespace VocalB1.Core.Models;

/// <summary>
/// Voicebank formant profile backed by the in-memory decomposition produced by the Python engine
/// (<see cref="VoicebankAnalysisCache"/>). No external formants file is read: a lyric phoneme without a
/// measured entry is a hard error and is never classified from its characters. Explicit vowel nodes may
/// still use the measured vowel averages, or the built-in table when the node opts out of voicebank data.
/// </summary>
public sealed class VoicebankFormantProfile
{
    private static readonly Dictionary<string, double[]> BuiltInVowels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = [800, 1200, 2500, 3400, 4000, 4500, 5000],
        ["i"] = [300, 2300, 3000, 3700, 4200, 4700, 5200],
        ["u"] = [350, 1300, 2400, 3300, 3900, 4400, 4900],
        ["e"] = [500, 1900, 2600, 3500, 4100, 4600, 5100],
        ["o"] = [450, 800, 2600, 3400, 4000, 4500, 5000],
        ["n"] = [300, 1200, 2500, 3400, 4000, 4500, 5000],
        ["-"] = [500, 1500, 2500, 3400, 4000, 4500, 5000],
    };

    private static readonly Dictionary<string, double> BuiltInBreath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1.00,
        ["i"] = 1.15,
        ["u"] = 0.90,
        ["e"] = 1.05,
        ["o"] = 1.00,
        ["n"] = 0.80,
        ["-"] = 0.90,
    };

    private readonly Dictionary<string, double[]> _vowels;
    private readonly Dictionary<string, PhonemeMeasurement> _phonemes;
    private readonly Dictionary<string, double> _breath;

    public string VoicebankRoot { get; }

    /// <summary>True when the bank has been decomposed and the result is cached for this session.</summary>
    public bool IsMeasured { get; }

    /// <summary>Samples the engine could not decompose; surfaced as warnings, never used as data.</summary>
    public IReadOnlyList<string> FailedSamples { get; }

    private VoicebankFormantProfile(
        string root,
        Dictionary<string, double[]> vowels,
        Dictionary<string, PhonemeMeasurement> phonemes,
        Dictionary<string, double> breath,
        bool measured,
        IReadOnlyList<string>? failed = null)
    {
        VoicebankRoot = root;
        _vowels = vowels;
        _phonemes = phonemes;
        _breath = breath;
        IsMeasured = measured;
        FailedSamples = failed ?? [];
    }

    /// <summary>
    /// Builds the profile from the session cache. A bank that has not been decomposed yet yields an
    /// unmeasured profile whose <see cref="Resolve"/> is a hard error.
    /// </summary>
    public static VoicebankFormantProfile Load(string? voicebankRoot)
    {
        var root = voicebankRoot ?? string.Empty;
        if (!VoicebankAnalysisCache.TryGet(root, out var analysis))
        {
            return new VoicebankFormantProfile(root,
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                false);
        }

        var vowels = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in analysis.Vowels)
        {
            if (TrySanitize(value, out var clean))
                vowels[NormalizeVowel(key)] = clean;
        }

        var phonemes = new Dictionary<string, PhonemeMeasurement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in analysis.Phonemes)
        {
            if (TrySanitize(value.Formants, out var clean))
                phonemes[key] = new PhonemeMeasurement
                {
                    Formants = clean,
                    F0Hz = value.F0Hz,
                    Aperiodicity = value.Aperiodicity,
                    BreathFactor = value.BreathFactor,
                };
        }

        var breath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in analysis.Breath)
            breath[NormalizeVowel(key)] = Math.Clamp(value, 0.05, 4.0);

        return new VoicebankFormantProfile(root, vowels, phonemes, breath, true, analysis.Failed);
    }

    private static bool TrySanitize(double[]? values, out double[] sanitized)
    {
        sanitized = [];
        if (values is null || values.Length < 7)
            return false;

        var copy = values.Take(7).ToArray();
        if (copy.Any(v => !double.IsFinite(v) || v <= 0))
            return false;
        sanitized = copy;
        return true;
    }

    /// <summary>
    /// Returns F1~F7 for a lyric phoneme from the bank's measured data. A phoneme without a measured
    /// entry is a hard error: the pipeline never falls back to a character-derived vowel.
    /// </summary>
    public double[] Resolve(string? vowel, string? phoneme)
    {
        if (!string.IsNullOrWhiteSpace(phoneme))
        {
            if (TryGetMeasured(phoneme, out var measured))
                return measured;

            var reason = IsMeasured
                ? "后台分解未得到该音节（详见日志），或该样本没有足够的浊音帧。"
                : "声库尚未完成分解分析；请选择声库后等待后台分析，或点击「分析声库」。";
            throw new StageException(nameof(VoicebankFormantProfile), "resolve",
                $"声库「{VoicebankRoot}」缺少音节「{phoneme}」的实测共振峰：{reason}");
        }

        var normalized = NormalizeVowel(vowel);
        if (_vowels.TryGetValue(normalized, out var value))
            return value.ToArray();

        return BuiltInVowels.TryGetValue(normalized, out var fallback)
            ? fallback.ToArray()
            : BuiltInVowels["a"].ToArray();
    }

    /// <summary>
    /// Looks up the measured F1~F7 for a phoneme. The keys come from sample stems, so both "あ" and
    /// "_あ" (and their underscored variants) are accepted.
    /// </summary>
    public bool TryGetMeasured(string? phoneme, out double[] formants)
    {
        if (TryGetSampleMeasurement(phoneme, out var measurement))
        {
            formants = measurement.Formants.ToArray();
            return true;
        }

        formants = [];
        return false;
    }

    /// <summary>Full measured entry (formants, f0, aperiodicity) for a phoneme or sample stem.</summary>
    public bool TryGetSampleMeasurement(string? phoneme, out PhonemeMeasurement measurement)
    {
        foreach (var candidate in VoicebankAnalysis.PhonemeCandidates(phoneme))
        {
            if (_phonemes.TryGetValue(candidate, out var found))
            {
                measurement = found;
                return true;
            }
        }

        measurement = new PhonemeMeasurement();
        return false;
    }

    /// <summary>
    /// Measured breath factor: the lyric phoneme wins, then the vowel row, then neutral.
    /// </summary>
    public double BreathFactor(string? vowel, string? phoneme = null)
    {
        if (!string.IsNullOrWhiteSpace(phoneme)
            && TryGetSampleMeasurement(phoneme, out var measurement)
            && double.IsFinite(measurement.BreathFactor)
            && measurement.BreathFactor > 0)
        {
            return Math.Clamp(measurement.BreathFactor, 0.05, 4.0);
        }

        return _breath.TryGetValue(NormalizeVowel(vowel), out var value) ? value : 1.0;
    }

    /// <summary>Built-in vowel table, used when a modifier explicitly disables voicebank measurements.</summary>
    public static double[] BuiltInResolve(string? vowel)
    {
        var normalized = NormalizeVowel(vowel);
        return BuiltInVowels.TryGetValue(normalized, out var value)
            ? value.ToArray()
            : BuiltInVowels["a"].ToArray();
    }

    public static double BuiltInBreathFactor(string? vowel) =>
        BuiltInBreath.TryGetValue(NormalizeVowel(vowel), out var value) ? value : 1.0;

    public static string NormalizeVowel(string? vowel)
    {
        if (string.IsNullOrWhiteSpace(vowel))
            return string.Empty;
        var value = vowel.Trim().ToLowerInvariant();
        return value switch
        {
            "あ" or "ア" => "a",
            "い" or "イ" => "i",
            "う" or "ウ" or "ぅ" => "u",
            "え" or "エ" => "e",
            "お" or "オ" => "o",
            "ん" or "ン" => "n",
            "a" or "i" or "u" or "e" or "o" or "n" => value,
            _ => value.Length == 0 ? string.Empty : value[..1],
        };
    }
}
