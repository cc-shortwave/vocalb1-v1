namespace VocalB1.Core.Models;

/// <summary>
/// A parameter data-block: the shared synthesis settings plus one node graph per parameter set. Several
/// singers may reference the same parameter set; editing it affects all of them.
/// </summary>
public sealed class ParameterSet : IDatablock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "参数";

    public bool FakeUser { get; set; }

    /// <summary>Static baseline values (sliders / legacy synthesis settings).</summary>
    public SynthesisSettings Base { get; set; } = new();

    /// <summary>
    /// The node canvas. Output nodes write canonical parameter keys; unwired outputs fall back to the
    /// baseline value of that key.
    /// </summary>
    public ParameterNodeGraph Graph { get; set; } = new();

    /// <summary>Reads the baseline value for a canonical parameter key.</summary>
    public double GetBase(string key)
    {
        var definition = ParameterKeys.Get(key);
        var settings = Base;

        if (key.StartsWith(ParameterKeys.FormantPrefix, StringComparison.Ordinal))
            return definition.Default;
        if (key.StartsWith(ParameterKeys.FormantBandwidthPrefix, StringComparison.Ordinal)
            || key.StartsWith(ParameterKeys.FormantGainPrefix, StringComparison.Ordinal))
            return definition.Default;

        return key switch
        {
            "breathyThr" => settings.BreathyThr,
            "bandWidthHz" => settings.BandWidthHz,
            "apScale" => settings.ApScale,
            "apMax" => settings.ApMax,
            "breathGain" => settings.BreathGain,
            "plosiveGain" => settings.PlosiveGain,
            "jitter" => settings.Jitter,
            "shimmer" => settings.Shimmer,
            "matchRmsTo" => settings.MatchRmsTo,
            _ when key.StartsWith("followRatio.", StringComparison.Ordinal) =>
                Element(settings.FollowRatio, key, definition.Default),
            _ when key.StartsWith("bandRatios.", StringComparison.Ordinal) =>
                Element(settings.BandRatios, key, definition.Default),
            _ => definition.Default,
        };
    }

    private static double Element(double[] values, string key, double fallback)
    {
        var digits = new string(key.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var index) && index >= 0 && index < values.Length
            ? values[index]
            : fallback;
    }

    /// <summary>Writes the baseline value for a canonical parameter key (formant keys are lyric-owned).</summary>
    public void SetBase(string key, double value)
    {
        var definition = ParameterKeys.Get(key);
        value = Math.Clamp(value, definition.Min, definition.Max);
        switch (key)
        {
            case "breathyThr": Base.BreathyThr = value; return;
            case "bandWidthHz": Base.BandWidthHz = value; return;
            case "apScale": Base.ApScale = value; return;
            case "apMax": Base.ApMax = value; return;
            case "breathGain": Base.BreathGain = value; return;
            case "plosiveGain": Base.PlosiveGain = value; return;
            case "jitter": Base.Jitter = value; return;
            case "shimmer": Base.Shimmer = value; return;
            case "matchRmsTo": Base.MatchRmsTo = value; return;
        }

        if (key.StartsWith("followRatio.", StringComparison.Ordinal))
        {
            var index = Index(key);
            if (index < Base.FollowRatio.Length)
                Base.FollowRatio[index] = value;
            return;
        }

        if (key.StartsWith("bandRatios.", StringComparison.Ordinal))
        {
            var index = Index(key);
            if (index < Base.BandRatios.Length)
                Base.BandRatios[index] = value;
        }
    }

    private static int Index(string key) =>
        int.TryParse(new string(key.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var index) ? index : 0;

    public ParameterSet Clone() => new()
    {
        Id = Id,
        Name = Name,
        FakeUser = FakeUser,
        Base = Base.Clone(),
        Graph = Graph.Clone(),
    };
}
