using VocalB1.Core.Diagnostics;
using VocalB1.Core.Library;

namespace VocalB1.Core.Models;

/// <summary>
/// Resolves a lyric syllable to a wav path in dual mode:
/// <list type="number">
/// <item>a unit marked <see cref="VoiceSegment.PreferAbsoluteAudio"/> wins with its absolute path;</item>
/// <item>otherwise, when the lyric resolves from the voicebank, the phoneme is looked up in the singer's
/// bank (switching banks re-resolves automatically);</item>
/// <item>an existing absolute path is the fallback;</item>
/// <item>a missing sample is a hard failure with class + stage, never a silent drop.</item>
/// </list>
/// </summary>
public sealed class VoicebankSampleResolver
{
    private readonly Dictionary<string, string> _byLabel;

    public VoicebankSampleResolver(string voicebankRoot)
    {
        VoicebankRoot = voicebankRoot;
        _byLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(voicebankRoot))
        {
            foreach (var entry in LibraryScanner.Scan(voicebankRoot))
                _byLabel.TryAdd(entry.DisplayName, entry.Path);
        }
    }

    public string VoicebankRoot { get; }

    public string Resolve(VoiceSegment segment, string singerName, bool resolveFromVoicebank)
    {
        if (segment.PreferAbsoluteAudio
            && !string.IsNullOrWhiteSpace(segment.Audio)
            && File.Exists(segment.Audio))
        {
            return segment.Audio;
        }

        if (resolveFromVoicebank)
        {
            var phoneme = TryResolvePhoneme(segment);
            if (phoneme is not null)
                return phoneme;
        }

        if (!string.IsNullOrWhiteSpace(segment.Audio) && File.Exists(segment.Audio))
            return segment.Audio;

        var fallback = TryResolvePhoneme(segment);
        if (fallback is not null)
            return fallback;

        throw new StageException(nameof(VoicebankSampleResolver), "resolve",
            $"歌手「{singerName}」的声库中找不到音节「{segment.Phoneme ?? segment.Lyric}」对应的样本" +
            $"（声库：{VoicebankRoot}）。");
    }

    private string? TryResolvePhoneme(VoiceSegment segment)
    {
        foreach (var candidate in Candidates(segment))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (_byLabel.TryGetValue(candidate, out var path))
                return path;
            if (_byLabel.TryGetValue(candidate.TrimStart('_'), out path))
                return path;

            var direct = Path.Combine(VoicebankRoot, candidate + ".wav");
            if (File.Exists(direct))
                return direct;
            direct = Path.Combine(VoicebankRoot, "_" + candidate.TrimStart('_') + ".wav");
            if (File.Exists(direct))
                return direct;
        }

        return null;
    }

    private static IEnumerable<string> Candidates(VoiceSegment segment)
    {
        yield return segment.Phoneme ?? string.Empty;
        yield return segment.Lyric ?? string.Empty;
        yield return LyricPhrase.SampleLabel(segment.Audio);
        if (!string.IsNullOrWhiteSpace(segment.Audio))
            yield return Path.GetFileNameWithoutExtension(segment.Audio);
    }
}
