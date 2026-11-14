using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VocalB1.Core.Diagnostics;
using VocalB1.Core.Models;

namespace VocalB1.Core.Serialization;

/// <summary>
/// Project file I/O (*.vbproj.json). v3 stores three data-block libraries (lyrics / parameter sets /
/// singers). v2 projects are converted in memory for read-only inspection and rendering; they are never
/// written back, and every conversion failure is reported with class + stage.
/// </summary>
public static class ProjectJson
{
    public const string FileExtension = ".vbproj.json";

    public const string CurrentFormatVersion = "3.0";

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(ProjectDocument project, string? baseDirectory = null) =>
        JsonSerializer.Serialize(ToPortable(project, baseDirectory), WriteOptions);

    public static ProjectDocument Deserialize(string json, string? baseDirectory = null) =>
        StageGuard.Run(nameof(ProjectJson), "deserialize", () =>
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("formatVersion", out var versionElement)
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;
            var hasTracks = root.TryGetProperty("tracks", out var tracksElement)
                && tracksElement.ValueKind == JsonValueKind.Array
                && tracksElement.GetArrayLength() > 0;

            ProjectDocument project;
            if (version.StartsWith("3", StringComparison.Ordinal) && !hasTracks)
            {
                project = JsonSerializer.Deserialize<ProjectDocument>(json, ReadOptions)
                    ?? throw new InvalidDataException("工程 JSON 为空或无法解析。");
                project.IsLegacyReadOnly = false;
            }
            else
            {
                project = ConvertLegacy(json);
                project.IsLegacyReadOnly = true;
            }

            Sanitize(project);
            return ResolvePaths(project, baseDirectory);
        });

    /// <summary>
    /// Converts a v2 project (tracks + chorus stage) into v3 data-blocks. Each legacy track becomes one
    /// lyric + one parameter set + one singer; stage positions are discarded because v3 singers have no
    /// spatial component. If legacy chorus singers exist, only tracks actually used by them are kept.
    /// </summary>
    private static ProjectDocument ConvertLegacy(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacyProject>(json, ReadOptions);
        if (legacy is null)
            throw new InvalidDataException("旧工程 JSON 为空或无法解析。");

        var project = new ProjectDocument
        {
            FormatVersion = CurrentFormatVersion,
            Name = legacy.Name ?? "旧工程",
            Bpm = legacy.Bpm is > 0 ? legacy.Bpm : 120,
            BeatsPerBar = legacy.BeatsPerBar is > 0 ? legacy.BeatsPerBar : 3,
            BeatUnit = legacy.BeatUnit is > 0 ? legacy.BeatUnit : 4,
            BgmPath = legacy.BgmPath,
            BgmStartBeat = legacy.BgmStartBeat,
        };

        var tracks = legacy.Tracks ?? [];
        if (tracks.Count == 0 && legacy.Segments is { Count: > 0 })
        {
            tracks =
            [
                new LegacyTrack
                {
                    Name = "音轨 1",
                    Segments = legacy.Segments,
                    PitchLine = legacy.PitchLine ?? [],
                    Settings = legacy.Settings ?? new SynthesisSettings(),
                },
            ];
        }

        var usedTrackIds = (legacy.Chorus?.Singers ?? [])
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.TrackId))
            .Select(s => s.TrackId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            if (usedTrackIds.Count > 0 && track.Id is { Length: > 0 } id && !usedTrackIds.Contains(id))
                continue;

            var lyric = new LyricDocument
            {
                Name = track.Name ?? "歌词",
                ResolveFromVoicebank = false,
                Phrase = track.LyricPhrase ?? new LyricPhrase(),
                Segments = track.Segments ?? [],
                PitchLine = track.PitchLine ?? [],
            };
            if (lyric.Phrase.Words.Count == 0 && lyric.Segments.Count > 0)
                lyric.Phrase = LyricPhrase.FromSegments(lyric.Segments);

            var parameters = new ParameterSet
            {
                Name = (track.Name ?? "参数") + " - 参数",
                Base = track.Settings ?? new SynthesisSettings(),
            };

            var singer = new SingerDocument
            {
                Name = track.Name ?? "歌手",
                LibraryRoot = track.LibraryRoot ?? string.Empty,
                LyricId = lyric.Id,
                ParameterSetId = parameters.Id,
            };

            project.Lyrics.Add(lyric);
            project.ParameterSets.Add(parameters);
            project.Singers.Add(singer);
        }

        return project;
    }

    private static void Sanitize(ProjectDocument project)
    {
        project.Lyrics ??= [];
        project.ParameterSets ??= [];
        project.Singers ??= [];

        foreach (var lyric in project.Lyrics)
        {
            if (string.IsNullOrWhiteSpace(lyric.Id))
                lyric.Id = Guid.NewGuid().ToString("N");
            lyric.Segments ??= [];
            lyric.PitchLine ??= [];
            lyric.Phrase ??= new LyricPhrase();
            lyric.Phrase.Words ??= [];
            if (lyric.Phrase.Words.Count == 0 && lyric.Segments.Count > 0)
                lyric.Phrase = LyricPhrase.FromSegments(lyric.Segments);

            // Notes may never overlap: legacy projects with stacked segments are pushed apart on load.
            if (SegmentLayout.RemoveOverlaps(lyric))
                SegmentLayout.SyncTransitionPoints(lyric);
        }

        foreach (var parameters in project.ParameterSets)
        {
            if (string.IsNullOrWhiteSpace(parameters.Id))
                parameters.Id = Guid.NewGuid().ToString("N");
            parameters.Base ??= new SynthesisSettings();
            parameters.Graph = SanitizeGraph(parameters.Graph);
        }

        var lyricIds = project.Lyrics.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var parameterIds = project.ParameterSets.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var singer in project.Singers)
        {
            if (string.IsNullOrWhiteSpace(singer.Id))
                singer.Id = Guid.NewGuid().ToString("N");
            singer.Graph = SanitizeGraph(singer.Graph);
            if (singer.LyricId is not null && !lyricIds.Contains(singer.LyricId))
                singer.LyricId = null;
            if (singer.ParameterSetId is not null && !parameterIds.Contains(singer.ParameterSetId))
                singer.ParameterSetId = null;
            if (!double.IsFinite(singer.MixGain))
                singer.MixGain = 1.0;
        }

        // A lyric must belong to at most one singer. Duplicate bindings are broken explicitly so the
        // one-to-one invariant never depends on UI behaviour.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var singer in project.Singers)
        {
            if (singer.LyricId is null)
                continue;
            if (!claimed.Add(singer.LyricId))
                singer.LyricId = null;
        }
    }

    /// <summary>
    /// Normalises a node graph after loading: unique node ids, known node kinds, valid output keys and only
    /// links whose endpoints and sockets still exist. Broken links are dropped instead of failing the load.
    /// </summary>
    private static ParameterNodeGraph SanitizeGraph(ParameterNodeGraph? graph)
    {
        graph ??= new ParameterNodeGraph();
        graph.Nodes ??= [];
        graph.Links ??= [];

        if (!double.IsFinite(graph.PanX))
            graph.PanX = 40;
        if (!double.IsFinite(graph.PanY))
            graph.PanY = 32;
        if (!double.IsFinite(graph.Zoom))
            graph.Zoom = 1.0;
        graph.Zoom = Math.Clamp(graph.Zoom, 0.2, 3.0);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = graph.Nodes[i];
            if (node is null || !Enum.IsDefined(node.Kind))
            {
                graph.Nodes.RemoveAt(i);
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
            {
                node.Id = Guid.NewGuid().ToString("N");
                ids.Add(node.Id);
            }

            node.Label ??= string.Empty;
            node.Function ??= "sin";
            node.Global ??= "beat";
            node.Text ??= string.Empty;
            node.BreathOutput ??= "gain";
            node.Curve ??= new ParameterCurve();
            node.Curve.Points ??= [];
            node.InputDefaults ??= [];
            node.FormantIndex = Math.Clamp(node.FormantIndex, 0, 6);
            node.Field = VoicebankField.Get(node.Field).Key;

            if (node.Kind == NodeKind.ParameterInput
                && (string.IsNullOrWhiteSpace(node.Text) || !ParameterKeys.TryGet(node.Text, out _)))
            {
                node.Text = "breathGain";
            }
        }

        // Older graphs used one output node per parameter key; fold them into the four summary sockets.
        graph.MigrateLegacyOutputs();

        for (var i = graph.Links.Count - 1; i >= 0; i--)
        {
            var link = graph.Links[i];
            if (link is null)
            {
                graph.Links.RemoveAt(i);
                continue;
            }

            var source = graph.Find(link.FromNode);
            var target = graph.Find(link.ToNode);
            if (source is null || target is null
                || !ParameterNodeGraph.CanConnect(source, link.FromSocket, target, link.ToSocket))
            {
                graph.Links.RemoveAt(i);
            }
        }

        var claimed = new HashSet<(string, int)>();
        for (var i = graph.Links.Count - 1; i >= 0; i--)
        {
            var link = graph.Links[i];
            if (!claimed.Add((link.ToNode, link.ToSocket)))
                graph.Links.RemoveAt(i);
        }

        return graph;
    }

    public static void Save(ProjectDocument project, string path, string? baseDirectory = null)
    {
        StageGuard.Run(nameof(ProjectJson), "save", () =>
        {
            if (project.IsLegacyReadOnly)
                throw new StageException(nameof(ProjectJson), "save",
                    "旧版本工程（v2）为只读格式，不能保存；请新建 v3 工程。");

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            AtomicFile.WriteAllText(full, Serialize(project, baseDirectory));
            project.ProjectDirectory ??= dir;
        });
    }

    public static ProjectDocument Load(string path, string? baseDirectory = null) =>
        StageGuard.Run(nameof(ProjectJson), "load",
            () => Deserialize(File.ReadAllText(path), baseDirectory));

    private static ProjectDocument ToPortable(ProjectDocument project, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return project;

        var copy = project.Clone();
        foreach (var lyric in copy.Lyrics)
        {
            foreach (var segment in lyric.Segments)
                segment.Audio = ToRelativePath(segment.Audio, baseDirectory) ?? string.Empty;
            foreach (var unit in lyric.Phrase.Words.SelectMany(w => w.Units))
                unit.Audio = ToRelativePath(unit.Audio, baseDirectory) ?? string.Empty;
        }
        foreach (var singer in copy.Singers)
            singer.LibraryRoot = ToRelativePath(singer.LibraryRoot, baseDirectory) ?? string.Empty;

        copy.BgmPath = ToRelativePath(copy.BgmPath, baseDirectory);
        return copy;
    }

    private static ProjectDocument ResolvePaths(ProjectDocument project, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return project;

        foreach (var lyric in project.Lyrics)
        {
            foreach (var segment in lyric.Segments)
                segment.Audio = ToAbsolutePath(segment.Audio, baseDirectory) ?? string.Empty;
            foreach (var unit in lyric.Phrase.Words.SelectMany(w => w.Units))
                unit.Audio = ToAbsolutePath(unit.Audio, baseDirectory) ?? string.Empty;
        }
        foreach (var singer in project.Singers)
            singer.LibraryRoot = ToAbsolutePath(singer.LibraryRoot, baseDirectory) ?? string.Empty;

        project.BgmPath = ToAbsolutePath(project.BgmPath, baseDirectory);
        return project;
    }

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

    // ---- Legacy v2 shapes (read only) ----------------------------------

    private sealed class LegacyProject
    {
        public string? Name { get; set; }
        public double Bpm { get; set; } = 120;
        public int BeatsPerBar { get; set; } = 3;
        public int BeatUnit { get; set; } = 4;
        public List<LegacyTrack>? Tracks { get; set; }
        public List<VoiceSegment>? Segments { get; set; }
        public List<PitchPoint>? PitchLine { get; set; }
        public SynthesisSettings? Settings { get; set; }
        public LegacyChorus? Chorus { get; set; }
        public string? BgmPath { get; set; }
        public double BgmStartBeat { get; set; }
    }

    private sealed class LegacyTrack
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? LibraryRoot { get; set; }
        public List<VoiceSegment>? Segments { get; set; }
        public LyricPhrase? LyricPhrase { get; set; }
        public List<PitchPoint>? PitchLine { get; set; }
        public SynthesisSettings? Settings { get; set; }
    }

    private sealed class LegacyChorus
    {
        public List<LegacySinger>? Singers { get; set; }
    }

    private sealed class LegacySinger
    {
        public string? TrackId { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
