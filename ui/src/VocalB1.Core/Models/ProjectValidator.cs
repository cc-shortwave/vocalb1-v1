using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

public enum ProjectIssueSeverity
{
    Warning,
    Error,
}

public sealed record ProjectIssue(ProjectIssueSeverity Severity, string Message, int? SegmentIndex = null);

/// <summary>
/// Pre-render validation for the v3 object graph: singers, their lyric/parameter bindings, lyric scores,
/// formant targets and the shared beat grid.
/// </summary>
public static class ProjectValidator
{
    public static IReadOnlyList<ProjectIssue> Validate(ProjectDocument project, bool checkAudioFiles = true)
    {
        var issues = new List<ProjectIssue>();

        if (project.Bpm <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "BPM 必须大于 0。"));
        if (project.BeatsPerBar <= 0 || project.BeatUnit <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "拍型必须为正整数。"));

        var enabled = project.Singers.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "至少需要 1 个启用的歌手才能渲染。"));

        var claimedLyrics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var singer in enabled)
        {
            var name = string.IsNullOrWhiteSpace(singer.Name) ? "未命名歌手" : singer.Name;

            if (string.IsNullOrWhiteSpace(singer.LibraryRoot) || !Directory.Exists(singer.LibraryRoot))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, $"歌手「{name}」的声库目录不存在。"));

            var lyric = project.FindLyric(singer.LyricId);
            if (lyric is null)
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"歌手「{name}」未绑定歌词，无法渲染。"));
                continue;
            }

            if (!claimedLyrics.Add(lyric.Id))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                    $"歌词「{lyric.Name}」被多个歌手引用；歌词必须与歌手一对一。"));

            if (project.FindParameterSet(singer.ParameterSetId) is null)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"歌手「{name}」未绑定参数，无法渲染。"));

            ValidateLyric(lyric, name, issues, checkAudioFiles);
        }

        if (project.FindOrphanNames() is { Count: > 0 } orphans)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning,
                $"{orphans.Count} 个孤立数据块（0 用户且无伪用户）：{string.Join("、", orphans.Take(5))}"));

        return issues;
    }

    private static void ValidateLyric(LyricDocument lyric, string singerName,
        List<ProjectIssue> issues, bool checkAudioFiles)
    {
        if (lyric.Segments.Count == 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                $"歌词「{lyric.Name}」（歌手「{singerName}」）至少需要 1 个发音段才能合成。"));

        for (var i = 0; i < lyric.Segments.Count; i++)
        {
            var seg = lyric.Segments[i];
            var label = $"歌词「{lyric.Name}」第 {i + 1} 段";

            if (lyric.ResolveFromVoicebank && !string.IsNullOrWhiteSpace(seg.Phoneme))
            {
                // The singer's voicebank resolves this phoneme at render time; a stale absolute path
                // from another bank must not invalidate the project.
            }
            else if (string.IsNullOrWhiteSpace(seg.Audio))
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"{label}未解析到音频。", i));
            }
            else if (checkAudioFiles && !File.Exists(seg.Audio))
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"{label}音频不存在：{seg.Audio}", i));
            }

            if (seg.BeatEnd <= seg.BeatStart)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"{label}结束拍位必须大于起始拍位。", i));
            else if (seg.BeatEnd - seg.BeatStart < 0.25)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, $"{label}长度不足 1/4 拍。", i));

            if (!PitchMath.TryParseSemitones(seg.SrcPitch, out _))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"{label}源音高无法解析：{seg.SrcPitch}", i));
            if (!PitchMath.TryParseSemitones(seg.DstPitch, out _))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"{label}目标音高无法解析：{seg.DstPitch}", i));

            ValidateFormants(seg.FormantHz, $"{label}共振峰", issues, i);
        }

        if (lyric.PitchRaster is { } raster)
        {
            if (!double.IsFinite(raster.CellBeats) || raster.CellBeats <= 0)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                    $"歌词「{lyric.Name}」音高曲线格子长度非法。"));
            if (raster.Values.Any(v => v is { } value && !double.IsFinite(value) && !double.IsNaN(value)))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                    $"歌词「{lyric.Name}」音高曲线包含无效数值。"));
        }
        else
        {
            if (lyric.PitchLine.Count is > 0 and < 2)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning,
                    $"歌词「{lyric.Name}」音高线至少需要 2 个端点，当前将忽略。"));
            else if (lyric.PitchLine.Count >= 2 && lyric.PitchLine[^1].Beat <= lyric.PitchLine[0].Beat)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"歌词「{lyric.Name}」音高线端点拍位必须递增。"));

            foreach (var point in lyric.PitchLine.Where(p => p.Pitch is not null))
            {
                if (!string.Equals(point.Pitch, "KEEP", StringComparison.OrdinalIgnoreCase)
                    && !PitchMath.TryParseSemitones(point.Pitch!, out _))
                {
                    issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                        $"歌词「{lyric.Name}」音高线端点音高无法解析：{point.Pitch}"));
                }
            }
        }

        foreach (var unit in lyric.Phrase.Words.SelectMany(w => w.Units))
            ValidateFormants(unit.FormantHz, $"歌词「{lyric.Name}」音节「{unit.Text}」共振峰", issues, null);
    }

    private static void ValidateFormants(double[]? formants, string label,
        List<ProjectIssue> issues, int? index)
    {
        if (formants is null)
            return;

        if (formants.Length != 7)
        {
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                $"{label}需要 7 个目标位置，实际 {formants.Length} 个。", index));
            return;
        }

        for (var i = 0; i < formants.Length; i++)
        {
            if (!double.IsFinite(formants[i]) || formants[i] <= 0)
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                    $"{label} F{i + 1} 不是有效正数。", index));
                return;
            }
            if (i > 0 && formants[i] <= formants[i - 1])
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error,
                    $"{label} F{i + 1} 必须大于 F{i}。", index));
                return;
            }
        }
    }

    public static bool HasError(IEnumerable<ProjectIssue> issues) =>
        issues.Any(i => i.Severity == ProjectIssueSeverity.Error);
}
