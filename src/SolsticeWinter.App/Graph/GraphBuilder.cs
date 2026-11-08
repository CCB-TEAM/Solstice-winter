using System.IO;
using System.Windows;
using System.Windows.Media;
using SolsticeWinter.App.ViewModels;
using Solstice.Kismet;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 把 <see cref="KNode"/> 树投影成画布要的「平铺 exec 图 + 数据流图」。
///
/// <para>
/// 这是 D9「混合真相」里**读**的那一半：树形 AST 只用来渲染，编辑时再走另一条路。
/// 投影规则（与 <c>docs/设计-UI.md</c> §2.1 一致）：
/// 顶层节点串成 exec 链；<c>IfNode</c> 变成带 <c>true</c>/<c>false</c> 两个 exec 出脚的 Branch；
/// 嵌套的 <c>CallArg</c> 变成放在使用者上方的纯数据节点。
/// </para>
///
/// <para>
/// 布局是**算出来的**（cooked 资产里没有位置）：exec 从左到右，数据节点往上叠。
/// 手动位置将来进侧车文件，不在这里。
/// </para>
/// </summary>
public sealed class GraphBuilder
{
    /// <summary>布局与标题风格（来自 solstice-winter.settings.json）。</summary>
    public sealed record Options(
        double ColumnWidth,
        double RowHeight,
        double DataOffsetY,
        bool PrettyTitles,
        Func<string, SolsticeWinter.Index.ScriptFunction?>? SignatureOf = null)
    {
        public static Options Default { get; } = new(300, 260, -170, true);
    }

    private readonly Options _options;
    private double ColumnWidth => _options.ColumnWidth;
    private double RowHeight => _options.RowHeight;
    private double DataOffsetY => _options.DataOffsetY;

    private GraphBuilder(Options options) => _options = options;

    private readonly List<GraphNodeVm> _nodes = [];
    private readonly List<ConnectionVm> _connections = [];
    private double _maxX;

    public static (IReadOnlyList<GraphNodeVm> Nodes, IReadOnlyList<ConnectionVm> Connections) Build(
        IReadOnlyList<KNode> roots, Options? options = null)
    {
        var builder = new GraphBuilder(options ?? Options.Default);
        builder.BuildChain(roots, 0, 0, []);
        return (builder._nodes, builder._connections);
    }

    /// <summary>
    /// 串一条 exec 链。<paramref name="dangling"/> 是上游悬空的 exec 出脚
    /// （多个 = 分支汇合点，例如 if 的 then/else 都接到下一个节点）。
    /// 返回这条链末端悬空的 exec 出脚。
    /// </summary>
    private List<ConnectorVm> BuildChain(IReadOnlyList<KNode> nodes, double x, double y, List<ConnectorVm> dangling)
    {
        var cursorX = x;
        var pending = dangling;

        foreach (var node in nodes)
        {
            var vm = CreateNode(node, cursorX, y);
            _nodes.Add(vm);
            _maxX = Math.Max(_maxX, cursorX);

            var execIn = vm.Inputs.FirstOrDefault(p => p.IsExec);
            if (execIn is not null)
            {
                foreach (var upstream in pending) Connect(upstream, execIn, isExec: true);
                pending = [];
            }

            switch (node)
            {
                case IfNode branch:
                {
                    var conditionPin = vm.Inputs.FirstOrDefault(p => p.Title == "cond");
                    if (conditionPin is not null) BuildArg(branch.Condition, conditionPin, cursorX, y);

                    var thenPin = vm.Outputs.First(p => p.Title == "true");
                    var elsePin = vm.Outputs.First(p => p.Title == "false");

                    var thenEnds = BuildChain(branch.Then, cursorX + ColumnWidth, y - RowHeight / 2, [thenPin]);
                    var elseEnds = branch.Else is { Count: > 0 }
                        ? BuildChain(branch.Else, cursorX + ColumnWidth, y + RowHeight / 2, [elsePin])
                        : [elsePin];

                    pending = [.. thenEnds, .. elseEnds];
                    cursorX += ColumnWidth;
                    break;
                }

                case LoopNode loop:
                {
                    var conditionPin = vm.Inputs.FirstOrDefault(p => p.Title == "cond");
                    if (conditionPin is not null) BuildArg(loop.Condition, conditionPin, cursorX, y);

                    var loopIn = vm.Inputs.First(p => p.IsExec);
                    var bodyPin = vm.Outputs.First(p => p.Title == "body");
                    var donePin = vm.Outputs.First(p => p.Title == "done");
                    var bodyEnds = BuildChain(loop.Body, cursorX + ColumnWidth, y - RowHeight / 2, [bodyPin]);

                    // 回边接回「循环节点自己」的 exec 入脚，而不是接在 body 出脚上
                    // （接出脚会变成自环，空循环体时尤其离谱）
                    foreach (var end in bodyEnds) Connect(end, loopIn, isExec: true);

                    pending = [donePin];
                    cursorX += ColumnWidth;
                    break;
                }

                case BottomLoopNode loop:
                {
                    var conditionPin = vm.Inputs.FirstOrDefault(p => p.Title == "cond");
                    if (conditionPin is not null) BuildArg(loop.Condition, conditionPin, cursorX, y);

                    var loopIn = vm.Inputs.First(p => p.IsExec);
                    var bodyPin = vm.Outputs.First(p => p.Title == "body");
                    var donePin = vm.Outputs.First(p => p.Title == "done");
                    var bodyEnds = BuildChain([.. loop.Body, .. loop.Tail], cursorX + ColumnWidth, y - RowHeight / 2, [bodyPin]);
                    foreach (var end in bodyEnds) Connect(end, loopIn, isExec: true);

                    pending = [donePin];
                    cursorX += ColumnWidth;
                    break;
                }

                case AssignNode assign:
                {
                    var valuePin = vm.Inputs.FirstOrDefault(p => p.Title == "value");
                    if (valuePin is not null) BuildArg(assign.Value, valuePin, cursorX, y);
                    break;
                }

                case AssignBoolNode assign:
                {
                    var valuePin = vm.Inputs.FirstOrDefault(p => p.Title == "value");
                    if (valuePin is not null) BuildArg(assign.Value, valuePin, cursorX, y);
                    break;
                }

                case PersistentAssignNode assign:
                {
                    var valuePin = vm.Inputs.FirstOrDefault(p => p.Title == "value");
                    if (valuePin is not null) BuildArg(assign.Value, valuePin, cursorX, y);
                    break;
                }

                case ReturnNode ret:
                {
                    if (ret.Value is not null)
                    {
                        var valuePin = vm.Inputs.FirstOrDefault(p => p.Title == "value");
                        if (valuePin is not null) BuildArg(ret.Value, valuePin, cursorX, y);
                    }

                    break;
                }
            }

            pending = pending.Count > 0 ? pending : [.. vm.Outputs.Where(p => p.IsExec)];
            cursorX += ColumnWidth;
        }

        return pending;
    }

    /// <summary>把参数渲染成一个引脚；如果参数本身是调用，就递归造一个数据节点接上去。</summary>
    private void BuildArg(KArg? arg, ConnectorVm pin, double x, double y)
    {
        if (arg is null) return;

        if (arg is CallArg call)
        {
            var vm = CreateCallNode(call, x - 40, y + DataOffsetY, isExec: false);
            _nodes.Add(vm);
            var output = vm.Outputs.FirstOrDefault(p => !p.IsExec);
            if (output is not null) Connect(output, pin, isExec: false);

            for (var i = 0; i < call.Args.Count && i < vm.Inputs.Count; i++)
            {
                var childPin = vm.Inputs[i];
                BuildArg(call.Args[i], childPin, x - 40, y + DataOffsetY);
            }

            return;
        }

        // 字面量 / 变量：引脚本身已经带类型，这里只补一个可读的值提示
        pin.IsConnected = false;
    }

    private GraphNodeVm CreateCallNode(CallArg call, double x, double y, bool isExec)
    {
        var vm = new GraphNodeVm
        {
            Title = _options.PrettyTitles ? Pretty(call.Function) : call.Function,
            RawTitle = call.Function,
            CalleeName = call.Function,
            Subtitle = call.Owner ?? string.Empty,
            Kind = isExec ? "Call" : "Data",
            Location = new Point(x, y),
            Tooltip = $"{call.Shape}  {call.Function}",
        };

        if (isExec) vm.Inputs.Add(ExecPin("exec", true));
        AddParamPins(vm, call.Args, call.Function);

        if (isExec) vm.Outputs.Add(ExecPin("exec", false));
        vm.Outputs.Add(DataPin("return", "value", false));
        return vm;
    }

    private GraphNodeVm CreateNode(KNode node, double x, double y)
    {
        var (rawTitle, kind, tooltip) = Describe(node);
        var vm = new GraphNodeVm
        {
            Title = _options.PrettyTitles ? Pretty(rawTitle) : rawTitle,
            RawTitle = rawTitle,
            CalleeName = CalleeOf(node),
            Kind = kind,
            Location = new Point(x, y),
            Tooltip = tooltip,
            IsOpaque = node is KismetDecompiler.RawNode or BlockGraphNode or BasicBlockNode or UberSectionNode,
        };

        if (vm.IsOpaque) return vm;

        switch (node)
        {
            case CallNode call:
                AddExecPins(vm);
                AddParamPins(vm, call.Args, call.Function);
                break;

            case ContextCallNode call:
                AddExecPins(vm);
                vm.Inputs.Add(DataPin(call.ObjectVariable, "object", true));
                AddParamPins(vm, call.Args, call.Function);
                break;

            case VirtualCallNode call:
                AddExecPins(vm);
                AddParamPins(vm, call.Args, call.Function);
                break;

            case LocalFinalCallNode call:
                AddExecPins(vm);
                AddParamPins(vm, call.Args, call.Function);
                break;

            case MathCallNode call:
                AddExecPins(vm);
                AddParamPins(vm, call.Args, call.Function);
                break;

            case MulticastCallNode call:
                AddExecPins(vm);
                AddParamPins(vm, call.Args, call.Function);
                break;

            case IfNode:
                AddExecPins(vm, withOutput: false); // exec 入脚：没有它，上游连线接不进来
                vm.Inputs.Add(DataPin("cond", "bool", true));
                vm.Outputs.Add(ExecPin("true", false));
                vm.Outputs.Add(ExecPin("false", false));
                break;

            case LoopNode:
                AddExecPins(vm, withOutput: false);
                vm.Inputs.Add(DataPin("cond", "bool", true));
                vm.Outputs.Add(ExecPin("body", false));
                vm.Outputs.Add(ExecPin("done", false));
                break;

            case BottomLoopNode:
                AddExecPins(vm, withOutput: false);
                vm.Inputs.Add(DataPin("cond", "bool", true));
                vm.Outputs.Add(ExecPin("body", false));
                vm.Outputs.Add(ExecPin("done", false));
                break;

            case AssignNode:
                AddExecPins(vm);
                vm.Inputs.Add(DataPin("value", "value", true));
                break;

            case AssignBoolNode:
                AddExecPins(vm);
                vm.Inputs.Add(DataPin("value", "bool", true));
                break;

            case PersistentAssignNode:
                AddExecPins(vm);
                vm.Inputs.Add(DataPin("value", "value", true));
                break;

            case SetArrayNode setArray:
                AddExecPins(vm);
                for (var i = 0; i < setArray.Elements.Count; i++)
                    vm.Inputs.Add(DataPin($"elem{i}", ArgTypeName(setArray.Elements[i]), true));
                break;

            case ReturnNode:
                AddExecPins(vm, withOutput: false);
                vm.Inputs.Add(DataPin("value", "value", true));
                break;

            case ComputedJumpNode:
                AddExecPins(vm, withOutput: false);
                break;

            case PushFlowNode:
            case PopFlowNode:
            case PopFlowIfNotNode:
                AddExecPins(vm);
                break;

            default:
                AddExecPins(vm);
                break;
        }

        return vm;
    }

    private static void AddExecPins(GraphNodeVm vm, bool withOutput = true)
    {
        vm.Inputs.Add(ExecPin("exec", true));
        if (withOutput) vm.Outputs.Add(ExecPin("exec", false));
    }

    private static ConnectorVm ExecPin(string title, bool isInput) => new()
    {
        Title = title,
        IsInput = isInput,
        IsExec = true,
        TypeName = "exec",
        Brush = BrushOf("exec"),
    };

    private static ConnectorVm DataPin(string title, string typeName, bool isInput) => new()
    {
        Title = title,
        IsInput = isInput,
        IsExec = false,
        TypeName = typeName,
        Brush = BrushOf(typeName),
    };

    private void Connect(ConnectorVm output, ConnectorVm input, bool isExec)
    {
        if (output is null || input is null) return;
        _connections.Add(new ConnectionVm(output, input, isExec));
    }

    private static (string Title, string Kind, string Tooltip) Describe(KNode node) => node switch
    {
        CallNode n => (n.Function, "Call", n.Title),
        ContextCallNode n => ($"{n.ObjectVariable}.{n.Function}", "Call", n.Title),
        VirtualCallNode n => (n.Function, "Call", n.Title),
        LocalFinalCallNode n => (n.Function, "Call", n.Title),
        MathCallNode n => (n.Function, "Call", n.Title),
        MulticastCallNode n => ($"Multicast {n.Function}", "Call", n.Title),
        IfNode n => ("Branch", "Branch", n.Title),
        LoopNode n => ("While", "Branch", n.Title),
        BottomLoopNode n => ("DoWhile", "Branch", n.Title),
        AssignNode n => ($"Set {n.VariableName}", "Set", n.Title),
        AssignBoolNode n => ($"Set {n.VariableName}", "Set", n.Title),
        PersistentAssignNode n => ($"SetPersistent {n.Name}", "Set", n.Title),
        SetArrayNode n => ("SetArray", "Set", n.Title),
        ReturnNode n => ("Return", "Return", n.Title),
        ComputedJumpNode n => ("ComputedJump", "Opaque", n.Title),
        PushFlowNode n => ("PushFlow", "Opaque", n.Title),
        PopFlowNode n => ("PopFlow", "Opaque", n.Title),
        PopFlowIfNotNode n => ("PopFlowIfNot", "Opaque", n.Title),
        KismetDecompiler.RawNode n => (n.Title, "Opaque", n.Title),
        BlockGraphNode n => (n.Title, "Opaque", n.Title),
        BasicBlockNode n => (n.Title, "Opaque", n.Title),
        UberSectionNode n => (n.Title, "Opaque", n.Title),
        _ => (node.GetType().Name.Replace("Node", string.Empty), "Opaque", node.Title),
    };

    /// <summary>
    /// 标题美化：去掉反编译器带出来的常见前缀/后缀，只为了好读。
    /// 原始名字保留在 <see cref="GraphNodeVm.RawTitle"/> 与 tooltip 里，随时能切回去。
    /// </summary>
    private static string Pretty(string title)
    {
        var text = title;
        foreach (var prefix in new[] { "ObjectConst.", "K2Node_", "CallFunc_", "Temp_" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        const string returnSuffix = "_ReturnValue";
        if (text.EndsWith(returnSuffix, StringComparison.Ordinal) && text.Length > returnSuffix.Length)
            text = text[..^returnSuffix.Length];

        return text;
    }

    /// <summary>调用节点的被调函数名（双击跳转用）；非调用节点返回 null。</summary>
    private static string? CalleeOf(KNode node) => node switch
    {
        CallNode n => n.Function,
        ContextCallNode n => n.Function,
        VirtualCallNode n => n.Function,
        LocalFinalCallNode n => n.Function,
        MathCallNode n => n.Function,
        MulticastCallNode n => n.Function,
        _ => null,
    };

    /// <summary>按 jmap 签名补参数引脚名与类型；没有签名时退回 arg0..argN + 粗类型。</summary>
    private void AddParamPins(GraphNodeVm vm, IReadOnlyList<KArg> args, string functionName)
    {
        var signature = _options.SignatureOf?.Invoke(functionName);
        var parameters = signature?.Params.Where(p => p.Direction != "return").ToList();

        for (var i = 0; i < args.Count; i++)
        {
            var name = parameters is not null && i < parameters.Count ? parameters[i].Name : $"arg{i}";
            var type = parameters is not null && i < parameters.Count
                ? DisplayType(parameters[i])
                : ArgTypeName(args[i]);
            vm.Inputs.Add(DataPin(name, type, true));
        }
    }

    /// <summary>jmap 类型 → 引脚上显示的类型名（对象/结构体取类名末段）。</summary>
    private static string DisplayType(SolsticeWinter.Index.ScriptParam parameter)
    {
        if (!string.IsNullOrEmpty(parameter.Class))
        {
            var name = parameter.Class!;
            var dot = name.LastIndexOf('.');
            return dot >= 0 ? name[(dot + 1)..] : name;
        }

        return parameter.Type.Replace("Property", string.Empty, StringComparison.Ordinal);
    }

    private static string ArgTypeName(KArg arg) => arg switch
    {
        IntArg or LongArg or ByteArg or SkipOffsetArg => "int",
        BoolArg => "bool",
        FloatArg => "float",
        DoubleArg => "double",
        StringArg or UnicodeStringArg => "string",
        NameArg => "name",
        TextArg => "text",
        ObjectArg or NoObjectArg or SelfArg or DynamicCastArg or InterfaceCastArg
            or MetaCastArg or SoftObjectArg or ContextMemberArg or InstanceDelegateArg => "object",
        PointerArg p => string.IsNullOrEmpty(p.Name) ? "var" : p.Name,
        VectorArg or RotationArg or TransformArg or StructConstArg or StructMemberArg
            or ArrayConstArg or MapConstArg => "struct",
        ArrayGetByRefArg or PrimitiveCastArg or InterfaceContextArg => "value",
        CallArg => "value",
        _ => "unknown",
    };

    private static Brush BrushOf(string typeName) => typeName switch
    {
        "exec" => new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
        "bool" => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
        "int" => new SolidColorBrush(Color.FromRgb(0x1A, 0xBC, 0x9C)),
        "float" or "double" => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
        "string" or "name" or "text" => new SolidColorBrush(Color.FromRgb(0xE0, 0x56, 0xA0)),
        "object" => new SolidColorBrush(Color.FromRgb(0x3B, 0x8E, 0xD0)),
        "struct" => new SolidColorBrush(Color.FromRgb(0x2C, 0x5A, 0xA0)),
        _ => new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)),
    };
}






