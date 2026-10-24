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

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(ProjectDocument project) =>
        JsonSerializer.Serialize(project, Options);

    public static ProjectDocument Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<ProjectDocument>(json, Options)
            ?? throw new InvalidDataException("工程 JSON 为空或无法解析。");

        project.Tracks ??= [];

        // 1.x compatibility: top-level segments / pitchLine / settings → migrated to the first track.
        if (project.Tracks.Count == 0)
        {
            var legacy = JsonSerializer.Deserialize<LegacyProject>(json, Options);
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
            track.Segments ??= [];
            track.PitchLine ??= [];
            track.Settings ??= new SynthesisSettings();
        }

        return project;
    }

    public static void Save(ProjectDocument project, string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(full, Serialize(project));
        project.ProjectDirectory ??= dir;
    }

    public static ProjectDocument Load(string path) =>
        Deserialize(File.ReadAllText(path));

    /// <summary>Top-level structure of 1.x project files, used only for migration reads.</summary>
    private sealed class LegacyProject
    {
        public List<VoiceSegment> Segments { get; set; } = [];
        public List<PitchPoint> PitchLine { get; set; } = [];
        public SynthesisSettings? Settings { get; set; }
    }
}
