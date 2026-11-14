namespace VocalB1.Core.Models;

/// <summary>
/// Applies word/unit objects onto a track: object-level diff, segment creation, rest conversion and
/// synchronization with segments edited outside the lyric bar.
/// </summary>
public static class LyricPhraseComposer
{
    /// <summary>Outcome of a text commit (word counts, not unit counts).</summary>
    public sealed record ApplyResult(int Reused, int Added, int Removed);

    /// <summary>
    /// Diffs <paramref name="template"/> against <paramref name="phrase"/> by identity (text + audio + rest),
    /// reuses matched words with their existing melody, removes dropped words (and their segments),
    /// and appends new words one default unit length each.
    /// </summary>
    public static ApplyResult Apply(
        LyricPhrase phrase,
        IReadOnlyList<LyricWord> template,
        LyricDocument track,
        double defaultUnitBeats,
        double defaultTransitionFrames,
        string defaultPitch)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(track);

        var oldWords = phrase.Words.ToList();
        var newWords = template.ToList();
        var pairs = MatchInOrder(
            oldWords.Select(w => w.IdentityKey).ToList(),
            newWords.Select(w => w.IdentityKey).ToList());

        var result = new List<LyricWord>();
        var reused = 0;
        var added = 0;
        var removed = 0;
        var oldCursor = 0;
        var newCursor = 0;

        foreach (var (oldIndex, newIndex) in pairs)
        {
            for (var i = oldCursor; i < oldIndex; i++)
            {
                RemoveWordSegments(oldWords[i], track);
                removed++;
            }

            for (var i = newCursor; i < newIndex; i++)
            {
                result.Add(CreateWord(newWords[i], track, NextStart(result, defaultUnitBeats),
                    defaultUnitBeats, defaultTransitionFrames, defaultPitch));
                added++;
            }

            result.Add(oldWords[oldIndex]);
            reused++;
            oldCursor = oldIndex + 1;
            newCursor = newIndex + 1;
        }

        for (var i = oldCursor; i < oldWords.Count; i++)
        {
            RemoveWordSegments(oldWords[i], track);
            removed++;
        }

        for (var i = newCursor; i < newWords.Count; i++)
        {
            result.Add(CreateWord(newWords[i], track, NextStart(result, defaultUnitBeats),
                defaultUnitBeats, defaultTransitionFrames, defaultPitch));
            added++;
        }

        phrase.Words.Clear();
        phrase.Words.AddRange(result);
        track.Segments.Sort((a, b) => a.BeatStart.CompareTo(b.BeatStart));

        return new ApplyResult(reused, added, removed);
    }

    /// <summary>
    /// Converts every pronunciation unit of a word into a rest of the same beat span and removes its
    /// segments. The original text stays in <see cref="LyricUnit.Text"/> for the detail tooltip.
    /// </summary>
    public static void MuteWord(LyricPhrase phrase, LyricWord word, LyricDocument track)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(track);

        foreach (var unit in word.Units)
        {
            if (unit.IsRest)
                continue;

            RemoveSegment(unit, track);
            unit.IsRest = true;
            unit.Audio = string.Empty;
            unit.Display = "休止";
        }
    }

    /// <summary>
    /// Removes a rest word from the phrase. When <paramref name="pullFollowing"/> is true, the words
    /// after it are moved left by its span so the strip stays contiguous.
    /// </summary>
    public static void RemoveWord(LyricPhrase phrase, LyricWord word, LyricDocument track, bool pullFollowing)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(track);

        var length = word.BeatEnd - word.BeatStart;
        var index = phrase.Words.IndexOf(word);
        RemoveWordSegments(word, track);
        phrase.Words.Remove(word);

        if (!pullFollowing || index < 0 || length <= 0)
            return;

        for (var i = index; i < phrase.Words.Count; i++)
            ShiftWord(phrase.Words[i], -length, track);
    }

    /// <summary>
    /// Keeps the phrase consistent with the track after edits made outside the lyric bar:
    /// segments that disappeared turn their unit into a rest of the same span, moved/resized segments
    /// update their unit's timing, and segments not yet represented gain a word object.
    /// Returns the number of changes.
    /// </summary>
    public static int SyncWithSegments(LyricPhrase phrase, LyricDocument track)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(track);

        var changes = 0;
        var segmentsById = new Dictionary<string, VoiceSegment>();
        foreach (var segment in track.Segments)
            segmentsById[segment.Id] = segment;

        foreach (var word in phrase.Words)
        {
            foreach (var unit in word.Units)
            {
                if (unit.IsRest)
                    continue;

                if (unit.SegmentId is not string id || !segmentsById.TryGetValue(id, out var segment))
                {
                    // Deleted outside the lyric bar: keep the beat span as a rest.
                    unit.IsRest = true;
                    unit.SegmentId = null;
                    unit.Audio = string.Empty;
                    unit.Display = "休止";
                    changes++;
                    continue;
                }

                var length = Math.Max(0.01, segment.BeatEnd - segment.BeatStart);
                if (Math.Abs(unit.BeatStart - segment.BeatStart) > 1e-9
                    || Math.Abs(unit.Length - length) > 1e-9)
                {
                    unit.BeatStart = segment.BeatStart;
                    unit.Length = length;
                    changes++;
                }

                if (!string.Equals(unit.Audio, segment.Audio, StringComparison.Ordinal))
                {
                    unit.Audio = segment.Audio;
                    unit.Display = LyricPhrase.SampleLabel(segment.Audio);
                    changes++;
                }
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in phrase.Words)
        foreach (var unit in word.Units)
        {
            if (!unit.IsRest && unit.SegmentId is { } id)
                referenced.Add(id);
        }

        foreach (var segment in track.Segments.OrderBy(s => s.BeatStart))
        {
            if (referenced.Contains(segment.Id))
                continue;

            var word = LyricPhrase.WordFromSegment(segment);
            var index = phrase.Words.FindIndex(w => w.BeatStart > word.BeatStart + 1e-9);
            if (index < 0)
                phrase.Words.Add(word);
            else
                phrase.Words.Insert(index, word);

            referenced.Add(segment.Id);
            changes++;
        }

        return changes;
    }

    private static double NextStart(List<LyricWord> result, double defaultUnitBeats)
    {
        if (result.Count > 0)
            return result[^1].BeatEnd;
        return 1.0;
    }

    private static LyricWord CreateWord(
        LyricWord template,
        LyricDocument track,
        double startBeat,
        double unitBeats,
        double transitionFrames,
        string defaultPitch)
    {
        var word = new LyricWord { Surface = template.Surface };
        // Notes may never overlap: the word slides past any segment that already occupies its beat range.
        var cursor = SegmentLayout.FindFreeStart(track, startBeat, template.Units.Count * unitBeats);

        foreach (var templateUnit in template.Units)
        {
            var unit = new LyricUnit
            {
                Text = templateUnit.Text,
                Audio = templateUnit.Audio,
                Display = templateUnit.Display,
                SrcPitch = templateUnit.SrcPitch,
                KeepUnvoicedHead = templateUnit.KeepUnvoicedHead,
                IsRest = templateUnit.IsRest,
                BeatStart = cursor,
                Length = unitBeats,
            };

            if (!unit.IsRest)
            {
                var segment = new VoiceSegment
                {
                    Audio = unit.Audio,
                    SrcPitch = unit.SrcPitch,
                    DstPitch = defaultPitch,
                    BeatStart = cursor,
                    BeatEnd = cursor + unitBeats,
                    Transition = track.Segments.Any(s => s.BeatEnd <= cursor + 1e-9)
                        ? transitionFrames
                        : 0,
                    KeepUnvoicedHead = unit.KeepUnvoicedHead,
                    LyricOverride = unit.Text,
                };
                track.Segments.Add(segment);
                track.AddPitchNodes(segment, 5);
                unit.SegmentId = segment.Id;
            }

            word.Units.Add(unit);
            cursor += unitBeats;
        }

        return word;
    }

    private static void RemoveSegment(LyricUnit unit, LyricDocument track)
    {
        if (unit.SegmentId is not string id)
            return;

        track.Segments.RemoveAll(s => s.Id == id);
        track.PitchLine.RemoveAll(p => p.SegmentId == id);
        unit.SegmentId = null;
    }

    private static void RemoveWordSegments(LyricWord word, LyricDocument track)
    {
        foreach (var unit in word.Units)
        {
            RemoveSegment(unit, track);
            unit.SegmentId = null;
        }
    }

    private static void ShiftWord(LyricWord word, double delta, LyricDocument track)
    {
        if (Math.Abs(delta) < 1e-12)
            return;

        foreach (var unit in word.Units)
        {
            unit.BeatStart += delta;

            if (unit.SegmentId is not string id)
                continue;

            var segment = track.Segments.FirstOrDefault(s => s.Id == id);
            if (segment is null)
                continue;

            segment.BeatStart += delta;
            segment.BeatEnd += delta;
            foreach (var point in track.PitchLine)
            {
                if (point.SegmentId == id)
                    point.Beat += delta;
            }
        }
    }

    /// <summary>
    /// Pairs old and new words in document order, each old word with the earliest still-unused new word
    /// of the same identity. Duplicated syllables therefore keep their first in-place melody instead of
    /// jumping to a later occurrence.
    /// </summary>
    private static List<(int OldIndex, int NewIndex)> MatchInOrder(
        IReadOnlyList<string> oldKeys,
        IReadOnlyList<string> newKeys)
    {
        var pairs = new List<(int, int)>();
        var newCursor = 0;
        for (var oldIndex = 0; oldIndex < oldKeys.Count; oldIndex++)
        {
            for (var newIndex = newCursor; newIndex < newKeys.Count; newIndex++)
            {
                if (!string.Equals(oldKeys[oldIndex], newKeys[newIndex], StringComparison.Ordinal))
                    continue;

                pairs.Add((oldIndex, newIndex));
                newCursor = newIndex + 1;
                break;
            }
        }

        return pairs;
    }
}
