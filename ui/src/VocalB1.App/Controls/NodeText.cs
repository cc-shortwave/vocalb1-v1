using VocalB1.App.Services;
using VocalB1.Core.Models;

namespace VocalB1.App.Controls;

/// <summary>
/// Display text for the parameter graph and its node inspector. The node catalogue keeps its Chinese
/// source labels; this helper translates them (and the formatted subtitles) on the UI side only.
/// </summary>
public static class NodeText
{
    public static string Title(ParameterNode node) => LanguageService.Translate(node.DisplayTitle);

    public static string Socket(string name) => LanguageService.Translate(name);

    public static string Field(VoicebankField field)
    {
        if (string.Equals(field.Key, "breath.factor", StringComparison.Ordinal))
            return LanguageService.Translate("气声系数");

        var index = ParameterKeys.FormantIndexOf(field.Key);
        return index >= 0
            ? LanguageService.TranslateFormat("F{0} 位置 (Hz)", index + 1)
            : LanguageService.Translate(field.Label);
    }

    public static string ParameterLabel(string key)
    {
        if (!ParameterKeys.TryGet(key, out var definition))
            return LanguageService.Translate("未指定参数");

        if (key.StartsWith("followRatio.", StringComparison.Ordinal))
            return LanguageService.TranslateFormat("共振峰跟随 {0}", IndexAfterDot(key) + 1);
        if (key.StartsWith("bandRatios.", StringComparison.Ordinal))
            return LanguageService.TranslateFormat("频带增益 {0}", IndexAfterDot(key) + 1);
        if (key.StartsWith(ParameterKeys.FormantBandwidthPrefix, StringComparison.Ordinal))
            return LanguageService.TranslateFormat("F{0} 带宽 (Hz)", definition.FormantIndex + 1);
        if (key.StartsWith(ParameterKeys.FormantGainPrefix, StringComparison.Ordinal))
            return LanguageService.TranslateFormat("F{0} 增益", definition.FormantIndex + 1);
        if (key.StartsWith(ParameterKeys.FormantPrefix, StringComparison.Ordinal))
            return LanguageService.TranslateFormat("F{0} 位置 (Hz)", definition.FormantIndex + 1);

        return LanguageService.Translate(definition.Label);
    }

    public static string Subtitle(ParameterNode node) => node.Kind switch
    {
        NodeKind.Number => $"= {node.Value:0.###}",
        NodeKind.TimeInput => GlobalName(node.Global),
        NodeKind.ParameterInput => ParameterLabel(node.Text),
        NodeKind.TimeCurve => node.Curve.Points.Count == 0
            ? LanguageService.Translate("空曲线")
            : LanguageService.TranslateFormat("{0} 个关键点", node.Curve.Points.Count),
        NodeKind.TimeGradient => LanguageService.TranslateFormat(
            "{0:0.###} → {1:0.###}（{2:0.##}-{3:0.##} 拍）",
            node.Value, node.ValueB, node.StartBeat, node.EndBeat),
        NodeKind.Noise => $"±{node.Amplitude:0.###}",
        NodeKind.Random => $"±{node.Amplitude:0.###}",
        NodeKind.MathUnary => node.Function,
        NodeKind.MathBinary => node.Function,
        NodeKind.MapRange => LanguageService.Translate("钳制重映射"),
        NodeKind.Clamp => LanguageService.Translate("钳制到输入范围"),
        NodeKind.Mix => "A ↔ B",
        NodeKind.Separate => "1 → 3",
        NodeKind.Combine => "3 → 1",
        NodeKind.CurveRemap => node.Curve.Points.Count == 0
            ? LanguageService.Translate("直通")
            : LanguageService.TranslateFormat("曲线 {0} 点", node.Curve.Points.Count),
        NodeKind.VowelManager => $"F{node.FormantIndex + 1} · {VowelText(node.Text)}",
        NodeKind.BreathManager => $"{node.BreathOutput} · {VowelText(node.Text)}",
        NodeKind.ParameterOutput => LanguageService.Translate("四路汇总 → 生效"),
        NodeKind.VoicebankData => LanguageService.Translate("当前歌手声库"),
        NodeKind.FieldDecompose => Field(VoicebankField.Get(node.Field)),
        NodeKind.FormantPosition => LanguageService.Translate("F1~F7 → 位置"),
        NodeKind.FormantGain => LanguageService.Translate("F1~F7 → 增益"),
        NodeKind.FormantBandwidth => LanguageService.Translate("F1~F7 → 带宽"),
        NodeKind.BreathParameters => LanguageService.Translate("4 项 → 气声"),
        NodeKind.Add => "A + B",
        NodeKind.Subtract => "A − B",
        NodeKind.Multiply => "A × B",
        NodeKind.Divide => "A ÷ B",
        _ => node.Subtitle,
    };

    public static string GlobalName(string key) => key.ToLowerInvariant() switch
    {
        "time" => LanguageService.Translate("时间（秒）"),
        "beat" => LanguageService.Translate("拍"),
        "progress" => LanguageService.Translate("进度"),
        "pitchhz" => LanguageService.Translate("音高 (Hz)"),
        "pitch" => LanguageService.Translate("音高（半音）"),
        "vowelindex" => LanguageService.Translate("元音序号"),
        "singerindex" => LanguageService.Translate("歌手序号"),
        "singercount" => LanguageService.Translate("歌手总数"),
        "frame" => LanguageService.Translate("帧序号"),
        _ => key,
    };

    private static string VowelText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? LanguageService.Translate("歌词元音") : text;

    private static int IndexAfterDot(string key) =>
        int.TryParse(key[(key.LastIndexOf('.') + 1)..], out var index) ? index : 0;
}
