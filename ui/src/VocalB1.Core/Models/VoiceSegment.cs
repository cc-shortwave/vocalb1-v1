using System.Text.Json.Serialization;

namespace VocalB1.Core.Models;

/// <summary>
/// A pronunciation segment in the lyric area (corresponds to one dict in SEGMENTS on the Python side).
/// Time coordinates are 1-based beats, consistent with the Python pipeline.
/// </summary>
public sealed class VoiceSegment
{
    /// <summary>Unique segment identifier, used for Pitch node ownership and for rebuilding links after loading.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Absolute path to the voicebank wav.</summary>
    public string Audio { get; set; } = string.Empty;

    /// <summary>Source pitch assumed when a segment or dictionary entry does not specify one (the voicebank's recorded pitch).</summary>
    public const string DefaultSrcPitch = "D4";

    /// <summary>Source pitch of the sample, e.g. "D4", "440Hz", project style "1D".</summary>
    public string SrcPitch { get; set; } = DefaultSrcPitch;

    /// <summary>Target pitch.</summary>
    public string DstPitch { get; set; } = "C4";

    /// <summary>Start beat (1-based).</summary>
    public double BeatStart { get; set; } = 1.0;

    /// <summary>End beat (1-based); must be greater than <see cref="BeatStart"/>.</summary>
    public double BeatEnd { get; set; } = 2.0;

    /// <summary>Number of Bézier overlap frames with the previous segment; 0 = hard splice; ignored for the first segment.</summary>
    public double Transition { get; set; }

    /// <summary>Keeps the unvoiced frames in the first half of this segment.</summary>
    public bool KeepUnvoicedHead { get; set; }

    /// <summary>Segment-level override: formant follow ratio; null = use global settings.</summary>
    public double[]? FollowRatio { get; set; }

    /// <summary>Segment-level override: band energy multipliers; null = use global settings.</summary>
    public double[]? BandRatios { get; set; }

    /// <summary>Segment-level override: breath threshold; null = use global settings.</summary>
    public double? BreathyThr { get; set; }

    /// <summary>Manually entered lyric; null = derive from the file name.</summary>
    public string? LyricOverride { get; set; }

    /// <summary>Lyric shown on the segment: manual input takes precedence, otherwise the file name with leading underscores removed.</summary>
    [JsonIgnore]
    public string Lyric => LyricOverride ?? (string.IsNullOrWhiteSpace(Audio)
        ? "(空)"
        : Path.GetFileNameWithoutExtension(Audio).TrimStart('_') is { Length: > 0 } s ? s : "(空)");

    public double Length => BeatEnd - BeatStart;

    public VoiceSegment Clone() => new()
    {
        Id = Id,
        Audio = Audio,
        SrcPitch = SrcPitch,
        DstPitch = DstPitch,
        BeatStart = BeatStart,
        BeatEnd = BeatEnd,
        Transition = Transition,
        KeepUnvoicedHead = KeepUnvoicedHead,
        FollowRatio = FollowRatio?.ToArray(),
        BandRatios = BandRatios?.ToArray(),
        BreathyThr = BreathyThr,
        LyricOverride = LyricOverride,
    };
}
