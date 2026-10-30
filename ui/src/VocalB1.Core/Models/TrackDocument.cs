namespace VocalB1.Core.Models;

/// <summary>
/// A track in a project: has its own voicebank, segments, pitch line, and full set of synthesis parameters.
/// Tracks are fully independent; only the tempo/time signature belongs to the shared project grid.
/// </summary>
public sealed class TrackDocument
{
    public string Name { get; set; } = "音轨 1";

    /// <summary>Voicebank root directory used by this track (may be empty = use the engine's default voicebank).</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    public List<VoiceSegment> Segments { get; set; } = [];

    public List<PitchPoint> PitchLine { get; set; } = [];

    /// <summary>This track's own synthesis parameters (formant follow/gain, energy distribution, etc.).</summary>
    public SynthesisSettings Settings { get; set; } = new();

    /// <summary>
    /// Solo affects preview playback only: while any track is soloed, playback uses that track's rendered result
    /// (rendering still targets the active track).
    /// </summary>
    public bool Solo { get; set; }

    /// <summary>End beat of the last segment on this track.</summary>
    public double TotalBeats => Segments.Count == 0 ? 0 : Segments.Max(s => s.BeatEnd);

    /// <summary>
    /// Generates default Pitch nodes for a segment: 5 evenly spaced inside the segment (including both ends), using the segment's target pitch.
    /// The nodes belong to the segment and follow it when it is dragged or deleted.
    /// </summary>
    public void AddPitchNodes(VoiceSegment segment, int count = 5)
    {
        count = Math.Max(2, count);
        var span = Math.Max(0.01, segment.BeatEnd - segment.BeatStart);
        for (var i = 0; i < count; i++)
        {
            PitchLine.Add(new PitchPoint
            {
                SegmentId = segment.Id,
                // The first and last act only as curve anchors; no control points are drawn.
                IsEdgeAnchor = i == 0 || i == count - 1,
                Beat = segment.BeatStart + span * i / (count - 1),
                Pitch = segment.DstPitch,
            });
        }
        SortPitchLine();
    }

    /// <summary>Sorts the pitch line by beat ascending (equal beats keep their original order).</summary>
    public void SortPitchLine() =>
        PitchLine.Sort((a, b) => a.Beat.CompareTo(b.Beat));

    /// <summary>
    /// The sequence actually used for drawing the curve and for synthesis.
    /// Trailing edge anchors are folded away: a note's pitch is defined by its leading anchor and its
    /// visible control points, so the curve reaches the next note's pitch at that note's start
    /// instead of being pulled back by the previous note's hidden end point.
    /// The first/last node of the whole line always stays (the curve needs two ends), and a segment
    /// whose control points were all deleted still keeps one anchor so its pitch stays defined.
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

        // Two plain pitch nodes can end up on the same beat when a segment's last control point is
        // clamped to the start of an overlap.  A single beat can only carry one pitch, so the later
        // note wins; otherwise the curve would dip back to the previous note after reaching the new one.
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

    public TrackDocument Clone() => new()
    {
        Name = Name,
        LibraryRoot = LibraryRoot,
        Segments = Segments.Select(s => s.Clone()).ToList(),
        PitchLine = PitchLine.Select(p => p.Clone()).ToList(),
        Settings = Settings.Clone(),
        Solo = Solo,
    };
}
