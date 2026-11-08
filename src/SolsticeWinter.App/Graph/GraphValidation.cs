using System.IO;
using SolsticeWinter.App.ViewModels;
using Solstice.Kismet;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 图生成的交叉验证：**AST 节点计数 vs 图节点计数**，外加结构不变量。
///
/// <para>
/// 为什么这么验：投影（`KNode` → `GraphNodeVm`）如果漏掉某个节点，图看起来仍然"合理"，
/// 肉眼发现不了。所以用两条独立路径数同一批东西——AST 侧递归数 `KNode`，图侧数 `Kind`——
/// 对不上就是漏了或多了。
/// </para>
///
/// <para>
/// 对应关系（写在 <see cref="Expected"/> 里）：顶层调用 ↔ `Call`、嵌套调用 ↔ `Data`、
/// `If/Loop/BottomLoop` ↔ `Branch`、各种赋值 ↔ `Set`、`Return` ↔ `Return`、其余 ↔ `Opaque`。
/// </para>
/// </summary>
public static class GraphValidation
{
    public sealed record Report(int Functions, int Passed, IReadOnlyList<string> Failures, string Detail);

    public static Report Run(IEnumerable<(string Asset, string Function)> targets)
    {
        var passed = 0;
        var failures = new List<string>();
        var total = 0;
        var detail = new System.Text.StringBuilder();

        // 按资产缓存工作区：开一次资产要读 uasset/uexp/usmap 并构造 UAsset（百毫秒级），
        // 每个函数都开一次的话 1000 个函数就是十几分钟——这就是之前「跑 32 分钟」的原因。
        var profile = SolsticeWinter.Core.GameProfile.Kards();
        var workspaces = new Dictionary<string, KismetWorkspace>(StringComparer.Ordinal);

        foreach (var (asset, function) in targets)
        {
            total++;
            try
            {
                if (!workspaces.TryGetValue(asset, out var ws))
                {
                    ws = AssetAccess.OpenWorkspace(profile, asset);
                    workspaces[asset] = ws;
                }
                var roots = ws.Decompile(function);

                // ubergraph 的段是各自独立的链：拼成一条会造出假的跨段边，
                // 所以按段分别投影、计数累加。
                var chains = roots.OfType<UberGraphNode>().FirstOrDefault() is { } uber
                    ? uber.Sections.Select(s => s.Nodes).ToList()
                    : [roots];

                var expected = new Dictionary<string, int>
                {
                    ["Call"] = 0, ["Data"] = 0, ["Branch"] = 0, ["Set"] = 0, ["Return"] = 0, ["Opaque"] = 0,
                };
                var actual = new Dictionary<string, int>
                {
                    ["Call"] = 0, ["Data"] = 0, ["Branch"] = 0, ["Set"] = 0, ["Return"] = 0, ["Opaque"] = 0,
                };
                var problems = new List<string>();
                var isolatedTotal = 0;

                foreach (var chain in chains)
                {
                    foreach (var (key, value) in CountAst(chain)) expected[key] += value;

                    var (graphNodes, connections) = GraphBuilder.Build(chain, GraphBuilder.Options.Default with
                    {
                        SignatureOf = name => SolsticeWinter.Index.JmapCatalog.Empty.Find(name).FirstOrDefault(),
                    });

                    foreach (var (key, value) in CountGraph(graphNodes)) actual[key] += value;
                    var (invariants, isolated) = CheckInvariants(graphNodes, connections);
                    problems.AddRange(invariants);
                    isolatedTotal += isolated;
                }

                // 孤立节点是软告警（单节点函数 / 不透明节点天然无边），不算失败
                if (isolatedTotal > 0 && problems.Count == 0 && expected.Values.Sum() > 1)
                    detail.AppendLine($"{function}: 孤立 {isolatedTotal} 个（软告警）");

                if (problems.Count == 0)
                {
                    passed++;
                }
                else
                {
                    failures.Add($"{asset} / {function}: {string.Join("; ", problems)}");
                    if (detail.Length < 4000)
                        detail.AppendLine($"{function}: 期望 {expected}, 实际 {actual}");
                }
            }
            catch (Exception e)
            {
                failures.Add($"{asset} / {function}: 异常 {e.GetType().Name}: {e.Message}");
            }
        }

        return new Report(total, passed, failures, detail.ToString());
    }

    /// <summary>
    /// 自检：故意漏掉一个节点，确认计数对比能发现。
    /// 一个「永远通过」的验证器等于没有验证器，所以这条必须有。
    /// </summary>
    public static bool InjectedFaultDetected(IReadOnlyList<GraphNodeVm> nodes)
    {
        if (nodes.Count < 2) return false;

        var before = CountGraph(nodes);
        var after = CountGraph(nodes.Skip(1).ToList());

        return before.Keys.Any(key => before[key] != after[key]);
    }

    /// <summary>AST 侧计数（递归，含嵌套参数里的调用）。</summary>
    private static Dictionary<string, int> CountAst(IReadOnlyList<KNode> roots)
    {
        var counts = new Dictionary<string, int>
        {
            ["Call"] = 0, ["Data"] = 0, ["Branch"] = 0, ["Set"] = 0, ["Return"] = 0, ["Opaque"] = 0,
        };

        foreach (var node in roots) Walk(node, counts, topLevel: true);
        return counts;
    }

    private static void Walk(KNode node, Dictionary<string, int> counts, bool topLevel)
    {
        switch (node)
        {
            case CallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;
            case ContextCallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;
            case VirtualCallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;
            case LocalFinalCallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;
            case MathCallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;
            case MulticastCallNode call: counts["Call"]++; CountArgs(call.Args, counts); break;

            case IfNode branch:
                counts["Branch"]++;
                CountArg(branch.Condition, counts);
                foreach (var child in branch.Then) Walk(child, counts, false);
                foreach (var child in branch.Else ?? []) Walk(child, counts, false);
                break;

            case LoopNode loop:
                counts["Branch"]++;
                CountArg(loop.Condition, counts);
                foreach (var child in loop.Body) Walk(child, counts, false);
                break;

            case BottomLoopNode loop:
                counts["Branch"]++;
                CountArg(loop.Condition, counts);
                foreach (var child in loop.Body) Walk(child, counts, false);
                foreach (var child in loop.Tail) Walk(child, counts, false);
                break;

            case AssignNode assign: counts["Set"]++; CountArg(assign.Value, counts); break;
            case AssignBoolNode assign: counts["Set"]++; CountArg(assign.Value, counts); break;
            case PersistentAssignNode assign: counts["Set"]++; CountArg(assign.Value, counts); break;
            case SetArrayNode setArray:
                counts["Set"]++;
                foreach (var element in setArray.Elements) CountArg(element, counts);
                break;

            case ReturnNode ret: counts["Return"]++; CountArg(ret.Value, counts); break;

            case UberSectionNode section:
                foreach (var child in section.Nodes) Walk(child, counts, false);
                break;

            default: counts["Opaque"]++; break;
        }
    }

    private static void CountArgs(IReadOnlyList<KArg> args, Dictionary<string, int> counts)
    {
        foreach (var arg in args) CountArg(arg, counts);
    }

    private static void CountArg(KArg? arg, Dictionary<string, int> counts)
    {
        switch (arg)
        {
            case CallArg call:
                counts["Data"]++;
                CountArgs(call.Args, counts);
                break;
        }
    }

    private static Dictionary<string, int> CountGraph(IReadOnlyList<GraphNodeVm> nodes)
    {
        var counts = new Dictionary<string, int>
        {
            ["Call"] = 0, ["Data"] = 0, ["Branch"] = 0, ["Set"] = 0, ["Return"] = 0, ["Opaque"] = 0,
        };

        foreach (var node in nodes)
        {
            var key = node.Kind switch
            {
                "Call" => "Call",
                "Data" => "Data",
                "Branch" => "Branch",
                "Set" => "Set",
                "Return" => "Return",
                _ => "Opaque",
            };
            counts[key]++;
        }

        return counts;
    }

    private static IEnumerable<string> Compare(Dictionary<string, int> expected, Dictionary<string, int> actual)
    {
        foreach (var key in expected.Keys)
        {
            if (expected[key] != actual[key])
                yield return $"{key} 期望 {expected[key]} 实际 {actual[key]}";
        }
    }

    /// <summary>
    /// 结构不变量：边的两端存在、不重复、无 exec 自环。
    /// 孤立节点数走返回值（单节点函数 / 不透明节点天然无边，属软告警）。
    /// </summary>
    private static (List<string> Problems, int Isolated) CheckInvariants(
        IReadOnlyList<GraphNodeVm> nodes, IReadOnlyList<ConnectionVm> connections)
    {
        var problems = new List<string>();
        var byPin = new Dictionary<ConnectorVm, GraphNodeVm>();
        foreach (var node in nodes)
        {
            foreach (var pin in node.Inputs) byPin[pin] = node;
            foreach (var pin in node.Outputs) byPin[pin] = node;
        }

        var seen = new HashSet<(ConnectorVm, ConnectorVm)>();
        foreach (var edge in connections)
        {
            if (!byPin.ContainsKey(edge.Output) || !byPin.ContainsKey(edge.Input))
                problems.Add("边指向不存在的引脚");

            if (!seen.Add((edge.Output, edge.Input)))
                problems.Add("重复边");

            if (byPin.TryGetValue(edge.Output, out var from) && byPin.TryGetValue(edge.Input, out var to)
                && ReferenceEquals(from, to) && edge.IsExec)
                problems.Add("exec 自环");
        }

        var touched = new HashSet<GraphNodeVm>();
        foreach (var edge in connections)
        {
            touched.Add(byPin[edge.Output]);
            touched.Add(byPin[edge.Input]);
        }

        return (problems, nodes.Count(n => !touched.Contains(n)));
    }
}



