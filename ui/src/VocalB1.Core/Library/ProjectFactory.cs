using VocalB1.Core.Models;

namespace VocalB1.Core.Library;

/// <summary>
/// Creates empty/demo projects in the v3 object-library format: one lyric, one parameter set and one
/// singer wired together, so a new project can render immediately after adding syllables.
/// </summary>
public static class ProjectFactory
{
    private static readonly string[] DemoFiles = ["_で.wav", "_い.wav", "_じ.wav"];

    public static ProjectDocument CreateEmpty(string? libraryRoot = null)
    {
        var project = new ProjectDocument { Name = "未命名工程" };
        AddSingerBundle(project, "歌手 1", libraryRoot);
        return project;
    }

    /// <summary>Creates and wires a lyric + parameter set + singer triple.</summary>
    public static SingerDocument AddSingerBundle(ProjectDocument project, string name, string? libraryRoot,
        ParameterSet? sharedParameters = null)
    {
        var lyric = CreateLyric(name + " - 歌词");
        var parameters = sharedParameters ?? CreateParameterSet(name + " - 参数");
        if (sharedParameters is null)
            project.ParameterSets.Add(parameters);

        var singer = new SingerDocument
        {
            Name = ProjectDocument.UniqueName(name, project.Singers.Select(s => s.Name)),
            LibraryRoot = libraryRoot ?? string.Empty,
            LyricId = lyric.Id,
            ParameterSetId = parameters.Id,
        };

        project.Lyrics.Add(lyric);
        project.Singers.Add(singer);
        return singer;
    }

    /// <summary>Creates an independent lyric data-block (score + formant data).</summary>
    public static LyricDocument CreateLyric(string name) => new()
    {
        Name = name,
        Segments = [],
        PitchLine = [],
    };

    /// <summary>Creates a parameter data-block with default synthesis values and a formant node graph.</summary>
    public static ParameterSet CreateParameterSet(string name) => new()
    {
        Name = name,
        Base = new SynthesisSettings(),
        Graph = ParameterNodeGraph.CreateFormantDefault(),
    };

    public static ProjectDocument CreateDemo(string libraryRoot)
    {
        var project = CreateEmpty(libraryRoot);
        var lyric = project.FindLyric(project.Singers[0].LyricId)!;
        var singer = project.Singers[0];

        var paths = DemoFiles
            .Select(name => Path.Combine(libraryRoot, name))
            .ToArray();

        if (paths.Any(p => !File.Exists(p)))
            return project;

        project.Name = "示例旋律";
        lyric.Name = "主旋律 - 歌词";
        singer.Name = "主旋律";

        lyric.Segments =
        [
            new VoiceSegment
            {
                Audio = paths[0], SrcPitch = VoiceSegment.DefaultSrcPitch, DstPitch = "G4",
                BeatStart = 1, BeatEnd = 2.5, Transition = 0, KeepUnvoicedHead = true,
                Vowel = "e", Phoneme = "_で",
            },
            new VoiceSegment
            {
                Audio = paths[1], SrcPitch = VoiceSegment.DefaultSrcPitch, DstPitch = "G4",
                BeatStart = 2.5, BeatEnd = 4, Transition = 0, KeepUnvoicedHead = false,
                Vowel = "i", Phoneme = "_い",
            },
            new VoiceSegment
            {
                Audio = paths[2], SrcPitch = VoiceSegment.DefaultSrcPitch, DstPitch = "E4",
                BeatStart = 4, BeatEnd = 7, Transition = 0, KeepUnvoicedHead = false,
                Vowel = "i", Phoneme = "_じ",
            },
        ];

        lyric.Phrase = LyricPhrase.FromSegments(lyric.Segments);

        foreach (var segment in lyric.Segments)
            lyric.AddPitchNodes(segment, 5);

        return project;
    }

    /// <summary>Default output directory: &lt;repo&gt;/_output/ui.</summary>
    public static string DefaultRenderDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "_output", "ui");
}
