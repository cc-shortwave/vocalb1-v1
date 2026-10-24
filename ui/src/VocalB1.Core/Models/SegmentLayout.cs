namespace VocalB1.Core.Models;

/// <summary>
/// Segment timeline rules: adjacency, overlap detection, push/shift, and transition-node synchronization.
/// Conventions:
/// 1. New overlaps between segments are not allowed by default; when a segment is dragged past the next one, all following segments shift back together while keeping their relative spacing.
/// 2. Dragging left into the previous segment is forbidden (a segment's start may not precede the previous segment's end beat).
/// 3. Existing overlaps (e.g. head/tail overlaps in stored project data) are preserved, but normal control points may not fall inside an overlap;
///    the overlap is adjusted only by a transition node located at its center.
/// </summary>
public static class SegmentLayout
{
    private const double Epsilon = 0.001;

    /// <summary>A time overlap between two segments.</summary>
    public readonly record struct Overlap(VoiceSegment Left, VoiceSegment Right, double Start, double End);

    /// <summary>Snapshot of segments ordered by start beat ascending (ties broken by end beat).</summary>
    public static List<VoiceSegment> Ordered(TrackDocument track) => Ordered(track.Segments);

    /// <inheritdoc cref="Ordered(TrackDocument)"/>
    public static List<VoiceSegment> Ordered(IReadOnlyList<VoiceSegment> segments) =>
        [.. segments.OrderBy(s => s.BeatStart).ThenBy(s => s.BeatEnd)];

    /// <summary>Previous/next neighboring segments on the timeline; equal start beats are decided by list order.</summary>
    public static (VoiceSegment? Previous, VoiceSegment? Next) Neighbors(
        TrackDocument track, VoiceSegment segment)
    {
        var ordered = Ordered(track);
        var index = ordered.FindIndex(s => ReferenceEquals(s, segment));
        if (index < 0)
            return (null, null);

        return (index > 0 ? ordered[index - 1] : null,
            index + 1 < ordered.Count ? ordered[index + 1] : null);
    }

    /// <summary>Time overlaps of all adjacent segments (only counted when the overlap exceeds 1 millisecond in beat units).</summary>
    public static List<Overlap> Overlaps(TrackDocument track) => Overlaps(track.Segments);

    /// <inheritdoc cref="Overlaps(TrackDocument)"/>
    public static List<Overlap> Overlaps(IReadOnlyList<VoiceSegment> segments)
    {
        var overlaps = new List<Overlap>();
        var ordered = Ordered(segments);
        for (var i = 1; i < ordered.Count; i++)
        {
            var left = ordered[i - 1];
            var right = ordered[i];
            var start = Math.Max(left.BeatStart, right.BeatStart);
            var end = Math.Min(left.BeatEnd, right.BeatEnd);
            if (end - start > Epsilon)
                overlaps.Add(new Overlap(left, right, start, end));
        }

        return overlaps;
    }

    /// <summary>Whether a beat position falls inside any overlap.</summary>
    public static bool IsInOverlap(TrackDocument track, double beat) =>
        Overlaps(track).Any(o => beat > o.Start + Epsilon && beat < o.End - Epsilon);

    /// <summary>
    /// Shifts all segments after <paramref name="segment"/> back together
    /// so that their start is not earlier than <paramref name="minStart"/>, keeping their spacing unchanged.
    /// </summary>
    public static void PushAfter(TrackDocument track, VoiceSegment segment, double minStart)
    {
        var ordered = Ordered(track);
        var index = ordered.FindIndex(s => ReferenceEquals(s, segment));
        if (index < 0 || index + 1 >= ordered.Count)
            return;

        var overflow = minStart - ordered[index + 1].BeatStart;
        if (overflow <= Epsilon)
            return;

        var pushed = ordered.Skip(index + 1).ToList();
        foreach (var other in pushed)
        {
            other.BeatStart += overflow;
            other.BeatEnd += overflow;
        }

        ShiftPitchPoints(track, pushed, overflow);
    }

    /// <summary>
    /// Keeps transition nodes consistent with the current overlaps:
    /// creates/updates one center transition node per overlap, removes stale transition nodes,
    /// and pushes normal control points out of the overlaps.
    /// </summary>
    public static void SyncTransitionPoints(TrackDocument track)
    {
        var overlaps = Overlaps(track);

        // Each overlap is attached to the later segment: a segment has at most one leading transition node.
        var beats = new Dictionary<string, double>();
        foreach (var overlap in overlaps)
            beats[overlap.Right.Id] = (overlap.Start + overlap.End) / 2;

        track.PitchLine.RemoveAll(
            p => p.IsTransition && (p.SegmentId is null || !beats.ContainsKey(p.SegmentId)));

        foreach (var (segmentId, beat) in beats)
        {
            var node = track.PitchLine.FirstOrDefault(p => p.IsTransition && p.SegmentId == segmentId);
            if (node is null)
            {
                var owner = track.Segments.FirstOrDefault(s => s.Id == segmentId);
                if (owner is null)
                    continue;

                track.PitchLine.Add(new PitchPoint
                {
                    SegmentId = segmentId,
                    IsTransition = true,
                    Beat = beat,
                    Pitch = owner.DstPitch,
                });
            }
            else
            {
                node.Beat = beat;
                node.IsEdgeAnchor = false;
            }
        }

        ClampPointsOutOfOverlaps(track, overlaps);
        track.SortPitchLine();
    }

    /// <summary>When a normal control point falls inside an overlap, pushes it to the non-overlapping boundary on its segment's side.</summary>
    private static void ClampPointsOutOfOverlaps(TrackDocument track, List<Overlap> overlaps)
    {
        if (overlaps.Count == 0)
            return;

        var byId = new Dictionary<string, VoiceSegment>();
        foreach (var segment in track.Segments)
            byId[segment.Id] = segment;

        foreach (var point in track.PitchLine)
        {
            // Edge anchors are pinned to the segment ends and transition nodes already belong to the overlap, so neither is moved.
            if (point.IsTransition || point.IsEdgeAnchor)
                continue;
            if (point.SegmentId is not string segmentId || !byId.TryGetValue(segmentId, out var owner))
                continue;

            foreach (var overlap in overlaps)
            {
                if (point.Beat <= overlap.Start + Epsilon || point.Beat >= overlap.End - Epsilon)
                    continue;

                var target = ReferenceEquals(owner, overlap.Left) ? overlap.Start : overlap.End;
                point.Beat = Math.Clamp(target, owner.BeatStart + Epsilon, owner.BeatEnd - Epsilon);
            }
        }
    }

    /// <summary>Shifts all nodes owned by the given segments (used when dragging pushed segments).</summary>
    private static void ShiftPitchPoints(TrackDocument track, IEnumerable<VoiceSegment> segments, double delta)
    {
        var ids = segments.Select(s => s.Id).ToHashSet();
        foreach (var point in track.PitchLine)
        {
            if (point.SegmentId is string segmentId && ids.Contains(segmentId))
                point.Beat += delta;
        }
    }
}
