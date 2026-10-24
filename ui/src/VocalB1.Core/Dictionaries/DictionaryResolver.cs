using VocalB1.Core.Models;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Dictionaries;

/// <summary>
/// Expands dictionary entries into pronunciation segments for a project.
/// Performs only a deterministic "text → segment list" conversion: it neither reads/writes file contents nor starts the engine.
/// </summary>
public static class DictionaryResolver
{
    /// <summary>Default beat length per part (when neither the caller nor the entry specifies a length).</summary>
    public const double FallbackBeatsPerPart = 0.5;

    /// <summary>
    /// Resolves a single dictionary entry.
    /// </summary>
    /// <param name="entry">The dictionary entry.</param>
    /// <param name="dictionaryDirectory">Directory containing the dictionary file, used to resolve relative audio paths.</param>
    /// <param name="libraryRoot">Voicebank root directory, used as the second candidate for relative paths.</param>
    /// <param name="startBeat">Starting beat (1-based).</param>
    /// <param name="totalBeats">Total beat length; when ≤ 0, uses the entry's defaultBeats, then falls back to an estimate based on part count.</param>
    /// <param name="dstPitch">Target pitch; when null/empty, uses the entry's dstPitch, then falls back to "C4".</param>
    /// <param name="defaultSrcPitch">Dictionary-level default source pitch; when null/empty, uses "D4".</param>
    public static IReadOnlyList<VoiceSegment> Resolve(
        DictionaryEntry entry,
        string? dictionaryDirectory,
        string? libraryRoot,
        double startBeat,
        double totalBeats,
        string? dstPitch,
        string? defaultSrcPitch = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Parts.Count == 0)
            throw new InvalidOperationException($"字典条目 \"{entry.Text}\" 没有任何音频组合。");

        var beats = totalBeats > 0
            ? totalBeats
            : entry.DefaultBeats is > 0 ? entry.DefaultBeats.Value : entry.Parts.Count * FallbackBeatsPerPart;

        var target = string.IsNullOrWhiteSpace(dstPitch) ? entry.DstPitch : dstPitch;
        if (!PitchMath.TryParseSemitones(string.IsNullOrWhiteSpace(target) ? "C4" : target, out var baseSemitones))
            throw new InvalidOperationException($"目标音高无法解析：\"{target}\"");

        var weights = entry.Parts.Select(p => p.Weight > 0 ? p.Weight : 1.0).ToArray();
        var weightSum = weights.Sum();

        var segments = new List<VoiceSegment>(entry.Parts.Count);
        var cursor = startBeat;

        for (var i = 0; i < entry.Parts.Count; i++)
        {
            var part = entry.Parts[i];
            var audio = ResolveAudioPath(part.Audio, dictionaryDirectory, libraryRoot)
                ?? throw new InvalidOperationException(
                    $"找不到音频：\"{part.Audio}\"（已查找字典目录与声库根目录）。");

            var length = beats * weights[i] / weightSum;
            if (length <= 0)
                throw new InvalidOperationException($"条目 \"{entry.Text}\" 的第 {i + 1} 段拍长计算结果为 0。");

            segments.Add(new VoiceSegment
            {
                Audio = audio,
                SrcPitch = FirstNonEmpty(part.SrcPitch, entry.SrcPitch, defaultSrcPitch, "D4"),
                DstPitch = PitchMath.ToPitchName(baseSemitones + part.PitchOffset),
                BeatStart = cursor,
                BeatEnd = cursor + length,
                Transition = i == 0 ? 0 : part.Transition,
                KeepUnvoicedHead = part.KeepUnvoicedHead,
            });

            cursor += length;
        }

        return segments;
    }

    /// <summary>
    /// Resolves the audio path of a part: absolute path → dictionary directory → the dictionary's own voicebank root → voicebank root.
    /// Returns null when none of them exist.
    /// </summary>
    public static string? ResolveAudioPath(string? audio, string? dictionaryDirectory, string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(audio))
            return null;

        if (Path.IsPathRooted(audio))
            return File.Exists(audio) ? Path.GetFullPath(audio) : null;

        foreach (var root in new[] { dictionaryDirectory, libraryRoot })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var candidate = Path.GetFullPath(Path.Combine(root, audio));
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
