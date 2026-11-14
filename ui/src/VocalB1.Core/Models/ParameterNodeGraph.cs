namespace VocalB1.Core.Models;

/// <summary>Interpolation applied between two keys of a <see cref="ParameterCurve"/>.</summary>
public enum CurveInterpolation
{
    Linear,
    Smooth,
    Step,
}

public sealed class ParameterCurvePoint
{
    public double Beat { get; set; } = 1.0;

    public double Value { get; set; }

    public ParameterCurvePoint Clone() => new() { Beat = Beat, Value = Value };
}

/// <summary>Piecewise curve over beats (or over a 0..1 remap coordinate).</summary>
public sealed class ParameterCurve
{
    public CurveInterpolation Interpolation { get; set; } = CurveInterpolation.Smooth;

    public List<ParameterCurvePoint> Points { get; set; } = [];

    /// <summary>Evaluates the curve at a 1-based beat; outside the key range the end values are held.</summary>
    public double Evaluate(double beat)
    {
        if (Points.Count == 0)
            return 0;

        var ordered = Points.OrderBy(p => p.Beat).ToList();
        if (beat <= ordered[0].Beat)
            return ordered[0].Value;
        if (beat >= ordered[^1].Beat)
            return ordered[^1].Value;

        for (var i = 1; i < ordered.Count; i++)
        {
            var a = ordered[i - 1];
            var b = ordered[i];
            if (beat > b.Beat)
                continue;

            var span = Math.Max(1e-9, b.Beat - a.Beat);
            var t = Math.Clamp((beat - a.Beat) / span, 0, 1);
            t = Interpolation switch
            {
                CurveInterpolation.Step => 0,
                CurveInterpolation.Smooth => t * t * (3 - 2 * t),
                _ => t,
            };
            return a.Value + (b.Value - a.Value) * t;
        }

        return ordered[^1].Value;
    }

    public ParameterCurve Clone() => new()
    {
        Interpolation = Interpolation,
        Points = Points.Select(p => p.Clone()).ToList(),
    };
}

/// <summary>
/// Node kinds available inside <see cref="ParameterNodeGraph"/>. The set mirrors Blender's shader editor:
/// inputs, curves, math utilities, voice-specific generators and one output node per parameter key.
/// </summary>
public enum NodeKind
{
    /// <summary>Fixed numeric value.</summary>
    Number,

    /// <summary>Global variable input (beat / time / progress / pitch / vowel index / singer index...).</summary>
    TimeInput,

    /// <summary>Reads the baseline value of another parameter key (including measured formants).</summary>
    ParameterInput,

    /// <summary>Piecewise curve over beats.</summary>
    TimeCurve,

    /// <summary>Two values blended by a smoothstep over a beat range.</summary>
    TimeGradient,

    /// <summary>Smooth deterministic value noise over beats.</summary>
    Noise,

    /// <summary>Per-frame deterministic white noise.</summary>
    Random,

    /// <summary>Unary function (sin / cos / abs / sqrt / log / exp...).</summary>
    MathUnary,

    /// <summary>Binary function (add / multiply / pow / min / max...).</summary>
    MathBinary,

    /// <summary>Remaps a value from one range to another with clamping.</summary>
    MapRange,

    /// <summary>Clamps a value between two bounds.</summary>
    Clamp,

    /// <summary>Linear mix of two values by a factor.</summary>
    Mix,

    /// <summary>Fan-out with one gain per output channel.</summary>
    Separate,

    /// <summary>Sums three inputs into one value.</summary>
    Combine,

    /// <summary>Remaps a 0..1 input through a curve.</summary>
    CurveRemap,

    /// <summary>Vowel manager: outputs an F1~F7 target position from the voicebank / lyric data.</summary>
    VowelManager,

    /// <summary>Breath manager: outputs a breath-related value derived from the vowel.</summary>
    BreathManager,

    /// <summary>Parameter output: the node that writes one canonical parameter key.</summary>
    ParameterOutput,

    /// <summary>A + B.</summary>
    Add,

    /// <summary>A − B.</summary>
    Subtract,

    /// <summary>A × B.</summary>
    Multiply,

    /// <summary>A ÷ B (division by zero returns A).</summary>
    Divide,

    /// <summary>Voicebank data input: the current singer's measured profile as one bundle.</summary>
    VoicebankData,

    /// <summary>Field decompose: picks one field out of the voicebank bundle.</summary>
    FieldDecompose,

    /// <summary>Formant position summary: packs F1~F7 target positions for the output node.</summary>
    FormantPosition,

    /// <summary>Formant gain summary: packs F1~F7 gains for the output node.</summary>
    FormantGain,

    /// <summary>Formant bandwidth summary: packs F1~F7 bandwidths for the output node.</summary>
    FormantBandwidth,

    /// <summary>Breath summary: packs the breath-related settings for the output node.</summary>
    BreathParameters,
}

/// <summary>
/// Socket data type. Scalar sockets carry one number; bundle sockets only connect to the matching bundle
/// (voicebank data, formant position / gain / bandwidth, breath), which keeps the four summary nodes wired
/// to exactly one input of the output node.
/// </summary>
public enum SocketType
{
    Scalar,
    Voicebank,
    FormantPosition,
    FormantGain,
    FormantBandwidth,
    Breath,
}

/// <summary>One socket of a node definition: its display name, unwired default value and data type.</summary>
public sealed record NodeSocket(string Name, double Default = 0, SocketType Type = SocketType.Scalar);

/// <summary>One numeric field exposed by the voicebank bundle, selectable in the field-decompose node.</summary>
public sealed record VoicebankField(string Key, string Label)
{
    public static readonly VoicebankField Breath = new("breath.factor", "气声系数");

    public static IReadOnlyList<VoicebankField> All { get; } =
    [
        .. Enumerable.Range(1, 7)
            .Select(i => new VoicebankField($"{ParameterKeys.FormantPrefix}{i}", $"F{i} 位置 (Hz)")),
        Breath,
    ];

    public static VoicebankField Get(string? key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal)) ?? All[0];

    public override string ToString() => Label;
}

/// <summary>Static description of a node kind: title, category, header colour and socket layout.</summary>
public sealed record NodeDefinition(
    NodeKind Kind,
    string Title,
    string Category,
    string Color,
    NodeSocket[] Inputs,
    NodeSocket[] Outputs);

/// <summary>
/// The node catalogue. Every node kind declares its sockets here so the model, the evaluator and the
/// canvas share one source of truth for layout and default values.
/// </summary>
public static class NodeCatalog
{
    public const string CategoryInput = "输入";
    public const string CategoryCurve = "曲线";
    public const string CategoryMath = "数学";
    public const string CategoryVoice = "发声";
    public const string CategorySummary = "汇总";
    public const string CategoryOutput = "输出";

    private static readonly Dictionary<NodeKind, NodeDefinition> Definitions = new();

    static NodeCatalog()
    {
        Add(new NodeDefinition(NodeKind.Number, "数值", CategoryInput, "#3F6F92",
            [], [new NodeSocket("值", 1)]));

        Add(new NodeDefinition(NodeKind.TimeInput, "时间输入", CategoryInput, "#3F6F92",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.ParameterInput, "参数读取", CategoryInput, "#3F6F92",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.VoicebankData, "声库数据", CategoryInput, "#3F6F92",
            [], [new NodeSocket("声库", Type: SocketType.Voicebank)]));

        Add(new NodeDefinition(NodeKind.FieldDecompose, "字段分解", CategoryInput, "#3F6F92",
            [new NodeSocket("声库", Type: SocketType.Voicebank)], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.TimeCurve, "时间曲线", CategoryCurve, "#7A4E9E",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.TimeGradient, "时间渐变", CategoryCurve, "#7A4E9E",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Noise, "噪波", CategoryCurve, "#7A4E9E",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Random, "随机抖动", CategoryCurve, "#7A4E9E",
            [], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.CurveRemap, "曲线映射", CategoryCurve, "#7A4E9E",
            [new NodeSocket("输入", 0.5)], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.MathUnary, "一元函数", CategoryMath, "#4E5C6E",
            [new NodeSocket("输入")], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.MathBinary, "二元函数", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B", 1)], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.MapRange, "范围映射", CategoryMath, "#4E5C6E",
            [
                new NodeSocket("值"), new NodeSocket("源最小", 0), new NodeSocket("源最大", 1),
                new NodeSocket("目标最小", 0), new NodeSocket("目标最大", 1),
            ], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Clamp, "限制范围", CategoryMath, "#4E5C6E",
            [new NodeSocket("值"), new NodeSocket("最小", 0), new NodeSocket("最大", 1)],
            [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Mix, "混合", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B", 1), new NodeSocket("系数", 0.5)],
            [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Separate, "拆分", CategoryMath, "#4E5C6E",
            [
                new NodeSocket("输入"), new NodeSocket("X 增益", 1),
                new NodeSocket("Y 增益", 1), new NodeSocket("Z 增益", 1),
            ], [new NodeSocket("X"), new NodeSocket("Y"), new NodeSocket("Z")]));

        Add(new NodeDefinition(NodeKind.Combine, "合并", CategoryMath, "#4E5C6E",
            [new NodeSocket("X"), new NodeSocket("Y", 0), new NodeSocket("Z", 0)],
            [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.VowelManager, "元音管理器", CategoryVoice, "#8C5A34",
            [], [new NodeSocket("位置 (Hz)", 800)]));

        Add(new NodeDefinition(NodeKind.BreathManager, "气声管理器", CategoryVoice, "#8C5A34",
            [], [new NodeSocket("值", 1)]));

        Add(new NodeDefinition(NodeKind.FormantPosition, "共振峰位置", CategorySummary, "#8C5A34",
            FormantInputs(ParameterKeys.FormantPrefix),
            [new NodeSocket("共振峰位置", Type: SocketType.FormantPosition)]));

        Add(new NodeDefinition(NodeKind.FormantGain, "共振峰增益", CategorySummary, "#8C5A34",
            FormantInputs(ParameterKeys.FormantGainPrefix),
            [new NodeSocket("共振峰增益", Type: SocketType.FormantGain)]));

        Add(new NodeDefinition(NodeKind.FormantBandwidth, "共振峰带宽", CategorySummary, "#8C5A34",
            FormantInputs(ParameterKeys.FormantBandwidthPrefix),
            [new NodeSocket("共振峰带宽", Type: SocketType.FormantBandwidth)]));

        Add(new NodeDefinition(NodeKind.BreathParameters, "气声参数", CategorySummary, "#8C5A34",
            [
                new NodeSocket("阈值", ParameterKeys.Get("breathyThr").Default),
                new NodeSocket("AP 强度", ParameterKeys.Get("apScale").Default),
                new NodeSocket("AP 上限", ParameterKeys.Get("apMax").Default),
                new NodeSocket("气声增益", ParameterKeys.Get("breathGain").Default),
            ],
            [new NodeSocket("气声参数", Type: SocketType.Breath)]));

        Add(new NodeDefinition(NodeKind.ParameterOutput, "参数输出", CategoryOutput, "#3F7A50",
            [
                new NodeSocket("共振峰位置", Type: SocketType.FormantPosition),
                new NodeSocket("共振峰增益", Type: SocketType.FormantGain),
                new NodeSocket("共振峰带宽", Type: SocketType.FormantBandwidth),
                new NodeSocket("气声参数", Type: SocketType.Breath),
            ], []));

        Add(new NodeDefinition(NodeKind.Add, "加法 +", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B")], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Subtract, "减法 −", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B")], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Multiply, "乘法 ×", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B", 1)], [new NodeSocket("值")]));

        Add(new NodeDefinition(NodeKind.Divide, "除法 ÷", CategoryMath, "#4E5C6E",
            [new NodeSocket("A"), new NodeSocket("B", 1)], [new NodeSocket("值")]));
    }

    private static void Add(NodeDefinition definition) => Definitions[definition.Kind] = definition;

    /// <summary>Seven scalar sockets F1~F7, defaulted to the parameter key's own default value.</summary>
    private static NodeSocket[] FormantInputs(string prefix) =>
        [.. Enumerable.Range(1, 7).Select(i => new NodeSocket($"F{i}", ParameterKeys.Get($"{prefix}{i}").Default))];

    public static NodeDefinition Get(NodeKind kind) => Definitions[kind];

    /// <summary>Display order used by every node picker; independent of enum values so saving stays stable.</summary>
    private static readonly NodeKind[] DisplayOrder =
    [
        NodeKind.Number, NodeKind.TimeInput, NodeKind.ParameterInput,
        NodeKind.VoicebankData, NodeKind.FieldDecompose,
        NodeKind.TimeCurve, NodeKind.TimeGradient, NodeKind.Noise, NodeKind.Random,
        NodeKind.CurveRemap,
        NodeKind.Add, NodeKind.Subtract, NodeKind.Multiply, NodeKind.Divide,
        NodeKind.MathUnary, NodeKind.MathBinary, NodeKind.MapRange, NodeKind.Clamp,
        NodeKind.Mix, NodeKind.Separate, NodeKind.Combine,
        NodeKind.VowelManager, NodeKind.BreathManager,
        NodeKind.FormantPosition, NodeKind.FormantGain, NodeKind.FormantBandwidth,
        NodeKind.BreathParameters,
        NodeKind.ParameterOutput,
    ];

    // Must stay a computed property: field initialisers run before the static constructor that fills
    // Definitions, so a cached list would be empty.
    public static IReadOnlyList<NodeDefinition> All =>
        DisplayOrder.Select(Get).ToList();

    public static string CategoryOf(NodeKind kind) => Get(kind).Category;
}

/// <summary>
/// One node in a parameter graph. Every kind stores all its settings on the same class so a node can be
/// retyped without losing its layout; only the fields used by <see cref="Kind"/> are evaluated.
/// </summary>
public sealed class ParameterNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public NodeKind Kind { get; set; } = NodeKind.Number;

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>When true the node is bypassed: it forwards its first input (Blender mute).</summary>
    public bool Muted { get; set; }

    /// <summary>Optional custom header title; empty = the catalogue title.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Number value / curve fallback / gradient start / first channel gain.</summary>
    public double Value { get; set; }

    /// <summary>Gradient end value / second channel gain.</summary>
    public double ValueB { get; set; } = 1.0;

    public double StartBeat { get; set; } = 1.0;

    public double EndBeat { get; set; } = 4.0;

    /// <summary>Function name for <see cref="NodeKind.MathUnary"/> / <see cref="NodeKind.MathBinary"/>.</summary>
    public string Function { get; set; } = "sin";

    /// <summary>Global variable key for <see cref="NodeKind.TimeInput"/>.</summary>
    public string Global { get; set; } = "beat";

    /// <summary>
    /// Parameter key for <see cref="NodeKind.ParameterInput"/> / <see cref="NodeKind.ParameterOutput"/>;
    /// literal vowel class for the vowel / breath managers (empty = follow the lyric).
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>0-based formant index for the vowel manager (F1 = 0).</summary>
    public int FormantIndex { get; set; }

    /// <summary>Field key picked by a <see cref="NodeKind.FieldDecompose"/> node (see <see cref="VoicebankField"/>).</summary>
    public string Field { get; set; } = "formant.F1";

    /// <summary>True = read measured formants from the singer's voicebank profile.</summary>
    public bool UseVoicebank { get; set; } = true;

    /// <summary>Manual override (Hz for the vowel manager, raw value for the breath manager); NaN = unused.</summary>
    public double ManualValue { get; set; } = double.NaN;

    /// <summary>Breath manager output selector: gain / threshold / apScale / apMax.</summary>
    public string BreathOutput { get; set; } = "gain";

    /// <summary>Noise / random amplitude.</summary>
    public double Amplitude { get; set; } = 0.1;

    public double Min { get; set; }

    public double Max { get; set; } = 1.0;

    public ParameterCurve Curve { get; set; } = new();

    /// <summary>Unwired input socket values. Missing entries fall back to the catalogue defaults.</summary>
    public List<double> InputDefaults { get; set; } = [];

    public string DisplayTitle => string.IsNullOrWhiteSpace(Label)
        ? NodeCatalog.Get(Kind).Title
        : Label;

    /// <summary>Compact description of the configured value, drawn inside the node body and in the list.</summary>
    public string Subtitle => Kind switch
    {
        NodeKind.Number => $"= {Value:0.###}",
        NodeKind.TimeInput => GlobalName(Global),
        NodeKind.ParameterInput => KeyLabel(Text),
        NodeKind.TimeCurve => Curve.Points.Count == 0 ? "空曲线" : $"{Curve.Points.Count} 个关键点",
        NodeKind.TimeGradient => $"{Value:0.###} → {ValueB:0.###}（{StartBeat:0.##}-{EndBeat:0.##} 拍）",
        NodeKind.Noise => $"±{Amplitude:0.###}",
        NodeKind.Random => $"±{Amplitude:0.###}",
        NodeKind.MathUnary => Function,
        NodeKind.MathBinary => Function,
        NodeKind.MapRange => "钳制重映射",
        NodeKind.Clamp => "钳制到输入范围",
        NodeKind.Mix => "A ↔ B",
        NodeKind.Separate => "1 → 3",
        NodeKind.Combine => "3 → 1",
        NodeKind.CurveRemap => Curve.Points.Count == 0 ? "直通" : $"曲线 {Curve.Points.Count} 点",
        NodeKind.VowelManager => $"F{FormantIndex + 1} · {(string.IsNullOrWhiteSpace(Text) ? "歌词元音" : Text)}",
        NodeKind.BreathManager => $"{BreathOutput} · {(string.IsNullOrWhiteSpace(Text) ? "歌词元音" : Text)}",
        NodeKind.ParameterOutput => "四路汇总 → 生效",
        NodeKind.VoicebankData => "当前歌手声库",
        NodeKind.FieldDecompose => VoicebankField.Get(Field).Label,
        NodeKind.FormantPosition => "F1~F7 → 位置",
        NodeKind.FormantGain => "F1~F7 → 增益",
        NodeKind.FormantBandwidth => "F1~F7 → 带宽",
        NodeKind.BreathParameters => "4 项 → 气声",
        NodeKind.Add => "A + B",
        NodeKind.Subtract => "A − B",
        NodeKind.Multiply => "A × B",
        NodeKind.Divide => "A ÷ B",
        _ => string.Empty,
    };

    public static string KeyLabel(string key) =>
        ParameterKeys.TryGet(key, out var definition) ? definition.Label : "未指定参数";

    public static string GlobalName(string key) => key.ToLowerInvariant() switch
    {
        "time" => "时间（秒）",
        "beat" => "拍",
        "progress" => "进度",
        "pitchhz" => "音高 (Hz)",
        "pitch" => "音高（半音）",
        "vowelindex" => "元音序号",
        "singerindex" => "歌手序号",
        "singercount" => "歌手总数",
        "frame" => "帧序号",
        _ => key,
    };

    public double InputDefault(int index)
    {
        if (index >= 0 && index < InputDefaults.Count)
            return InputDefaults[index];
        var sockets = NodeCatalog.Get(Kind).Inputs;
        return index >= 0 && index < sockets.Length ? sockets[index].Default : 0;
    }

    public void SetInputDefault(int index, double value)
    {
        if (index < 0)
            return;
        while (InputDefaults.Count <= index)
            InputDefaults.Add(InputDefault(InputDefaults.Count));
        InputDefaults[index] = value;
    }

    public ParameterNode Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        X = X,
        Y = Y,
        Muted = Muted,
        Label = Label,
        Value = Value,
        ValueB = ValueB,
        StartBeat = StartBeat,
        EndBeat = EndBeat,
        Function = Function,
        Global = Global,
        Text = Text,
        FormantIndex = FormantIndex,
        Field = Field,
        UseVoicebank = UseVoicebank,
        ManualValue = ManualValue,
        BreathOutput = BreathOutput,
        Amplitude = Amplitude,
        Min = Min,
        Max = Max,
        Curve = Curve.Clone(),
        InputDefaults = [.. InputDefaults],
    };
}

/// <summary>A directed connection from one node's output socket to another node's input socket.</summary>
public sealed class NodeLink
{
    public string FromNode { get; set; } = string.Empty;

    public int FromSocket { get; set; }

    public string ToNode { get; set; } = string.Empty;

    public int ToSocket { get; set; }

    public NodeLink Clone() => new()
    {
        FromNode = FromNode,
        FromSocket = FromSocket,
        ToNode = ToNode,
        ToSocket = ToSocket,
    };
}

/// <summary>
/// One node canvas per parameter set (or per singer overlay). Links are stored separately so the graph can
/// be edited, cloned and serialised without touching the evaluation code.
/// </summary>
public sealed class ParameterNodeGraph
{
    public List<ParameterNode> Nodes { get; set; } = [];

    public List<NodeLink> Links { get; set; } = [];

    /// <summary>Persisted canvas pan (pixels) and zoom, so reopening a project restores the view.</summary>
    public double PanX { get; set; } = 40;

    public double PanY { get; set; } = 32;

    public double Zoom { get; set; } = 1.0;

    public ParameterNode? Find(string? id) =>
        id is null ? null : Nodes.FirstOrDefault(n => n.Id == id);

    public NodeLink? LinkInto(string nodeId, int socket) =>
        Links.FirstOrDefault(l => l.ToNode == nodeId && l.ToSocket == socket);

    public ParameterNode Add(NodeKind kind, double x, double y)
    {
        var node = new ParameterNode
        {
            Kind = kind,
            X = x,
            Y = y,
            Value = kind == NodeKind.Number ? 1 : 0,
            ValueB = kind == NodeKind.TimeGradient ? 1 : 1,
            Min = 0,
            Max = kind switch
            {
                NodeKind.VowelManager => 8000,
                _ => 1,
            },
        };

        switch (kind)
        {
            case NodeKind.ParameterInput:
                node.Text = "breathGain";
                break;
            case NodeKind.TimeGradient:
                node.Value = 0;
                node.ValueB = 1;
                break;
            case NodeKind.TimeCurve:
                node.Curve.Points.Add(new ParameterCurvePoint { Beat = 1, Value = 0 });
                node.Curve.Points.Add(new ParameterCurvePoint { Beat = 4, Value = 1 });
                break;
            case NodeKind.VowelManager:
                node.UseVoicebank = true;
                node.FormantIndex = 0;
                break;
            case NodeKind.BreathManager:
                node.BreathOutput = "gain";
                break;
        }

        // Every input socket gets an explicit default so the inspector can edit them uniformly.
        var sockets = NodeCatalog.Get(kind).Inputs;
        for (var i = 0; i < sockets.Length; i++)
            node.InputDefaults.Add(sockets[i].Default);

        Nodes.Add(node);
        return node;
    }

    public void RemoveNode(string nodeId)
    {
        Nodes.RemoveAll(n => n.Id == nodeId);
        Links.RemoveAll(l => l.FromNode == nodeId || l.ToNode == nodeId);
    }

    /// <summary>Connects output → input; an existing link into the same input socket is replaced.</summary>
    public bool Connect(string fromNode, int fromSocket, string toNode, int toSocket)
    {
        if (fromNode == toNode
            || Find(fromNode) is not { } source || Find(toNode) is not { } target)
            return false;
        if (!CanConnect(source, fromSocket, target, toSocket))
            return false;

        Links.RemoveAll(l => l.ToNode == toNode && l.ToSocket == toSocket);
        Links.Add(new NodeLink
        {
            FromNode = fromNode,
            FromSocket = fromSocket,
            ToNode = toNode,
            ToSocket = toSocket,
        });
        return true;
    }

    /// <summary>Whether the two sockets exist and carry the same data type (scalar / bundle).</summary>
    public static bool CanConnect(ParameterNode source, int fromSocket, ParameterNode target, int toSocket)
    {
        var outputs = NodeCatalog.Get(source.Kind).Outputs;
        var inputs = NodeCatalog.Get(target.Kind).Inputs;
        if (fromSocket < 0 || fromSocket >= outputs.Length || toSocket < 0 || toSocket >= inputs.Length)
            return false;
        return outputs[fromSocket].Type == inputs[toSocket].Type;
    }

    // ------------------------------------------------------------------
    // Output routing: the four summary sockets of the parameter output node
    // ------------------------------------------------------------------
    public const int PositionSocket = 0;
    public const int GainSocket = 1;
    public const int BandwidthSocket = 2;
    public const int BreathSocket = 3;

    /// <summary>Breath keys packed by the breath summary node, in socket order.</summary>
    public static readonly string[] BreathKeys = ["breathyThr", "apScale", "apMax", "breathGain"];

    /// <summary>Summary node kind expected on the given output socket.</summary>
    public static NodeKind GroupKindOfSocket(int socket) => socket switch
    {
        PositionSocket => NodeKind.FormantPosition,
        GainSocket => NodeKind.FormantGain,
        BandwidthSocket => NodeKind.FormantBandwidth,
        BreathSocket => NodeKind.BreathParameters,
        _ => NodeKind.ParameterOutput,
    };

    /// <summary>Parameter keys packed by the given output socket.</summary>
    public static string[] KeysOfSocket(int socket) => socket switch
    {
        PositionSocket => [.. Enumerable.Range(1, 7).Select(i => $"{ParameterKeys.FormantPrefix}{i}")],
        GainSocket => [.. Enumerable.Range(1, 7).Select(i => $"{ParameterKeys.FormantGainPrefix}{i}")],
        BandwidthSocket => [.. Enumerable.Range(1, 7).Select(i => $"{ParameterKeys.FormantBandwidthPrefix}{i}")],
        BreathSocket => BreathKeys,
        _ => [],
    };

    /// <summary>Output socket that carries the given parameter key, or -1 when no summary covers it.</summary>
    public static int SocketOfKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return -1;
        for (var socket = 0; socket <= BreathSocket; socket++)
        {
            if (KeysOfSocket(socket).Contains(key, StringComparer.Ordinal))
                return socket;
        }
        return -1;
    }

    /// <summary>The first parameter output node of the graph (a graph only needs one).</summary>
    public ParameterNode? OutputNode() =>
        Nodes.FirstOrDefault(n => n.Kind == NodeKind.ParameterOutput);

    /// <summary>Parameter keys that the connected summary nodes write this frame.</summary>
    public List<string> WrittenKeys()
    {
        var keys = new List<string>();
        foreach (var output in Nodes.Where(n => n.Kind == NodeKind.ParameterOutput))
        {
            for (var socket = 0; socket <= BreathSocket; socket++)
            {
                if (LinkInto(output.Id, socket) is not { } link)
                    continue;
                if (Find(link.FromNode) is not { } group
                    || group.Kind != GroupKindOfSocket(socket))
                    continue;

                foreach (var key in KeysOfSocket(socket))
                {
                    if (!keys.Contains(key, StringComparer.Ordinal))
                        keys.Add(key);
                }
            }
        }
        return keys;
    }

    /// <summary>The summary node that writes the given key, or null when it falls back to its default.</summary>
    public ParameterNode? GroupFor(string? key)
    {
        var socket = SocketOfKey(key);
        if (socket < 0 || OutputNode() is not { } output)
            return null;
        if (LinkInto(output.Id, socket) is not { } link)
            return null;
        return Find(link.FromNode) is { } group && group.Kind == GroupKindOfSocket(socket) ? group : null;
    }

    public ParameterNodeGraph Clone() => new()
    {
        Nodes = Nodes.Select(n => n.Clone()).ToList(),
        Links = Links.Select(l => l.Clone()).ToList(),
        PanX = PanX,
        PanY = PanY,
        Zoom = Zoom,
    };

    /// <summary>
    /// Default graph for a fresh parameter set: voicebank data feeds a field-decompose node (available for
    /// patching), the four summary nodes are wired into the four sockets of a single parameter output node.
    /// An unwired summary input falls back to the voicebank / lyric value of that key.
    /// </summary>
    public static ParameterNodeGraph CreateFormantDefault()
    {
        // Laid out to fit the parameter canvas without panning: four summaries in a 2x2 block,
        // the output node to their right, and the voicebank chain underneath.
        var graph = new ParameterNodeGraph { Zoom = 0.66, PanX = 14, PanY = 8 };

        var position = graph.Add(NodeKind.FormantPosition, 10, 0);
        var bandwidth = graph.Add(NodeKind.FormantBandwidth, 210, 0);
        var gain = graph.Add(NodeKind.FormantGain, 10, 200);
        var breath = graph.Add(NodeKind.BreathParameters, 210, 200);
        var output = graph.Add(NodeKind.ParameterOutput, 420, 80);

        var voicebank = graph.Add(NodeKind.VoicebankData, 10, 400);
        var decompose = graph.Add(NodeKind.FieldDecompose, 210, 400);
        decompose.Field = $"{ParameterKeys.FormantPrefix}1";
        graph.Connect(voicebank.Id, 0, decompose.Id, 0);

        graph.Connect(position.Id, 0, output.Id, PositionSocket);
        graph.Connect(gain.Id, 0, output.Id, GainSocket);
        graph.Connect(bandwidth.Id, 0, output.Id, BandwidthSocket);
        graph.Connect(breath.Id, 0, output.Id, BreathSocket);

        return graph;
    }

    /// <summary>
    /// Converts pre-summary graphs (one output node per parameter key) into the four-socket layout:
    /// each old output's upstream node is rewired into the matching summary input, and one output node
    /// is kept. Keys outside the four summaries have no socket and are dropped with their old node.
    /// Returns true when the graph changed.
    /// </summary>
    public bool MigrateLegacyOutputs()
    {
        var legacy = Nodes
            .Where(n => n.Kind == NodeKind.ParameterOutput && !string.IsNullOrWhiteSpace(n.Text))
            .ToList();
        if (legacy.Count == 0)
            return false;

        var output = OutputNode() is { } existing && string.IsNullOrWhiteSpace(existing.Text)
            ? existing
            : null;

        foreach (var old in legacy)
        {
            var socket = SocketOfKey(old.Text);
            if (socket < 0 || !ParameterKeys.TryGet(old.Text, out _))
                continue;

            var groupKind = GroupKindOfSocket(socket);
            var group = Nodes.FirstOrDefault(n => n.Kind == groupKind)
                ?? Add(groupKind, Math.Max(0, old.X - 260), old.Y);

            var legacyLink = LinkInto(old.Id, 0);
            var upstream = legacyLink is null ? null : Find(legacyLink.FromNode);
            var upstreamSocket = legacyLink?.FromSocket ?? 0;
            var inputIndex = IndexOfKey(old.Text, socket);

            Links.RemoveAll(l => l.FromNode == old.Id || l.ToNode == old.Id);
            Nodes.Remove(old);

            if (upstream is not null && CanConnect(upstream, upstreamSocket, group, inputIndex))
                Connect(upstream.Id, upstreamSocket, group.Id, inputIndex);

            output ??= Add(NodeKind.ParameterOutput, old.X + 280, old.Y);
            Connect(group.Id, 0, output.Id, socket);
        }

        // Uncovered legacy outputs have no summary socket; they cannot be represented any more.
        Nodes.RemoveAll(n => n.Kind == NodeKind.ParameterOutput && !string.IsNullOrWhiteSpace(n.Text));
        Links.RemoveAll(l => Find(l.FromNode) is null || Find(l.ToNode) is null);
        return true;
    }

    /// <summary>Input index of a key inside its summary node (F2 → 1, breathGain → 3).</summary>
    public static int IndexOfKey(string key, int socket)
    {
        var keys = KeysOfSocket(socket);
        var index = Array.FindIndex(keys, k => string.Equals(k, key, StringComparison.Ordinal));
        return index < 0 ? 0 : index;
    }
}
