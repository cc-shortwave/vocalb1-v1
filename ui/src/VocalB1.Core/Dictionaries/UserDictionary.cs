using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VocalB1.Core.Dictionaries;

/// <summary>
/// User dictionary: maps a piece of unicode text (e.g. "きゃ", "あー") to a combination of audio files in a voicebank.
/// Pure data model; contains no path resolution or synthesis logic.
/// </summary>
public sealed class UserDictionary
{
    public string FormatVersion { get; set; } = "1.0";

    public string Name { get; set; } = "用户字典";

    public string? Description { get; set; }

    /// <summary>Optional: voicebank root directory bundled with the dictionary (second candidate for relative audio paths).</summary>
    public string? LibraryRoot { get; set; }

    /// <summary>Default value used when no source pitch is specified in the entry/part.</summary>
    public string DefaultSrcPitch { get; set; } = "D4";

    public List<DictionaryEntry> Entries { get; set; } = [];

    /// <summary>Structural issues (empty text, empty parts, invalid weight, etc.) reported on import.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            issues.Add("字典缺少名称。");
        if (Entries.Count == 0)
            issues.Add("字典没有任何条目。");

        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            var label = string.IsNullOrWhiteSpace(entry.Text) ? $"#{i + 1}" : entry.Text;
            if (string.IsNullOrWhiteSpace(entry.Text))
                issues.Add($"第 {i + 1} 个条目缺少 text。");
            if (entry.Parts.Count == 0)
                issues.Add($"条目 \"{label}\" 没有任何音频组合。");
            if (entry.DefaultBeats is <= 0)
                issues.Add($"条目 \"{label}\" 的 defaultBeats 必须大于 0。");
            for (var j = 0; j < entry.Parts.Count; j++)
            {
                var part = entry.Parts[j];
                if (string.IsNullOrWhiteSpace(part.Audio))
                    issues.Add($"条目 \"{label}\" 的第 {j + 1} 个组合缺少 audio。");
                if (part.Weight <= 0)
                    issues.Add($"条目 \"{label}\" 的第 {j + 1} 个组合 weight 必须大于 0。");
            }
        }

        return issues;
    }
}

/// <summary>An entry in the dictionary: one piece of text plus a list of audio parts.</summary>
public sealed class DictionaryEntry
{
    /// <summary>The mapped unicode text, e.g. "きゃ", "あー".</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Entry-level default source pitch; null = use the dictionary-level default.</summary>
    public string? SrcPitch { get; set; }

    /// <summary>Entry-level default target pitch; used when the caller does not specify one.</summary>
    public string? DstPitch { get; set; }

    /// <summary>Default used when the caller does not specify a beat length; null = 0.5 beat per part.</summary>
    public double? DefaultBeats { get; set; }

    public List<DictionaryPart> Parts { get; set; } = [];
}

/// <summary>A single audio part in an entry and its pronunciation parameters.</summary>
public sealed class DictionaryPart
{
    /// <summary>Audio path: an absolute path, or a file name relative to the dictionary directory / voicebank root.</summary>
    public string Audio { get; set; } = string.Empty;

    /// <summary>Source pitch for this part; null = use the entry/dictionary default.</summary>
    public string? SrcPitch { get; set; }

    /// <summary>Beat-length weight (positive), proportionally distributing the entry's total beat length.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Semitone offset relative to the target pitch, used for pitch variation within the combined sound.</summary>
    public double PitchOffset { get; set; }

    /// <summary>Number of overlapping transition frames with the previous part.</summary>
    public double Transition { get; set; }

    /// <summary>Keeps the unvoiced frames in the first half of this part.</summary>
    public bool KeepUnvoicedHead { get; set; }
}

/// <summary>Dictionary file I/O (*.json, camelCase, non-ASCII characters not escaped).</summary>
public static class DictionaryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(UserDictionary dictionary) =>
        JsonSerializer.Serialize(dictionary, Options);

    public static UserDictionary Deserialize(string json)
    {
        var dictionary = JsonSerializer.Deserialize<UserDictionary>(json, Options)
            ?? throw new InvalidDataException("字典 JSON 为空或无法解析。");

        dictionary.Entries ??= [];
        foreach (var entry in dictionary.Entries)
            entry.Parts ??= [];
        return dictionary;
    }

    public static UserDictionary Load(string path) =>
        Deserialize(File.ReadAllText(path));

    public static void Save(UserDictionary dictionary, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(full, Serialize(dictionary));
    }
}
