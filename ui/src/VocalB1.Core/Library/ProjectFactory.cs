using VocalB1.Core.Models;

namespace VocalB1.Core.Library;

/// <summary>
/// Sample project used on first launch: if the recommended three audio files exist in the voicebank, builds the melody
/// matching the default example in VocalB1.py (G4 → G4 → E4); otherwise returns an empty project.
/// </summary>
public static class ProjectFactory
{
    private static readonly string[] DemoFiles = ["_で.wav", "_い.wav", "_じ.wav"];

    public static ProjectDocument CreateEmpty(string? libraryRoot = null) => new()
    {
        Name = "未命名工程",
        Tracks = [CreateTrack(1, libraryRoot)],
    };

    /// <summary>Creates a track, carrying the current voicebank path by default.</summary>
    public static TrackDocument CreateTrack(int number, string? libraryRoot = null) => new()
    {
        Name = $"音轨 {number}",
        LibraryRoot = libraryRoot ?? string.Empty,
        Segments = [],
        PitchLine = [],
    };

    public static ProjectDocument CreateDemo(string libraryRoot)
    {
        var project = CreateEmpty(libraryRoot);
        var track = project.Tracks[0];

        var paths = DemoFiles
            .Select(name => Path.Combine(libraryRoot, name))
            .ToArray();

        if (paths.Any(p => !File.Exists(p)))
            return project;

        project.Name = "示例旋律";
        track.Name = "主旋律";
        track.Segments =
        [
            new VoiceSegment
            {
                Audio = paths[0], SrcPitch = "D4", DstPitch = "G4",
                BeatStart = 1, BeatEnd = 2.5, Transition = 0, KeepUnvoicedHead = true,
            },
            new VoiceSegment
            {
                Audio = paths[1], SrcPitch = "D4", DstPitch = "G4",
                BeatStart = 2, BeatEnd = 4, Transition = 0, KeepUnvoicedHead = false,
            },
            new VoiceSegment
            {
                Audio = paths[2], SrcPitch = "D4", DstPitch = "E4",
                BeatStart = 4, BeatEnd = 7, Transition = 0, KeepUnvoicedHead = false,
            },
        ];

        // Each segment gets 5 owned nodes by default (including both ends), using the segment's target pitch.
        foreach (var segment in track.Segments)
            track.AddPitchNodes(segment, 5);

        return project;
    }

    /// <summary>Default output directory: &lt;repo&gt;/_output/ui.</summary>
    public static string DefaultRenderDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "_output", "ui");
}
