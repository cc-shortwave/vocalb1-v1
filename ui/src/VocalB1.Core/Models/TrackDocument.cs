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

    public TrackDocument Clone() => new()
    {
        Name = Name,
        LibraryRoot = LibraryRoot,
        Segments = Segments.Select(s => s.Clone()).ToList(),
        PitchLine = PitchLine.Select(p => p.Clone()).ToList(),
        Settings = Settings.Clone(),
    };
}
