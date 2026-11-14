using VocalB1.Core.Diagnostics;

namespace VocalB1.Core.Models;

/// <summary>Global variables available to every node graph at one synthesis frame.</summary>
public readonly record struct FrameGlobals(
    double TimeSeconds,
    double Beat,
    double Progress,
    double PitchHz,
    double PitchSemitone,
    string Vowel,
    string Phoneme,
    int SingerIndex,
    int SingerCount,
    int FrameIndex);

/// <summary>
/// Evaluates a <see cref="ParameterNodeGraph"/> for one frame. Evaluation is pull-based from the output
/// nodes, memoised per socket, and cycle-safe: a cycle, an unknown function or a non-finite result raises
/// a <see cref="StageException"/> naming the graph stage and the offending node — nothing is skipped.
/// </summary>
public sealed class NodeGraphEvaluator
{
    private const int MaxDepth = 96;

    private readonly ParameterNodeGraph _graph;
    private readonly FrameGlobals _globals;
    private readonly VoicebankFormantProfile _vowels;
    private readonly Func<string, double> _resolveBase;
    private readonly Func<string, double> _resolveOutputFallback;
    private readonly Dictionary<(string Node, int Socket), double> _cache = new();
    private readonly HashSet<(string Node, int Socket)> _visiting = new();

    /// <param name="resolveBase">Per-frame baseline value of any parameter key (measured formants included).</param>
    /// <param name="resolveOutputFallback">
    /// Value used when a <see cref="NodeKind.ParameterOutput"/> input is unwired. For a shared set this is
    /// the baseline; for a singer overlay it is the already evaluated shared value.
    /// </param>
    public NodeGraphEvaluator(
        ParameterNodeGraph graph,
        FrameGlobals globals,
        VoicebankFormantProfile vowels,
        Func<string, double> resolveBase,
        Func<string, double>? resolveOutputFallback = null)
    {
        _graph = graph;
        _globals = globals;
        _vowels = vowels;
        _resolveBase = resolveBase;
        _resolveOutputFallback = resolveOutputFallback ?? resolveBase;
    }

    /// <summary>
    /// Evaluates every parameter output node: each connected summary node writes the keys it packs. Keys
    /// whose summary is not connected keep their baseline value and are not written.
    /// </summary>
    public Dictionary<string, double> EvaluateOutputs()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var node in _graph.Nodes)
        {
            if (node.Kind != NodeKind.ParameterOutput || node.Muted)
                continue;

            for (var socket = 0; socket <= ParameterNodeGraph.BreathSocket; socket++)
                WriteSummary(node, socket, result);
        }
        return result;
    }

    /// <summary>Writes every key packed by the summary node connected to one output socket.</summary>
    private void WriteSummary(ParameterNode output, int socket, Dictionary<string, double> result)
    {
        if (_graph.LinkInto(output.Id, socket) is not { } link)
            return;
        if (_graph.Find(link.FromNode) is not { } summary
            || summary.Kind != ParameterNodeGraph.GroupKindOfSocket(socket))
            return;

        var keys = ParameterNodeGraph.KeysOfSocket(socket);
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            var value = summary.Muted
                ? _resolveOutputFallback(key)
                : SummaryInput(summary, index, key);

            if (!double.IsFinite(value))
                throw new StageException(nameof(NodeGraphEvaluator), $"summary:{key}",
                    $"汇总节点「{summary.DisplayTitle}」为「{key}」输出非有限值（{value}）。");
            result[key] = value;
        }
    }

    /// <summary>
    /// One input of a summary node. An unwired input falls back to the shared value (singer overlays) or
    /// to the baseline of that key (voicebank measurement, lyric override, then the built-in default).
    /// </summary>
    private double SummaryInput(ParameterNode summary, int index, string key)
    {
        var link = _graph.LinkInto(summary.Id, index);
        return link is null ? _resolveOutputFallback(key) : Output(link.FromNode, link.FromSocket);
    }

    private double Output(string nodeId, int socket)
    {
        var key = (nodeId, socket);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var node = _graph.Find(nodeId)
            ?? throw new StageException(nameof(NodeGraphEvaluator), "evaluate",
                $"节点图引用了不存在的节点 {nodeId}。");

        if (!_visiting.Add(key))
            throw new StageException(nameof(NodeGraphEvaluator), "cycle",
                $"节点「{node.DisplayTitle}」存在循环连接。");
        if (_visiting.Count > MaxDepth)
            throw new StageException(nameof(NodeGraphEvaluator), "depth",
                $"节点图连接深度超过 {MaxDepth}，可能存在循环。");

        try
        {
            var value = StageGuard.Run(nameof(NodeGraphEvaluator), $"{node.Kind}:{node.DisplayTitle}",
                () => Compute(node, socket));
            _cache[key] = value;
            return value;
        }
        finally
        {
            _visiting.Remove(key);
        }
    }

    /// <summary>Reads an input socket: the upstream node when wired, otherwise the socket default.</summary>
    private double Input(ParameterNode node, int index)
    {
        var link = _graph.LinkInto(node.Id, index);
        if (link is not null)
            return Output(link.FromNode, link.FromSocket);

        return node.InputDefault(index);
    }

    private double Compute(ParameterNode node, int socket)
    {
        if (node.Muted)
        {
            var sockets = NodeCatalog.Get(node.Kind).Inputs;
            return socket == 0 && sockets.Length > 0 && sockets[0].Type == SocketType.Scalar
                ? Input(node, 0)
                : 0;
        }

        switch (node.Kind)
        {
            case NodeKind.Number:
                return node.Value;

            case NodeKind.TimeInput:
                return Global(node.Global, _globals);

            case NodeKind.ParameterInput:
                return string.IsNullOrWhiteSpace(node.Text)
                    ? 0
                    : _resolveBase(node.Text);

            case NodeKind.TimeCurve:
                return node.Curve.Points.Count == 0
                    ? node.Value
                    : node.Curve.Evaluate(_globals.Beat);

            case NodeKind.TimeGradient:
            {
                var span = node.EndBeat - node.StartBeat;
                var t = Math.Abs(span) < 1e-9
                    ? 1.0
                    : Math.Clamp((_globals.Beat - node.StartBeat) / span, 0, 1);
                t = t * t * (3 - 2 * t);
                return node.Value + (node.ValueB - node.Value) * t;
            }

            case NodeKind.Noise:
                return SmoothNoise(node, _globals.Beat) * node.Amplitude;

            case NodeKind.Random:
                return WhiteNoise(node) * node.Amplitude;

            case NodeKind.CurveRemap:
                // Curve keys are used as the input coordinate (0..1 for a normalised remap).
                return node.Curve.Points.Count == 0
                    ? Input(node, 0)
                    : node.Curve.Evaluate(Math.Clamp(Input(node, 0), 0, 1));

            case NodeKind.MathUnary:
                return Unary(node.Function, Input(node, 0));

            case NodeKind.MathBinary:
                return Binary(node.Function, Input(node, 0), Input(node, 1));

            case NodeKind.MapRange:
            {
                var value = Input(node, 0);
                var fromMin = Input(node, 1);
                var fromMax = Input(node, 2);
                var toMin = Input(node, 3);
                var toMax = Input(node, 4);
                var span = fromMax - fromMin;
                var t = Math.Abs(span) < 1e-12 ? 1.0 : Math.Clamp((value - fromMin) / span, 0, 1);
                return toMin + (toMax - toMin) * t;
            }

            case NodeKind.Clamp:
                return Math.Clamp(Input(node, 0), Input(node, 1), Input(node, 2));

            case NodeKind.Mix:
            {
                var a = Input(node, 0);
                var b = Input(node, 1);
                var factor = Math.Clamp(Input(node, 2), 0, 1);
                return a + (b - a) * factor;
            }

            case NodeKind.Separate:
            {
                var value = Input(node, 0);
                return socket switch
                {
                    1 => value * Input(node, 2),
                    2 => value * Input(node, 3),
                    _ => value * Input(node, 1),
                };
            }

            case NodeKind.Combine:
                return Input(node, 0) + Input(node, 1) + Input(node, 2);

            case NodeKind.VowelManager:
                return Vowel(node);

            case NodeKind.BreathManager:
                return Breath(node);

            case NodeKind.VoicebankData:
                throw new StageException(nameof(NodeGraphEvaluator), "voicebank",
                    "声库数据是打包输出，请用字段分解节点读取字段。");

            case NodeKind.FieldDecompose:
                return Field(node);

            case NodeKind.FormantPosition:
            case NodeKind.FormantGain:
            case NodeKind.FormantBandwidth:
            case NodeKind.BreathParameters:
                throw new StageException(nameof(NodeGraphEvaluator), "summary",
                    $"汇总节点「{node.DisplayTitle}」只能连接到参数输出节点。");

            case NodeKind.ParameterOutput:
                throw new StageException(nameof(NodeGraphEvaluator), "output",
                    "参数输出节点不产生数值，它只负责汇总生效。");

            case NodeKind.Add:
                return Input(node, 0) + Input(node, 1);

            case NodeKind.Subtract:
                return Input(node, 0) - Input(node, 1);

            case NodeKind.Multiply:
                return Input(node, 0) * Input(node, 1);

            case NodeKind.Divide:
            {
                var divisor = Input(node, 1);
                return Math.Abs(divisor) < 1e-12 ? Input(node, 0) : Input(node, 0) / divisor;
            }

            default:
                throw new StageException(nameof(NodeGraphEvaluator), "compute",
                    $"未知节点类型：{node.Kind}");
        }
    }

    private double Vowel(ParameterNode node)
    {
        if (double.IsFinite(node.ManualValue))
            return node.ManualValue;

        var index = Math.Clamp(node.FormantIndex, 0, 6);
        var key = $"{ParameterKeys.FormantPrefix}{index + 1}";
        var vowel = VoicebankFormantProfile.NormalizeVowel(node.Text);

        // Empty text = follow the lyric: the baseline resolver already applies manual lyric formants,
        // then the measured voicebank profile, then the built-in table.
        if (string.IsNullOrWhiteSpace(node.Text))
            return _resolveBase(key);

        var formants = node.UseVoicebank
            ? _vowels.Resolve(vowel, null)
            : VoicebankFormantProfile.BuiltInResolve(vowel);
        return formants[index];
    }

    private double Breath(ParameterNode node)
    {
        if (double.IsFinite(node.ManualValue))
            return node.ManualValue;

        var vowel = string.IsNullOrWhiteSpace(node.Text)
            ? VoicebankFormantProfile.NormalizeVowel(_globals.Vowel)
            : VoicebankFormantProfile.NormalizeVowel(node.Text);
        var factor = node.UseVoicebank
            ? _vowels.BreathFactor(vowel)
            : VoicebankFormantProfile.BuiltInBreathFactor(vowel);

        return node.BreathOutput switch
        {
            "threshold" => Math.Clamp(0.3 / Math.Max(0.2, factor), 0, 1),
            "apScale" => Math.Clamp(0.25 * factor, 0, 1),
            "apMax" => Math.Clamp(0.20 * factor, 0, 1),
            _ => factor,
        };
    }

    /// <summary>One field of the current singer's voicebank; unwired voicebank input yields 0.</summary>
    private double Field(ParameterNode node)
    {
        if (_graph.LinkInto(node.Id, 0) is null)
            return 0;

        var field = VoicebankField.Get(node.Field);
        if (field.Key == VoicebankField.Breath.Key)
            return _vowels.BreathFactor(_globals.Vowel);

        var index = ParameterKeys.FormantIndexOf(field.Key);
        if (index < 0)
            return 0;
        var formants = _vowels.Resolve(_globals.Vowel, _globals.Phoneme);
        return formants[index];
    }

    private static double Unary(string function, double value) => function.ToLowerInvariant() switch
    {
        "sin" => Math.Sin(value),
        "cos" => Math.Cos(value),
        "tan" => Math.Tan(value),
        "asin" => Math.Asin(Math.Clamp(value, -1, 1)),
        "acos" => Math.Acos(Math.Clamp(value, -1, 1)),
        "atan" => Math.Atan(value),
        "abs" => Math.Abs(value),
        "sqrt" => Math.Sqrt(Math.Max(0, value)),
        "log" => Math.Log(Math.Max(1e-12, value)),
        "log10" => Math.Log10(Math.Max(1e-12, value)),
        "exp" => Math.Exp(Math.Clamp(value, -50, 50)),
        "floor" => Math.Floor(value),
        "ceil" => Math.Ceiling(value),
        "round" => Math.Round(value),
        "sign" => Math.Sign(value),
        "negate" => -value,
        "smoothstep" => Math.Clamp(value, 0, 1) * Math.Clamp(value, 0, 1) * (3 - 2 * Math.Clamp(value, 0, 1)),
        _ => throw new StageException(nameof(NodeGraphEvaluator), "unary",
            $"未知一元函数：{function}"),
    };

    private static double Binary(string function, double a, double b) => function.ToLowerInvariant() switch
    {
        "add" => a + b,
        "subtract" => a - b,
        "multiply" => a * b,
        "divide" => Math.Abs(b) < 1e-12 ? a : a / b,
        "pow" => Math.Pow(Math.Max(0, a), Math.Clamp(b, -20, 20)),
        "min" => Math.Min(a, b),
        "max" => Math.Max(a, b),
        "mod" => Math.Abs(b) < 1e-12 ? 0 : a % b,
        "atan2" => Math.Atan2(a, b),
        "lerp" => a + (b - a) * Math.Clamp(b, 0, 1),
        _ => throw new StageException(nameof(NodeGraphEvaluator), "binary",
            $"未知二元函数：{function}"),
    };

    private static double Global(string key, FrameGlobals globals) => key.ToLowerInvariant() switch
    {
        "time" => globals.TimeSeconds,
        "beat" => globals.Beat,
        "progress" => globals.Progress,
        "pitchhz" => globals.PitchHz,
        "pitch" => globals.PitchSemitone,
        "vowelindex" => VowelIndex(globals.Vowel),
        "singerindex" => globals.SingerIndex,
        "singercount" => globals.SingerCount,
        "frame" => globals.FrameIndex,
        _ => throw new StageException(nameof(NodeGraphEvaluator), "global",
            $"未知全局变量：{key}"),
    };

    public static int VowelIndex(string? vowel) => VoicebankFormantProfile.NormalizeVowel(vowel) switch
    {
        "a" => 0,
        "i" => 1,
        "u" => 2,
        "e" => 3,
        "o" => 4,
        "n" => 5,
        _ => 6,
    };

    /// <summary>Smooth deterministic value noise over the beat lattice.</summary>
    private double SmoothNoise(ParameterNode node, double beat)
    {
        var lattice = Math.Floor(beat);
        var fraction = beat - lattice;
        var a = LatticeNoise(node, (long)lattice);
        var b = LatticeNoise(node, (long)lattice + 1);
        var t = fraction * fraction * (3 - 2 * fraction);
        return a + (b - a) * t;
    }

    /// <summary>Deterministic per-frame white noise (same node, singer and frame → same value).</summary>
    private double WhiteNoise(ParameterNode node)
    {
        var hash = Mix(Fnv(node.Id) ^ (uint)(_globals.FrameIndex * 2654435761L)
            ^ (uint)(_globals.SingerIndex * 40503));
        return (hash % 2001) / 1000.0 - 1.0;
    }

    private static double LatticeNoise(ParameterNode node, long lattice)
    {
        var hash = Mix(Fnv(node.Id) ^ (uint)(lattice * 2654435761L));
        return (hash % 2001) / 1000.0 - 1.0;
    }

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return value;
    }
}
