using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Nodes;
using SolsticeWinter.Decompiler.Signatures;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace SolsticeWinter.Decompiler;

/// <summary>
/// 字节码 → 节点图。**忠实移植** <c>UEBlueprintGraphViewer.Decompiler.FunctionDecompiler</c> 的形状。
///
/// <para>
/// 旧实现（已删除）用了工作队列 + 约 20 个互相影响的可变字段 + 5 个事后图修整 pass，
/// 结果是入口节点挂满编译器临时变量、`<export>` 占位名、`Set Context`、悬空连线等等。
/// 这次重写的三条硬约束：
/// </para>
///
/// <list type="number">
/// <item>**线性指令流**：按偏移顺序走，跳转用递归处理目标块，不再有「每个位置最多走 2 次」的队列。</item>
/// <item>**只有一份临时变量表**（<see cref="_locals"/>），且**只装编译器临时变量**；
///       函数参数挂在入口节点上，类成员走 Get/Set 节点。入口节点不再当临时变量仓库。</item>
/// <item>**不做事后修整**：宏折叠 / 结构体拆包等如果要做，必须在构建期完成，
///       不能删完节点再补扫引用（那正是悬空连线的来源）。</item>
/// </list>
///
/// <para>
/// 取舍：暂时**不做**宏还原（For Each Loop / While Loop）和结构体 Break 折叠。
/// 先把「基础图是对的」这件事做扎实——折叠是锦上添花，基础错了折出来的也是错的。
/// </para>
/// </summary>
public sealed class GraphCompiler
{
    /// <summary>走查追踪（探针用）。设为 null 时无开销。</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>构建结果。<paramref name="Reached"/> 是走查实际访问过的指令偏移，诊断用。</summary>
    public sealed record BuildResult(
        IReadOnlyList<BPNode> Nodes,
        IReadOnlyList<string> Problems,
        IReadOnlyCollection<uint> Reached);

    /// <summary>
    /// out 参数调用统计（诊断用）：区分原生函数（jmap，`dir` 不可靠）与蓝图函数
    /// （索引，方向来自真实的 `CPF_OutParm` 标志）。
    /// </summary>
    public static Dictionary<string, int> OutParamCallCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>清零统计。</summary>
    public static void ResetOutParamStats()
    {
        OutParamCallCounts.Clear();
        CallInstructions = 0;
        CallNodesCreated = 0;
        CallNodesPruned = 0;
    }

    /// <summary>调用指令数（`CallNode` 被调用次数）。</summary>
    public static int CallInstructions { get; private set; }

    /// <summary>建出来的调用类节点数。</summary>
    public static int CallNodesCreated { get; private set; }

    /// <summary>被「死纯节点」清理删掉的调用类节点数。</summary>
    public static int CallNodesPruned { get; private set; }

    private readonly UAsset _asset;
    private readonly IFunctionSignatures? _signatures;
    private readonly IVariableTypes? _variables;

    private readonly List<BPNode> _nodes = [];
    private readonly List<string> _problems = [];

    /// <summary>编译器临时变量名 → 它的引脚（`TemporaryVariable` 节点的输出脚，或调用节点的输出脚）。</summary>
    private readonly Dictionary<string, GraphPin> _locals = new(StringComparer.Ordinal);

    /// <summary>临时变量名 → 它的变量节点（首次赋值/读取时懒创建）。</summary>
    private readonly Dictionary<string, K2Node_TemporaryVariable> _tempNodes = new(StringComparer.Ordinal);

    /// <summary>函数属性表里的**所有**变量类型（参数 + 局部变量），按名字索引。</summary>
    private readonly Dictionary<string, GraphPinType> _localTypes = new(StringComparer.Ordinal);

    /// <summary>已经处理过的块起始偏移，避免跳转递归无限循环。</summary>
    private readonly HashSet<uint> _visited = [];

    /// <summary>回边的目标偏移 = 循环头。</summary>
    private readonly HashSet<uint> _loopHeaders = [];

    /// <summary>循环头 → 已经发出的 Loop 节点。</summary>
    private readonly Dictionary<uint, K2Node_Loop> _loopNodes = [];

    /// <summary>函数参数名（入口节点的输出脚），用于区分「参数」和「编译器临时变量」。</summary>
    private readonly HashSet<string> _paramNames = new(StringComparer.Ordinal);

    private string _ownerClass = string.Empty;
    private uint? _currentOffset;
    private BPNode? _lastNode;
    private GraphPin? _tailExec;
    private uint _blockStart;
    private bool _pendingLoopHeader;

    /// <summary>每条指令所属**基本块**的起始偏移（与 `offsets` 同长）。</summary>
    private uint[] _blockStartOf = [];
    private uint _currentBlockStart;

    /// <summary>
    /// 基本块划分：块首（leader）= 函数第一条指令、所有跳转目标、跳转指令的下一条。
    ///
    /// <para>
    /// 没有这一步就没法判断「这个 `JumpIfNot` 是不是循环条件」——前两轮用「回边目标 ==
    /// JumpIfNot 偏移」和「回边目标落在链范围内」都失败了：前者永远对不上，后者把整条链
    /// 当成循环体、一次吞掉 65 个节点。
    /// </para>
    /// </summary>
    private void BuildBlocks(KismetExpression[] instructions, uint[] offsets)
    {
        var leaders = new HashSet<uint> { offsets.Length > 0 ? offsets[0] : 0u };

        for (var i = 0; i < instructions.Length; i++)
        {
            switch (instructions[i])
            {
                case EX_Jump jump:
                    leaders.Add(jump.CodeOffset);
                    break;
                case EX_JumpIfNot jumpIfNot:
                    leaders.Add(jumpIfNot.CodeOffset);
                    break;
                case EX_PushExecutionFlow push:
                    leaders.Add(push.PushingAddress);
                    break;
                case EX_PopExecutionFlowIfNot:
                case EX_PopExecutionFlow:
                case EX_Return:
                case EX_EndOfScript:
                    break;
            }

            // 跳转 / 返回 / 流程栈指令的下一条是新块的开头
            if (instructions[i] is EX_Jump or EX_JumpIfNot or EX_PopExecutionFlow
                or EX_PopExecutionFlowIfNot or EX_Return or EX_EndOfScript
                && i + 1 < instructions.Length)
            {
                leaders.Add(offsets[i + 1]);
            }
        }

        _blockStartOf = new uint[offsets.Length];
        uint current = offsets.Length > 0 ? offsets[0] : 0u;
        for (var i = 0; i < offsets.Length; i++)
        {
            if (leaders.Contains(offsets[i])) current = offsets[i];
            _blockStartOf[i] = current;
        }
    }
    private int _nodeBudget;

    private GraphCompiler(UAsset asset, IFunctionSignatures? signatures, IVariableTypes? variables)
    {
        _asset = asset;
        _signatures = signatures;
        _variables = variables;
    }

    /// <summary>把一个函数构建成节点图。</summary>
    public static BuildResult Build(UAsset asset, FunctionExport function,
        IReadOnlyList<uint> entryPoints, uint? startOffset, int maxNodes,
        IFunctionSignatures? signatures = null, IVariableTypes? variables = null)
    {
        var builder = new GraphCompiler(asset, signatures, variables) { _nodeBudget = maxNodes };
        try
        {
            builder.Run(function, entryPoints, startOffset);
        }
        catch (Exception e)
        {
            builder._problems.Add($"{e.GetType().Name}: {e.Message}");
        }

        return new BuildResult(builder._nodes, builder._problems, builder._visited);
    }

    private void Run(FunctionExport function, IReadOnlyList<uint> entryPoints, uint? startOffset)
    {
        var instructions = function.ScriptBytecode;
        if (instructions is null || instructions.Length == 0)
        {
            _problems.Add("函数没有字节码");
            return;
        }

        // 函数所属类：成员读的类型要靠它查变量表（传 null 会全部查不到 → 52% 引脚类型未知）
        _ownerClass = function.OuterIndex is { Index: > 0 } outer && outer.Index - 1 < _asset.Exports.Count
            ? _asset.Exports[outer.Index - 1].ObjectName?.ToString() ?? string.Empty
            : string.Empty;

        var offsets = BuildOffsets(instructions);

        // 预扫回边：`Jump` 的目标比自身偏移小 = 跳回去，那个目标就是**循环头**。
        // `While Loop` 宏要在循环头发出（条件在宏内部），所以必须先知道哪些偏移是循环头，
        // 不能等走到回边才回头补——那时候循环体已经走完了，结构就错了。
        for (var i = 0; i < instructions.Length; i++)
        {
            if (instructions[i] is EX_Jump { } jump && jump.CodeOffset < offsets[i])
                _loopHeaders.Add(jump.CodeOffset);

            // do-while 形状：条件在循环体末尾，回边是 `JumpIfNot`。
            if (instructions[i] is EX_JumpIfNot { } jumpIfNot && jumpIfNot.CodeOffset < offsets[i])
                _loopHeaders.Add(jumpIfNot.CodeOffset);
        }

        BuildBlocks(instructions, offsets);

        // 入口节点：只放**真参数**（`CPF_Parm`），编译器临时变量绝不挂上来
        var parameters = ParameterPins(function);
        var entry = new K2Node_FunctionEntry(function.ObjectName?.ToString() ?? "Entry", parameters, null);
        Add(entry);

        // 参数登记进 `_locals`：读参数要复用入口节点的输出脚。
        // 不登记的话每次读参数都会新建一个 `VariableGet`（类型还是 Unknown）——
        // 这是节点数翻倍和 38% 类型未知的主因。
        foreach (var parameter in entry.Output.Skip(1))
        {
            if (parameter.Name.Length > 0) _locals[parameter.Name] = parameter;
        }

        var exec = entry.ExecOutPin!;

        // 有多个入口（uber graph 的事件）：每个入口都从 entry 拉一条链
        var starts = entryPoints.Count > 0
            ? entryPoints.ToList()
            : [startOffset ?? 0u];

        // 走查本身可能因为畸形字节码抛异常。**不能**让它跳过下面的收尾清理——
        // 否则图里会留下一堆悬空引用（实测 124 处），而且整个函数看起来像坏了一半。
        try
        {
            foreach (var start in starts.Distinct())
            {
                var index = IndexOf(instructions, offsets, start);
                if (index < 0) continue;
                WalkBlock(instructions, offsets, index, exec, isFirst: true);
            }
        }
        catch (Exception e)
        {
            _problems.Add($"[走查中断] {e.GetType().Name}: {e.Message}");
        }

        // 兜底：字节码里有 `Return`，但图里没走到 Return 节点（执行链在某条
        // `PopExecutionFlow` / 回边处终止了，实测 `DrawTopCardFromDeck` 就丢了出口）。
        // 用记录下来的链尾补一个 Return 节点，至少让「这个函数有出口」在图上是可见的。
        //
        // **ubergraph 除外**：`ExecuteUbergraph_*` 在 UE 编辑器里没有单一出口，
        // 每个事件各自结束，补一个 Return 反而是错的（实测两个资产各触发 1 次，都是它）。
        var isUbergraph = function.ObjectName?.ToString()
            is { } functionName && functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal);

        var returnInstruction = isUbergraph ? null : instructions.OfType<EX_Return>().FirstOrDefault();
        if (returnInstruction is not null
            && !_nodes.Any(node => node is K2Node_FunctionResult)
            && _tailExec is { } tail)
        {
            EmitReturn(returnInstruction, 0, ref tail);
            _problems.Add("[补 Return] 执行链未到达 Return，已用链尾补上");
        }

        // 类型传播：已知类型沿数据边推断未知的类型。
        // 这是唯一能压住「成员读 → 未知 → 下游全未知」连锁的办法，也顺带检查矛盾。
        PropagateTypes();

        // 结构体字段读 → 一个 Break 节点（UE 的形状）。
        // 放在收尾清理之前：折叠会删掉一批 Get 节点，它们的连线由后面的不动点清理兜底。
        // **必须先算出结果再替换**：折叠抛异常时保留原图，否则 `_nodes` 已被清空、整个函数变成空视图
        // （实测 180 个函数全空）。
        try
        {
            Func<string, IReadOnlyList<(string Name, GraphPinType Type)>?>? fieldsOf =
                _variables is null ? null : _variables.FieldsOf;
            var collapsed = BreakStructCollapse.Apply(_nodes, fieldsOf);
            if (collapsed.Count > 0 && !ReferenceEquals(collapsed, _nodes))
            {
                _nodes.Clear();
                _nodes.AddRange(collapsed);
            }
        }
        catch (Exception e)
        {
            _problems.Add($"[Break 折叠失败，保留原图] {e.GetType().Name}: {e.Message}");
        }

        // 宏折叠：For Each Loop / While Loop 在字节码里是内联展开的一串指令，
        // UE 编辑器里是一个节点。必须在收尾清理**之前**跑，让清理兜住折叠留下的孤儿边。
        try
        {
            var macros = MacroCollapse.Apply(_nodes);
            if (macros.Count > 0 && !ReferenceEquals(macros, _nodes))
            {
                _nodes.Clear();
                _nodes.AddRange(macros);
            }
        }
        catch (Exception e)
        {
            _problems.Add($"[宏折叠失败，保留原图] {e.GetType().Name}: {e.Message}");
        }

        // 类型传播：已知类型沿数据边推断未知的类型。
        // **必须放在所有折叠之后**：折叠会重建连线（宏把消费者的上游改接到自己的输出脚上），
        // 放在前面的话这些新边没参与传播，校验器就会把它们报成「连错线」（实测 2 处）。
        PropagateTypes();

        // 收尾：反复做两件事直到不动点——
        //   A) 摘掉「不属于任何节点」的中间消费脚（`ValueOf` 造的、最终没挂到节点上的）
        //   B) 丢掉因此变成「纯节点 + 输出无人消费」的死表达式
        // 只做一轮是不够的：A 会摘掉某些节点唯一的消费者，那些节点随即变成死节点（实测孤立 750 个）。
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;
            var alive = new HashSet<BPNode>(_nodes);

            foreach (var node in _nodes)
            {
                foreach (var pin in node.Output)
                {
                    var removed = pin.LinkedTo.RemoveAll(consumer =>
                        consumer.ParentNode is null || !alive.Contains(consumer.ParentNode));
                    if (removed > 0) changed = true;
                }
            }

            // 纯节点只要输出没人消费就是死表达式（它的输入可能还连着东西，但没人用它的结果）
            var dead = _nodes.Where(node =>
                node.Pure && node.Output.All(pin => pin.LinkedTo.Count == 0)).ToList();

            if (dead.Count > 0)
            {
                foreach (var node in dead)
                {
                    // 统计：被清掉的调用类节点（纯调用没人消费 = 死表达式，属正常；
                    // 这个数突然变大说明「本来该有人消费」的连接断了）。
                    if (node is K2Node_CallFunction or K2Node_PromotableOperator) CallNodesPruned++;

                    // 删之前把它的连线**两端**都断开：只把它从 `_nodes` 里拿掉的话，
                    // 它的输出脚仍然挂在消费者的 `LinkedTo` 里，就成了「指向不存在节点」的悬空引用
                    // （实测 124 处）。
                    foreach (var pin in node.Output)
                    {
                        foreach (var consumer in pin.LinkedTo.ToList())
                        {
                            GraphPin.Disconnect(pin, consumer);
                        }
                    }

                    foreach (var pin in node.Input)
                    {
                        foreach (var producer in pin.LinkedTo.ToList())
                        {
                            GraphPin.Disconnect(producer, pin);
                        }
                    }

                    _nodes.Remove(node);
                }

                changed = true;
            }

            if (!changed) break;
        }

        // 最后一道闸：两端都没连线的**纯节点 / 临时变量节点**一律删掉。
        // 它们不该出现在 UE 编辑器里，留着就是画布上的孤立孤岛（实测 7~11 个）。
        // 迭代到不动点也未必收敛干净，所以这里再做一次显式清理。
        for (var pass = 0; pass < 4; pass++)
        {
            var orphans = _nodes.Where(node =>
                node.Output.All(pin => pin.LinkedTo.Count == 0)
                && node.Input.All(pin => pin.LinkedTo.Count == 0)
                && (node.Pure || node is K2Node_TemporaryVariable)).ToList();

            if (orphans.Count == 0) break;

            foreach (var node in orphans)
            {
                foreach (var pin in node.Input.Concat(node.Output))
                {
                    foreach (var other in pin.LinkedTo.ToList()) GraphPin.Disconnect(pin, other);
                }

                _nodes.Remove(node);
            }
        }
    }

    // ── 控制流 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 从 <paramref name="index"/> 开始走一条执行链，直到遇到跳转/返回。
    /// 分支用**递归**处理目标块，不再排队——这是 UEBGV 的形状。
    /// </summary>
    private void WalkBlock(KismetExpression[] instructions, uint[] offsets, int index, GraphPin exec, bool isFirst = false)
    {
        var guard = 0;

        // 这条链从哪个偏移开始——循环头判定要用（见 EmitJumpIfNot）。
        _blockStart = index >= 0 && index < instructions.Length ? offsets[index] : 0u;

        while (index >= 0 && index < instructions.Length)
        {
            if (--_nodeBudget <= 0) return;
            if (guard++ > 20000)
            {
                _problems.Add("单条执行链过长，已截断");
                return;
            }

            var instruction = instructions[index];
            var offset = offsets[index];

            if (!isFirst && _visited.Contains(offset)) return;
            _visited.Add(offset);
            isFirst = false;

            _currentOffset = offset;
            _tailExec = exec;
            _currentBlockStart = _blockStartOf[index];

            switch (instruction)
            {
                case EX_EndOfScript:
                    return;

                case EX_Return ret:
                    EmitReturn(ret, offset, ref exec);
                    return;

                case EX_Jump jump:
                    exec = EmitJump(instructions, offsets, jump, offset, exec);
                    return;

                case EX_JumpIfNot jumpIfNot:
                    EmitJumpIfNot(instructions, offsets, jumpIfNot, offset, exec);
                    return;

                case EX_PushExecutionFlow push:
                    // 记录「后续要跳到哪」，继续顺序执行
                    _pendingJumpTargets.Push(push.PushingAddress);
                    index++;
                    continue;

                case EX_PopExecutionFlowIfNot popIfNot:
                    EmitPopIfNot(instructions, offsets, popIfNot, offset, exec);
                    return;

                case EX_PopExecutionFlow:
                    if (_pendingJumpTargets.Count > 0)
                    {
                        var target = _pendingJumpTargets.Pop();
                        var targetIndex = IndexOf(instructions, offsets, target);
                        if (targetIndex >= 0) WalkBlock(instructions, offsets, targetIndex, exec);
                    }
                    return;

                default:
                    exec = EmitStatement(instruction, offset, exec);
                    index++;
                    continue;
            }
        }
    }

    private readonly Stack<uint> _pendingJumpTargets = new();

    /// <summary>
    /// 条件是不是 For Each Loop 的形态：`LENGTH(arr) &lt; index`。
    /// 命中就让位给 `MacroCollapse`（它需要一个 Branch 才能折成 For Each Loop 宏）。
    /// </summary>
    private static bool LooksLikeForEach(GraphPin condition)
    {
        if (condition.Source?.ParentNode is not K2Node_PromotableOperator { Name: "<" } less) return false;

        return less.Input
            .Where(p => p.Type.Category != PinType.Exec)
            .Any(p => p.Source?.ParentNode is K2Node_CallFunction
            {
                Name: "LENGTH",
            });
    }

    /// <summary>
    /// `JumpIfNot`：发 Branch，两条路各自递归。
    ///
    /// <para>
    /// **注意方向**：`EX_JumpIfNot &lt;addr&gt;` 是「条件为**假**时跳到 addr」，
    /// 所以跳转目标接 <c>False</c>、顺序执行接 <c>True</c>。
    /// 接反了图上每条 Branch 的 True/False 分支都是反的（这个 bug 之前一直存在）。
    /// </para>
    ///
    /// <para>
    /// 如果这个 `JumpIfNot` 是**循环头**（有回边跳回它），就发一个 `While Loop` 宏而不是 Branch：
    /// 条件在宏内部，顺序执行是循环体，跳转目标是循环出口——这正是 UE 的形状。
    /// </para>
    /// </summary>
    private void EmitJumpIfNot(KismetExpression[] instructions, uint[] offsets, EX_JumpIfNot jumpIfNot,
        uint offset, GraphPin exec)
    {
        var condition = ValueOf(jumpIfNot.BooleanExpression);
        Trace?.Invoke($"[cond] expr={jumpIfNot.BooleanExpression.GetType().Name} " +
                      $"pin='{condition.Name}' type={condition.Type.Display()} " +
                      $"source='{condition.Source?.Name}' srcType={condition.Source?.Type.Display()}");
        var nextIndex = IndexOf(instructions, offsets, offset) + 1;
        var targetIndex = IndexOf(instructions, offsets, jumpIfNot.CodeOffset);

        // 循环头判定：块首是回边目标 **且** 这个 `JumpIfNot` 是**向前跳**（出口在目标处）。
        //
        // 只认 while 形状：`<cond>; JumpIfNot exit; <body>; Jump header`。
        // do-while 形状是反过来的（`<body>; <cond>; JumpIfNot header`），body 在 JumpIfNot **之前**，
        // 按 while 处理会把出口当循环体、把循环体整个吞掉（实测 `autoPickHandTargets` 89 → 24 节点）。
        // do-while 暂时留作普通 Branch：少折一个宏，好过丢一半节点。
        var isLoopHeader = _loopHeaders.Contains(_currentBlockStart) && jumpIfNot.CodeOffset > offset;

        // **但 For Each Loop 也长成 while 的形状**：条件就是 `LENGTH(arr) < index`。
        // 如果这里抢先把 Branch 换成 While Loop，`MacroCollapse` 就再也认不出 For Each
        // （它必须看到 Branch + `<` + LENGTH + GET），实测 `For Each Loop` 折叠数直接掉到 0。
        // 所以条件形如 `LENGTH(...) < ...` 时让位给宏折叠，这里发普通 Branch。
        if (isLoopHeader && LooksLikeForEach(condition)) isLoopHeader = false;

        if (isLoopHeader && ProducerPin(condition) is { } conditionSource)
        {
            // 必须用**生产者输出脚**：`ValueOf` 返回的是消费者输入脚，直接当上游会抛
            // 「连线方向错误」并中断整条走查（实测 `autoPickHandTargets` 因此塌成 24 节点）。
            var macro = new K2Node_MacroInstance("While Loop", null, null, null, null, conditionSource);
            Add(macro);
            GraphPin.Connect(exec, macro.ExecPin);

            if (nextIndex > 0 && nextIndex < instructions.Length)
                WalkBlock(instructions, offsets, nextIndex, macro.ThenPin);
            if (targetIndex >= 0)
                WalkBlock(instructions, offsets, targetIndex, macro.CompletedPin);

            return;
        }

        var branch = new K2Node_IfThenElse(condition, jumpIfNot);
        Add(branch);
        GraphPin.Connect(exec, branch.ExecPin);

        // 跳转目标 = 条件为假
        if (targetIndex >= 0) WalkBlock(instructions, offsets, targetIndex, branch.ExecElsePin);

        // 顺序执行 = 条件为真
        if (nextIndex > 0 && nextIndex < instructions.Length)
            WalkBlock(instructions, offsets, nextIndex, branch.ExecOutPin);
    }

    /// <summary>
    /// `Jump`：向后 = 循环回边，向前 = 直接跳。
    ///
    /// <para>
    /// 回边**不再发 `Loop` 节点**：循环头已经在 `EmitJumpIfNot` 里发成 `While Loop` 宏了，
    /// 回边只是「绕回去」，图上没有对应的边（UE 编辑器里也没有）。以前在这里发一个 Loop
    /// 节点，位置在循环体末尾，结构是错的，`While Loop` 也永远折不出来。
    /// </para>
    /// </summary>
    private GraphPin EmitJump(KismetExpression[] instructions, uint[] offsets, EX_Jump jump, uint offset, GraphPin exec)
    {
        var target = jump.CodeOffset;

        // 回边：跳回已经走过的循环头，直接结束这条链（宏的 LoopBody 已经在别处接好）。
        if (target < offset) return exec;

        var targetIndex = IndexOf(instructions, offsets, target);
        if (targetIndex < 0) return exec;

        WalkBlock(instructions, offsets, targetIndex, exec);
        return exec;
    }

    /// <summary>`PopExecutionFlowIfNot`：条件不成立时跳到 push 记录的位置。</summary>
    private void EmitPopIfNot(KismetExpression[] instructions, uint[] offsets, EX_PopExecutionFlowIfNot popIfNot,
        uint offset, GraphPin exec)
    {
        var condition = ValueOf(popIfNot.BooleanExpression);
        var branch = new K2Node_IfThenElse(condition, popIfNot);
        Add(branch);
        GraphPin.Connect(exec, branch.ExecPin);

        // 条件为真 → 继续顺序；条件为假 → 跳到 push 的目标
        if (_pendingJumpTargets.Count > 0)
        {
            var target = _pendingJumpTargets.Pop();
            var targetIndex = IndexOf(instructions, offsets, target);
            if (targetIndex >= 0) WalkBlock(instructions, offsets, targetIndex, branch.ExecElsePin);
        }

        var nextIndex = IndexOf(instructions, offsets, offset) + 1;
        if (nextIndex > 0 && nextIndex < instructions.Length)
            WalkBlock(instructions, offsets, nextIndex, branch.ExecOutPin);
    }

    /// <summary>`Return`：发 Return 节点，接上返回值 / out 参数。</summary>
    private void EmitReturn(EX_Return ret, uint offset, ref GraphPin exec)
    {
        var pins = new List<GraphPin>();

        if (ret.ReturnExpression is not null and not EX_Nothing)
        {
            pins.Add(ValueOf(ret.ReturnExpression, "Return Value"));
        }
        else
        {
            // 用 out 参数返回：把登记过的 out 引脚接上
            foreach (var name in _outParams)
            {
                if (!_locals.TryGetValue(name, out var source)) continue;

                // 类型优先用**函数声明里的返回类型**：上游引脚的推断值常常是 Unknown，
                // 于是 Return 节点上显示 `value:Unknown`，而函数明明返回 String。
                var pinType = _localTypes.TryGetValue(name, out var declared) ? declared : source.Type;
                var pin = new GraphPin(name, PinDirection.Input, pinType);
                pins.Add(pin);
                GraphPin.Connect(source, pin);
            }
        }

        var node = new K2Node_FunctionResult(pins, ret);
        Add(node);
        GraphPin.Connect(exec, node.ExecPin);
    }

    private readonly List<string> _outParams = [];

    // ── 语句 ─────────────────────────────────────────────────────────────

    /// <summary>一条普通语句：赋值 / 调用 / 其它。返回新的执行链末端。</summary>
    private GraphPin EmitStatement(KismetExpression instruction, uint offset, GraphPin exec)
    {
        var node = instruction switch
        {
            EX_Let let => ProcessLet(let.Variable, let.Expression, let),
            EX_LetBase letBase => ProcessLet(letBase.VariableExpression, letBase.AssignmentExpression, letBase),
            EX_LetValueOnPersistentFrame persistent =>
                ProcessPersistentLet(persistent),
            EX_SetArray setArray => EmitSetArray(setArray),

            // 委托操作：以前完全没处理，语句被静默丢掉（实测 BP_Logic 有 12 条）。
            EX_BindDelegate bind => EmitDelegateOp(
                $"Bind Event to {bind.FunctionName}", bind.Delegate, bind.ObjectTerm, bind, multicast: false),
            EX_AddMulticastDelegate add => EmitDelegateOp(
                $"Add {VariableName(add.Delegate)}", add.Delegate, add.DelegateToAdd, add, multicast: true),
            EX_RemoveMulticastDelegate remove => EmitDelegateOp(
                $"Remove {VariableName(remove.Delegate)}", remove.Delegate, remove.DelegateToAdd, remove, multicast: true),
            EX_ClearMulticastDelegate clear => EmitDelegateOp(
                $"Clear {VariableName(clear.DelegateToClear)}", clear.DelegateToClear, null, clear, multicast: true),

            // 集合 / 映射字面量：和 `SetArray` 同一类，走容器字面量路径。
            EX_SetSet setSet => EmitContainerLiteral(VariableName(setSet.SetProperty), setSet.Elements, setSet),
            EX_SetMap setMap => EmitContainerLiteral(VariableName(setMap.MapProperty), setMap.Elements, setMap),

            _ => null,
        };

        if (node is not null)
        {
            // `ProcessLet` 可能返回一个**已经由 `ValueOf` 建好并加进列表**的节点（impure 调用），
            // 这时不能再 Add 一次；只补上 exec 连线。
            if (!_nodes.Contains(node)) Add(node);
            if (node.ExecPin is not null) GraphPin.Connect(exec, node.ExecPin);
            return node.ExecOutPin ?? exec;
        }

        // 顶层「裸调用」语句（`EX_LocalFinalFunction` 等直接当语句用）：
        // 之前这里返回 null，整个调用被丢掉——`AIStopHangingOnMulligan` 就只剩入口和返回两个节点。
        if (IsCallExpression(instruction))
        {
            _lastNode = null;
            ValueOf(instruction, string.Empty, isStatement: true);

            // 注意：不能靠「返回脚有没有上游」判断是否建了节点——
            // 无返回值的调用没有输出脚，那样会漏掉整个语句（实测 `LocalVirtualFunction @106`）。
            if (_lastNode is { } callNode)
            {
                if (callNode.ExecPin is not null) GraphPin.Connect(exec, callNode.ExecPin);
                return callNode.ExecOutPin ?? exec;
            }
        }

        // 赋值 / SetArray 折叠掉（不发节点）是正常的，不算「未处理」；其它类型才记。
        if (instruction is not (EX_Let or EX_LetBase or EX_LetBool or EX_SetArray or EX_SetSet or EX_SetMap))
        {
            _problems.Add($"[未处理语句] {instruction.GetType().Name.Replace("EX_", string.Empty)} @{offset}");
        }

        return exec;
    }

    /// <summary>
    /// 赋值。规则（与 UEBGV 的 `ProcessLetNode` 一致）：
    /// 目标是 out 参数 → 只登记；赋值是调用 → 只发调用节点并登记；
    /// 目标是编译器临时变量 → 变量节点 + Assign；否则 → Set 节点。
    /// </summary>
    private BPNode? ProcessLet(KismetExpression variable, KismetExpression assignment, KismetExpression instruction)
    {
        var name = VariableName(variable);
        Trace?.Invoke($"[let] '{name}' ← {assignment.GetType().Name}");

        if (variable is EX_LocalOutVariable)
        {
            var value = ValueOf(assignment);
            RegisterProducer(name, value);
            if (!_outParams.Contains(name)) _outParams.Add(name);
            return null;
        }

        // **值表达式**直接登记生产者，不建临时变量节点 + 赋值节点。
        // 原来只认「调用」，于是 `Let X = <结构体成员读>`（`BreakVector2D` 那种）
        // 会生成一个 `Local Double (X)` 占位节点 + 一个赋值节点，图上多出两坨东西，
        // 而 UE 里它只是 `Break Vector2D` 的一个输出脚。
        if (IsValueExpression(assignment))
        {
            var value = ValueOf(assignment);

            // 如果调用的某个 `out` 参数正好叫这个名字，就登记**那个**输出脚，
            // 而不是笼统取第一个（多 out 参数的函数会登记错）。
            if (_lastNode?.Output.FirstOrDefault(p => p.IsOutput && p.Name == name) is { } outPin)
                RegisterProducer(name, outPin);
            else
                RegisterProducer(name, value);

            // **impure 调用在 `Let` 里同样是一条语句**：必须挂到 exec 链上。
            if (_lastNode is { ExecPin: not null } callNode) return callNode;
            return null;
        }

        // `CallFunc_*_ReturnValue` / `K2Node_*` 这类**调用结果临时变量**：
        // 只把生产者引脚登记进 `_locals`，**不建节点**（UEBGV的 `LetFunctionValue` 就是这样）。
        // 建节点会让节点数翻倍（实测 2235 → 3909），而且 UE 编辑器里这些根本不可见。
        if (IsCompilerLocal(name) && !IsTempVariable(name))
        {
            var producer = ValueOf(assignment, name);
            RegisterProducer(name, producer);
            return null;
        }

        if (IsCompilerLocal(name))
        {
            var tempNode = EnsureTempNode(name);
            var value = ValueOf(assignment, name);

            // 临时变量节点继承被赋值的类型（先读后写时首次读到是 Unknown）。
            // 传 `inferred: true`：这是**推断**出来的类型，不是签名权威值，
            // 遇到权威消费者时应该让位（否则会顶出「连错线」的假阳性）。
            if (tempNode.VarPin.Type.Category == PinType.Unknown && value.Type.Category != PinType.Unknown)
            {
                tempNode.VarPin.Retype(value.Type, inferred: true);
            }
            var variablePin = new GraphPin("Variable", PinDirection.Input, tempNode.VarPin.Type);
            var valuePin = new GraphPin("Value", PinDirection.Input, value.Type);

            var assign = new K2Node_AssignmentStatement(variablePin, valuePin, instruction);
            GraphPin.Connect(tempNode.VarPin, variablePin);

            // `ValueOf` 返回的是**消费者输入脚**，`value.IsOutput` 永远为假——
            // 于是赋值节点的 Value 脚从来没连上，生产者成了孤立节点
            // （实测 `Make S_SalvagedCardInfo` 就是这么孤立的）。必须过 `ProducerPin`。
            if (ProducerPin(value) is { } producer) GraphPin.Connect(producer, valuePin);
            else valuePin.Value = value.Value;

            if (ProducerPin(value) is { } registered) _locals[name] = registered;
            return assign;
        }

        // 类成员 / 普通变量 → Set 节点
        var assigned = ValueOf(assignment, name);
        var setValue = new GraphPin(name, PinDirection.Input, assigned.Type) { IsNameHidden = true };
        if (ProducerPin(assigned) is { } assignedSource) GraphPin.Connect(assignedSource, setValue);
        else setValue.Value = assigned.Value;

        var set = new K2Node_VariableSet(new PropertyRef(name, assigned.Type), setValue, null, instruction);
        // 不要把 setValue（输入脚）缓存进 _locals：它没有下游，后续拿它当上游会连出「输入→输入」的非法边。
        return set;
    }

    /// <summary>
    /// 容器字面量（数组 / 集合 / 映射）。目标和 `SetArray` 一样：编译器临时变量就发
    /// `Make Array` 并把输出登记为生产者，普通变量才发 Set 节点。
    /// </summary>
    private BPNode? EmitContainerLiteral(string name, KismetExpression[]? elements, KismetExpression instruction)
    {
        var pins = new List<GraphPin>();
        foreach (var element in elements ?? []) pins.Add(ValueOf(element));

        if (IsCompilerLocal(name))
        {
            var array = new K2Node_MakeArray(new GraphPinType(PinType.Unknown), pins, instruction);
            Add(array);
            RegisterProducer(name, array.OutPin);
            return null;
        }

        var node = new K2Node_VariableSet(
            new PropertyRef(name, new GraphPinType(PinType.Unknown, Container: PinContainerType.Array)),
            new GraphPin("Array", PinDirection.Input, new GraphPinType(PinType.Unknown, Container: PinContainerType.Array)),
            null,
            instruction);

        foreach (var pin in pins) node.AddInputPin(pin);
        return node;
    }

    /// <summary>委托操作节点（Bind / Add / Remove / Clear）。</summary>
    private BPNode EmitDelegateOp(string title, KismetExpression delegateExpr, KismetExpression? handlerExpr,
        KismetExpression instruction, bool multicast)
    {
        var delegatePin = ValueOf(delegateExpr, "Delegate");
        var handlerPin = handlerExpr is null ? null : ValueOf(handlerExpr, multicast ? "Delegate To Add" : "Object");
        return new K2Node_DelegateOp(title, delegatePin, handlerPin, instruction, multicast);
    }

    /// <summary>持久帧写入（目标是指针而不是表达式）。</summary>
    private BPNode? ProcessPersistentLet(EX_LetValueOnPersistentFrame persistent)
    {
        var name = VariableName(persistent.DestinationProperty);
        var value = ValueOf(persistent.AssignmentExpression, name);

        var valuePin = new GraphPin(name, PinDirection.Input, value.Type) { IsNameHidden = true };
        if (ProducerPin(value) is { } source) GraphPin.Connect(source, valuePin);
        else valuePin.Value = value.Value;

        var node = new K2Node_VariableSet(new PropertyRef(name, value.Type), valuePin, null, persistent);
        _locals[name] = valuePin;
        return node;
    }

    private BPNode? EmitSetArray(EX_SetArray setArray)
    {
        var name = VariableName(setArray.AssigningProperty);

        var elements = new List<GraphPin>();
        foreach (var element in setArray.Elements ?? [])
        {
            elements.Add(ValueOf(element));
        }

        // 目标是**编译器临时变量**（`K2Node_MakeArray_Array`）时不能画成 `Set <临时名>`：
        // UE 里这是一个 `Make Array` 节点。画成 Set 会露出编译器的内部名（实测就是那样）。
        if (IsCompilerLocal(name))
        {
            var array = new K2Node_MakeArray(new GraphPinType(PinType.Unknown), elements, setArray);
            Add(array);
            RegisterProducer(name, array.OutPin);
            return null;
        }

        var node = new K2Node_VariableSet(
            new PropertyRef(name, new GraphPinType(PinType.Unknown, Container: PinContainerType.Array)),
            new GraphPin("Array", PinDirection.Input, new GraphPinType(PinType.Unknown, Container: PinContainerType.Array)),
            null,
            setArray);

        foreach (var element in elements) node.AddInputPin(element);

        return node;
    }

    // ── 表达式 → 引脚 ────────────────────────────────────────────────────

    /// <summary>
    /// 表达式 → 输入脚。会产生节点的情况（调用 / 成员读 / 结构体常量）在这里建节点。
    /// </summary>
    private GraphPin ValueOf(KismetExpression expression, string name = "", bool isStatement = false)
    {
        switch (expression)
        {
            case EX_LocalVariable or EX_DefaultVariable:
            {
                var variableName = VariableName(expression);
                if (_locals.TryGetValue(variableName, out var known)) return Consumer(known, name);
                if (IsCompilerLocal(variableName))
                {
                    // 带上当前指令偏移：定位「这个编译器临时变量到底是被哪条指令读的」。
                    Trace?.Invoke($"[miss] '{variableName}' @{_currentOffset}");
                    return Consumer(EnsureTempNode(variableName).VarPin, name);
                }
                return ReadMember(variableName, name, null);
            }

            case EX_LocalOutVariable:
            {
                var variableName = VariableName(expression);
                if (_locals.TryGetValue(variableName, out var known)) return Consumer(known, name);
                return Consumer(EnsureTempNode(variableName).VarPin, name);
            }

            case EX_InstanceVariable:
                return ReadMember(VariableName(expression), name, null);

            case EX_Context context:
                return ReadContext(context, name);

            case EX_CallMath call:
                return CallNode(call.StackNode, call.Parameters, call, name, null, null, isStatement);

            case EX_LocalFinalFunction call:
                return CallNode(call.StackNode, call.Parameters, call, name, null, null, isStatement);

            case EX_FinalFunction call:
                return CallNode(call.StackNode, call.Parameters, call, name, null, null, isStatement);

            case EX_LocalVirtualFunction call:
                return CallNode(null, call.Parameters, call, name, call.VirtualFunctionName.ToString(), null, isStatement);

            case EX_VirtualFunction call:
                return CallNode(null, call.Parameters, call, name, call.VirtualFunctionName.ToString(), null, isStatement);

            case EX_StructConst constant:
                return MakeStruct(constant, name);

            case EX_StructMemberContext member:
                return ReadMember(VariableName(member.StructMemberExpression), name, null);

            case EX_Self:
                return new GraphPin("self", PinDirection.Input, new GraphPinType(PinType.Object)) { Value = "self" };

            case EX_IntConst c: return Literal(name, PinType.Int, c.Value.ToString());
            case EX_Int64Const c: return Literal(name, PinType.Int64, c.Value.ToString());
            case EX_UInt64Const c: return Literal(name, PinType.Int64, c.Value.ToString());
            case EX_FloatConst c: return Literal(name, PinType.Float, c.Value.ToString("0.###"));
            case EX_DoubleConst c: return Literal(name, PinType.Double, c.Value.ToString("0.###"));
            case EX_ByteConst c: return Literal(name, PinType.Byte, c.Value.ToString());
            case EX_True: return Literal(name, PinType.Bool, "true");
            case EX_False: return Literal(name, PinType.Bool, "false");
            case EX_StringConst c: return Literal(name, PinType.String, c.Value ?? string.Empty);
            case EX_UnicodeStringConst c: return Literal(name, PinType.String, c.Value ?? string.Empty);
            case EX_TextConst c: return Literal(name, PinType.Text, c.ToString() ?? "text");
            case EX_NameConst c: return Literal(name, PinType.Name, c.Value?.ToString() ?? string.Empty);
            case EX_NoObject: return Literal(name, PinType.Object, "None");
            case EX_Nothing: return new GraphPin(name, PinDirection.Input, new GraphPinType(PinType.Unknown));
            case EX_IntZero: return Literal(name, PinType.Int, "0");
            case EX_IntOne: return Literal(name, PinType.Int, "1");

            default:
                return new GraphPin(name, PinDirection.Input, new GraphPinType(PinType.Unknown));
        }
    }

    private static GraphPin Literal(string name, PinType type, string value) =>
        new(name, PinDirection.Input, new GraphPinType(type)) { Value = value };

    /// <summary>把上游输出脚包一个输入脚（消费者）。</summary>
    private GraphPin Consumer(GraphPin source, string name)
    {
        var pin = new GraphPin(string.IsNullOrEmpty(name) ? "value" : name, PinDirection.Input, source.Type);
        GraphPin.Connect(source, pin);
        return pin;
    }

    /// <summary>读类成员 / 普通变量 → `VariableGet` 节点。</summary>
    private GraphPin ReadMember(string variableName, string name, GraphPin? target)
    {
        // 类型归属：优先用**对象本身的类型**（`card.CardID` 的 CardID 属于 BaseCardObject，
        // 不属于当前函数所在的 BP_Logic），没有再退回当前类。
        var owner = target?.Type.SubCategoryObject is { Length: > 0 } objectType
            ? objectType[(objectType.LastIndexOf('.') + 1)..]
            : _ownerClass;

        var type = _variables?.Find(owner, variableName)
                   ?? _variables?.Find(_ownerClass, variableName)
                   ?? new GraphPinType(PinType.Unknown);
        var node = new K2Node_VariableGet(new PropertyRef(variableName, type), target, null);
        Add(node);
        return Consumer(node.VarPin, name);
    }

    /// <summary>`EX_Context`：对象上的调用或成员读。</summary>
    private GraphPin ReadContext(EX_Context context, string name)
    {
        var target = ValueOf(context.ObjectExpression, "Target");

        switch (context.ContextExpression)
        {
            case EX_StructMemberContext member:
                return ReadMember(VariableName(member.StructMemberExpression), name, target);

            case EX_InstanceVariable variable:
                return ReadMember(VariableName(variable), name, target);

            case EX_LocalVariable variable:
                return ReadMember(VariableName(variable), name, target);

            case EX_LocalFinalFunction call:
                return CallNode(call.StackNode, call.Parameters, context, name, null, target);

            case EX_FinalFunction call:
                return CallNode(call.StackNode, call.Parameters, context, name, null, target);

            case EX_LocalVirtualFunction call:
                return CallNode(null, call.Parameters, context, name, call.VirtualFunctionName.ToString(), target);

            case EX_VirtualFunction call:
                return CallNode(null, call.Parameters, context, name, call.VirtualFunctionName.ToString(), target);

            default:
                return new GraphPin(name, PinDirection.Input, new GraphPinType(PinType.Unknown));
        }
    }

    /// <summary>调用 → `CallFunction` 节点（运算符会折成 `PromotableOperator`）。</summary>
    private GraphPin CallNode(FPackageIndex? stackNode, KismetExpression[] parameters,
        KismetExpression instruction, string name, string? virtualName = null, GraphPin? target = null, bool isStatement = false)
    {
        CallInstructions++;

        var (functionName, owner) = virtualName is not null
            ? (virtualName, string.Empty)
            : ResolveFunction(stackNode);

        var signature = ResolveSignature(stackNode, functionName);

        // 统计：带 out 参数的调用里，**原生函数**和**蓝图函数**各占多少。
        // 决定「能不能只对蓝图函数启用精确的 out 判据」要靠这个数字，不是靠猜。
        if (signature is not null)
        {
            var outCount = signature.Parameters.Count(p => p.IsOut && !p.IsReturn);
            if (outCount > 0)
            {
                var ownerPath = signature.Owner ?? string.Empty;
                var isNative = ownerPath.StartsWith("/Script", StringComparison.Ordinal)
                               || (!ownerPath.Contains("_C", StringComparison.Ordinal)
                                   && !ownerPath.StartsWith("BP_", StringComparison.Ordinal));
                var key = isNative ? "native" : "blueprint";
                OutParamCallCounts[key] = OutParamCallCounts.GetValueOrDefault(key) + 1;
                OutParamCallCounts[$"{key}:out={outCount}"] =
                    OutParamCallCounts.GetValueOrDefault($"{key}:out={outCount}") + 1;
            }
            else
            {
                OutParamCallCounts["no-out"] = OutParamCallCounts.GetValueOrDefault("no-out") + 1;
            }
        }

        // `out` 参数的两义性：jmap 把 const-ref 输入也标成 `dir:out`，我们分不出来。
        //   · 没有返回值 → 全部当输出（`GetHandLocationBySide(side, out handLocation)`，已稳定）；
        //   · 有返回值   → 全部当 const-ref 输入（`TextIsEmpty(const FText& InText)`）。
        //
        // 试过三种通用判据想救出 `Array_Get` 的真 out 参数，全部失败并已回退：
        //   ① 按「实参是不是编译器临时变量」——Cards 类型不兼容 0→13、节点少 900；
        //   ② 按「实参此前有没有被生产过」——校验通过，但节点少 475 且 `For Each Loop` 仍 0；
        //   ③ 只对蓝图函数启用——实测 out 调用里原生占 2/3（342:132、1509:758），
        //      而 For Each 依赖的 `Array_Get` 正是原生，圈不住。
        //   ④ 容器访问器白名单——跳过实参后没有配套的登记，`[miss]` 依旧、For Each 仍 0，已回退。
        //
        // 结论：**jmap 的 `dir` 分不出「真 out」和「const-ref 输入」**，这条路在当前数据源下走不通。
        var hasReturnParam = signature?.Parameters.Any(p => p.IsReturn) == true;

        var pins = new List<GraphPin>();
        if (target is not null) pins.Add(target);
        pins.AddRange(BuildArgumentPins(parameters, signature));

        // 造输出脚：
        //   · 没有返回值 → 所有 `out` 参数都是输出（`GetHandLocationBySide(side, out handLocation)`）；
        //   · 有返回值 → 只给**真输出**（实参此前没被生产过的那些）建输出脚。
        if (signature is not null && !hasReturnParam)
        {
            var outCandidates = signature.Parameters.Where(p => p.IsOut && !p.IsReturn);

            foreach (var outParam in outCandidates)
            {
                if (pins.Any(p => p.Name == outParam.Name && p.IsOutput)) continue;
                pins.Add(new GraphPin(outParam.Name, PinDirection.Output, outParam.Type));
            }
        }

        // 语句位置的调用默认 impure（要 exec）；已知纯函数例外。
        var pure = KismetPurity.Of(functionName, signature?.Owner ?? owner)
                   ?? (isStatement ? false : !(signature?.Parameters.Any(p => p.IsOut) ?? false));
        var returnPin = signature?.Parameters.FirstOrDefault(p => p.IsReturn) is { } ret
            ? new GraphPin(ret.Name, PinDirection.Output, ret.Type)
            : signature is null
                ? new GraphPin("ReturnValue", PinDirection.Output, new GraphPinType(PinType.Unknown))
                : null;

        BPNode node = PromotableOperators.Resolve(functionName) is { } symbol
            ? new K2Node_PromotableOperator(symbol, functionName, pins, instruction, pure, returnPin)
            : new K2Node_CallFunction(functionName, signature?.Owner ?? owner, pins, instruction, pure, returnPin,
                KismetDisplayNames.Resolve(functionName));

        Add(node);
        if (node is K2Node_CallFunction or K2Node_PromotableOperator) CallNodesCreated++;

        // 泛型容器访问器的返回类型是**抹掉的**：jmap 里 `Map_Find(...)` 一律返回 `Int`，
        // 不管容器实际装的是 Object / String / Name。这种类型不是事实，标成推断值，
        // 让真正的消费者类型赢（否则校验器会把数据噪声报成「连错线」，实测 4 处）。
        if (IsGenericContainerAccessor(functionName))
        {
            foreach (var pin in node.Output.Where(p => !ReferenceEquals(p, node.ExecOutPin)))
            {
                pin.Retype(pin.Type, inferred: true);
            }
        }

        // **out 参数的实参就是「结果写到哪」**：UE 字节码把 out 变量当实参传进调用
        // （`Array_Get(arr, index, CallFunc_Array_Get_Item)`），写入发生在调用内部、**没有 Let**。
        // 不登记的话，后面读这个名字就会 miss，调用节点因为「纯节点没人消费」被当死节点删掉——
        // 于是 `For Each Loop` 找不到 GET，折叠数直接掉到 0（实测 475 个函数全中）。

        // 有返回值时返回**返回值**脚：多 out 参数的函数里第一个输出脚可能是别的参数，
        // 拿错了整条链路类型都错（实测 `Branch.Condition` 拿到 Text 而不是 Bool）。
        var produced = node.Output.FirstOrDefault(p => !ReferenceEquals(p, node.ExecOutPin)
                                                      && p.Name == "ReturnValue")
                       ?? node.GetFirstOutputParam();
        if (produced is { } output) return Consumer(output, name);
        return new GraphPin(name, PinDirection.Input, new GraphPinType(PinType.Unknown));
    }

    /// <summary>
    /// <summary>
    /// 泛型容器的存取器：返回类型在 jmap 里被抹成 `Int`，不能当权威类型用。
    /// </summary>
    private static bool IsGenericContainerAccessor(string functionName) => functionName
        is "Map_Find" or "Map_FindChecked" or "Map_Get" or "Array_Get" or "GET" or "ArrayGetByRef";

    /// <summary>
    /// 已知的**容器访问器**：最后一个非返回的 `out` 参数是真正的写入目标。
    /// 用白名单而不是通用规则——通用判据已经把 Cards 弄坏过两次（0→13 类型错乱、节点少 900）。
    /// </summary>
    private static int ContainerAccessorOutParam(string functionName, FunctionSignature? signature)
    {
        if (signature is null) return -1;
        if (functionName is not ("Array_Get" or "Map_Find" or "Map_FindChecked" or "ArrayGetByRef")) return -1;

        for (var i = signature.Parameters.Count - 1; i >= 0; i--)
        {
            var parameter = signature.Parameters[i];
            if (parameter.IsOut && !parameter.IsReturn) return i;
        }

        return -1;
    }

    /// 按签名造参数脚：有签名用签名名，没有就 `arg{i}`。
    /// <paramref name="skipOutIndex"/> 是「写结果的实参」下标，不建输入脚。
    /// </summary>
    private List<GraphPin> BuildArgumentPins(KismetExpression[] parameters, FunctionSignature? signature,
        int skipOutIndex = -1)
    {
        var hasReturnParam = signature?.Parameters.Any(p => p.IsReturn) == true;
        var outParamsAreOutputs = signature is not null && !hasReturnParam;

        var pins = new List<GraphPin>(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = signature is not null && i < signature.Parameters.Count ? signature.Parameters[i] : null;

            // 真输出参数：字节码实参是「结果写到哪」，不是数据来源 → 不建输入脚。
            // const-ref 的 `out` 参数（有返回值的函数）仍然是输入，必须建。
            if (parameter is { IsOut: true } && outParamsAreOutputs) continue;
            if (i == skipOutIndex) continue;

            var pin = ValueOf(parameters[i], parameter?.Name ?? $"arg{i}");

            // 参数脚显式采用**签名里的类型**：它的值往往来自局部变量读（类型未知），
            // 但调用点要求的类型是确定的，显示成 Unknown 没有意义（实测 `cardID:Unknown`）。
            if (parameter is not null
                && pin.Type.Category == PinType.Unknown
                && parameter.Type.Category != PinType.Unknown)
            {
                pin.Retype(parameter.Type);
            }

            pins.Add(pin);
        }

        return pins;
    }

    /// <summary>`EX_StructConst` → `MakeStruct` 节点。</summary>
    private GraphPin MakeStruct(EX_StructConst constant, string name)
    {
        var structName = ResolveIndexPath(constant.Struct) ?? "Struct";
        var fields = _variables?.FieldsOf(structName);

        var pins = new List<GraphPin>();
        var values = constant.Value ?? [];
        for (var i = 0; i < values.Length; i++)
        {
            var fieldName = fields is not null && i < fields.Count ? fields[i].Name : $"field{i}";
            var fieldType = fields is not null && i < fields.Count ? fields[i].Type : (GraphPinType?)null;
            pins.Add(ValueOf(values[i], fieldName));
            if (fieldType is { } ft) pins[^1] = new GraphPin(fieldName, PinDirection.Input, ft)
            {
                Value = pins[^1].Value,
            };
        }

        var node = new K2Node_MakeStruct(structName, pins, constant);
        Add(node);
        return Consumer(node.OutPin, name);
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private void Add(BPNode node)
    {
        _nodes.Add(node);
        _lastNode = node;
        if (_nodes.Count > _nodeBudget) _problems.Add("节点数超过上限");
    }

    /// <summary>
    /// 类型传播：沿数据边把已知类型推给未知的一侧，迭代到不动点。
    ///
    /// <para>
    /// 两个方向都要做：生产者已知 → 消费者未知（普通情况）；
    /// 消费者已知（来自签名）→ 生产者未知（成员读、临时变量这种「自己查不到但下游知道」的情况）。
    /// 不做的话，一个未知的成员读会让整条下游链路都保持未知。
    /// </para>
    ///
    /// <para>
    /// **冲突不传播**：同一根线两端都是已知但类型不兼容时直接跳过，留给校验器报
    /// 「连线两端类型不兼容」——默默改掉一个类型会把真问题藏起来。
    /// </para>
    /// </summary>
    private void PropagateTypes()
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;

            foreach (var node in _nodes)
            {
                foreach (var output in node.Output)
                {
                    foreach (var input in output.LinkedTo.Where(p => p.IsInput))
                    {
                        var from = output.Type.Category;
                        var to = input.Type.Category;

                        if (from == to) continue;

                        var fromSoft = output.IsTypeInferred;
                        var toSoft = input.IsTypeInferred;

                        // 未知的、或者只是「推断值」的一侧，都让位给权威的一侧。
                        if ((to == PinType.Unknown || (toSoft && !fromSoft)) && from != PinType.Unknown)
                        {
                            input.Retype(output.Type, inferred: true);
                            changed = true;
                        }
                        else if ((from == PinType.Unknown || (fromSoft && !toSoft)) && to != PinType.Unknown)
                        {
                            output.Retype(input.Type, inferred: true);
                            RetypeNodeHeader(output.ParentNode);
                            changed = true;
                        }
                        // 两边都权威且不兼容：**不动**，交给校验器报「连错线」。
                        Trace?.Invoke($"[冲突] 「{output.ParentNode?.Name}」.{output.Name}" +
                                      $"({output.Type.Display()},soft={fromSoft}) → " +
                                      $"「{input.ParentNode?.Name}」.{input.Name}({input.Type.Display()},soft={toSoft})");
                    }
                }
            }

            if (!changed) break;
        }
    }

    /// <summary>临时变量节点的标题里嵌了类型（`Local Unknown (X)`），定型后要跟着改。</summary>
    private static void RetypeNodeHeader(BPNode? node)
    {
        // 标题规则交给节点自己（它会隐藏 `CallFunc_*` / `K2Node_*` 这类编译器内部名）。
        if (node is K2Node_TemporaryVariable temp) temp.RefreshTitle();
    }

    /// <summary>取（必要时创建）编译器临时变量的变量节点。</summary>
    private K2Node_TemporaryVariable EnsureTempNode(string name)
    {
        if (_tempNodes.TryGetValue(name, out var existing)) return existing;

        var node = new K2Node_TemporaryVariable(name, new GraphPinType(PinType.Unknown), null);

        // 临时变量的类型是我们推断的（先读后写时甚至还是 Unknown），标成 soft：
        // 遇到签名给的权威类型时应当让位，否则会顶出「连错线」的假阳性。
        node.VarPin.Retype(node.VarPin.Type, inferred: true);
        _tempNodes[name] = node;
        Add(node);
        return node;
    }

    /// <summary>
    /// 登记一个临时变量的真正生产者。
    ///
    /// <para>
    /// **先读后写**是关键场景：读的时候 `EnsureTempNode` 建了一个没有生产者的 `Local` 节点，
    /// 消费者已经挂在它上面；后来赋值才出现真正的生产者。这里把消费者改接过去，
    /// 否则图上会留一个悬空的 Local 节点，而下游（如 Branch.Condition）永远读不到值。
    /// </para>
    /// </summary>
    /// <summary>
    /// `ValueOf` 返回的是**消费者输入脚**（用来挂到节点上）；但登记进 `_locals` 的必须是
    /// **生产者输出脚**——否则后续 `Consumer()` 会拿输入脚当上游，连出「输入→输入」的非法边，
    /// 而 `if (pin.IsOutput)` 这类判断会全部为假，导致临时变量根本没登记（实测所有
    /// `K2Node_SwitchString_CmpSuccess` 读都 miss、比较节点被当死节点删掉）。
    /// </summary>
    private static GraphPin? ProducerPin(GraphPin pin) =>
        pin.IsOutput ? pin : pin.Source;

    private void RegisterProducer(string name, GraphPin producer)
    {
        // 解析不到真正的输出脚就不登记：把输入脚当上游会连出「输入→输入」的非法边，
        // 抛出的异常会中断整条走查、连收尾清理都跑不到（实测 `tmpHandLocation`）。
        if (ProducerPin(producer) is not { } output) return;

        if (_tempNodes.TryGetValue(name, out var placeholder) && placeholder.VarPin.IsConnected)
        {
            placeholder.VarPin.RedirectConsumersTo(output);
        }

        _locals[name] = output;
    }

    /// <summary>编译器临时变量（不是函数参数、也不是类成员）。</summary>
    /// <summary>`Temp_` 前缀：UE 会为它发一个「Local X」变量节点。</summary>
    private static bool IsTempVariable(string name) => name.StartsWith("Temp_", StringComparison.Ordinal);

    private bool IsCompilerLocal(string name) =>
        !_paramNames.Contains(name)
        && (name.StartsWith("Temp_", StringComparison.Ordinal)
            || name.StartsWith("CallFunc_", StringComparison.Ordinal)
            || name.StartsWith("K2Node_", StringComparison.Ordinal));

    private List<GraphPin> ParameterPins(FunctionExport function)
    {
        var pins = new List<GraphPin>();
        foreach (var property in function.LoadedProperties ?? [])
        {
            var name = property.Name?.ToString() ?? "?";
            var type = PropertyTypeMapper.From(property, ResolveIndexPath);

            // **所有**属性都登记类型，不只参数：函数的局部变量（`side` / `cardID` 这类）
            // 也在属性表里，读取时从 `_localTypes` 拿到类型，就不再是 Unknown。
            _localTypes[name] = type;

            var (isReturn, _) = PropertyTypeMapper.Direction(property.PropertyFlags);
            if (isReturn) continue;
            if ((property.PropertyFlags & EPropertyFlags.CPF_Parm) == 0) continue;

            _paramNames.Add(name);
            pins.Add(new GraphPin(name, PinDirection.Output, type));
        }

        return pins;
    }

    /// <summary>
    /// 这个表达式能不能**直接产出一个引脚**（调用、成员读、结构体字段读）。
    /// 能的话 `Let X = ...` 只登记生产者，不建临时变量节点。
    /// </summary>
    private static bool IsValueExpression(KismetExpression expression) => expression switch
    {
        EX_CallMath or EX_FinalFunction or EX_LocalFinalFunction => true,
        EX_VirtualFunction or EX_LocalVirtualFunction => true,
        EX_CastBase or EX_StructMemberContext or EX_StructConst => true,
        EX_Context context => IsValueExpression(context.ContextExpression),
        _ => false,
    };

    private static bool IsCallExpression(KismetExpression expression) => expression switch
    {
        EX_CallMath or EX_FinalFunction or EX_LocalFinalFunction => true,
        EX_VirtualFunction or EX_LocalVirtualFunction => true,
        EX_Context context => IsCallExpression(context.ContextExpression),
        EX_CastBase => true,
        _ => false,
    };

    private static string VariableName(KismetExpression expression) =>
        StripGuidSuffix(expression switch
        {
            EX_LocalVariable v => v.Variable?.New?.Path is { Length: > 0 } p ? p[^1].ToString() : "?",
            EX_LocalOutVariable v => v.Variable?.New?.Path is { Length: > 0 } p ? p[^1].ToString() : "?",
            EX_InstanceVariable v => v.Variable?.New?.Path is { Length: > 0 } p ? p[^1].ToString() : "?",
            EX_DefaultVariable v => v.Variable?.New?.Path is { Length: > 0 } p ? p[^1].ToString() : "?",
            EX_StructMemberContext s => s.StructMemberExpression?.New?.Path is { Length: > 0 } p ? p[^1].ToString() : "?",
            _ => "?",
        });

    private static string VariableName(KismetPropertyPointer pointer) =>
        StripGuidSuffix(pointer.New?.Path is { Length: > 0 } path ? path[^1].ToString() : "?");

    /// <summary>
    /// 结构体字段名在 cooked 资产里带 UE 生成的 `_&lt;序号&gt;_&lt;32位GUID&gt;` 后缀
    /// （`isSalvaged_1_D83369C2...`），显示时必须去掉——否则画布上全是这种鬼名字。
    /// </summary>
    private static string StripGuidSuffix(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"_\d+_[0-9A-F]{32}$");
        return match.Success ? name[..match.Index] : name;
    }

    private uint[] BuildOffsets(KismetExpression[] instructions)
    {
        var offsets = new uint[instructions.Length];
        uint running = 0;
        for (var i = 0; i < instructions.Length; i++)
        {
            offsets[i] = running;
            running += (uint)instructions[i].GetSize(_asset);
        }

        return offsets;
    }

    private static int IndexOf(KismetExpression[] instructions, uint[] offsets, uint offset)
    {
        for (var i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] == offset) return i;
        }

        return -1;
    }

    private (string Name, string Owner) ResolveFunction(FPackageIndex? index)
    {
        if (index is null || index.Index == 0) return ("<unknown>", string.Empty);

        if (index.Index < 0)
        {
            var i = -index.Index - 1;
            if (i < _asset.Imports.Count)
            {
                var import = _asset.Imports[i];
                var owner = import.OuterIndex is { Index: < 0 } outer && -outer.Index - 1 < _asset.Imports.Count
                    ? _asset.Imports[-outer.Index - 1].ObjectName?.ToString() ?? string.Empty
                    : string.Empty;
                return (import.ObjectName?.ToString() ?? "<import>", owner);
            }
        }

        if (index.Index > 0 && index.Index - 1 < _asset.Exports.Count)
        {
            var export = _asset.Exports[index.Index - 1];
            var owner = export.OuterIndex is { Index: > 0 } outer && outer.Index - 1 < _asset.Exports.Count
                ? _asset.Exports[outer.Index - 1].ObjectName?.ToString() ?? string.Empty
                : string.Empty;
            return (export.ObjectName?.ToString() ?? "<export>", owner);
        }

        return ("<unknown>", string.Empty);
    }

    private FunctionSignature? ResolveSignature(FPackageIndex? stackNode, string name)
    {
        if (stackNode is { Index: > 0 } && stackNode.Index - 1 < _asset.Exports.Count
            && _asset.Exports[stackNode.Index - 1] is FunctionExport export)
        {
            return SignatureFromExport(export);
        }

        if (_asset.Exports.OfType<FunctionExport>().FirstOrDefault(e => e.ObjectName?.ToString() == name) is { } local)
            return SignatureFromExport(local);

        var found = _signatures?.Find(name).FirstOrDefault();
        if (found is not null) return found;

        // jmap 里缺的知名 UE API：补一张**有界**的小表，让参数名不再是 `arg0/arg1`。
        // 只收录「实测出现过、名字确定」的，不猜。
        return KnownSignatures(name);
    }

    /// <summary>
    /// jmap 缺失但确定无疑的 UE API 签名。
    ///
    /// <para>
    /// 存在理由：`Clamp` / `Round` / `GetPlayerController` 在 jmap 里查不到，
    /// 于是引脚只能显示 `arg0/arg1/arg2`（实测 Cards 有 42 个、BP_Logic 有 2 个）。
    /// 这些都是引擎里名字固定的函数，补签名是补**事实**，不是猜。
    /// </para>
    /// </summary>
    private static FunctionSignature? KnownSignatures(string name) => name switch
    {
        // `UKismetMathLibrary::Clamp(int32 Value, int32 Min, int32 Max)`
        "Clamp" => new FunctionSignature("KismetMathLibrary", name,
        [
            new SignatureParam("Value", new GraphPinType(PinType.Int), false, false),
            new SignatureParam("Min", new GraphPinType(PinType.Int), false, false),
            new SignatureParam("Max", new GraphPinType(PinType.Int), false, false),
            new SignatureParam("ReturnValue", new GraphPinType(PinType.Int), true, false),
        ]),

        // `UKismetMathLibrary::Round(float A) -> int32`
        "Round" => new FunctionSignature("KismetMathLibrary", name,
        [
            new SignatureParam("A", new GraphPinType(PinType.Double), false, false),
            new SignatureParam("ReturnValue", new GraphPinType(PinType.Int), true, false),
        ]),

        // `UGameplayStatics::GetPlayerController(WorldContext, int32 PlayerIndex)`
        "GetPlayerController" => new FunctionSignature("GameplayStatics", name,
        [
            new SignatureParam("WorldContextObject", new GraphPinType(PinType.Object), false, false),
            new SignatureParam("PlayerIndex", new GraphPinType(PinType.Int), false, false),
            new SignatureParam("ReturnValue", new GraphPinType(PinType.Object), true, false),
        ]),

        _ => null,
    };

    private FunctionSignature SignatureFromExport(FunctionExport export)
    {
        var owner = export.OuterIndex is { Index: > 0 } outer && outer.Index - 1 < _asset.Exports.Count
            ? _asset.Exports[outer.Index - 1].ObjectName?.ToString() ?? string.Empty
            : string.Empty;

        var parameters = new List<SignatureParam>();
        foreach (var property in export.LoadedProperties ?? [])
        {
            var (isReturn, isOut) = PropertyTypeMapper.Direction(property.PropertyFlags);
            parameters.Add(new SignatureParam(
                property.Name?.ToString() ?? "?",
                PropertyTypeMapper.From(property, ResolveIndexPath),
                isReturn,
                isOut));
        }

        return new FunctionSignature(owner, export.ObjectName?.ToString() ?? "?", parameters);
    }

    private string? ResolveIndexPath(FPackageIndex? index)
    {
        if (index is null || index.Index == 0) return null;

        if (index.Index < 0)
        {
            var i = -index.Index - 1;
            return i < _asset.Imports.Count ? _asset.Imports[i].ObjectName?.ToString() : null;
        }

        var e = index.Index - 1;
        return e < _asset.Exports.Count ? _asset.Exports[e].ObjectName?.ToString() : null;
    }
}





































