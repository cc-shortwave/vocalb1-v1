namespace VocalB1.Core.Models;

/// <summary>
/// A savable/loadable project (.vbproj.json).
/// A project contains several independent tracks; only the tempo and time signature are shared.
/// </summary>
public sealed class ProjectDocument
{
    /// <summary>Format version, used for future migrations.</summary>
    public string FormatVersion { get; set; } = "2.0";

    public string Name { get; set; } = "未命名工程";

    /// <summary>Project root directory (used to resolve relative audio paths when saving).</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>Tempo (BPM), which determines the number of frames per beat.</summary>
    public double Bpm { get; set; } = 120.0;

    /// <summary>Beats per bar (time-signature numerator).</summary>
    public int BeatsPerBar { get; set; } = 3;

    /// <summary>Beat unit (time-signature denominator).</summary>
    public int BeatUnit { get; set; } = 4;

    /// <summary>Tracks in tab order (at least 1).</summary>
    public List<TrackDocument> Tracks { get; set; } = [];

    /// <summary>Total project length in beats, taken as the maximum across all tracks.</summary>
    public double TotalBeats => Tracks.Count == 0 ? 0 : Tracks.Max(t => t.TotalBeats);

    public ProjectDocument Clone() => new()
    {
        FormatVersion = FormatVersion,
        Name = Name,
        ProjectDirectory = ProjectDirectory,
        Bpm = Bpm,
        BeatsPerBar = BeatsPerBar,
        BeatUnit = BeatUnit,
        Tracks = Tracks.Select(t => t.Clone()).ToList(),
    };
}
