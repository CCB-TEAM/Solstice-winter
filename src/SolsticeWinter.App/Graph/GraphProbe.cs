using System.IO;
using SolsticeWinter.Core;
using Solstice.Kismet;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 无窗口自检：打开一个资产的一个函数，投影成图，把结果写到
/// <c>%TEMP%\solstice-winter-ui\graph.txt</c>（GUI 程序没有控制台，stdout 看不到）。
///
/// <para>
/// 用途：UI 有没有渲染错我这边看不见（模型不支持读图），
/// 所以图的**数据形状**必须能在命令行上验：节点数、连线数、每个节点的种类与坐标。
/// </para>
/// </summary>
public static class GraphProbe
{
    /// <summary>切片诊断：<c>--slice &lt;资产&gt; &lt;入口偏移&gt; &lt;段尾偏移|end&gt;</c>。</summary>
    public static int SliceReport(string virtualPath, string entryText, string endText)
    {
        var path = Path.Combine(Path.GetTempPath(), "solstice-winter-ui", "graph.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            var uber = ws.Functions.First(f => f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal));
            var entry = int.Parse(entryText);
            var end = endText.Equals("end", StringComparison.OrdinalIgnoreCase) ? int.MaxValue : int.Parse(endText);
            log.AppendLine(SectionSlicer.Debug(ws, uber, entry, end));
            return 0;
        }
        catch (Exception e)
        {
            log.AppendLine($"失败：{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, log.ToString());
        }
    }

    /// <summary>
    /// 交叉验证：<c>--validate &lt;资产&gt; [资产…]</c>。
    /// 对每个资产的全部带字节码函数做「AST 计数 vs 图计数」对账 + 结构不变量。
    /// </summary>
    public static int ValidateReport(IReadOnlyList<string> assets)
    {
        var path = Path.Combine(Path.GetTempPath(), "solstice-winter-ui", "graph.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var profile = GameProfile.Kards();
            var targets = new List<(string Asset, string Function)>();

            foreach (var asset in assets)
            {
                var ws = AssetAccess.OpenWorkspace(profile, asset);
                foreach (var fn in ws.Functions) targets.Add((asset, fn));
            }

            log.AppendLine($"目标：{assets.Count} 个资产 / {targets.Count} 个函数");
            var report = GraphValidation.Run(targets);

            log.AppendLine($"通过 {report.Passed} / {report.Functions}");

            // 注入故障自检：拿第一个资产的第一段图，漏掉一个节点看能不能被发现
            var first = AssetAccess.OpenWorkspace(profile, assets[0]);
            var probeRoots = first.Decompile(first.Functions.First(f => f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)));
            if (probeRoots.OfType<UberGraphNode>().FirstOrDefault() is { } probeUber)
            {
                var (probeNodes, _) = GraphBuilder.Build(probeUber.Sections[0].Nodes, GraphBuilder.Options.Default);
                log.AppendLine($"自检（注入故障）：{(GraphValidation.InjectedFaultDetected(probeNodes) ? "能被发现 ✅" : "发现不了 ❌")}" +
                               $"（探针图 {probeNodes.Count} 节点）");
            }
            if (report.Failures.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("失败明细（前 40 条）：");
                foreach (var failure in report.Failures.Take(40)) log.AppendLine("  " + failure);
                if (report.Detail.Length > 0)
                {
                    log.AppendLine();
                    log.AppendLine("计数明细：");
                    log.Append(report.Detail);
                }
            }

            return report.Failures.Count == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            log.AppendLine($"失败：{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, log.ToString());
        }
    }

    public static int Run(string virtualPath, string functionName, int limit = 40)
    {
        var log = new System.Text.StringBuilder();
        var path = Path.Combine(Path.GetTempPath(), "solstice-winter-ui", "graph.txt");

        try
        {
            var profile = GameProfile.Kards();

            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            var top = ws.Decompile(functionName);

            IReadOnlyList<KNode> roots = top;
            var note = string.Empty;
            if (top.OfType<UberGraphNode>().FirstOrDefault() is { } uber)
            {
                var section = uber.Sections.FirstOrDefault(s => !string.IsNullOrEmpty(s.Label)) ?? uber.Sections.OrderByDescending(s => s.Nodes.Count).FirstOrDefault();
                roots = section?.Nodes ?? [];
                note = $"ubergraph {uber.Sections.Count} 段，取「{section?.Label ?? "最大段"}」";
            }

            // 自检也带上 jmap 签名，否则看不到「参数名是否接对」
            SolsticeWinter.Index.JmapCatalog? catalog = null;
            var jmap = @"D:\files\projects\jmap-out\functions.json";
            if (File.Exists(jmap)) catalog = SolsticeWinter.Index.JmapCatalog.Load(jmap);

            var options = GraphBuilder.Options.Default with
            {
                SignatureOf = name => catalog?.Find(name).FirstOrDefault(),
            };
            var (nodes, connections) = GraphBuilder.Build(roots, options);

            log.AppendLine($"资产     : {virtualPath}");
            log.AppendLine($"函数     : {functionName}  表达式 {ws.ExpressionCount(functionName)} 条");
            if (note.Length > 0) log.AppendLine($"说明     : {note}");
            log.AppendLine($"图       : {nodes.Count} 节点 / {connections.Count} 连线");
            log.AppendLine();

            foreach (var node in nodes.Take(limit))
            {
                var ins = string.Join(",", node.Inputs.Select(p => $"{p.Title}:{p.TypeName}"));
                var outs = string.Join(",", node.Outputs.Select(p => $"{p.Title}:{p.TypeName}"));
                log.AppendLine($"  ({node.Location.X,6:0},{node.Location.Y,6:0})  [{node.Kind,-6}] {node.Title,-32} in=[{ins}] out=[{outs}]");
            }

            if (nodes.Count > limit) log.AppendLine($"  … 其余 {nodes.Count - limit} 个节点省略");

            log.AppendLine();
            log.AppendLine("连线：");
            foreach (var edge in connections.Take(limit))
            {
                var from = nodes.FirstOrDefault(n => n.Outputs.Contains(edge.Output));
                var to = nodes.FirstOrDefault(n => n.Inputs.Contains(edge.Input));
                log.AppendLine($"  {from?.Title ?? "?"} .{edge.Output.Title} -> {to?.Title ?? "?"} .{edge.Input.Title}" +
                               (edge.IsExec ? "  [exec]" : string.Empty));
            }

            if (connections.Count > limit) log.AppendLine($"  … 其余 {connections.Count - limit} 条连线省略");
            return 0;
        }
        catch (Exception e)
        {
            log.AppendLine($"失败：{e.GetType().Name}: {e.Message}");
            log.AppendLine(e.StackTrace);
            return 1;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, log.ToString());
            }
            catch
            {
                // 写不进去也没别的办法了
            }
        }
    }
}






