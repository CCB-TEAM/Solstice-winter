using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Nodes;

namespace SolsticeWinter.Decompiler;

/// <summary>
/// 把「For Each Loop / While Loop」在字节码里展开的那一串节点折回**一个** <c>MacroInstance</c>。
///
/// <para>
/// UE 编译宏时是**内联**的：`For Each Loop` 会变成「索引初始化 → LENGTH → 比较 → Branch →
/// 自增 → GET」十来条指令，而编辑器里它仍然是一个节点。不折的话图会比编辑器稀疏得多。
/// </para>
///
/// <para>
/// 判定（全部满足才折，认不出就原样保留——宁可少折，不能折错）：
/// 一个 <c>Branch</c>，条件是 <c>&lt;</c>；<c>&lt;</c> 两侧分别是 <c>LENGTH</c> 调用和 <c>Temp_</c> 整数；
/// 同一条链上有一个 <c>GET</c> 喂同一个数组；索引有赋值初始化。
/// </para>
///
/// <para>
/// **重写后的简化**：`GraphPin.Source` 现在是派生属性（连接只存 `LinkedTo`），
/// 所以旧版里到处出现的 `input.Source ?? 扫全图的 LinkedTo` 兜底全部删掉了，
/// 改连线也统一走 <see cref="Redirect"/>，不会再留下「只连了一头」的半条边。
/// </para>
/// </summary>
public static class MacroCollapse
{
    public static List<BPNode> Apply(List<BPNode> nodes)
    {
        var removed = new HashSet<BPNode>();
        var created = new List<BPNode>();

        foreach (var cluster in FindClusters(nodes))
        {
            if (cluster.Nodes.Any(removed.Contains)) continue;

            var macro = Build(cluster);
            if (macro is null) continue;

            GraphCompiler.Trace?.Invoke($"[macro] 簇 {cluster.Nodes.Count} 节点，临时变量 {string.Join(",", cluster.TempNames)}");
            created.Add(macro);
            foreach (var node in cluster.Nodes) removed.Add(node);

            // 二次清理：循环临时变量的声明/赋值节点即使没被识别进簇，也一并移除，
            // 否则会在图上留一截悬空的初始化链。
            foreach (var node in nodes)
            {
                var isTempChain = node switch
                {
                    K2Node_TemporaryVariable temp => cluster.TempNames.Contains(temp.VarPin.Name),
                    K2Node_AssignmentStatement or K2Node_VariableSet =>
                        node.Input.Any(p => IsPin(p, "Variable") && UpstreamName(p) is { } n
                                            && cluster.TempNames.Contains(n)),
                    _ => false,
                };

                if (isTempChain)
                {
                    removed.Add(node);
                    GraphCompiler.Trace?.Invoke($"[macro] 二次清理移除 {node.NodeType} {node.Name}");
                }
            }
        }

        CollapseWhileLoops(nodes, removed, created);

        // 宏的 exec 入口如果落在「临时变量初始化赋值」上，就继续往上溯，把那一截初始化链吃掉。
        foreach (var macro in created.OfType<K2Node_MacroInstance>())
            SpliceExecUpstream(macro, removed);

        if (created.Count == 0) return nodes;

        // 折叠后变成「死语句」的赋值节点和没人读的临时变量声明一并丢掉。
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in nodes)
            {
                if (removed.Contains(node)) continue;

                var deadStatement = node is K2Node_AssignmentStatement or K2Node_VariableSet
                                    && !HasExecConnection(node, removed);
                var deadTemp = node is K2Node_TemporaryVariable temp
                               && !temp.VarPin.LinkedTo.Any(t => t.ParentNode is null || !removed.Contains(t.ParentNode));

                if (deadStatement || deadTemp)
                {
                    removed.Add(node);
                    changed = true;
                }
            }
        }

        var result = nodes.Where(n => !removed.Contains(n)).ToList();
        result.AddRange(created);
        return result;
    }

    private sealed record Cluster(
        List<BPNode> Nodes,
        K2Node_IfThenElse Branch,
        K2Node_CallFunction Get,
        GraphPin IndexVar,
        GraphPin ArrayPin,
        GraphPin? ExecIn,
        HashSet<string> TempNames);

    /// <summary>
    /// 把一条连线改接到新的生产者上。**先断旧边再连新边**——`Source` 由 `LinkedTo` 派生，
    /// 只往新的一端加边会让旧的一端仍然「以为自己连着」。
    /// </summary>
    private static void Redirect(GraphPin consumer, GraphPin newProducer)
    {
        if (consumer.Source is { } old) GraphPin.Disconnect(old, consumer);
        GraphPin.Connect(newProducer, consumer);
    }

    /// <summary>
    /// 把「循环索引」追到那个 `Temp_` 变量脚。有时 `<` 左侧是 `(counter + 1)` 这种自增表达式，
    /// 这时往下追一层 `+`。只追一层——追多了会把真正的 while 循环误判成 For Each。
    /// </summary>
    private static GraphPin? TraceToTemp(GraphPin? pin)
    {
        if (pin is null) return null;
        if (pin.Name.StartsWith("Temp_", StringComparison.Ordinal)) return pin;

        if (pin.ParentNode is not K2Node_PromotableOperator { Name: "+" } plus) return null;

        foreach (var input in plus.Input.Where(p => p.Type.Category != PinType.Exec))
        {
            var upstream = input.Source;
            if (upstream?.Name.StartsWith("Temp_", StringComparison.Ordinal) == true) return upstream;
        }

        return null;
    }

    private static bool IsPin(GraphPin pin, string name) =>
        pin.Name == name || pin.FriendlyName == name;

    private static string? UpstreamName(GraphPin pin) => pin.Source?.Name;

    /// <summary>
    /// While Loop：`Loop` 节点的 execute 来自某个 `Branch` 的 `True`，那个 Branch 就是循环条件。
    /// UE 编辑器里这是**一个** `While Loop` 宏节点（Condition 在宏内部）。
    /// </summary>
    private static void CollapseWhileLoops(List<BPNode> nodes, HashSet<BPNode> removed, List<BPNode> created)
    {
        foreach (var loop in nodes.OfType<K2Node_Loop>())
        {
            if (removed.Contains(loop)) continue;

            var execIn = loop.Input.FirstOrDefault(p => IsPin(p, "execute"));
            if (execIn is null) continue;

            if (execIn.Source?.ParentNode is not K2Node_IfThenElse branch) continue;

            var truePin = branch.Output.FirstOrDefault(p => IsPin(p, "True"));
            if (truePin is null || !truePin.LinkedTo.Contains(execIn)) continue;

            var condition = branch.Input.FirstOrDefault(p => IsPin(p, "Condition"))?.Source;
            var bodyOut = loop.Output.FirstOrDefault(p => IsPin(p, "LoopBody"));
            var completed = branch.Output.FirstOrDefault(p => IsPin(p, "False"));
            var execUp = branch.Input.FirstOrDefault(p => IsPin(p, "execute"))?.Source;

            GraphCompiler.Trace?.Invoke($"[while] branch={branch.Name} cond={condition?.Name ?? "null"} execUp={execUp?.Name ?? "null"}");

            var macro = new K2Node_MacroInstance("While Loop", null, null, null, bodyOut, condition);

            if (execUp is not null) GraphPin.Connect(execUp, macro.ExecPin);

            if (completed is not null)
            {
                foreach (var consumer in completed.LinkedTo.Where(p => p.IsInput).ToList())
                {
                    if (consumer.ParentNode is not null && removed.Contains(consumer.ParentNode)) continue;
                    Redirect(consumer, macro.CompletedPin);
                }
            }

            created.Add(macro);
            removed.Add(branch);
            removed.Add(loop);
        }
    }

    /// <summary>把宏的 exec 入口沿着「临时变量赋值」往上溯，吃掉整截初始化链。</summary>
    private static void SpliceExecUpstream(K2Node_MacroInstance macro, HashSet<BPNode> removed)
    {
        var guard = 0;
        while (guard++ < 8)
        {
            var producer = macro.ExecPin.Source?.ParentNode;
            if (producer is not K2Node_AssignmentStatement and not K2Node_VariableSet) return;

            // 只吃「写编译器临时变量」的赋值；写真实变量的 SET 是正经语句，不能吃
            var written = producer.Input.FirstOrDefault(p => IsPin(p, "Variable"))?.Source?.Name;
            if (written is null || !written.StartsWith("Temp_", StringComparison.Ordinal)) return;

            var assignExec = producer.Input.FirstOrDefault(p => IsPin(p, "execute"))?.Source;
            if (assignExec is null) return;

            GraphPin.Connect(assignExec, macro.ExecPin);
            removed.Add(producer);
        }
    }

    /// <summary>节点的 exec 引脚是否连着**还活着**的东西。</summary>
    private static bool HasExecConnection(BPNode node, HashSet<BPNode> removed)
    {
        bool Live(GraphPin pin) =>
            pin.LinkedTo.Any(t => t.ParentNode is null || !removed.Contains(t.ParentNode));

        return node.Input.Any(p => p.Type.Category == PinType.Exec && Live(p))
               || node.Output.Any(p => p.Type.Category == PinType.Exec && Live(p));
    }

    private static IEnumerable<Cluster> FindClusters(List<BPNode> nodes)
    {
        foreach (var branch in nodes.OfType<K2Node_IfThenElse>())
        {
            var condition = branch.Input.FirstOrDefault(p => IsPin(p, "Condition"));
            if (condition?.Source?.ParentNode is not K2Node_PromotableOperator { Name: "<" } less)
            {
                GraphCompiler.Trace?.Invoke($"[for] 跳过：条件不是 '<'（实际 {condition?.Source?.ParentNode?.Name ?? "无上游"}）");
                continue;
            }

            var operands = less.Input.Where(p => p.Type.Category != PinType.Exec).Take(2).ToList();
            if (operands.Count < 2)
            {
                GraphCompiler.Trace?.Invoke($"[for] 跳过：'<' 操作数不足（{operands.Count}）");
                continue;
            }

            var lengthOperand = operands.FirstOrDefault(p =>
                p.Source?.ParentNode is K2Node_CallFunction { Name: "LENGTH" });
            var indexOperand = operands.FirstOrDefault(p => !ReferenceEquals(p, lengthOperand));
            if (lengthOperand?.Source?.ParentNode is not K2Node_CallFunction length)
            {
                GraphCompiler.Trace?.Invoke(
                    $"[for] 跳过：没有 LENGTH 操作数（A={operands[0].Source?.ParentNode?.Name}，" +
                    $"B={operands[1].Source?.ParentNode?.Name}）");
                continue;
            }

            var indexVar = TraceToTemp(indexOperand?.Source);
            if (indexVar is null)
            {
                GraphCompiler.Trace?.Invoke(
                    $"[for] 跳过：索引追不到 Temp_（index source = {indexOperand?.Source?.Name ?? "null"}" +
                    $" / 节点 {indexOperand?.Source?.ParentNode?.Name ?? "null"}）");
                continue;
            }

            var arrayPin = length.Input.FirstOrDefault(p => IsPin(p, "TargetArray"))?.Source;
            if (arrayPin is null)
            {
                GraphCompiler.Trace?.Invoke("[for] 跳过：LENGTH 的 TargetArray 没有上游");
                continue;
            }

            // GET 只按「同一个数组」匹配：`<` 那一侧是**循环计数器**，GET 的 Index 可能是
            // **另一个**索引变量（计数器和数组索引不同名），要求同名会漏掉一大批。
            //
            // 匹配用「同一引脚**或**同名」：`GraphCompiler` 重构后，LENGTH 和 GET 的
            // TargetArray 可能来自两个不同的引脚实例（同一个临时变量被读了两次），
            // 只比引用会全部落空——实测 `For Each Loop` 折叠数掉到 **0**。
            var get = nodes.OfType<K2Node_CallFunction>().FirstOrDefault(call =>
                call.Name == "GET"
                && call.Input.Any(p => IsPin(p, "TargetArray")
                                       && (ReferenceEquals(p.Source, arrayPin)
                                           || (p.Source is { } source
                                               && arrayPin.Name.Length > 0
                                               && source.Name == arrayPin.Name))));
            if (get is null)
            {
                // 失败时把所有 GET 的数组来源打出来，才能看出到底差在哪
                var allGets = nodes.OfType<K2Node_CallFunction>()
                    .Where(call => call.Name is "GET" or "Array_Get")
                    .Select(call => call.Input.FirstOrDefault(p => IsPin(p, "TargetArray"))?.Source?.Name ?? "null")
                    .ToList();

                GraphCompiler.Trace?.Invoke(
                    $"[for] 跳过：找不到同一个数组的 GET（LENGTH 数组 = '{arrayPin.Name}'，" +
                    $"现存 GET/Array_Get 的数组 = [{string.Join(", ", allGets)}]）");
                continue;
            }

            var arrayIndexVar = get.Input.FirstOrDefault(p => IsPin(p, "Index"))?.Source ?? indexVar;

            var tempNames = new HashSet<string>(StringComparer.Ordinal) { indexVar.Name };
            if (arrayIndexVar.Name.StartsWith("Temp_", StringComparison.Ordinal)) tempNames.Add(arrayIndexVar.Name);
            foreach (var operand in operands)
            {
                if (UpstreamName(operand) is { } name && name.StartsWith("Temp_", StringComparison.Ordinal))
                    tempNames.Add(name);
            }

            var members = new List<BPNode> { branch, length, less, get };

            foreach (var node in nodes)
            {
                var participates = node switch
                {
                    K2Node_AssignmentStatement or K2Node_VariableSet =>
                        node.Input.Any(p => IsPin(p, "Variable") && UpstreamName(p) is { } n && tempNames.Contains(n)),
                    K2Node_TemporaryVariable temp => tempNames.Contains(temp.VarPin.Name),
                    K2Node_PromotableOperator plus => plus.Name == "+"
                        && plus.Input.Any(p => UpstreamName(p) is { } n && tempNames.Contains(n)),
                    _ => false,
                };

                if (participates) members.Add(node);
            }

            foreach (var sequence in nodes.OfType<K2Node_ExecutionSequence>())
            {
                if (sequence.Output.Any(p => p.LinkedTo.Any(t => t.ParentNode is not null && members.Contains(t.ParentNode))))
                    members.Add(sequence);
            }

            var distinct = members.Distinct().ToList();

            var execIn = distinct
                .SelectMany(n => n.Input.Where(p => IsPin(p, "execute")))
                .Select(p => p.Source)
                .FirstOrDefault(source => source is not null && source.ParentNode is not null && !distinct.Contains(source.ParentNode));

            yield return new Cluster(distinct, branch, get, arrayIndexVar, arrayPin, execIn, tempNames);
        }
    }

    /// <summary>建宏节点，并把簇的对外连接改接到它身上。</summary>
    private static K2Node_MacroInstance? Build(Cluster cluster)
    {
        var element = cluster.Get.Input.FirstOrDefault(p => IsPin(p, "Item"))?.Source;
        var bodyOut = cluster.Get.Output.FirstOrDefault(p => IsPin(p, "then"));
        var completed = cluster.Branch.Output.FirstOrDefault(p => IsPin(p, "True"));

        var macro = new K2Node_MacroInstance("For Each Loop", cluster.ArrayPin, cluster.IndexVar, element, bodyOut);

        if (cluster.ExecIn is { } execIn) GraphPin.Connect(execIn, macro.ExecPin);

        // 循环退出（Branch 的 True 分支）→ Completed
        if (completed is not null)
        {
            foreach (var consumer in completed.LinkedTo.Where(p => p.IsInput).ToList())
            {
                if (consumer.ParentNode is not null && cluster.Nodes.Contains(consumer.ParentNode)) continue;
                Redirect(consumer, macro.CompletedPin);
            }
        }

        // 数组元素：GET 的 Item 的消费者 → Array Element
        if (element is not null && macro.ElementPin is not null)
        {
            foreach (var consumer in element.LinkedTo.Where(p => p.IsInput).ToList())
            {
                if (consumer.ParentNode is not null && cluster.Nodes.Contains(consumer.ParentNode)) continue;
                Redirect(consumer, macro.ElementPin);
            }
        }

        return macro;
    }
}
