using VocalB1.Core.Pitch;

namespace VocalB1.Core.Models;

public enum ProjectIssueSeverity
{
    Warning,
    Error,
}

public sealed record ProjectIssue(ProjectIssueSeverity Severity, string Message, int? SegmentIndex = null);

/// <summary>
/// Pre-submit validation for a single track: segments, pitch line, and parameters.
/// Tempo/time signature belong to the project, so they must be passed in as well.
/// </summary>
public static class ProjectValidator
{
    public static IReadOnlyList<ProjectIssue> Validate(
        TrackDocument track,
        ProjectDocument project,
        bool checkAudioFiles = true)
    {
        var issues = new List<ProjectIssue>();

        if (track.Segments.Count == 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "至少需要 1 个发音段才能合成。"));

        for (var i = 0; i < track.Segments.Count; i++)
        {
            var seg = track.Segments[i];

            if (string.IsNullOrWhiteSpace(seg.Audio))
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"第 {i + 1} 段未选择音频。", i));
            }
            else if (checkAudioFiles && !File.Exists(seg.Audio))
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"第 {i + 1} 段音频不存在：{seg.Audio}", i));
            }

            if (seg.BeatEnd <= seg.BeatStart)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"第 {i + 1} 段结束拍位必须大于起始拍位。", i));
            else if (seg.BeatEnd - seg.BeatStart < 0.25)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, $"第 {i + 1} 段长度不足 1/4 拍。", i));

            if (!PitchMath.TryParseSemitones(seg.SrcPitch, out _))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"第 {i + 1} 段源音高无法解析：{seg.SrcPitch}", i));

            if (!PitchMath.TryParseSemitones(seg.DstPitch, out _))
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"第 {i + 1} 段目标音高无法解析：{seg.DstPitch}", i));

            if (seg.Transition < 0)
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, $"第 {i + 1} 段过渡帧数不能为负。", i));
        }

        if (track.PitchLine.Count is > 0 and < 2)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, "音高线至少需要 2 个端点，当前将忽略。"));
        else if (track.PitchLine.Count >= 2 && track.PitchLine[^1].Beat <= track.PitchLine[0].Beat)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "音高线端点拍位必须递增。"));

        foreach (var point in track.PitchLine.Where(p => p.Pitch is not null))
        {
            if (!string.Equals(point.Pitch, "KEEP", StringComparison.OrdinalIgnoreCase)
                && !PitchMath.TryParseSemitones(point.Pitch!, out _))
            {
                issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, $"音高线端点音高无法解析：{point.Pitch}"));
            }
        }

        if (project.Bpm <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "BPM 必须大于 0。"));

        if (project.BeatsPerBar <= 0 || project.BeatUnit <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "拍型必须为正整数。"));

        if (track.Settings.BitDepth is not (16 or 24))
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Warning, "输出位深仅支持 16 / 24。"));

        // Chorus state is project-wide, so it is reported once instead of once per track.
        if (project.Tracks.Count == 0 || project.Tracks[0] == track)
            issues.AddRange(ValidateChorus(project));

        return issues;
    }

    /// <summary>
    /// Stage geometry and singer paths are consumed directly by the chorus renderer, so invalid values
    /// (non-finite coordinates, out-of-stage singers, dangling track bindings) must surface before a render.
    /// </summary>
    private static IEnumerable<ProjectIssue> ValidateChorus(ProjectDocument project)
    {
        var chorus = project.Chorus;
        if (chorus is null)
            return [];

        var issues = new List<ProjectIssue>();

        if (!double.IsFinite(chorus.StageWidth) || chorus.StageWidth <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "合唱舞台宽度必须为正数。"));
        if (!double.IsFinite(chorus.StageDepth) || chorus.StageDepth <= 0)
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "合唱舞台纵深必须为正数。"));
        if (!double.IsFinite(chorus.MicX) || !double.IsFinite(chorus.MicY))
            issues.Add(new ProjectIssue(ProjectIssueSeverity.Error, "合唱麦克风坐标必须为有效数值。"));

        var halfWidth = double.IsFinite(chorus.StageWidth) ? chorus.StageWidth * 0.5 : 0;

        foreach (var singer in chorus.Singers)
        {
            var name = string.IsNullOrWhiteSpace(singer.Name) ? "未命名歌手" : singer.Name;

            if (singer.Enabled && string.IsNullOrWhiteSpace(singer.TrackId))
            {
                issues.Add(new ProjectIssue(
                    ProjectIssueSeverity.Warning, $"歌手「{name}」已启用但未绑定音轨，不会参与渲染。"));
            }
            else if (!string.IsNullOrWhiteSpace(singer.TrackId)
                     && !project.Tracks.Any(t => t.Id == singer.TrackId))
            {
                issues.Add(new ProjectIssue(
                    ProjectIssueSeverity.Warning, $"歌手「{name}」绑定的音轨已不存在，不会参与渲染。"));
            }

            if (singer.Path.Count == 0)
            {
                issues.Add(new ProjectIssue(
                    ProjectIssueSeverity.Warning, $"歌手「{name}」没有走位关键帧，将固定在舞台中心。"));
            }

            for (var i = 0; i < singer.Path.Count; i++)
            {
                var point = singer.Path[i];
                if (!double.IsFinite(point.Beat) || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    issues.Add(new ProjectIssue(
                        ProjectIssueSeverity.Error, $"歌手「{name}」第 {i + 1} 个走位关键帧包含无效数值。"));
                    continue;
                }

                if (point.Beat < 1)
                    issues.Add(new ProjectIssue(
                        ProjectIssueSeverity.Warning, $"歌手「{name}」第 {i + 1} 个走位关键帧拍位小于 1。"));
                if (Math.Abs(point.X) > halfWidth + 1e-6)
                    issues.Add(new ProjectIssue(
                        ProjectIssueSeverity.Warning, $"歌手「{name}」第 {i + 1} 个走位关键帧超出舞台横向范围。"));
                if (point.Y < -1e-6 || point.Y > chorus.StageDepth + 1e-6)
                    issues.Add(new ProjectIssue(
                        ProjectIssueSeverity.Warning, $"歌手「{name}」第 {i + 1} 个走位关键帧超出舞台纵深范围。"));
                if (i > 0 && point.Beat <= singer.Path[i - 1].Beat && double.IsFinite(singer.Path[i - 1].Beat))
                    issues.Add(new ProjectIssue(
                        ProjectIssueSeverity.Warning, $"歌手「{name}」走位关键帧拍位未严格递增。"));
            }
        }

        return issues;
    }

    public static bool HasError(IEnumerable<ProjectIssue> issues) =>
        issues.Any(i => i.Severity == ProjectIssueSeverity.Error);
}
