using System.Text.Json;
using VocalB1.Core.Diagnostics;

namespace VocalB1.Core.Models;

/// <summary>
/// Voicebank formant profile: the tool the vowel manager reads from. Prefers measured formants stored in
/// &lt;voicebank&gt;/formants.json; falls back to a built-in vowel table when the voicebank has not been
/// analysed yet. Both paths are explicit: a malformed profile raises a <see cref="StageException"/>
/// instead of silently degrading.
/// </summary>
public sealed class VoicebankFormantProfile
{
    public const string FileName = "formants.json";

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
    private readonly Dictionary<string, double[]> _phonemes;
    private readonly Dictionary<string, double> _breath;
    private readonly double _scale;

    public string VoicebankRoot { get; }

    public bool IsMeasured { get; }

    private VoicebankFormantProfile(
        string root,
        Dictionary<string, double[]> vowels,
        Dictionary<string, double[]> phonemes,
        Dictionary<string, double> breath,
        double scale,
        bool measured)
    {
        VoicebankRoot = root;
        _vowels = vowels;
        _phonemes = phonemes;
        _breath = breath;
        _scale = scale;
        IsMeasured = measured;
    }

    public static VoicebankFormantProfile Load(string? voicebankRoot)
    {
        var root = voicebankRoot ?? string.Empty;
        var path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, FileName);

        if (path is null || !File.Exists(path))
            return new VoicebankFormantProfile(root, BuiltInVowels, new(StringComparer.OrdinalIgnoreCase),
                BuiltInBreath, 1.0, false);

        return StageGuard.Run(nameof(VoicebankFormantProfile), "load", () =>
        {
            var document = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("formants.json 为空或无法解析。");

            var vowels = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in document.Vowels ?? [])
                vowels[NormalizeVowel(key)] = Sanitize(value, key);

            var phonemes = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in document.Phonemes ?? [])
                phonemes[key] = Sanitize(value, key);

            var breath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in document.Breath ?? [])
                breath[NormalizeVowel(key)] = Math.Clamp(value, 0, 4);

            return new VoicebankFormantProfile(root, vowels, phonemes, breath,
                double.IsFinite(document.Scale) && document.Scale > 0 ? document.Scale : 1.0, true);
        });
    }

    private static double[] Sanitize(double[]? values, string key)
    {
        if (values is null || values.Length < 7)
            throw new StageException(nameof(VoicebankFormantProfile), "load",
                $"formants.json 中「{key}」需要 7 个共振峰，实际 {values?.Length ?? 0} 个。");

        var copy = values.Take(7).Select(v => double.IsFinite(v) && v > 0 ? v : 0).ToArray();
        if (copy.Any(v => v <= 0))
            throw new StageException(nameof(VoicebankFormantProfile), "load",
                $"formants.json 中「{key}」包含非正数或 NaN。");
        return copy;
    }

    /// <summary>Returns F1~F7 for a vowel class, preferring measured data for the given phoneme.</summary>
    public double[] Resolve(string? vowel, string? phoneme)
    {
        if (!string.IsNullOrWhiteSpace(phoneme) && _phonemes.TryGetValue(phoneme, out var measured))
            return Scale(measured);

        var normalized = NormalizeVowel(vowel);
        if (_vowels.TryGetValue(normalized, out var value))
            return Scale(value);

        return Scale(BuiltInVowels.TryGetValue(normalized, out var fallback) ? fallback : BuiltInVowels["a"]);
    }

    public double BreathFactor(string? vowel) =>
        _breath.TryGetValue(NormalizeVowel(vowel), out var value) ? value : 1.0;

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

    private double[] Scale(double[] values) =>
        Math.Abs(_scale - 1.0) < 1e-9 ? values.ToArray() : values.Select(v => v * _scale).ToArray();

    public static string NormalizeVowel(string? vowel)
    {
        if (string.IsNullOrWhiteSpace(vowel))
            return "a";
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
            _ => value.Length == 0 ? "a" : value[..1],
        };
    }

    private sealed class ProfileFile
    {
        public Dictionary<string, double[]>? Vowels { get; set; }
        public Dictionary<string, double[]>? Phonemes { get; set; }
        public Dictionary<string, double>? Breath { get; set; }
        public double Scale { get; set; } = 1.0;
    }
}
