using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VocalB1.Core.Models;

namespace VocalB1.Core.Serialization;

/// <summary>
/// Project file I/O (*.vbproj.json). The format is defined by the C# side and is decoupled from Python;
/// the engine bridge translates a single track into the routing parameters for synthesize().
/// </summary>
public static class ProjectJson
{
    public const string FileExtension = ".vbproj.json";

    /// <summary>Read options: tolerant of projects written by older versions (including NaN/Infinity literals).</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Write options: rejects NaN/Infinity instead of persisting unusable values.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes the project. When <paramref name="baseDirectory"/> is given, voicebank paths
    /// (library roots and wav paths inside it) are stored relative to it, so the file does not
    /// carry the original machine's user name or drive layout.
    /// </summary>
    public static string Serialize(ProjectDocument project, string? baseDirectory = null) =>
        JsonSerializer.Serialize(ToPortable(project, baseDirectory), WriteOptions);

    public static ProjectDocument Deserialize(string json, string? baseDirectory = null)
    {
        var project = JsonSerializer.Deserialize<ProjectDocument>(json, ReadOptions)
            ?? throw new InvalidDataException("工程 JSON 为空或无法解析。");

        project.Tracks ??= [];

        // 1.x compatibility: top-level segments / pitchLine / settings → migrated to the first track.
        if (project.Tracks.Count == 0)
        {
            var legacy = JsonSerializer.Deserialize<LegacyProject>(json, ReadOptions);
            if (legacy is not null && (legacy.Segments.Count > 0 || legacy.PitchLine.Count > 0))
            {
                project.Tracks.Add(new TrackDocument
                {
                    Name = "音轨 1",
                    Segments = legacy.Segments,
                    PitchLine = legacy.PitchLine,
                    Settings = legacy.Settings ?? new SynthesisSettings(),
                });
            }
        }

        if (project.Tracks.Count == 0)
            project.Tracks.Add(new TrackDocument());

        foreach (var track in project.Tracks)
        {
            // Projects written before chorus mode have no track ids; singers bind to ids, so backfill them.
            if (string.IsNullOrWhiteSpace(track.Id))
                track.Id = Guid.NewGuid().ToString("N");
            track.Segments ??= [];
            track.PitchLine ??= [];
            track.Settings ??= new SynthesisSettings();
            track.LyricPhrase ??= new LyricPhrase();
            track.LyricPhrase.Words ??= [];

            // Projects written before the lyric object model store no phrase: rebuild it from the segments
            // so the lyric bar can manage every existing segment.
            if (track.LyricPhrase.Words.Count == 0 && track.Segments.Count > 0)
                track.LyricPhrase = LyricPhrase.FromSegments(track.Segments);
        }

        project.Chorus ??= new ChorusDocument();
        SanitizeChorus(project.Chorus);
        project.Chorus.Singers ??= [];
        foreach (var singer in project.Chorus.Singers)
        {
            if (string.IsNullOrWhiteSpace(singer.Id))
                singer.Id = Guid.NewGuid().ToString("N");
            singer.Path ??= [];
            singer.Path.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        }

        project.Chorus.Formations ??= [];
        foreach (var formation in project.Chorus.Formations)
        {
            if (string.IsNullOrWhiteSpace(formation.Id))
                formation.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(formation.Name))
                formation.Name = "导入队形";
            formation.Positions ??= [];
        }

        return ResolvePaths(project, baseDirectory);
    }

    /// <summary>
    /// Repairs non-finite chorus values read from hand-edited or older projects. A NaN coordinate used to
    /// propagate through the whole STFT block and silently silence the rendered audio, so it is replaced
    /// with the neutral stage defaults here instead of reaching the mixer.
    /// </summary>
    private static void SanitizeChorus(ChorusDocument chorus)
    {
        if (!double.IsFinite(chorus.StageWidth) || chorus.StageWidth <= 0)
            chorus.StageWidth = 10.0;
        if (!double.IsFinite(chorus.StageDepth) || chorus.StageDepth <= 0)
            chorus.StageDepth = 6.0;
        if (!double.IsFinite(chorus.MicX))
            chorus.MicX = 0.0;
        if (!double.IsFinite(chorus.MicY))
            chorus.MicY = -2.0;

        chorus.Singers ??= [];
        foreach (var singer in chorus.Singers)
        {
            singer.Path ??= [];
            singer.Path.RemoveAll(p =>
                !double.IsFinite(p.Beat) || !double.IsFinite(p.X) || !double.IsFinite(p.Y));
            foreach (var point in singer.Path)
                point.Beat = Math.Max(1.0, point.Beat);
            if (singer.Path.Count == 0)
                singer.Path.Add(new ChorusKeyframe { Beat = 1, X = 0, Y = 0 });
        }

        chorus.Formations ??= [];
        foreach (var formation in chorus.Formations)
        {
            formation.Positions ??= [];
            formation.Positions.RemoveAll(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y));
        }
    }

    public static void Save(ProjectDocument project, string path, string? baseDirectory = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        AtomicFile.WriteAllText(full, Serialize(project, baseDirectory));
        project.ProjectDirectory ??= dir;
    }

    public static ProjectDocument Load(string path, string? baseDirectory = null) =>
        Deserialize(File.ReadAllText(path), baseDirectory);

    /// <summary>Copy of the project with every voicebank path made relative to the base directory.</summary>
    private static ProjectDocument ToPortable(ProjectDocument project, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return project;

        var copy = project.Clone();
        foreach (var track in copy.Tracks)
        {
            track.LibraryRoot = ToRelativePath(track.LibraryRoot, baseDirectory) ?? string.Empty;
            foreach (var segment in track.Segments)
                segment.Audio = ToRelativePath(segment.Audio, baseDirectory) ?? string.Empty;
            foreach (var unit in track.LyricPhrase.Words.SelectMany(w => w.Units))
                unit.Audio = ToRelativePath(unit.Audio, baseDirectory) ?? string.Empty;
        }
        copy.BgmPath = ToRelativePath(copy.BgmPath, baseDirectory);
        return copy;
    }

    /// <summary>Makes stored relative paths absolute again; absolute paths from older projects are kept.</summary>
    private static ProjectDocument ResolvePaths(ProjectDocument project, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return project;

        foreach (var track in project.Tracks)
        {
            track.LibraryRoot = ToAbsolutePath(track.LibraryRoot, baseDirectory) ?? string.Empty;
            foreach (var segment in track.Segments)
                segment.Audio = ToAbsolutePath(segment.Audio, baseDirectory) ?? string.Empty;
            foreach (var unit in track.LyricPhrase.Words.SelectMany(w => w.Units))
                unit.Audio = ToAbsolutePath(unit.Audio, baseDirectory) ?? string.Empty;
        }
        project.BgmPath = ToAbsolutePath(project.BgmPath, baseDirectory);
        return project;
    }

    /// <summary>
    /// Converts an absolute path inside <paramref name="baseDirectory"/> to a forward-slash relative path.
    /// Paths outside the base directory are left untouched because they cannot be made portable.
    /// </summary>
    private static string? ToRelativePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return path;

        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(baseDirectory), full);
        if (Path.IsPathFullyQualified(relative))
            return path;
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return path;
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves a stored relative path against the base directory. An absolute path that no longer
    /// exists (a project written on another machine) is re-anchored to this machine's voicebank
    /// directory when the same relative location exists here.
    /// </summary>
    private static string? ToAbsolutePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (!Path.IsPathFullyQualified(path))
            return Path.GetFullPath(Path.Combine(baseDirectory, path));

        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
            return full;

        return RebaseOnVoicebank(full, baseDirectory) ?? full;
    }

    /// <summary>Maps ".../voicebank/&lt;rest&gt;" of an old absolute path onto the current base directory.</summary>
    private static string? RebaseOnVoicebank(string fullPath, string baseDirectory)
    {
        var segments = fullPath.Split('\\', '/');
        var index = Array.FindLastIndex(
            segments, s => s.Equals("voicebank", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;

        var suffix = string.Join(Path.DirectorySeparatorChar, segments[(index + 1)..]);
        var candidate = string.IsNullOrEmpty(suffix)
            ? Path.Combine(baseDirectory, "voicebank")
            : Path.Combine(baseDirectory, "voicebank", suffix);
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>Top-level structure of 1.x project files, used only for migration reads.</summary>
    private sealed class LegacyProject
    {
        public List<VoiceSegment> Segments { get; set; } = [];
        public List<PitchPoint> PitchLine { get; set; } = [];
        public SynthesisSettings? Settings { get; set; }
    }
}
