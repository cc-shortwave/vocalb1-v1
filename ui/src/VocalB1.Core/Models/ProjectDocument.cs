using System.Text.Json.Serialization;

namespace VocalB1.Core.Models;

/// <summary>
/// Project data-block container (v3). A project owns three libraries — lyrics, parameter sets and singers —
/// and nothing else: there are no tracks and no stage. Singers are the render units; each references one
/// lyric (one-to-one) and one shared parameter set (many-to-one).
/// </summary>
public sealed class ProjectDocument
{
    /// <summary>Format version, used for migration. v3 is the object-library format.</summary>
    public string FormatVersion { get; set; } = "3.0";

    public string Name { get; set; } = "未命名工程";

    /// <summary>Project root directory (used to resolve relative audio paths when saving).</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>Tempo (BPM), which determines the number of frames per beat.</summary>
    public double Bpm { get; set; } = 120.0;

    /// <summary>Beats per bar (time-signature numerator).</summary>
    public int BeatsPerBar { get; set; } = 3;

    /// <summary>Beat unit (time-signature denominator).</summary>
    public int BeatUnit { get; set; } = 4;

    /// <summary>Lyric data-blocks (each belongs to at most one singer).</summary>
    public List<LyricDocument> Lyrics { get; set; } = [];

    /// <summary>Parameter data-blocks (shared by any number of singers).</summary>
    public List<ParameterSet> ParameterSets { get; set; } = [];

    /// <summary>Singers; the synthesizer is invoked once per singer.</summary>
    public List<SingerDocument> Singers { get; set; } = [];

    /// <summary>
    /// Project-wide BGM (backing track) WAV. Always plays at its original speed.
    /// </summary>
    public string? BgmPath { get; set; }

    /// <summary>BGM start offset in beats from the project start.</summary>
    public double BgmStartBeat { get; set; }

    /// <summary>
    /// True when this instance was converted from a legacy v2 project. Such projects are read-only:
    /// they can be validated and rendered, but edits and ordinary saving are disabled.
    /// </summary>
    [JsonIgnore]
    public bool IsLegacyReadOnly { get; set; }

    /// <summary>Total project length in beats, taken as the maximum across all lyrics.</summary>
    [JsonIgnore]
    public double TotalBeats => Lyrics.Count == 0 ? 0 : Lyrics.Max(l => l.TotalBeats);

    public LyricDocument? FindLyric(string? id) =>
        id is null ? null : Lyrics.FirstOrDefault(l => l.Id == id);

    public ParameterSet? FindParameterSet(string? id) =>
        id is null ? null : ParameterSets.FirstOrDefault(p => p.Id == id);

    public SingerDocument? FindSinger(string? id) =>
        id is null ? null : Singers.FirstOrDefault(s => s.Id == id);

    /// <summary>Number of singers referencing a lyric (Blender-style user count).</summary>
    public int LyricUsers(string lyricId) => Singers.Count(s => s.LyricId == lyricId);

    /// <summary>Number of singers referencing a parameter set.</summary>
    public int ParameterSetUsers(string parameterSetId) =>
        Singers.Count(s => s.ParameterSetId == parameterSetId);

    public DatablockUsage UsageOf(LyricDocument lyric) =>
        new(LyricUsers(lyric.Id), LyricUsers(lyric.Id) == 0 && !lyric.FakeUser);

    public DatablockUsage UsageOf(ParameterSet set) =>
        new(ParameterSetUsers(set.Id), ParameterSetUsers(set.Id) == 0 && !set.FakeUser);

    /// <summary>Orphaned data-blocks (zero users, no fake user) in the order they would be purged.</summary>
    public List<string> FindOrphanNames()
    {
        var orphans = new List<string>();
        orphans.AddRange(Lyrics.Where(l => LyricUsers(l.Id) == 0 && !l.FakeUser).Select(l => $"歌词：{l.Name}"));
        orphans.AddRange(ParameterSets
            .Where(p => ParameterSetUsers(p.Id) == 0 && !p.FakeUser)
            .Select(p => $"参数：{p.Name}"));
        return orphans;
    }

    /// <summary>Removes orphaned lyrics and parameter sets; returns the number of purged data-blocks.</summary>
    public int PurgeOrphans()
    {
        var lyrics = Lyrics.RemoveAll(l => LyricUsers(l.Id) == 0 && !l.FakeUser);
        var parameters = ParameterSets.RemoveAll(p => ParameterSetUsers(p.Id) == 0 && !p.FakeUser);
        return lyrics + parameters;
    }

    /// <summary>Generates a name that is unique among the given name list.</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(baseName))
            return baseName;

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} ({suffix++})";
        } while (names.Contains(candidate));
        return candidate;
    }

    public ProjectDocument Clone() => new()
    {
        FormatVersion = FormatVersion,
        Name = Name,
        ProjectDirectory = ProjectDirectory,
        Bpm = Bpm,
        BeatsPerBar = BeatsPerBar,
        BeatUnit = BeatUnit,
        BgmPath = BgmPath,
        BgmStartBeat = BgmStartBeat,
        IsLegacyReadOnly = IsLegacyReadOnly,
        Lyrics = Lyrics.Select(l => l.Clone()).ToList(),
        ParameterSets = ParameterSets.Select(p => p.Clone()).ToList(),
        Singers = Singers.Select(s => s.Clone()).ToList(),
    };
}
