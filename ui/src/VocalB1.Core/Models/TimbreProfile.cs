using System.Text.Json;
using VocalB1.Core.Serialization;

namespace VocalB1.Core.Models;

/// <summary>
/// Core timbre parameters of a single track, saved as *.vbtimbre.json so a tuning result can be
/// carried over to another project. Only the parameters that shape the voice colour are stored;
/// engine bookkeeping (sample rate, frame period, intermediate files) stays in the project.
/// </summary>
public sealed class TimbreProfile
{
    public const string FileExtension = ".vbtimbre.json";

    /// <summary>Format version, kept for future field changes.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Formant follow ratio per band (7 bands).</summary>
    public double[] FollowRatio { get; set; } = [];

    /// <summary>Energy distribution / formant gain per band (8 bands).</summary>
    public double[] BandRatios { get; set; } = [];

    public double BandWidthHz { get; set; } = 250.0;
    public double BreathyThr { get; set; } = 0.3;
    public double ApScale { get; set; } = 0.25;
    public double ApMax { get; set; } = 0.20;
    public bool AddBreath { get; set; } = true;
    public double BreathGain { get; set; } = 1.0;
    public bool BreathFollowPitch { get; set; } = true;
    public bool BreathShapeToMain { get; set; } = true;
    public double Jitter { get; set; } = 0.0;
    public double Shimmer { get; set; } = 0.0;

    public static TimbreProfile From(SynthesisSettings settings) => new()
    {
        FollowRatio = [.. settings.FollowRatio],
        BandRatios = [.. settings.BandRatios],
        BandWidthHz = settings.BandWidthHz,
        BreathyThr = settings.BreathyThr,
        ApScale = settings.ApScale,
        ApMax = settings.ApMax,
        AddBreath = settings.AddBreath,
        BreathGain = settings.BreathGain,
        BreathFollowPitch = settings.BreathFollowPitch,
        BreathShapeToMain = settings.BreathShapeToMain,
        Jitter = settings.Jitter,
        Shimmer = settings.Shimmer,
    };

    /// <summary>
    /// Copy the stored values onto a track's settings. Values are clamped to the ranges the UI
    /// sliders expose, and unusable fields (wrong band count or non-finite numbers) are skipped
    /// instead of poisoning the synthesis parameters; the skipped fields are returned for logging.
    /// </summary>
    public List<string> ApplyTo(SynthesisSettings settings)
    {
        var skipped = new List<string>();

        void Band(string name, double[]? source, double[] target, double max, Action<double[]> assign)
        {
            if (source is null || source.Length != target.Length)
            {
                skipped.Add($"{name}: 需要 {target.Length} 个值，实际 {source?.Length ?? 0} 个");
                return;
            }
            if (source.Any(v => !double.IsFinite(v)))
            {
                skipped.Add(name + ": 含 NaN/Inf");
                return;
            }
            assign(source.Select(v => Math.Clamp(v, 0.0, max)).ToArray());
        }

        void Scalar(string name, double value, double min, double max, Action<double> assign)
        {
            if (!double.IsFinite(value))
            {
                skipped.Add(name + ": 含 NaN/Inf");
                return;
            }
            assign(Math.Clamp(value, min, max));
        }

        Band("followRatio", FollowRatio, settings.FollowRatio, 1.0, v => settings.FollowRatio = v);
        Band("bandRatios", BandRatios, settings.BandRatios, 3.0, v => settings.BandRatios = v);

        Scalar("bandWidthHz", BandWidthHz, 20.0, 5000.0, v => settings.BandWidthHz = v);
        Scalar("breathyThr", BreathyThr, 0.0, 1.0, v => settings.BreathyThr = v);
        Scalar("apScale", ApScale, 0.0, 1.0, v => settings.ApScale = v);
        Scalar("apMax", ApMax, 0.0, 1.0, v => settings.ApMax = v);
        Scalar("breathGain", BreathGain, 0.0, 10.0, v => settings.BreathGain = v);
        Scalar("jitter", Jitter, 0.0, 100.0, v => settings.Jitter = v);
        Scalar("shimmer", Shimmer, 0.0, 100.0, v => settings.Shimmer = v);

        settings.AddBreath = AddBreath;
        settings.BreathFollowPitch = BreathFollowPitch;
        settings.BreathShapeToMain = BreathShapeToMain;

        return skipped;
    }

    public static TimbreProfile Load(string path) =>
        JsonSerializer.Deserialize<TimbreProfile>(File.ReadAllText(path), ProjectJson.ReadOptions)
        ?? throw new InvalidDataException("音色参数 JSON 为空或无法解析。");

    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        AtomicFile.WriteAllText(full, JsonSerializer.Serialize(this, ProjectJson.WriteOptions));
    }
}
