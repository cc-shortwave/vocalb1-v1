using System.Globalization;
using System.IO;
using VocalB1.App.Controls;
using VocalB1.App.Infrastructure;
using VocalB1.App.Services;
using VocalB1.Core.Dictionaries;
using VocalB1.Core.Models;

namespace VocalB1.App.ViewModels;

/// <summary>A lyric data-block row in the outliner; name edits write straight back to the model.</summary>
public sealed class LyricRow(LyricDocument model) : ObservableObject
{
    public LyricDocument Model { get; } = model;

    public string Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value)
                return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public bool FakeUser
    {
        get => Model.FakeUser;
        set
        {
            if (Model.FakeUser == value)
                return;
            Model.FakeUser = value;
            OnPropertyChanged();
        }
    }

    public string Detail => LanguageService.TranslateFormat("{0} 段 · {1} 拍",
        Model.Segments.Count, Model.TotalBeats);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FakeUser));
        OnPropertyChanged(nameof(Detail));
    }

    public override string ToString() => Model.Name;
}

/// <summary>A parameter data-block row in the outliner.</summary>
public sealed class ParameterSetRow(ParameterSet model) : ObservableObject
{
    public ParameterSet Model { get; } = model;

    public string Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value)
                return;
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public bool FakeUser
    {
        get => Model.FakeUser;
        set
        {
            if (Model.FakeUser == value)
                return;
            Model.FakeUser = value;
            OnPropertyChanged();
        }
    }

    public string Detail => LanguageService.TranslateFormat("{0} 个节点 · {1} kHz",
        Model.Graph.Nodes.Count, Model.Base.SampleRate / 1000);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FakeUser));
        OnPropertyChanged(nameof(Detail));
    }

    public override string ToString() => Model.Name;
}

/// <summary>A singer data-block row. Users are delegated because the project graph may change.</summary>
public sealed class SingerRow(
    SingerDocument model,
    Func<string?, string?> resolveLyricName,
    Func<string?, string?> resolveParameterName,
    Action? onChanged = null) : ObservableObject
{
    public SingerDocument Model { get; } = model;

    public string Id => Model.Id;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value)
                return;
            Model.Name = value;
            OnPropertyChanged();
            onChanged?.Invoke();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
                return;
            Model.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Detail));
            onChanged?.Invoke();
        }
    }

    public double MixGain
    {
        get => Model.MixGain;
        set
        {
            value = Math.Clamp(value, 0, 2);
            if (Math.Abs(Model.MixGain - value) < 1e-9)
                return;
            Model.MixGain = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MixGainText));
            onChanged?.Invoke();
        }
    }

    public string MixGainText => Model.MixGain.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>X/Y/Z spatial trajectory edited by the singer page's curve editor.</summary>
    public SpatialTrajectory Spatial => Model.Spatial;

    public string LyricName => resolveLyricName(Model.LyricId) ?? LanguageService.Translate("未绑定歌词");

    public string ParameterName => resolveParameterName(Model.ParameterSetId) ?? LanguageService.Translate("未绑定参数");

    public string VoicebankName => string.IsNullOrWhiteSpace(Model.LibraryRoot)
        ? LanguageService.Translate("未选择声库")
        : Path.GetFileName(Model.LibraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string Detail => Enabled
        ? LanguageService.TranslateFormat("{0} · {1} · {2}", LyricName, ParameterName, VoicebankName)
        : LanguageService.TranslateFormat("已停用 · {0}", LyricName);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(MixGain));
        OnPropertyChanged(nameof(MixGainText));
        OnPropertyChanged(nameof(Spatial));
        OnPropertyChanged(nameof(LyricName));
        OnPropertyChanged(nameof(ParameterName));
        OnPropertyChanged(nameof(VoicebankName));
        OnPropertyChanged(nameof(Detail));
    }

    public override string ToString() => Model.Name;
}

/// <summary>A row of the segment list belonging to the active lyric.</summary>
public sealed class SegmentRow(VoiceSegment model, int index) : ObservableObject
{
    public int Number { get; } = index + 1;

    public VoiceSegment Model { get; } = model;

    public string Lyric
    {
        get => Model.Lyric;
        set
        {
            if (Model.LyricOverride == value)
                return;
            Model.LyricOverride = string.IsNullOrEmpty(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public string RangeText { get; } = $"{model.BeatStart:0.##} → {model.BeatEnd:0.##}";

    public string PitchText { get; } = $"{model.SrcPitch} → {model.DstPitch}";

    public string Vowel => Model.Vowel;

    public string FormantText => Model.FormantHz is { Length: 7 } hz
        ? string.Join(" / ", hz.Select(v => v.ToString("0")))
        : LanguageService.Translate("自动（元音管理器）");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Lyric));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(PitchText));
        OnPropertyChanged(nameof(Vowel));
        OnPropertyChanged(nameof(FormantText));
    }

    public override string ToString() => $"{Number} {Lyric} {RangeText} {PitchText}";
}

/// <summary>One minimal voicebank unit below the piano roll, with vowel/formant summary.</summary>
public sealed class LyricUnitRow(LyricUnit model) : ObservableObject
{
    public LyricUnit Model { get; } = model;

    public string Id => Model.Id;

    public string Source => Model.Text;

    public string Display => Model.Display;

    public string Vowel => Model.Vowel;

    public double Length => Model.Length;

    public double BeatStart => Model.BeatStart;

    public double BeatEnd => Model.BeatEnd;

    public bool IsRest => Model.IsRest;

    public string Caption => IsRest
        ? LanguageService.TranslateFormat("{0}(休止)", Display)
        : LanguageService.TranslateFormat("{0}({1})", Display, Source);

    public string BeatText => LanguageService.TranslateFormat("{0:0.##} → {1:0.##}（{2:0.##} 拍）",
        BeatStart, BeatEnd, Length);

    public string FormantText => Model.FormantHz is { Length: 7 } hz
        ? string.Join(" / ", hz.Select(v => v.ToString("0")))
        : LanguageService.Translate("自动");

    public string Detail => IsRest
        ? LanguageService.TranslateFormat("歌词：{0}（休止） · {1}",
            string.IsNullOrWhiteSpace(Model.Text)
                ? LanguageService.Translate("空格")
                : LanguageService.TranslateFormat("「{0}」", Model.Text),
            BeatText)
        : LanguageService.TranslateFormat("歌词「{0}」 · 音素 {1} · 元音 {2} · {3}",
            Source, Display, Vowel, BeatText);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Vowel));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(BeatStart));
        OnPropertyChanged(nameof(BeatEnd));
        OnPropertyChanged(nameof(IsRest));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(BeatText));
        OnPropertyChanged(nameof(FormantText));
        OnPropertyChanged(nameof(Detail));
    }

    /// <summary>Readable UI Automation name; without this the automation name falls back to the CLR type name.</summary>
    public override string ToString() => $"{Caption} · {Vowel} · {BeatText}";
}

/// <summary>A row in the parameter-key navigator; shows whether the graph writes this key.</summary>
public sealed class ParameterKeyRow(ParameterKeys.Definition definition) : ObservableObject
{
    public ParameterKeys.Definition Definition { get; } = definition;

    public string Key => Definition.Key;

    public string Label => NodeText.ParameterLabel(Definition.Key);

    public string Group => Definition.IsFormant
        ? (Definition.Key.Contains(".BW", StringComparison.Ordinal)
            ? LanguageService.Translate("共振峰带宽")
            : Definition.Key.Contains(".G", StringComparison.Ordinal)
                ? LanguageService.Translate("共振峰增益")
                : LanguageService.Translate("共振峰位置"))
        : Definition.Key.StartsWith("followRatio", StringComparison.Ordinal)
            ? LanguageService.Translate("跟随")
        : Definition.Key.StartsWith("bandRatios", StringComparison.Ordinal)
            ? LanguageService.Translate("频带")
        : LanguageService.Translate("合成参数");

    private bool _hasOutput;

    public string CountText => _hasOutput
        ? LanguageService.Translate("输出节点")
        : LanguageService.Translate("直通基础值");

    public void Refresh(bool hasOutput)
    {
        _hasOutput = hasOutput;
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Re-reads the translated group labels after a language switch.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(CountText));
    }

    public override string ToString() => Label;
}

/// <summary>
/// The inspector row for one graph node. Every edit writes straight back to the model and notifies the
/// view model so the canvas repaints and the render preview is invalidated.
/// </summary>
public sealed class NodeRow : ObservableObject
{
    private readonly ParameterNodeGraph _graph;

    public NodeRow(ParameterNode model, ParameterNodeGraph graph, Action? onChanged = null)
    {
        Model = model;
        _graph = graph;
        Changed = onChanged;
    }

    public ParameterNode Model { get; }

    private Action? Changed { get; }

    public string Id => Model.Id;

    public NodeKind Kind
    {
        get => Model.Kind;
        set
        {
            if (Model.Kind == value)
                return;
            Model.Kind = value;
            Model.InputDefaults.Clear();
            foreach (var socket in NodeCatalog.Get(value).Inputs)
                Model.InputDefaults.Add(socket.Default);
            _graph.Links.RemoveAll(l =>
                l.ToNode == Model.Id && l.ToSocket >= NodeCatalog.Get(value).Inputs.Length);
            Touch(nameof(Kind), nameof(KindTitle), nameof(Subtitle), nameof(Preview), nameof(InputDefaultsText));
        }
    }

    public string KindTitle => NodeText.Title(Model);

    public string Subtitle => NodeText.Subtitle(Model);

    public string Title
    {
        get => Model.Label;
        set
        {
            if (Model.Label == value)
                return;
            Model.Label = value ?? string.Empty;
            Touch(nameof(Title), nameof(Subtitle));
        }
    }

    public bool Muted
    {
        get => Model.Muted;
        set
        {
            if (Model.Muted == value)
                return;
            Model.Muted = value;
            Touch(nameof(Muted));
        }
    }

    public double Value
    {
        get => Model.Value;
        set
        {
            if (Math.Abs(Model.Value - value) < 1e-12)
                return;
            Model.Value = value;
            Touch(nameof(Value), nameof(Subtitle), nameof(Preview));
        }
    }

    public double ValueB
    {
        get => Model.ValueB;
        set
        {
            if (Math.Abs(Model.ValueB - value) < 1e-12)
                return;
            Model.ValueB = value;
            Touch(nameof(ValueB), nameof(Subtitle), nameof(Preview));
        }
    }

    public double StartBeat
    {
        get => Model.StartBeat;
        set
        {
            if (Math.Abs(Model.StartBeat - value) < 1e-12)
                return;
            Model.StartBeat = value;
            Touch(nameof(StartBeat), nameof(Subtitle), nameof(Preview));
        }
    }

    public double EndBeat
    {
        get => Model.EndBeat;
        set
        {
            if (Math.Abs(Model.EndBeat - value) < 1e-12)
                return;
            Model.EndBeat = value;
            Touch(nameof(EndBeat), nameof(Subtitle), nameof(Preview));
        }
    }

    public string Function
    {
        get => Model.Function;
        set
        {
            if (Model.Function == value)
                return;
            Model.Function = value;
            Touch(nameof(Function), nameof(Subtitle));
        }
    }

    public string Global
    {
        get => Model.Global;
        set
        {
            if (Model.Global == value)
                return;
            Model.Global = value;
            Touch(nameof(Global), nameof(Subtitle));
        }
    }

    /// <summary>Parameter key for parameter-input / parameter-output nodes.</summary>
    public string ParameterKey
    {
        get => Model.Text;
        set
        {
            if (Model.Text == value)
                return;
            Model.Text = value ?? string.Empty;
            Touch(nameof(ParameterKey), nameof(Subtitle));
        }
    }

    /// <summary>Literal vowel class for the vowel / breath managers; empty = follow the lyric.</summary>
    public string Vowel
    {
        get => Model.Text;
        set
        {
            if (Model.Text == value)
                return;
            Model.Text = value ?? string.Empty;
            Touch(nameof(Vowel), nameof(Subtitle));
        }
    }

    public int FormantIndex
    {
        get => Model.FormantIndex;
        set
        {
            value = Math.Clamp(value, 0, 6);
            if (Model.FormantIndex == value)
                return;
            Model.FormantIndex = value;
            Touch(nameof(FormantIndex), nameof(Subtitle));
        }
    }

    /// <summary>Field key read by a field-decompose node (see <see cref="VoicebankField"/>).</summary>
    public string Field
    {
        get => Model.Field;
        set
        {
            var field = VoicebankField.Get(value).Key;
            if (string.Equals(Model.Field, field, StringComparison.Ordinal))
                return;
            Model.Field = field;
            Touch(nameof(Field), nameof(Subtitle));
        }
    }

    public bool UseVoicebank
    {
        get => Model.UseVoicebank;
        set
        {
            if (Model.UseVoicebank == value)
                return;
            Model.UseVoicebank = value;
            Touch(nameof(UseVoicebank), nameof(Subtitle));
        }
    }

    /// <summary>Manual override; empty text clears the override back to NaN.</summary>
    public string ManualValueText
    {
        get => double.IsFinite(Model.ManualValue)
            ? Model.ManualValue.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            var manual = text.Length == 0
                ? double.NaN
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : double.NaN;
            if (double.IsNaN(manual) && double.IsNaN(Model.ManualValue))
                return;
            if (Math.Abs(manual - Model.ManualValue) < 1e-12)
                return;
            Model.ManualValue = manual;
            Touch(nameof(ManualValueText));
        }
    }

    public string BreathOutput
    {
        get => Model.BreathOutput;
        set
        {
            if (Model.BreathOutput == value)
                return;
            Model.BreathOutput = value;
            Touch(nameof(BreathOutput), nameof(Subtitle));
        }
    }

    public double Amplitude
    {
        get => Model.Amplitude;
        set
        {
            if (Math.Abs(Model.Amplitude - value) < 1e-12)
                return;
            Model.Amplitude = value;
            Touch(nameof(Amplitude), nameof(Subtitle), nameof(Preview));
        }
    }

    /// <summary>Unwired input socket values, in socket order: "0, 1, 1".</summary>
    public string InputDefaultsText
    {
        get => string.Join(", ", NodeCatalog.Get(Model.Kind).Inputs
            .Select((_, i) => Model.InputDefault(i).ToString("0.###", CultureInfo.InvariantCulture)));
        set
        {
            var sockets = NodeCatalog.Get(Model.Kind).Inputs;
            if (sockets.Length == 0)
                return;

            var parts = (value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != sockets.Length)
                throw new FormatException($"需要 {sockets.Length} 个输入默认值（用逗号分隔）。");

            var values = parts
                .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : throw new FormatException($"「{p}」不是数字。"))
                .ToArray();

            for (var i = 0; i < values.Length; i++)
                Model.SetInputDefault(i, values[i]);
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    /// <summary>Editable curve: "拍:值, 拍:值, ...".</summary>
    public string CurveText
    {
        get => string.Join(", ", Model.Curve.Points.Select(p => $"{p.Beat:0.##}:{p.Value:0.###}"));
        set
        {
            var points = new List<ParameterCurvePoint>();
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length != 2
                    || !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var beat)
                    || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                {
                    throw new FormatException($"曲线节点「{part.Trim()}」应为 拍:值。");
                }
                points.Add(new ParameterCurvePoint { Beat = beat, Value = val });
            }
            Model.Curve.Points.Clear();
            Model.Curve.Points.AddRange(points.OrderBy(p => p.Beat));
            Touch(nameof(CurveText), nameof(Subtitle), nameof(Preview));
        }
    }

    /// <summary>Sampled shape of this node, for the graphical preview.</summary>
    public IReadOnlyList<double> Preview
    {
        get
        {
            const int samples = 33;
            switch (Model.Kind)
            {
                case NodeKind.TimeCurve:
                {
                    var points = Model.Curve.Points;
                    if (points.Count < 2)
                        return [];
                    var from = points[0].Beat;
                    var to = points[^1].Beat;
                    if (Math.Abs(to - from) < 1e-9)
                        return [];
                    var result = new double[samples];
                    for (var i = 0; i < samples; i++)
                        result[i] = Model.Curve.Evaluate(from + (to - from) * i / (samples - 1));
                    return result;
                }
                case NodeKind.TimeGradient:
                {
                    var from = Model.StartBeat;
                    var to = Model.EndBeat;
                    if (Math.Abs(to - from) < 1e-9)
                        return [];
                    var result = new double[samples];
                    for (var i = 0; i < samples; i++)
                    {
                        var t = (double)i / (samples - 1);
                        t = t * t * (3 - 2 * t);
                        result[i] = Model.Value + (Model.ValueB - Model.Value) * t;
                    }
                    return result;
                }
                case NodeKind.CurveRemap:
                {
                    if (Model.Curve.Points.Count < 2)
                        return [];
                    var result = new double[samples];
                    for (var i = 0; i < samples; i++)
                        result[i] = Model.Curve.Evaluate((double)i / (samples - 1));
                    return result;
                }
                case NodeKind.Noise:
                case NodeKind.Random:
                {
                    var result = new double[samples];
                    for (var i = 0; i < samples; i++)
                    {
                        var t = 1 + i * 7.0 / (samples - 1);
                        var lattice = Math.Floor(t);
                        var fraction = t - lattice;
                        var smooth = fraction * fraction * (3 - 2 * fraction);
                        result[i] = Model.Kind == NodeKind.Noise
                            ? Math.Sin(lattice * 1.7 + Model.Amplitude) * Model.Amplitude * smooth
                            : Math.Sin(i * 2.3 + Model.Amplitude * 10) * Model.Amplitude;
                    }
                    return result;
                }
                default:
                    return [];
            }
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindTitle));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueB));
        OnPropertyChanged(nameof(StartBeat));
        OnPropertyChanged(nameof(EndBeat));
        OnPropertyChanged(nameof(Function));
        OnPropertyChanged(nameof(Global));
        OnPropertyChanged(nameof(ParameterKey));
        OnPropertyChanged(nameof(Vowel));
        OnPropertyChanged(nameof(FormantIndex));
        OnPropertyChanged(nameof(Field));
        OnPropertyChanged(nameof(UseVoicebank));
        OnPropertyChanged(nameof(ManualValueText));
        OnPropertyChanged(nameof(BreathOutput));
        OnPropertyChanged(nameof(Amplitude));
        OnPropertyChanged(nameof(InputDefaultsText));
        OnPropertyChanged(nameof(CurveText));
        OnPropertyChanged(nameof(Preview));
    }

    private void Touch(params string[] properties)
    {
        foreach (var property in properties)
            OnPropertyChanged(property);
        Changed?.Invoke();
    }
}

/// <summary>A row of the user dictionary list.</summary>
public sealed class DictionaryEntryRow(DictionaryEntry model) : ObservableObject
{
    public string Text { get; } = model.Text;
    public DictionaryEntry Model { get; } = model;

    public string Detail => LanguageService.TranslateFormat("{0} 音 · {1}",
        Model.Parts.Count,
        string.Join(" + ", Model.Parts.Select(p => Path.GetFileNameWithoutExtension(p.Audio))));

    public void Refresh() => OnPropertyChanged(nameof(Detail));
}

public sealed record LogEntry(DateTime Time, string Kind, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsWarning => Kind is "warn" or "error";
}

/// <summary>A row of the language combo box: shows the name declared by the language pack.</summary>
public sealed class LanguageRow(LanguagePack pack)
{
    public LanguagePack Pack { get; } = pack;
    public string Code => Pack.Code;
    public string Name => Pack.Name;

    public override string ToString() => Name;
}

/// <summary>A row of the voicebank combo box: the singer name is the folder name under voicebank/.</summary>
public sealed class VoicebankOption(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;

    public override string ToString() => Name;
}

/// <summary>A row of the voicebank-field picker, with the label translated for the UI.</summary>
public sealed class VoicebankFieldRow(VoicebankField field) : ObservableObject
{
    public VoicebankField Field { get; } = field;
    public string Key => Field.Key;
    public string Label => NodeText.Field(Field);

    public void Refresh() => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}

/// <summary>A row of a parameter slider: dragging writes back to the model.</summary>
public sealed class ParameterRow(
    string label, double minimum, double maximum, double step, double value, Action<double> apply)
    : ObservableObject
{
    private double _value = value;

    public string Label { get; } = label;
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
    public double Step { get; } = step;
    public double LargeStep => Step * 10;
    public string ValueText => _value.ToString("0.##", CultureInfo.InvariantCulture);

    public double Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value))
                return;
            OnPropertyChanged(nameof(ValueText));
            apply(value);
        }
    }
}
