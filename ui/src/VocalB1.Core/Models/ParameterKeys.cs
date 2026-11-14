namespace VocalB1.Core.Models;

/// <summary>
/// Canonical parameter keys used by per-parameter modifier stacks. Formant keys are exposed by the
/// synthesizer as absolute target positions; every other key maps onto an existing synthesis setting.
/// </summary>
public static class ParameterKeys
{
    public const string FormantPrefix = "formant.F";
    public const string FormantBandwidthPrefix = "formant.BW";
    public const string FormantGainPrefix = "formant.G";

    public sealed record Definition(string Key, string Label, double Min, double Max, double Default,
        bool IsFormant = false, int FormantIndex = -1);

    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.Ordinal);

    static ParameterKeys()
    {
        Add(new Definition("breathyThr", "气声阈值", 0, 1, 0.3));
        Add(new Definition("bandWidthHz", "频带宽度", 20, 5000, 250));
        Add(new Definition("apScale", "AP 增强强度", 0, 1, 0.25));
        Add(new Definition("apMax", "AP 增强上限", 0, 1, 0.20));
        Add(new Definition("breathGain", "气声增益", 0, 10, 1.0));
        Add(new Definition("plosiveGain", "爆破音增益", 0, 2, 0.3));
        Add(new Definition("jitter", "抖动强度", 0, 100, 0));
        Add(new Definition("shimmer", "振幅抖动", 0, 100, 0));
        Add(new Definition("matchRmsTo", "目标响度", 0, 2, 0.3));

        var followDefaults = new[] { 0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0 };
        for (var i = 0; i < 7; i++)
            Add(new Definition($"followRatio.{i}", $"共振峰跟随 {i + 1}", 0, 2, followDefaults[i]));

        for (var i = 0; i < 8; i++)
            Add(new Definition($"bandRatios.{i}", $"频带增益 {i + 1}", 0, 4, 1.0));

        var formantDefaults = new[] { 800.0, 1200.0, 2500.0, 3400.0, 4000.0, 4500.0, 5000.0 };
        for (var i = 0; i < 7; i++)
        {
            Add(new Definition($"{FormantPrefix}{i + 1}", $"F{i + 1} 位置 (Hz)", 50, 8000,
                formantDefaults[i], true, i));
            Add(new Definition($"{FormantBandwidthPrefix}{i + 1}", $"F{i + 1} 带宽 (Hz)", 20, 2000,
                80, true, i));
            Add(new Definition($"{FormantGainPrefix}{i + 1}", $"F{i + 1} 增益", 0, 4,
                1.0, true, i));
        }
    }

    private static void Add(Definition definition) => Definitions[definition.Key] = definition;

    public static IReadOnlyCollection<Definition> All => Definitions.Values;

    public static IReadOnlyList<Definition> Curveable => Definitions.Values
        .Where(d => !d.Key.StartsWith("bandWidthHz", StringComparison.Ordinal))
        .ToList();

    public static bool TryGet(string key, out Definition definition) =>
        Definitions.TryGetValue(key, out definition!);

    public static Definition Get(string key) => Definitions.TryGetValue(key, out var definition)
        ? definition
        : throw new KeyNotFoundException($"未知参数键：{key}");

    public static bool IsFormantKey(string key) =>
        key.StartsWith(FormantPrefix, StringComparison.Ordinal)
        || key.StartsWith(FormantBandwidthPrefix, StringComparison.Ordinal)
        || key.StartsWith(FormantGainPrefix, StringComparison.Ordinal);

    /// <summary>Extracts the 0-based formant index from formant.F3 / formant.BW3 / formant.G3.</summary>
    public static int FormantIndexOf(string key)
    {
        if (!IsFormantKey(key))
            return -1;
        var digits = new string(key.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var oneBased) ? Math.Clamp(oneBased - 1, 0, 6) : -1;
    }
}
