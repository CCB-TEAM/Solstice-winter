using System.Windows;
using System.Windows.Media;
using SolsticeWinter.App.ViewModels;
using SolsticeWinter.Decompiler;
using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Nodes;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 把新反编译器（<see cref="BPNode"/> 图）转成画布用的 VM。
///
/// <para>
/// 这一层是**唯一**知道两边模型的代码：左边是移植来的 UE 式节点（引脚带类型与连线），
/// 右边是 Nodify 用的 <see cref="GraphNodeVm"/>。换布局引擎或换画布库只动这里。
/// </para>
/// </summary>
public static class GraphAdapter
{
    private const double ColumnWidth = 320;
    private const double RowGap = 40;


    /// <summary>是否在引脚上显示类型（对应设置里的 <c>ShowPinTypes</c>）。</summary>
    public static bool ShowPinTypes { get; set; }
    /// <summary>Msagl 布局失败、退回简单分列的次数（自检用：这个数不为 0 说明图会排得很难看）。</summary>
    public static int LayoutFallbacks { get; private set; }

    /// <summary>清零布局回退计数（自检开始前调用）。</summary>
    public static void ResetLayoutFallbacks() => LayoutFallbacks = 0;

    public static (List<GraphNodeVm> Nodes, List<ConnectionVm> Connections) ToView(IReadOnlyList<BPNode> nodes)
    {
        var nodeVms = new List<GraphNodeVm>(nodes.Count);
        var pinMap = new Dictionary<GraphPin, ConnectorVm>(nodes.Count * 4);
        var byNode = new Dictionary<BPNode, GraphNodeVm>(nodes.Count);

        foreach (var node in nodes)
        {
            var vm = new GraphNodeVm
            {
                Title = TitleOf(node),
                RawTitle = node.Name,
                Kind = KindOf(node),
                HeaderBrush = NodeColors.For(node),
                Subtitle = SubtitleOf(node),
                Tooltip = node is K2Node_CallFunction cf
                    ? $"{cf.FunctionName}  ({cf.OwnerPath})"
                    : $"{node.NodeType}  {node.Name}",
                IsOpaque = node is UnknownNode,
            };

            foreach (var pin in node.Input.Where(p => !p.IsHidden))
            {
                var connector = new ConnectorVm
                {
                    Title = pin.IsNameHidden ? string.Empty : pin.FriendlyName,
                    IsInput = true,
                    IsExec = pin.Type.Category == PinType.Exec,
                    TypeName = pin.Type.Display(),
                    Value = pin.Value,
                    ShowType = ShowPinTypes,
                    Brush = BrushOf(pin.Type) ?? FallbackBrush,
                };
                connector.IsConnected = pin.IsConnected;
                pinMap[pin] = connector;
                vm.Inputs.Add(connector);
            }

            foreach (var pin in node.Output.Where(p => !p.IsHidden))
            {
                var connector = new ConnectorVm
                {
                    Title = pin.IsNameHidden ? string.Empty : pin.FriendlyName,
                    IsInput = false,
                    IsExec = pin.Type.Category == PinType.Exec,
                    TypeName = pin.Type.Display(),
                    Value = pin.Value,
                    ShowType = ShowPinTypes,
                    Brush = BrushOf(pin.Type) ?? FallbackBrush,
                };
                connector.IsConnected = pin.IsConnected;
                pinMap[pin] = connector;
                vm.Outputs.Add(connector);
            }

            nodeVms.Add(vm);
            byNode[node] = vm;
        }

        // 连线：输出脚 → 它连到的输入脚
        var connections = new List<ConnectionVm>();
        foreach (var node in nodes)
        {
            foreach (var output in node.Output)
            {
                if (!pinMap.TryGetValue(output, out var from)) continue;
                foreach (var input in output.LinkedTo)
                {
                    if (!pinMap.TryGetValue(input, out var to)) continue;
                    connections.Add(new ConnectionVm(from, to, output.Type.Category == PinType.Exec));
                }
            }
        }

        Layout(nodes, byNode);
        return (nodeVms, connections);
    }

    /// <summary>
    /// 布局：优先 Msagl（与 UEBGV 同一套 Sugiyama），失败才退回简单分列。
    /// </summary>
    private static void Layout(IReadOnlyList<BPNode> nodes, IReadOnlyDictionary<BPNode, GraphNodeVm> byNode)
    {
        var sizes = nodes.ToDictionary(n => n, LayoutSize);

        var result = MsaglLayout.Apply(nodes, sizes, (node, location) =>
        {
            if (byNode.TryGetValue(node, out var vm)) vm.Location = location;
        });

        if (result.Applied) return;

        LayoutFallbacks++;
        UiLog.Write($"Msagl 布局失败，退回简单分列：{result.FallbackReason}");
        SimpleLayout(nodes, byNode);
    }

    /// <summary>
    /// 给布局用的节点尺寸——按 **UI 实际渲染的高度** 算，不是模型估的。
    ///
    /// <para>
    /// `BPNode.CalculateSize` 里每个引脚按 32px 算（那是给旧画布留的），而 XAML 里
    /// 一行引脚（字号 11 + 边距）实际约 22px。用模型高度会让 Msagl 把层间距撑开：
    /// 实测 7 个节点的 `AddCardStats` 铺了 690×1559，最大层间距 541px——
    /// 一个 13 脚节点模型算 450px 高，实际只画 ~320px。
    /// </para>
    /// </summary>
    private static Point LayoutSize(BPNode node)
    {
        const double PinRowHeight = 22;
        const double HeaderHeight = 28;
        const double VerticalPadding = 14;

        var rows = Math.Max(
            node.Input.Count(p => !p.IsHidden),
            node.Output.Count(p => !p.IsHidden));

        var height = Math.Max(60, HeaderHeight + rows * PinRowHeight + VerticalPadding);
        var width = Math.Clamp(node.NodeWidth, 160, 320);
        return new Point(width, height);
    }

    /// <summary>简单分层：按 exec 边算深度（列），同一列纵向堆叠。Msagl 不可用时的兜底。</summary>
    private static void SimpleLayout(IReadOnlyList<BPNode> nodes, IReadOnlyDictionary<BPNode, GraphNodeVm> byNode)
    {
        var depth = new Dictionary<BPNode, int>();
        var queue = new Queue<BPNode>();

        foreach (var node in nodes.Where(n => n.ExecPin is null || !n.ExecPin.IsConnected))
        {
            depth[node] = 0;
            queue.Enqueue(node);
        }

        if (queue.Count == 0 && nodes.Count > 0)
        {
            depth[nodes[0]] = 0;
            queue.Enqueue(nodes[0]);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var current = depth[node];
            var next = current + 1;

            foreach (var pin in node.Output.Where(p => !p.IsHidden))
            {
                foreach (var linked in pin.LinkedTo)
                {
                    if (linked.ParentNode is not { } target) continue;
                    if (depth.TryGetValue(target, out var known) && known <= next) continue;
                    depth[target] = next;
                    queue.Enqueue(target);
                }
            }
        }

        // 列宽按该列最宽的节点算：写死 320 会让宽节点压到下一列
        var columnWidths = new Dictionary<int, double>();
        foreach (var node in nodes)
        {
            var column = depth.TryGetValue(node, out var d) ? d : 0;
            var width = Math.Max(ColumnWidth, node.NodeWidth + 60);
            columnWidths[column] = Math.Max(columnWidths.TryGetValue(column, out var known) ? known : 0, width);
        }

        var columnOffsets = new Dictionary<int, double>();
        var running = 0.0;
        foreach (var column in columnWidths.Keys.OrderBy(c => c))
        {
            columnOffsets[column] = running;
            running += columnWidths[column];
        }

        var rowHeights = new Dictionary<int, double>();
        foreach (var node in nodes)
        {
            var column = depth.TryGetValue(node, out var d) ? d : 0;
            var y = rowHeights.TryGetValue(column, out var used) ? used : 0;
            rowHeights[column] = y + node.NodeHeight + RowGap;

            if (byNode.TryGetValue(node, out var vm))
                vm.Location = new Point(columnOffsets.TryGetValue(column, out var x) ? x : 0, y);
        }
    }

    /// <summary>
    /// 节点正文文字。
    ///
    /// <para>
    /// UE 里调用节点正文是空的（信息都在引脚上），但全路径 `/Script/Engine.GameplayStatics`
    /// 会把节点撑得很宽，所以这里只显示**所属类的短名**——既能区分 KismetArrayLibrary /
    /// KismetMathLibrary，又不占地方。宏节点给一句人话说明。
    /// </para>
    /// </summary>
    private static string SubtitleOf(BPNode node) => node switch
    {
        K2Node_CallFunction call => ShortOwner(call.OwnerPath),
        K2Node_MacroInstance macro => macro.Name.Contains("While", StringComparison.Ordinal)
            ? "条件在宏内部"
            : "遍历数组",
        _ => string.Empty,
    };

    /// <summary>
    /// 标题：直接用**反编译器给节点设的标题**。
    ///
    /// <para>
    /// 之前这里把 Get 硬改成空串、Set 改成 "SET"，把反编译器设好的标题（Get = 变量名、
    /// Assign = "Set X"）全部覆盖掉了——于是画布上一排空白小方块。不要再在这里改标题。
    /// </para>
    /// </summary>
    /// <summary>所属类路径 → 短名（`/Script/Engine.GameplayStatics` → `GameplayStatics`）。</summary>
    private static string ShortOwner(string ownerPath)
    {
        if (string.IsNullOrEmpty(ownerPath)) return string.Empty;
        var dot = ownerPath.LastIndexOf('.');
        return dot >= 0 ? ownerPath[(dot + 1)..] : ownerPath;
    }

    private static string TitleOf(BPNode node) =>
        string.IsNullOrEmpty(node.Name) ? node.NodeType : node.Name;

    /// <summary>节点种类 → 标题栏颜色键（与 <c>Converters/KindToBrushConverter</c> 的调色板对应）。</summary>
    private static string KindOf(BPNode node) => node switch
    {
        // 与 `NodeColors.KindOf` 保持一致：入口是 `Entry`（红）、宏是 `Macro`（紫）。
        K2Node_FunctionEntry => "Entry",
        K2Node_MacroInstance => "Macro",
        K2Node_IfThenElse => "Branch",
        K2Node_ExecutionSequence => "Branch",
        K2Node_VariableSet => "Set",
        K2Node_AssignmentStatement => "Set",
        K2Node_FunctionResult => "Return",
        K2Node_VariableGet => "Data",
        K2Node_TemporaryVariable => "Data",
        K2Node_CallFunction call => call.Pure ? "Data" : "Call",
        UnknownNode => "Opaque",
        _ => "Call",
    };

    /// <summary>
    /// 引脚类型 → 颜色。取 UE 编辑器的调色板（Bool 暗红 / Int 青 / Float 绿 / String 品红 /
    /// Object 蓝 / Struct 青绿 / Enum 绿松石），让同一类型的脚在画布上和 UE 里同色。
    /// 之前只有 8 种颜色，`Enum` / `Name` / `Text` / `Byte` 全落到灰色兜底，很难认。
    /// </summary>
    private static Brush? BrushOf(GraphPinType type) => type.Category switch
    {
        PinType.Exec => ExecBrush,
        PinType.Bool => BoolBrush,
        PinType.Byte => ByteBrush,
        PinType.Int => IntBrush,
        PinType.Int64 => Int64Brush,
        PinType.Float => FloatBrush,
        PinType.Double or PinType.Vector or PinType.Rotator or PinType.Transform => FloatBrush,
        PinType.String => StringBrush,
        PinType.Name => NameBrush,
        PinType.Text => TextBrush,
        PinType.Object or PinType.Class or PinType.SoftObject or PinType.SoftClass or PinType.Interface => ObjectBrush,
        PinType.Struct => StructBrush,
        PinType.Enum => EnumBrush,
        PinType.Delegate => DelegateBrush,
        _ => null,
    };

    private static readonly Brush ExecBrush = Freeze("#F0F0F0");
    private static readonly Brush BoolBrush = Freeze("#C0392B");
    private static readonly Brush ByteBrush = Freeze("#1E8449");
    private static readonly Brush IntBrush = Freeze("#1ABC9C");
    private static readonly Brush Int64Brush = Freeze("#2E86C1");
    private static readonly Brush FloatBrush = Freeze("#2ECC71");
    private static readonly Brush StringBrush = Freeze("#E056A0");
    private static readonly Brush NameBrush = Freeze("#C39BD3");
    private static readonly Brush TextBrush = Freeze("#D98880");
    private static readonly Brush ObjectBrush = Freeze("#3B8ED0");
    private static readonly Brush StructBrush = Freeze("#148F77");
    private static readonly Brush EnumBrush = Freeze("#27AE60");
    private static readonly Brush DelegateBrush = Freeze("#B7950B");
    internal static readonly Brush FallbackBrush = Freeze("#7F8C8D");

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}


