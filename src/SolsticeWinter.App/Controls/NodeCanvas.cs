using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SolsticeWinter.App.ViewModels;

namespace SolsticeWinter.App.Controls;

/// <summary>
/// 自绘节点画布（WPF <see cref="Canvas"/> + 贝塞尔连线）。
///
/// <para>
/// <b>为什么不是 Nodify</b>：本机 NuGet 源连不上（SSL/认证失败），Nodify 拿不到。
/// 但 ViewModel 的形状是按 Nodify 的契约设计的（<c>Connector.Anchor</c> 回填、Connection 持两个引脚），
/// 所以等源恢复后换回 Nodify 只需要替换这一层视图，图构建与 VM 不动。
/// </para>
///
/// <para>
/// 引脚坐标是**算出来的**，不依赖布局回填：节点宽度固定，行高固定，
/// 所以输入脚在左边缘、输出脚在右边缘，纵坐标 = 头高 + 行号 × 行高。连线据此画贝塞尔。
/// </para>
/// </summary>
public sealed class NodeCanvas : UserControl
{
    private const double NodeWidth = 240;
    private const double HeaderHeight = 26;
    private const double PinRowHeight = 18;
    private const double Padding = 6;

    private readonly Canvas _world = new();
    private readonly Canvas _edges = new();
    private readonly Canvas _nodes = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new(0, 0);

    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;

    public NodeCanvas()
    {
        var transform = new TransformGroup();
        transform.Children.Add(_scale);
        transform.Children.Add(_translate);

        _edges.RenderTransform = transform;
        _nodes.RenderTransform = transform;

        _world.Children.Add(_edges);
        _world.Children.Add(_nodes);

        var root = new Grid { ClipToBounds = true, Background = Brush(0x1E1E1E) };
        root.Children.Add(new Rectangle
        {
            Fill = GridBrush(),
            IsHitTestVisible = false,
        });
        root.Children.Add(_world);
        Content = root;

        root.MouseWheel += OnWheel;
        root.MouseLeftButtonDown += OnLeftDown;
        root.MouseMove += OnMove;
        root.MouseLeftButtonUp += OnLeftUp;
        root.MouseRightButtonDown += (_, _) => { _panning = false; };

        SizeChanged += (_, _) => { };
    }

    #region 依赖属性

    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IEnumerable<GraphNodeVm>), typeof(NodeCanvas),
        new PropertyMetadata(null, (d, _) => ((NodeCanvas)d).Render()));

    public static readonly DependencyProperty ConnectionsProperty = DependencyProperty.Register(
        nameof(Connections), typeof(IEnumerable<ConnectionVm>), typeof(NodeCanvas),
        new PropertyMetadata(null, (d, _) => ((NodeCanvas)d).Render()));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(GraphNodeVm), typeof(NodeCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IEnumerable<GraphNodeVm>? Nodes
    {
        get => (IEnumerable<GraphNodeVm>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IEnumerable<ConnectionVm>? Connections
    {
        get => (IEnumerable<ConnectionVm>?)GetValue(ConnectionsProperty);
        set => SetValue(ConnectionsProperty, value);
    }

    public GraphNodeVm? SelectedNode
    {
        get => (GraphNodeVm?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    #endregion

    private void Render()
    {
        _nodes.Children.Clear();
        _edges.Children.Clear();

        var nodeList = Nodes?.ToList() ?? [];
        var connectionList = Connections?.ToList() ?? [];

        // 连线先画，节点盖在上面
        foreach (var connection in connectionList)
        {
            var from = AnchorOf(connection.Output, isOutput: true, nodeList);
            var to = AnchorOf(connection.Input, isOutput: false, nodeList);
            if (from is null || to is null) continue;
            _edges.Children.Add(CreateEdge(from.Value, to.Value, connection.IsExec));
        }

        foreach (var node in nodeList)
        {
            var visual = CreateNodeVisual(node);
            Canvas.SetLeft(visual, node.Location.X);
            Canvas.SetTop(visual, node.Location.Y);
            _nodes.Children.Add(visual);
        }

        if (nodeList.Count > 0)
        {
            var minX = nodeList.Min(n => n.Location.X);
            var minY = nodeList.Min(n => n.Location.Y);
            _translate.X = 60 - minX;
            _translate.Y = 60 - minY;
        }
    }

    /// <summary>
    /// 引脚锚点：根据节点位置 + 引脚序号算，不依赖实际渲染尺寸。
    /// 输入脚在左边缘、输出脚在右边缘，纵坐标 = 头高 + 内边距 + 行号 × 行高 + 半行。
    /// </summary>
    private static Point? AnchorOf(ConnectorVm pin, bool isOutput, IReadOnlyList<GraphNodeVm> nodes)
    {
        var owner = nodes.FirstOrDefault(n =>
            (isOutput ? n.Outputs : n.Inputs).Contains(pin));
        if (owner is null) return null;

        var index = (isOutput ? owner.Outputs : owner.Inputs).IndexOf(pin);
        if (index < 0) index = 0;

        return new Point(
            owner.Location.X + (isOutput ? NodeWidth : 0),
            owner.Location.Y + HeaderHeight + Padding + index * PinRowHeight + PinRowHeight / 2);
    }

    private static Path CreateEdge(Point from, Point to, bool isExec)
    {
        var dx = Math.Max(40, Math.Abs(to.X - from.X) * 0.5);
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = from, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(from.X + dx, from.Y),
            new Point(to.X - dx, to.Y),
            to, isStroked: true));
        geometry.Figures.Add(figure);

        return new Path
        {
            Data = geometry,
            Stroke = isExec ? Brush(0xD0, 0xD0, 0xD0) : Brush(0x6A, 0x8F, 0xB8),
            StrokeThickness = isExec ? 2.2 : 1.4,
            StrokeDashArray = isExec ? null : new DoubleCollection([4, 3]),
            IsHitTestVisible = false,
        };
    }

    private Border CreateNodeVisual(GraphNodeVm node)
    {
        var inputs = node.Inputs.ToList();
        var outputs = node.Outputs.ToList();
        var rows = Math.Max(inputs.Count, outputs.Count);
        var height = HeaderHeight + Padding * 2 + rows * PinRowHeight;

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = HeaderBrush(node),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
        };
        var headerText = new TextBlock
        {
            Text = node.IsOpaque ? $"? {node.Title}" : node.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Child = headerText;
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        var pins = new Grid { Margin = new Thickness(Padding, Padding, Padding, Padding) };
        pins.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pins.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var inputPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var pin in inputs) inputPanel.Children.Add(CreatePinRow(pin, isInput: true));

        var outputPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var pin in outputs) outputPanel.Children.Add(CreatePinRow(pin, isInput: false));

        Grid.SetColumn(inputPanel, 0);
        Grid.SetColumn(outputPanel, 1);
        pins.Children.Add(inputPanel);
        pins.Children.Add(outputPanel);

        Grid.SetRow(pins, 1);
        body.Children.Add(pins);

        var border = new Border
        {
            Width = NodeWidth,
            Height = height,
            Background = Brush(0x2D, 0x2D, 0x30),
            BorderBrush = node == SelectedNode ? Brush(0x0E, 0x63, 0x9C) : Brush(0x3F, 0x3F, 0x46),
            BorderThickness = new Thickness(node == SelectedNode ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Child = body,
            ToolTip = string.IsNullOrEmpty(node.Tooltip) ? null : node.Tooltip,
            Cursor = Cursors.Hand,
            Tag = node,
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            SelectedNode = node;
            e.Handled = true;
            Render();
        };

        return border;
    }

    private static UIElement CreatePinRow(ConnectorVm pin, bool isInput)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = PinRowHeight,
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = pin.IsExec ? Brush(0xF0, 0xF0, 0xF0) : pin.Brush,
            Stroke = pin.IsUnknown ? Brush(0x9A, 0x9A, 0x9A) : null,
            StrokeThickness = pin.IsUnknown ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isInput ? new Thickness(-4, 0, 4, 0) : new Thickness(4, 0, -4, 0),
        };

        var label = new TextBlock
        {
            Text = pin.IsExec ? "exec" : $"{pin.Title}",
            Foreground = pin.IsUnknown ? Brush(0x8A, 0x8A, 0x8A) : Brush(0xC8, 0xC8, 0xC8),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (isInput)
        {
            panel.Children.Add(dot);
            panel.Children.Add(label);
        }
        else
        {
            panel.Children.Add(label);
            panel.Children.Add(dot);
        }

        return panel;
    }

    private static Brush HeaderBrush(GraphNodeVm node) => node.Kind switch
    {
        "Branch" => Brush(0x8A, 0x6D, 0x1F),
        "Set" => Brush(0x1F, 0x6F, 0x6A),
        "Return" => Brush(0x6A, 0x2F, 0x2F),
        "Data" => Brush(0x2A, 0x4A, 0x6A),
        "Opaque" => Brush(0x4A, 0x4A, 0x4A),
        _ => Brush(0x2E, 0x3E, 0x50),
    };

    private static Brush Brush(byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush Brush(int rgb) =>
        new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));

    private static Brush GridBrush()
    {
        var pen = new Pen(Brush(0x2A, 0x2A, 0x2A), 1);
        var drawing = new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(0, 0, 15, 15)));
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 15, 15),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var position = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newScale = Math.Clamp(_scale.ScaleX * factor, 0.2, 3.0);
        factor = newScale / _scale.ScaleX;

        _translate.X = position.X - (position.X - _translate.X) * factor;
        _translate.Y = position.Y - (position.Y - _translate.Y) * factor;
        _scale.ScaleX = newScale;
        _scale.ScaleY = newScale;
        e.Handled = true;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(this);
        _panOrigin = new Point(_translate.X, _translate.Y);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var position = e.GetPosition(this);
        _translate.X = _panOrigin.X + (position.X - _panStart.X);
        _translate.Y = _panOrigin.Y + (position.Y - _panStart.Y);
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e) => _panning = false;
}
