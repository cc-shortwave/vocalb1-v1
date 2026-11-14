using VocalB1.Core.Diagnostics;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

/// <summary>
/// The fully evaluated render input for one singer: resolved segments, pitch line, per-frame parameter
/// curves (including F1~F7 targets), and the baseline settings used as Python-side fallbacks.
/// </summary>
public sealed class SingerRenderPlan
{
    public required SingerDocument Singer { get; init; }

    public required LyricDocument Lyric { get; init; }

    public required ParameterSet Parameters { get; init; }

    public required VoicebankFormantProfile FormantProfile { get; init; }

    public int SingerIndex { get; init; }

    public int SingerCount { get; init; }

    public double Bpm { get; init; }

    public double FramePeriod { get; init; }

    public double FramesPerBeat { get; init; }

    public int FrameCount { get; init; }

    public double TotalBeats { get; init; }

    public int SampleRate { get; init; }

    public int BitDepth { get; init; }

    public double MixGain { get; init; }

    /// <summary>Per-frame curves keyed by canonical parameter key (only curveable / formant keys).</summary>
    public Dictionary<string, double[]> Curves { get; } = new(StringComparer.Ordinal);

    public List<VoiceSegment> Segments { get; init; } = [];

    public List<PitchPoint> PitchLine { get; init; } = [];

    public SynthesisSettings Settings { get; init; } = new();

    /// <summary>Frames × 7 formant target positions in Hz, always present.</summary>
    public double[,] FormantTargetsHz { get; private set; } = new double[0, 7];

    /// <summary>True when the singer's spatial trajectory produced per-frame data.</summary>
    public bool HasSpatial { get; private set; }

    /// <summary>Per-frame pan position (-1..1); empty when <see cref="HasSpatial"/> is false.</summary>
    public double[] SpatialPan { get; private set; } = [];

    /// <summary>Per-frame distance gain; empty when <see cref="HasSpatial"/> is false.</summary>
    public double[] SpatialGain { get; private set; } = [];

    /// <summary>Per-frame high-frequency rolloff cutoff in Hz; empty when <see cref="HasSpatial"/> is false.</summary>
    public double[] SpatialCutoffHz { get; private set; } = [];

    /// <summary>
    /// Evaluates one singer. Every failure is raised with the exact class and stage; nothing is skipped.
    /// </summary>
    public static SingerRenderPlan Build(ProjectDocument project, SingerDocument singer,
        int singerIndex, int singerCount)
    {
        return StageGuard.Run(nameof(SingerRenderPlan), "build", () =>
        {
            var lyric = project.FindLyric(singer.LyricId)
                ?? throw new StageException(nameof(SingerRenderPlan), "bind-lyric",
                    $"歌手「{singer.Name}」没有绑定歌词。");
            var parameters = project.FindParameterSet(singer.ParameterSetId)
                ?? throw new StageException(nameof(SingerRenderPlan), "bind-parameters",
                    $"歌手「{singer.Name}」没有绑定参数。");

            var resolver = new VoicebankSampleResolver(singer.LibraryRoot);
            var segments = lyric.Segments
                .OrderBy(s => s.BeatStart)
                .Select(s =>
                {
                    var copy = s.Clone();
                    copy.Audio = resolver.Resolve(s, singer.Name, lyric.ResolveFromVoicebank);
                    return copy;
                })
                .ToList();

            var framePeriod = parameters.Base.FramePeriod > 0 ? parameters.Base.FramePeriod : 5.0;
            var framesPerBeat = 60000.0 / (Math.Max(1, project.Bpm) * framePeriod);
            var totalBeats = Math.Max(0, lyric.TotalBeats);
            var frameCount = Math.Max(1, (int)Math.Ceiling(totalBeats * framesPerBeat));

            var plan = new SingerRenderPlan
            {
                Singer = singer,
                Lyric = lyric,
                Parameters = parameters,
                FormantProfile = VoicebankFormantProfile.Load(singer.LibraryRoot),
                SingerIndex = singerIndex,
                SingerCount = singerCount,
                Bpm = project.Bpm,
                FramePeriod = framePeriod,
                FramesPerBeat = framesPerBeat,
                FrameCount = frameCount,
                TotalBeats = totalBeats,
                SampleRate = parameters.Base.SampleRate,
                BitDepth = parameters.Base.BitDepth,
                MixGain = double.IsFinite(singer.MixGain) ? singer.MixGain : 1.0,
                Segments = segments,
                PitchLine = lyric.BuildRenderPitchLine(),
                Settings = parameters.Base.Clone(),
            };

            var formantTargets = new double[frameCount, 7];
            var unitBySegment = BuildUnitMap(lyric, segments);

            for (var frame = 0; frame < frameCount; frame++)
            {
                var beat = 1.0 + frame / framesPerBeat;
                var segment = SegmentAt(segments, beat);
                unitBySegment.TryGetValue(segment?.Id ?? string.Empty, out var unit);

                var vowel = !string.IsNullOrWhiteSpace(unit?.Vowel)
                    ? unit!.Vowel
                    : segment?.Vowel ?? "a";
                var phoneme = unit?.Phoneme ?? segment?.Phoneme ?? string.Empty;
                var semitone = PitchAt(plan.PitchLine, beat,
                    segment is null ? 0 : ParseSemitone(segment.DstPitch));
                var pitchHz = PitchMath.SemitonesToHz(semitone);
                var progress = totalBeats <= 1 ? 1.0 : Math.Clamp((beat - 1) / (totalBeats - 1), 0, 1);
                var globals = new FrameGlobals(
                    (beat - 1) * 60.0 / Math.Max(1, project.Bpm), beat, progress,
                    pitchHz, semitone, VoicebankFormantProfile.NormalizeVowel(vowel), phoneme,
                    singerIndex, singerCount, frame);

                var formants = unit?.FormantHz is { Length: 7 } manual
                    ? manual
                    : plan.FormantProfile.Resolve(vowel, phoneme);

                // The baseline resolver honours manual lyric formants first, then the measured voicebank
                // profile, then the built-in table — the node graphs only ever add to that baseline.
                double ResolveBase(string key) =>
                    BaseValue(key, formants, unit, plan.FormantProfile, parameters);

                var sharedOutputs = new NodeGraphEvaluator(parameters.Graph, globals,
                        plan.FormantProfile, ResolveBase)
                    .EvaluateOutputs();

                var overlaid = new NodeGraphEvaluator(singer.Graph, globals,
                        plan.FormantProfile, ResolveBase,
                        key => sharedOutputs.TryGetValue(key, out var sharedValue)
                            ? sharedValue
                            : ResolveBase(key))
                    .EvaluateOutputs();

                foreach (var definition in ParameterKeys.All)
                {
                    double? evaluated = null;
                    if (overlaid.TryGetValue(definition.Key, out var singerValue))
                        evaluated = singerValue;
                    else if (sharedOutputs.TryGetValue(definition.Key, out var sharedValue))
                        evaluated = sharedValue;

                    if (evaluated is null && !ParameterKeys.IsFormantKey(definition.Key))
                        continue;

                    var value = Math.Clamp(evaluated ?? ResolveBase(definition.Key),
                        definition.Min, definition.Max);

                    if (!plan.Curves.TryGetValue(definition.Key, out var curve))
                    {
                        curve = new double[frameCount];
                        plan.Curves[definition.Key] = curve;
                    }
                    curve[frame] = value;

                    if (ParameterKeys.IsFormantKey(definition.Key))
                    {
                        var index = ParameterKeys.FormantIndexOf(definition.Key);
                        if (index >= 0 && definition.Key.StartsWith(ParameterKeys.FormantPrefix, StringComparison.Ordinal)
                            && !definition.Key.StartsWith(ParameterKeys.FormantBandwidthPrefix, StringComparison.Ordinal)
                            && !definition.Key.StartsWith(ParameterKeys.FormantGainPrefix, StringComparison.Ordinal))
                        {
                            formantTargets[frame, index] = value;
                        }
                    }
                }

                // Ensure every formant has a target even when no stack/key ran for this frame.
                for (var i = 0; i < 7; i++)
                {
                    var hz = plan.Curves.TryGetValue($"{ParameterKeys.FormantPrefix}{i + 1}", out var curve)
                        ? curve[frame]
                        : formants[i];
                    formantTargets[frame, i] = Math.Clamp(hz, 50, 8000);
                }
            }

            plan.FormantTargetsHz = formantTargets;

            var spatial = SpatialSolver.Solve(singer.Spatial, frameCount, framesPerBeat);
            if (spatial is not null)
            {
                plan.HasSpatial = true;
                plan.SpatialPan = spatial.Pan;
                plan.SpatialGain = spatial.Gain;
                plan.SpatialCutoffHz = spatial.CutoffHz;
            }

            ApplyCurveMeans(plan);
            return plan;
        });
    }

    private static double BaseValue(string key, double[] formants, LyricUnit? unit,
        VoicebankFormantProfile profile, ParameterSet parameters)
    {
        var definition = ParameterKeys.Get(key);

        if (key.StartsWith(ParameterKeys.FormantPrefix, StringComparison.Ordinal)
            && !key.StartsWith(ParameterKeys.FormantBandwidthPrefix, StringComparison.Ordinal)
            && !key.StartsWith(ParameterKeys.FormantGainPrefix, StringComparison.Ordinal))
        {
            return formants[Math.Clamp(ParameterKeys.FormantIndexOf(key), 0, 6)];
        }

        if (key.StartsWith(ParameterKeys.FormantBandwidthPrefix, StringComparison.Ordinal))
        {
            var index = Math.Clamp(ParameterKeys.FormantIndexOf(key), 0, 6);
            return unit?.FormantBandwidthHz is { Length: 7 } band
                ? band[index]
                : definition.Default;
        }

        if (key.StartsWith(ParameterKeys.FormantGainPrefix, StringComparison.Ordinal))
        {
            var index = Math.Clamp(ParameterKeys.FormantIndexOf(key), 0, 6);
            return unit?.FormantGainDb is { Length: 7 } gain
                ? gain[index]
                : definition.Default;
        }

        return parameters.GetBase(key);
    }

    /// <summary>Writes the average of every curve back into the baseline settings for Python fallbacks.</summary>
    private static void ApplyCurveMeans(SingerRenderPlan plan)
    {
        var settings = plan.Settings;
        foreach (var (key, curve) in plan.Curves)
        {
            if (curve.Length == 0 || ParameterKeys.IsFormantKey(key))
                continue;

            var mean = curve.Average();
            switch (key)
            {
                case "breathyThr": settings.BreathyThr = mean; break;
                case "apScale": settings.ApScale = mean; break;
                case "apMax": settings.ApMax = mean; break;
                case "breathGain": settings.BreathGain = mean; break;
                case "plosiveGain": settings.PlosiveGain = mean; break;
                case "jitter": settings.Jitter = mean; break;
                case "shimmer": settings.Shimmer = mean; break;
                case "matchRmsTo": settings.MatchRmsTo = mean; break;
                case "bandWidthHz": settings.BandWidthHz = mean; break;
                default:
                    if (key.StartsWith("followRatio.", StringComparison.Ordinal))
                        settings.FollowRatio[Index(key)] = mean;
                    else if (key.StartsWith("bandRatios.", StringComparison.Ordinal))
                        settings.BandRatios[Index(key)] = mean;
                    break;
            }
        }
    }

    private static int Index(string key) =>
        int.TryParse(new string(key.SkipWhile(c => !char.IsDigit(c)).ToArray()), out var index) ? index : 0;

    private static Dictionary<string, LyricUnit> BuildUnitMap(LyricDocument lyric,
        IReadOnlyList<VoiceSegment> segments)
    {
        var map = new Dictionary<string, LyricUnit>(StringComparer.Ordinal);
        foreach (var unit in lyric.Phrase.Words.SelectMany(w => w.Units))
        {
            if (!unit.IsRest && unit.SegmentId is { } id)
                map[id] = unit;
        }

        foreach (var segment in segments)
            map.TryAdd(segment.Id, LyricPhrase.WordFromSegment(segment).Units[0]);

        return map;
    }

    private static VoiceSegment? SegmentAt(IReadOnlyList<VoiceSegment> segments, double beat)
    {
        VoiceSegment? found = null;
        foreach (var segment in segments)
        {
            if (segment.BeatStart <= beat + 1e-9)
                found = segment;
            else
                break;
        }
        return found ?? segments.FirstOrDefault();
    }

    private static double PitchAt(IReadOnlyList<PitchPoint> points, double beat, double fallback)
    {
        if (points.Count == 0)
            return fallback;

        if (beat <= points[0].Beat)
            return ParsePitch(points[0].Pitch, fallback);

        // Dense raster curves can hold thousands of points; binary search keeps the per-frame
        // parameter evaluation linear instead of quadratic.
        var lo = 0;
        var hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (points[mid].Beat <= beat)
                lo = mid;
            else
                hi = mid - 1;
        }

        if (lo >= points.Count - 1)
            return ParsePitch(points[^1].Pitch, fallback);

        var previous = points[lo];
        var point = points[lo + 1];
        var a = ParsePitch(previous.Pitch, fallback);
        var b = ParsePitch(point.Pitch, fallback);
        var span = Math.Max(1e-9, point.Beat - previous.Beat);
        var t = Math.Clamp((beat - previous.Beat) / span, 0, 1);
        return a + (b - a) * t;
    }

    private static double ParsePitch(string? pitch, double fallback)
    {
        if (string.IsNullOrWhiteSpace(pitch)
            || string.Equals(pitch.Trim(), "KEEP", StringComparison.OrdinalIgnoreCase))
            return fallback;
        return PitchMath.TryParseSemitones(pitch, out var semitones) ? semitones : fallback;
    }

    private static double ParseSemitone(string? pitch) =>
        PitchMath.TryParseSemitones(pitch, out var semitones) ? semitones : 0;
}
