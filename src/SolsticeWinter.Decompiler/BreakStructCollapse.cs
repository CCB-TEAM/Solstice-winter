using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Nodes;

namespace SolsticeWinter.Decompiler;

/// <summary>
/// 把「同一个结构体上的一堆字段读」折成**一个** <c>BreakStruct</c> 节点。
///
/// <para>
/// UE 里读结构体字段是一个 `Break CampaignCardStats` 节点、每字段一个输出脚；
/// 字节码里却是每个字段一条 <c>EX_Context</c> 读指令。不折的话一个函数里会散出十几个
/// 只带 `Target` 脚的小 Get 节点（实测 `AddCardStats` 有 12 个）。
/// </para>
///
/// <para>
/// 判定：多个 <c>K2Node_VariableGet</c>，它们的 `Target` 输入来自**同一个引脚**，且该引脚类型是结构体。
/// 折不出来就原样保留——宁可少折，不能折错。
/// </para>
///
/// <para>
/// 重写后（GraphPin 单一真相）这里只需要 `GraphPin.Disconnect` + `GraphPin.Connect`，
/// 不会再出现「删了节点但连线还挂着」的悬空引用——调用方在折叠后还有一轮不动点清理兜底。
/// </para>
/// </summary>
public static class BreakStructCollapse
{
    /// <summary>少于这个数量的字段读就不折（两个 Get 折成一个 Break 反而更乱）。</summary>
    private const int MinimumFields = 3;

    public static List<BPNode> Apply(List<BPNode> nodes, Func<string, IReadOnlyList<(string Name, GraphPinType Type)>?>? fieldsOf)
    {
        var removed = new HashSet<BPNode>();
        var created = new List<BPNode>();

        // 按「Target 来源引脚」分组
        var groups = new Dictionary<GraphPin, List<K2Node_VariableGet>>(ReferenceEqualityComparer.Instance);

        foreach (var get in nodes.OfType<K2Node_VariableGet>())
        {
            var target = get.Input.FirstOrDefault(p => p.Name == "Target" || p.FriendlyName == "Target");
            var source = target?.Source;
            if (source is null) continue;

            if (!groups.TryGetValue(source, out var list)) groups[source] = list = [];
            list.Add(get);
        }

        foreach (var (source, gets) in groups)
        {
            if (gets.Count < MinimumFields) continue;
            if (gets.Any(removed.Contains)) continue;

            var structName = source.Type.SubCategoryObject;
            if (string.IsNullOrEmpty(structName)) continue;

            var shortName = structName[(structName.LastIndexOf('.') + 1)..];

            // 字段顺序：优先用 usmap / 索引给的定义顺序，没有就按出现顺序
            var declared = fieldsOf?.Invoke(shortName);
            var fieldNames = declared is { Count: > 0 }
                ? declared.Select(f => f.Name).ToList()
                : gets.Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();

            var node = new K2Node_BreakStruct(shortName, source, fieldNames, null);

            foreach (var get in gets)
            {
                var output = node.OutputFor(get.Name);
                if (output is null) continue;

                foreach (var consumer in get.VarPin.LinkedTo.Where(p => p.IsInput).ToList())
                {
                    GraphPin.Disconnect(get.VarPin, consumer);
                    GraphPin.Connect(output, consumer);
                }

                // 删节点前必须断开它的**输入**：`Target` 脚仍挂在上游的 `LinkedTo` 里，
                // 只把它从列表里拿掉会留下「指向不存在节点」的悬空引用（实测 124 处）。
                foreach (var input in get.Input)
                {
                    foreach (var producer in input.LinkedTo.ToList())
                    {
                        GraphPin.Disconnect(producer, input);
                    }
                }

                removed.Add(get);
            }

            created.Add(node);
        }

        if (created.Count == 0) return nodes;

        var result = nodes.Where(n => !removed.Contains(n)).ToList();
        result.AddRange(created);
        return result;
    }
}
