namespace VocalB1.Core.Models;

/// <summary>
/// A lyric data-block: a complete score (words, pronunciation units, segments, pitch line and beat grid)
/// that belongs to exactly one singer. The singer supplies the voicebank and resolves each syllable's
/// phoneme to a wav sample; the lyric itself never stores a track.
/// </summary>
public sealed class LyricDocument : IDatablock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "歌词";

    public bool FakeUser { get; set; }

    /// <summary>
    /// True (default) = syllable samples are phoneme names resolved against the singer's voicebank.
    /// A unit may still override with <see cref="LyricUnit.Audio"/> (dual mode).
    /// </summary>
    public bool ResolveFromVoicebank { get; set; } = true;

    public LyricPhrase Phrase { get; set; } = new();

    public List<VoiceSegment> Segments { get; set; } = [];

    public List<PitchPoint> PitchLine { get; set; } = [];

    /// <summary>End beat of the last segment.</summary>
    public double TotalBeats => Segments.Count == 0 ? 0 : Segments.Max(s => s.BeatEnd);

    public void AddPitchNodes(VoiceSegment segment, int count = 5)
    {
        count = Math.Max(2, count);
        var span = Math.Max(0.01, segment.BeatEnd - segment.BeatStart);
        for (var i = 0; i < count; i++)
        {
            PitchLine.Add(new PitchPoint
            {
                SegmentId = segment.Id,
                IsEdgeAnchor = i == 0 || i == count - 1,
                Beat = segment.BeatStart + span * i / (count - 1),
                Pitch = segment.DstPitch,
            });
        }
        SortPitchLine();
    }

    public void SortPitchLine() => PitchLine.Sort((a, b) => a.Beat.CompareTo(b.Beat));

    /// <summary>
    /// The pitch sequence used for drawing and synthesis; trailing edge anchors are folded away.
    /// (Same rule as the former per-track implementation, now owned by the lyric score.)
    /// </summary>
    public List<PitchPoint> BuildRenderPitchLine()
    {
        if (PitchLine.Count == 0)
            return [];

        var sorted = PitchLine.OrderBy(p => p.Beat).ToList();
        var segmentById = new Dictionary<string, VoiceSegment>();
        foreach (var segment in Segments)
            segmentById[segment.Id] = segment;

        var render = sorted.Where(p =>
        {
            if (!p.IsEdgeAnchor || p.IsTransition)
                return true;
            if (ReferenceEquals(p, sorted[0]) || ReferenceEquals(p, sorted[^1]))
                return true;
            return p.SegmentId is not string id
                || !segmentById.TryGetValue(id, out var owner)
                || p.Beat < owner.BeatEnd - 0.001;
        }).ToList();

        foreach (var segment in Segments)
        {
            if (render.Any(p => p.SegmentId == segment.Id))
                continue;

            var anchor = sorted.FirstOrDefault(
                p => p.SegmentId == segment.Id && p.IsEdgeAnchor && !p.IsTransition);
            if (anchor is not null)
                render.Add(anchor);
        }

        var deduped = new List<PitchPoint>();
        foreach (var point in render.OrderBy(p => p.Beat))
        {
            if (deduped.Count > 0
                && Math.Abs(deduped[^1].Beat - point.Beat) < 0.001
                && IsPlainPitch(deduped[^1]) && IsPlainPitch(point))
            {
                if (SegmentStart(deduped[^1]) <= SegmentStart(point))
                    deduped[^1] = point;
                continue;
            }

            deduped.Add(point);
        }

        return deduped;

        double SegmentStart(PitchPoint p) =>
            p.SegmentId is string id && segmentById.TryGetValue(id, out var owner)
                ? owner.BeatStart
                : double.NegativeInfinity;
    }

    private static bool IsPlainPitch(PitchPoint p) =>
        p.Pitch is not null
        && !string.Equals(p.Pitch.Trim(), "KEEP", StringComparison.OrdinalIgnoreCase);

    public LyricDocument Clone() => new()
    {
        Id = Id,
        Name = Name,
        FakeUser = FakeUser,
        ResolveFromVoicebank = ResolveFromVoicebank,
        Phrase = Phrase.Clone(),
        Segments = Segments.Select(s => s.Clone()).ToList(),
        PitchLine = PitchLine.Select(p => p.Clone()).ToList(),
    };
}
