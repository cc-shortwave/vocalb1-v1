using VocalB1.Core.Models;
using VocalB1.Core.Pitch;

namespace VocalB1.Core.Dictionaries;

/// <summary>
/// One minimal unit token produced by <see cref="DictionaryResolver.Tokenize"/>: a single entry part
/// (span = the entry's text) or one whitespace character, which acts as a rest of the same length.
/// Token order matches the generated note-block order.
/// </summary>
public readonly record struct LyricToken(
    string Text, DictionaryEntry? Entry, DictionaryPart? Part, int Start, int Length)
{
    public bool IsRest => Entry is null || Part is null;
}

/// <summary>
/// Expands dictionary entries into pronunciation segments for a project.
/// Performs only a deterministic "text → segment list" conversion: it neither reads/writes file contents nor starts the engine.
/// </summary>
public static class DictionaryResolver
{
    /// <summary>Default beat length per part (when neither the caller nor the entry specifies a length).</summary>
    public const double FallbackBeatsPerPart = 0.5;

    /// <summary>A piece of lyric text matched to one dictionary entry.</summary>
    public readonly record struct TextMatch(string Text, DictionaryEntry Entry);

    /// <summary>
    /// Splits lyric text into minimal-unit tokens for the lyric bar: every entry part becomes one token
    /// sharing the entry's text span, and every whitespace character becomes one rest token.
    /// Punctuation and characters missing from the dictionary are skipped (they produce no token).
    /// </summary>
    public static IReadOnlyList<LyricToken> Tokenize(UserDictionary dictionary, string? text)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        var tokens = new List<LyricToken>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var byText = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        var maxLength = 0;
        foreach (var entry in dictionary.Entries)
        {
            if (string.IsNullOrEmpty(entry.Text))
                continue;
            byText.TryAdd(entry.Text, entry);
            maxLength = Math.Max(maxLength, entry.Text.Length);
        }

        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                tokens.Add(new LyricToken(" ", null, null, index, 1));
                index++;
                continue;
            }

            if (text[index] is '、' or '，' or '。' or '！' or '？')
            {
                index++;
                continue;
            }

            var matched = false;
            for (var length = Math.Min(maxLength, text.Length - index); length >= 1; length--)
            {
                var candidate = text.Substring(index, length);
                if (!byText.TryGetValue(candidate, out var entry))
                    continue;

                foreach (var part in entry.Parts)
                    tokens.Add(new LyricToken(candidate, entry, part, index, length));

                index += length;
                matched = true;
                break;
            }

            if (!matched)
                index++;
        }

        return tokens;
    }

    /// <summary>
    /// Splits lyric text into dictionary entries with longest-match-first semantics
    /// ("わたし" resolves as the word entry when the dictionary contains it, otherwise kana by kana).
    /// Whitespace and punctuation (、，。 spaces) are skipped; characters missing from the dictionary are ignored.
    /// </summary>
    public static IReadOnlyList<TextMatch> Split(UserDictionary dictionary, string? text)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        var matches = new List<TextMatch>();
        if (string.IsNullOrWhiteSpace(text))
            return matches;

        var byText = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
        var maxLength = 0;
        foreach (var entry in dictionary.Entries)
        {
            if (string.IsNullOrEmpty(entry.Text))
                continue;
            byText.TryAdd(entry.Text, entry);
            maxLength = Math.Max(maxLength, entry.Text.Length);
        }
        if (maxLength == 0)
            return matches;

        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] is '、' or '，' or '。' or '！' or '？')
            {
                index++;
                continue;
            }

            var matched = false;
            for (var length = Math.Min(maxLength, text.Length - index); length >= 1; length--)
            {
                var candidate = text.Substring(index, length);
                if (!byText.TryGetValue(candidate, out var entry))
                    continue;

                matches.Add(new TextMatch(candidate, entry));
                index += length;
                matched = true;
                break;
            }

            if (!matched)
                index++;
        }

        return matches;
    }

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
