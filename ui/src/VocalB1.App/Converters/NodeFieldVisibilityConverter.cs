using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VocalB1.Core.Models;

namespace VocalB1.App.Converters;

/// <summary>
/// Shows only the inspector fields that the selected node kind actually evaluates. Parameter is a field
/// tag such as <c>ValueA</c>, <c>Function</c>, <c>Beats</c> …
/// </summary>
public sealed class NodeFieldVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NodeKind kind || parameter is not string field)
            return Visibility.Visible;
        return Shows(kind, field) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool Shows(NodeKind kind, string field) => field switch
    {
        "ValueA" => kind is NodeKind.Number or NodeKind.TimeCurve or NodeKind.TimeGradient,
        "ValueB" => kind is NodeKind.TimeGradient,
        "Function" => kind is NodeKind.MathUnary or NodeKind.MathBinary,
        "Global" => kind is NodeKind.TimeInput,
        "Beats" => kind is NodeKind.TimeGradient,
        "Vowel" => kind is NodeKind.VowelManager or NodeKind.BreathManager,
        "FormantIndex" => kind is NodeKind.VowelManager,
        "BreathOutput" => kind is NodeKind.BreathManager,
        "Manual" => kind is NodeKind.VowelManager or NodeKind.BreathManager,
        "UseVoicebank" => kind is NodeKind.VowelManager or NodeKind.BreathManager,
        "Field" => kind is NodeKind.FieldDecompose,
        "ParameterKey" => kind is NodeKind.ParameterInput,
        "Amplitude" => kind is NodeKind.Noise or NodeKind.Random,
        "Curve" => kind is NodeKind.TimeCurve or NodeKind.CurveRemap,
        "Inputs" => NodeCatalog.Get(kind).Inputs.Length > 0
            && !IsSummary(kind)
            && NodeCatalog.Get(kind).Inputs.All(s => s.Type == SocketType.Scalar),
        "SummaryHint" => IsSummary(kind),
        _ => true,
    };

    /// <summary>Summary nodes feed the four output sockets; their unwired inputs follow the voicebank defaults.</summary>
    private static bool IsSummary(NodeKind kind) => kind is NodeKind.FormantPosition or NodeKind.FormantGain
        or NodeKind.FormantBandwidth or NodeKind.BreathParameters;
}
