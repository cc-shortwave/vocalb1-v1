using System;
using System.Globalization;
using System.Windows.Data;
using VocalB1.Core.Models;

namespace VocalB1.App.Converters;

/// <summary>Chinese labels for the node enums, so the editor does not show raw C# names.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        NodeKind.Number => "数值",
        NodeKind.TimeInput => "时间输入",
        NodeKind.ParameterInput => "参数读取",
        NodeKind.TimeCurve => "时间曲线",
        NodeKind.TimeGradient => "时间渐变",
        NodeKind.Noise => "噪波",
        NodeKind.Random => "随机抖动",
        NodeKind.MathUnary => "一元函数",
        NodeKind.MathBinary => "二元函数",
        NodeKind.MapRange => "范围映射",
        NodeKind.Clamp => "限制范围",
        NodeKind.Mix => "混合",
        NodeKind.Separate => "拆分",
        NodeKind.Combine => "合并",
        NodeKind.CurveRemap => "曲线映射",
        NodeKind.VowelManager => "元音管理器",
        NodeKind.BreathManager => "气声管理器",
        NodeKind.ParameterOutput => "参数输出",
        NodeKind.Add => "加法 +",
        NodeKind.Subtract => "减法 −",
        NodeKind.Multiply => "乘法 ×",
        NodeKind.Divide => "除法 ÷",
        NodeKind.VoicebankData => "声库数据",
        NodeKind.FieldDecompose => "字段分解",
        NodeKind.FormantPosition => "共振峰位置",
        NodeKind.FormantGain => "共振峰增益",
        NodeKind.FormantBandwidth => "共振峰带宽",
        NodeKind.BreathParameters => "气声参数",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
