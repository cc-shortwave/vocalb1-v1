using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VocalB1.Core.Models;

namespace VocalB1.App.Controls;

/// <summary>
/// Implemented by the view model that owns the canvas, so the control can push undo steps and refresh
/// derived lists without knowing anything about the window.
/// </summary>
public interface INodeGraphHost
{
    /// <summary>Called once before a structural graph edit (drag, connect, add, delete).</summary>
    void PushGraphUndo();

    /// <summary>Called after a graph edit so counts, previews and the inspector stay in sync.</summary>
    void NotifyGraphChanged();

    /// <summary>Parameter key that should be pre-filled when a new output node is created.</summary>
    string? SuggestedOutputKey();
}

/// <summary>
/// Blender-style node canvas. Draws nodes, sockets and bezier noodles, and supports the full editing set:
/// drag nodes, drag a noodle between sockets, box select, Delete, wheel zoom, middle-drag pan and a
/// right-click add-node menu. The graph model and its pan/zoom are persisted in the project file.
/// </summary>
public sealed class NodeGraphView : Canvas
{
    private const double NodeWidth = 178;
    private const double HeaderHeight = 26;
    private const double SubtitleHeight = 16;
    private const double RowHeight = 20;
    private const double BodyPadding = 6;
    private const double SocketRadius = 5;
    private const double SocketHitRadius = 9;
    private const double NodeRadius = 7;

    private static readonly Color CanvasColor = Color.FromRgb(0x1A, 0x1A, 0x1C);
    private static readonly Color BodyColor = Color.FromRgb(0x2B, 0x2B, 0x2E);
    private static readonly Color BodyBorderColor = Color.FromRgb(0x12, 0x12, 0x14);
    private static readonly Color SubtitleColor = Color.FromRgb(0x9E, 0x9E, 0xA4);
    private static readonly Color SocketColor = Color.FromRgb(0x8E, 0x8E, 0x96);
    private static readonly Color SelectionColor = Color.FromRgb(0xE8, 0xA3, 0x3D);
    private static readonly Color TextColor = Color.FromRgb(0xF2, 0xF2, 0xF4);

    private readonly List<ParameterNode> _selection = [];
    private readonly Brush _canvasBrush = Frozen(CanvasColor);
    private readonly Typeface _typeface = new("Microsoft YaHei UI, Segoe UI");
    private readonly Typeface _boldTypeface = new(new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Point _dragStartScreen;
    private Point _dragStartGraph;
    private bool _moving;
    private bool _boxSelecting;
    private Point _boxStart;
    private Point _boxCurrent;
    private bool _panning;
    private Point _panStartScreen;
    private double _panStartX;
    private double _panStartY;
    private ParameterNode? _linkSourceNode;
    private int _linkSourceSocket;
    private bool _linkSourceIsOutput;
    private Point _linkCurrent;
    private bool _syncingSelection;
    private Point _lastContextPoint;

    public NodeGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        Background = _canvasBrush;
        SnapsToDevicePixels = true;
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => BuildContextMenu();
    }

    // ------------------------------------------------------------------
    // Dependency properties
    // ------------------------------------------------------------------
    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(ParameterNodeGraph), typeof(NodeGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((NodeGraphView)d).OnGraphChanged()));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(NodeGraphView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameRevisionProperty = DependencyProperty.Register(
        nameof(FrameRevision), typeof(int), typeof(NodeGraphView),
        new FrameworkPropertyMetadata(0, (d, _) => ((NodeGraphView)d).FrameAll()));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(ParameterNode), typeof(NodeGraphView),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((NodeGraphView)d).OnSelectedNodeChanged(e.NewValue as ParameterNode)));

    public ParameterNodeGraph? Graph
    {
        get => (ParameterNodeGraph?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    /// <summary>Bumped by the view model to ask the canvas to frame all nodes.</summary>
    public int FrameRevision
    {
        get => (int)GetValue(FrameRevisionProperty);
        set => SetValue(FrameRevisionProperty, value);
    }

    /// <summary>Bumped by the view model on any graph edit so inspector changes repaint the canvas.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public ParameterNode? SelectedNode
    {
        get => (ParameterNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    private INodeGraphHost? Host => DataContext as INodeGraphHost;

    private double Zoom => Graph is { Zoom: > 0 } graph ? graph.Zoom : 1.0;

    private double PanX => Graph?.PanX ?? 0;

    private double PanY => Graph?.PanY ?? 0;

    /// <summary>Repaints after the view model mutated the graph.</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>Fits every node into the viewport (Blender's Home key).</summary>
    public void FrameAll()
    {
        if (Graph is not { Nodes.Count: > 0 } graph
            || ActualWidth < 40 || ActualHeight < 40)
            return;

        var bounds = Rect.Empty;
        foreach (var node in graph.Nodes)
            bounds.Union(NodeRect(node));
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            return;

        var zoom = Math.Clamp(Math.Min(
            (ActualWidth - 60) / bounds.Width,
            (ActualHeight - 60) / bounds.Height), 0.4, 1.3);
        graph.Zoom = zoom;
        graph.PanX = (ActualWidth - bounds.Width * zoom) / 2 - bounds.X * zoom;
        graph.PanY = (ActualHeight - bounds.Height * zoom) / 2 - bounds.Y * zoom;
        InvalidateVisual();
    }

    private void OnGraphChanged()
    {
        _selection.Clear();
        _linkSourceNode = null;
        _syncingSelection = true;
        SetCurrentValue(SelectedNodeProperty, null);
        _syncingSelection = false;
        InvalidateVisual();
    }

    private void OnSelectedNodeChanged(ParameterNode? node)
    {
        if (_syncingSelection)
            return;
        _selection.Clear();
        if (node is not null)
            _selection.Add(node);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------
    private static int SocketRows(ParameterNode node)
    {
        var definition = NodeCatalog.Get(node.Kind);
        return Math.Max(1, Math.Max(definition.Inputs.Length, definition.Outputs.Length));
    }

    private static double NodeHeight(ParameterNode node) =>
        HeaderHeight + SubtitleHeight + BodyPadding * 2 + SocketRows(node) * RowHeight;

    private static Rect NodeRect(ParameterNode node) =>
        new(node.X, node.Y, NodeWidth, NodeHeight(node));

    private static double RowCenterY(ParameterNode node, int index) =>
        node.Y + HeaderHeight + SubtitleHeight + BodyPadding + index * RowHeight + RowHeight / 2;

    private Point InputSocket(ParameterNode node, int index) =>
        new(node.X, RowCenterY(node, index));

    private Point OutputSocket(ParameterNode node, int index) =>
        new(node.X + NodeWidth, RowCenterY(node, index));

    private Point ToGraph(Point screen) =>
        new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom);

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_canvasBrush, null, new Rect(RenderSize));

        var graph = Graph;
        if (graph is null)
        {
            DrawCentered(dc, "未选择参数数据块 · 在左侧大纲中选择一个参数", 12, SubtitleColor);
            return;
        }

        if (graph.Nodes.Count == 0)
        {
            DrawCentered(dc, "右键添加节点 · 四个汇总节点接入参数输出后生效", 12, SubtitleColor);
            return;
        }

        dc.PushTransform(new TranslateTransform(graph.PanX, graph.PanY));
        dc.PushTransform(new ScaleTransform(graph.Zoom, graph.Zoom));

        foreach (var link in graph.Links)
            DrawLink(dc, graph, link);
        if (_linkSourceNode is not null)
            DrawPendingLink(dc);

        foreach (var node in graph.Nodes)
            DrawNode(dc, node);

        if (_boxSelecting)
            DrawBoxSelection(dc);

        dc.Pop();
        dc.Pop();

        DrawHint(dc);
    }

    private void DrawLink(DrawingContext dc, ParameterNodeGraph graph, NodeLink link)
    {
        var source = graph.Find(link.FromNode);
        var target = graph.Find(link.ToNode);
        if (source is null || target is null)
            return;

        var start = OutputSocket(source, link.FromSocket);
        var end = InputSocket(target, link.ToSocket);
        var color = ParseColor(NodeCatalog.Get(source.Kind).Color);
        var pen = new Pen(Frozen(Lighten(color, 0.35), 0.72), 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, NoodleGeometry(start, end));
    }

    private void DrawPendingLink(DrawingContext dc)
    {
        var source = _linkSourceNode!;
        var start = _linkSourceIsOutput
            ? OutputSocket(source, _linkSourceSocket)
            : InputSocket(source, _linkSourceSocket);
        var pen = new Pen(Frozen(SelectionColor, 0.9), 2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, _linkSourceIsOutput
            ? NoodleGeometry(start, _linkCurrent)
            : NoodleGeometry(_linkCurrent, start));
    }

    private static StreamGeometry NoodleGeometry(Point start, Point end)
    {
        var dx = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(start, false, false);
        context.BezierTo(
            new Point(start.X + dx, start.Y),
            new Point(end.X - dx, end.Y),
            end, true, false);
        geometry.Freeze();
        return geometry;
    }

    private void DrawNode(DrawingContext dc, ParameterNode node)
    {
        var definition = NodeCatalog.Get(node.Kind);
        var header = ParseColor(definition.Color);
        var rect = NodeRect(node);
        var selected = _selection.Contains(node);

        dc.DrawRoundedRectangle(Frozen(Color.FromArgb(90, 0, 0, 0)), null,
            new Rect(rect.X + 3, rect.Y + 4, rect.Width, rect.Height), NodeRadius, NodeRadius);

        dc.DrawRoundedRectangle(Frozen(BodyColor), new Pen(Frozen(BodyBorderColor), 1),
            rect, NodeRadius, NodeRadius);
        dc.DrawGeometry(Frozen(node.Muted ? Desaturate(header) : header), null, TopRounded(rect, NodeRadius));

        if (selected)
        {
            var outline = new Pen(Frozen(SelectionColor), 1.8);
            outline.Freeze();
            dc.DrawRoundedRectangle(null, outline, rect, NodeRadius, NodeRadius);
        }

        var title = node.DisplayTitle;
        DrawText(dc, title, new Point(node.X + 10, node.Y + 5), Frozen(TextColor), 11.5,
            maxWidth: NodeWidth - 20, bold: true, strike: node.Muted);

        DrawText(dc, node.Subtitle, new Point(node.X + 10, node.Y + HeaderHeight + 1),
            Frozen(SubtitleColor), 9.5, maxWidth: NodeWidth - 20);

        if (node.Muted)
            DrawText(dc, "已静音", new Point(node.X + NodeWidth - 48, node.Y + 5),
                Frozen(Color.FromRgb(0xD9, 0xA0, 0x60)), 9);

        for (var i = 0; i < definition.Inputs.Length; i++)
        {
            var point = InputSocket(node, i);
            dc.DrawEllipse(Frozen(SocketColor), new Pen(Frozen(BodyBorderColor), 1), point,
                SocketRadius, SocketRadius);
            DrawText(dc, definition.Inputs[i].Name, new Point(point.X + 9, point.Y - 7),
                Frozen(Color.FromRgb(0xC6, 0xC6, 0xCC)), 9.5,
                maxWidth: NodeWidth / 2 + 12);
        }

        for (var i = 0; i < definition.Outputs.Length; i++)
        {
            var point = OutputSocket(node, i);
            dc.DrawEllipse(Frozen(Lighten(header, 0.45)), new Pen(Frozen(BodyBorderColor), 1), point,
                SocketRadius, SocketRadius);
            DrawText(dc, definition.Outputs[i].Name, new Point(0, point.Y - 7),
                Frozen(Color.FromRgb(0xC6, 0xC6, 0xCC)), 9.5,
                maxWidth: NodeWidth / 2 + 12, alignRight: true,
                rightEdge: point.X - 9);
        }
    }

    private void DrawBoxSelection(DrawingContext dc)
    {
        var rect = new Rect(_boxStart, _boxCurrent);
        var fill = new SolidColorBrush(Color.FromArgb(40, 0xE8, 0xA3, 0x3D));
        fill.Freeze();
        var pen = new Pen(Frozen(SelectionColor, 0.8), 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
        };
        pen.Freeze();
        dc.DrawRectangle(fill, pen, rect);
    }

    private void DrawCentered(DrawingContext dc, string text, double size, Color color)
    {
        var formatted = Format(text, size, Frozen(color), ActualWidth - 40);
        dc.DrawText(formatted, new Point((ActualWidth - formatted.Width) / 2,
            (ActualHeight - formatted.Height) / 2));
    }

    private void DrawHint(DrawingContext dc)
    {
        var text = "左键拖动节点 · 从插口拖出连线 · 框选 · Delete 删除 · 滚轮缩放 · 中键平移 · 右键添加节点";
        var formatted = Format(text, 10, Frozen(Color.FromRgb(0x7C, 0x7C, 0x84)),
            Math.Max(100, ActualWidth - 160));
        dc.DrawText(formatted, new Point(14, Math.Max(6, ActualHeight - formatted.Height - 8)));
    }

    private void DrawText(DrawingContext dc, string text, Point origin, Brush brush, double size,
        double maxWidth = 0, bool bold = false, bool alignRight = false, bool strike = false,
        double rightEdge = 0)
    {
        var formatted = Format(text, size, brush, maxWidth, bold, strike);
        var x = alignRight ? rightEdge - formatted.Width : origin.X;
        dc.DrawText(formatted, new Point(x, origin.Y));
    }

    private FormattedText Format(string text, double size, Brush brush, double maxWidth,
        bool bold = false, bool strike = false)
    {
        var formatted = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            bold ? _boldTypeface : _typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth > 8 ? maxWidth : double.PositiveInfinity,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (strike)
            formatted.SetTextDecorations(TextDecorations.Strikethrough);
        return formatted;
    }

    // ------------------------------------------------------------------
    // Mouse
    // ------------------------------------------------------------------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var screen = e.GetPosition(this);
        var point = ToGraph(screen);

        if (HitSocket(point) is { } socket)
        {
            _linkSourceNode = socket.Node;
            _linkSourceSocket = socket.Index;
            _linkSourceIsOutput = socket.IsOutput;
            _linkCurrent = point;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        var node = HitNode(point);
        if (node is not null)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (additive)
            {
                if (!_selection.Remove(node))
                    _selection.Add(node);
            }
            else if (!_selection.Contains(node))
            {
                _selection.Clear();
                _selection.Add(node);
            }

            _syncingSelection = true;
            SetCurrentValue(SelectedNodeProperty, _selection.LastOrDefault());
            _syncingSelection = false;

            _moving = true;
            _dragStartScreen = screen;
            _dragStartGraph = point;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _selection.Clear();
        _syncingSelection = true;
        SetCurrentValue(SelectedNodeProperty, null);
        _syncingSelection = false;

        _boxSelecting = true;
        _boxStart = point;
        _boxCurrent = point;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var screen = e.GetPosition(this);
        var point = ToGraph(screen);

        if (_panning)
        {
            var dx = screen.X - _panStartScreen.X;
            var dy = screen.Y - _panStartScreen.Y;
            if (Graph is { } panned)
            {
                panned.PanX = _panStartX + dx;
                panned.PanY = _panStartY + dy;
            }
            InvalidateVisual();
            return;
        }

        if (_linkSourceNode is not null)
        {
            _linkCurrent = point;
            InvalidateVisual();
            return;
        }

        if (_moving)
        {
            var dx = point.X - _dragStartGraph.X;
            var dy = point.Y - _dragStartGraph.Y;
            if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
            {
                if (!_moveStarted)
                {
                    Host?.PushGraphUndo();
                    _moveStarted = true;
                }
                foreach (var node in _selection)
                {
                    node.X += dx;
                    node.Y += dy;
                }
                _dragStartGraph = point;
                InvalidateVisual();
            }
            return;
        }

        if (_boxSelecting)
        {
            _boxCurrent = point;
            InvalidateVisual();
        }
    }

    private bool _moveStarted;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = ToGraph(e.GetPosition(this));

        if (_linkSourceNode is not null)
        {
            if (HitSocket(point) is { } socket && socket.Node != _linkSourceNode)
            {
                var fromOutput = _linkSourceIsOutput;
                var fromNode = _linkSourceNode;
                var fromSocket = _linkSourceSocket;
                var toNode = socket.Node;
                var toSocket = socket.Index;

                if (!fromOutput && socket.IsOutput)
                {
                    (fromNode, toNode) = (toNode, fromNode);
                    (fromSocket, toSocket) = (toSocket, fromSocket);
                }
                else if (fromOutput == socket.IsOutput)
                {
                    fromNode = null;
                }

                if (fromNode is not null)
                {
                    Host?.PushGraphUndo();
                    if (Graph?.Connect(fromNode.Id, fromSocket, toNode.Id, toSocket) == true)
                        Host?.NotifyGraphChanged();
                }
            }

            _linkSourceNode = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_moving)
        {
            _moving = false;
            _moveStarted = false;
            ReleaseMouseCapture();
            if (Host is not null)
                Host.NotifyGraphChanged();
            return;
        }

        if (_boxSelecting)
        {
            _boxSelecting = false;
            ReleaseMouseCapture();
            var box = new Rect(_boxStart, _boxCurrent);
            if (Graph is { } graph)
            {
                _selection.Clear();
                foreach (var node in graph.Nodes.Where(n => box.IntersectsWith(NodeRect(n))))
                    _selection.Add(node);
            }
            _syncingSelection = true;
            SetCurrentValue(SelectedNodeProperty, _selection.LastOrDefault());
            _syncingSelection = false;
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            Focus();
            _panning = true;
            _panStartScreen = e.GetPosition(this);
            _panStartX = PanX;
            _panStartY = PanY;
            CaptureMouse();
            e.Handled = true;
            Cursor = Cursors.ScrollAll;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Graph is not { } graph)
            return;

        var screen = e.GetPosition(this);
        var before = ToGraph(screen);
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        graph.Zoom = Math.Clamp(graph.Zoom * factor, 0.25, 2.5);
        graph.PanX = screen.X - before.X * graph.Zoom;
        graph.PanY = screen.Y - before.Y * graph.Zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.F2:
                if (SelectedNode is { } node)
                    BeginRename(node);
                e.Handled = true;
                break;
            case Key.Home:
                FrameAll();
                e.Handled = true;
                break;
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                if (Graph is { } graph)
                {
                    _selection.Clear();
                    _selection.AddRange(graph.Nodes);
                    InvalidateVisual();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                if (_linkSourceNode is not null)
                {
                    _linkSourceNode = null;
                    InvalidateVisual();
                }
                break;
            case Key.M when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                if (SelectedNode is { } muted)
                {
                    Host?.PushGraphUndo();
                    muted.Muted = !muted.Muted;
                    Host?.NotifyGraphChanged();
                    InvalidateVisual();
                }
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------
    // Context menu
    // ------------------------------------------------------------------
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var point = ToGraph(e.GetPosition(this));
        _lastContextPoint = point;
        var node = HitNode(point);
        if (node is not null)
        {
            _selection.Clear();
            _selection.Add(node);
            _syncingSelection = true;
            SetCurrentValue(SelectedNodeProperty, node);
            _syncingSelection = false;
            InvalidateVisual();
        }
    }

    /// <summary>Rebuilds the menu right before it opens, so items are sized with the popup.</summary>
    private void BuildContextMenu()
    {
        if (ContextMenu is not { } menu)
            return;

        menu.Items.Clear();
        // Keyboard invocation (Shift+F10) has no cursor point, so fall back to the selected node.
        var node = SelectedNode ?? HitNode(_lastContextPoint);

        if (node is not null)
        {
            menu.Items.Add(MenuItem("重命名 (F2)", () => BeginRename(node)));
            menu.Items.Add(MenuItem(node.Muted ? "取消静音" : "静音", () =>
            {
                Host?.PushGraphUndo();
                node.Muted = !node.Muted;
                Host?.NotifyGraphChanged();
                InvalidateVisual();
            }));
            menu.Items.Add(MenuItem("复制节点", () => DuplicateNode(node)));
            menu.Items.Add(MenuItem("删除节点 (Delete)", DeleteSelection));
            menu.Items.Add(new Separator());
        }

        foreach (var group in NodeCatalog.All.GroupBy(d => d.Category))
        {
            var parent = new MenuItem { Header = "添加 " + group.Key };
            foreach (var definition in group)
            {
                var item = new MenuItem { Header = definition.Title };
                item.Click += (_, _) => AddNode(definition.Kind, _lastContextPoint);
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("整理到初始视图", ResetView));
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void AddNode(NodeKind kind, Point position)
    {
        if (Graph is not { } graph)
            return;

        Host?.PushGraphUndo();
        var node = graph.Add(kind, Math.Round(position.X), Math.Round(position.Y));
        if (kind == NodeKind.ParameterInput)
            node.Text = Host?.SuggestedOutputKey() ?? "breathGain";

        _selection.Clear();
        _selection.Add(node);
        _syncingSelection = true;
        SetCurrentValue(SelectedNodeProperty, node);
        _syncingSelection = false;
        Host?.NotifyGraphChanged();
        InvalidateVisual();
    }

    private void DuplicateNode(ParameterNode node)
    {
        if (Graph is not { } graph)
            return;

        Host?.PushGraphUndo();
        var copy = node.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.X += 26;
        copy.Y += 26;
        graph.Nodes.Add(copy);
        _selection.Clear();
        _selection.Add(copy);
        _syncingSelection = true;
        SetCurrentValue(SelectedNodeProperty, copy);
        _syncingSelection = false;
        Host?.NotifyGraphChanged();
        InvalidateVisual();
    }

    private void DeleteSelection()
    {
        if (Graph is not { } graph || _selection.Count == 0)
            return;

        Host?.PushGraphUndo();
        foreach (var node in _selection.ToList())
            graph.RemoveNode(node.Id);
        _selection.Clear();
        _syncingSelection = true;
        SetCurrentValue(SelectedNodeProperty, null);
        _syncingSelection = false;
        Host?.NotifyGraphChanged();
        InvalidateVisual();
    }

    private void ResetView()
    {
        if (Graph is not { } graph)
            return;
        graph.PanX = 40;
        graph.PanY = 32;
        graph.Zoom = 1.0;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Inline rename
    // ------------------------------------------------------------------
    private void BeginRename(ParameterNode node)
    {
        foreach (var child in Children.OfType<TextBox>().ToList())
            Children.Remove(child);

        var box = new TextBox
        {
            Text = node.Label,
            FontSize = 11.5 * Zoom,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = HeaderHeight * Zoom,
            Width = NodeWidth * Zoom,
        };
        Canvas.SetLeft(box, node.X * Zoom + PanX);
        Canvas.SetTop(box, node.Y * Zoom + PanY);
        Children.Add(box);

        var committed = false;
        void Commit()
        {
            if (committed)
                return;
            committed = true;
            Children.Remove(box);
            var text = box.Text.Trim();
            if (text == node.Label)
                return;
            Host?.PushGraphUndo();
            node.Label = text;
            Host?.NotifyGraphChanged();
            InvalidateVisual();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                committed = true;
                Children.Remove(box);
                InvalidateVisual();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();

        box.Focus();
        box.SelectAll();
    }

    // ------------------------------------------------------------------
    // Hit testing
    // ------------------------------------------------------------------
    private ParameterNode? HitNode(Point point)
    {
        if (Graph is not { } graph)
            return null;
        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            if (NodeRect(graph.Nodes[i]).Contains(point))
                return graph.Nodes[i];
        }
        return null;
    }

    private (ParameterNode Node, int Index, bool IsOutput)? HitSocket(Point point)
    {
        if (Graph is not { } graph)
            return null;

        var radius = SocketHitRadius / Math.Max(0.35, Zoom);
        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = graph.Nodes[i];
            var definition = NodeCatalog.Get(node.Kind);

            for (var s = 0; s < definition.Outputs.Length; s++)
            {
                if (Distance(OutputSocket(node, s), point) <= radius)
                    return (node, s, true);
            }
            for (var s = 0; s < definition.Inputs.Length; s++)
            {
                if (Distance(InputSocket(node, s), point) <= radius)
                    return (node, s, false);
            }
        }
        return null;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------
    // Drawing helpers
    // ------------------------------------------------------------------
    private static StreamGeometry TopRounded(Rect rect, double radius)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(rect.Left, rect.Top + radius), true, true);
        context.ArcTo(new Point(rect.Left + radius, rect.Top), new Size(radius, radius), 0,
            false, SweepDirection.Clockwise, true, false);
        context.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
        context.ArcTo(new Point(rect.Right, rect.Top + radius), new Size(radius, radius), 0,
            false, SweepDirection.Clockwise, true, false);
        context.LineTo(new Point(rect.Right, rect.Top + HeaderHeight), true, false);
        context.LineTo(new Point(rect.Left, rect.Top + HeaderHeight), true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Brush Frozen(Color color, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x4E, 0x5C, 0x6E);
        }
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + (255 - color.R) * amount, 0, 255),
        (byte)Math.Clamp(color.G + (255 - color.G) * amount, 0, 255),
        (byte)Math.Clamp(color.B + (255 - color.B) * amount, 0, 255));

    private static Color Desaturate(Color color)
    {
        var grey = (byte)((color.R + color.G + color.B) / 3 * 0.6 + 40);
        return Color.FromRgb(grey, grey, grey);
    }
}
