using System.IO;
using System.Reflection;
using SolsticeWinter.Core;
using SolsticeWinter.Decompiler.Infrastructure;
using Solstice.Kismet;
using UAssetAPI;
using UAssetAPI.ExportTypes;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 移植验证：把函数的字节码切成基本块 / 跳转 / 序列，打印统计。
///
/// <para>
/// 用途：走查器（<c>FunctionDecompiler</c>）还没移植完，但「字节码 → 控制流骨架」这一层
/// 已经可以独立验证——块数、跳转数、序列数、入口点，都能和
/// <c>KismetWorkspace.UbergraphCallSites</c> 的入口表对上。
/// </para>
/// </summary>
public static class BlockProbe
{
    private static readonly FieldInfo? AssetField =
        typeof(KismetWorkspace).GetField("_asset", BindingFlags.NonPublic | BindingFlags.Instance);

    public static int Run(string virtualPath, string functionName)
    {
        var path = ProbePath("graph.txt");
        var log = new System.Text.StringBuilder();

        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            if (AssetField?.GetValue(ws) is not UAsset asset) return Fail(log, "反射拿不到 _asset", path);

            var function = asset.Exports.OfType<FunctionExport>()
                .FirstOrDefault(e => e.ObjectName?.ToString() == functionName);
            if (function?.ScriptBytecode is not { Length: > 0 } bytecode)
                return Fail(log, $"没有函数 {functionName} 的字节码", path);

            var offsets = StatementMap.Build(bytecode, asset);
            var total = StatementMap.Length(bytecode, asset);

            var flow = new ExecutionFlow();
            if (functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
                flow.EntryPoints.AddRange(ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset));

            flow.DecompileFlow(bytecode, offsets);

            var types = flow.Blocks.GroupBy(b => b.Type).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}");

            log.AppendLine($"资产     : {virtualPath}");
            log.AppendLine($"函数     : {functionName}");
            log.AppendLine($"字节码   : {bytecode.Length} 条指令 / {total} 字节");
            log.AppendLine($"基本块   : {flow.Blocks.Count}");
            log.AppendLine($"块类型   : {string.Join(", ", types)}");
            log.AppendLine($"跳转     : {flow.Blocks.Sum(b => b.Jumps.Count)}");
            log.AppendLine($"序列     : {flow.Blocks.Sum(b => b.Sequences.Count)}");
            log.AppendLine($"入口点   : {flow.EntryPoints.Count}");

            log.AppendLine();
            log.AppendLine("前 8 个块：");
            foreach (var block in flow.Blocks.Take(8))
            {
                log.AppendLine($"  {block.Start,6}..{block.End,-6} {block.Type,-14} " +
                               $"指令 {block.Instructions.Count,3}  跳转 {block.Jumps.Count}  序列 {block.Sequences.Count}");
            }

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
            Write(path, log);
        }
    }

    /// <summary>直接跑走查器：<c>--walk &lt;资产&gt; &lt;函数&gt; [入口偏移]</c>。</summary>
    public static int Walk(string virtualPath, string functionName, string? offsetText)
    {
        var path = ProbePath("graph.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(log, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(log, $"没有函数 {functionName}", path);

            var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                : new List<uint>();
            uint? start = uint.TryParse(offsetText, out var parsed) ? parsed : null;

            var result = SolsticeWinter.Decompiler.GraphCompiler.Build(asset, function, entries, start, 4000);
            log.AppendLine($"函数 {functionName}  入口偏移 {(start?.ToString() ?? "无")}");
            log.AppendLine($"节点 {result.Nodes.Count}  循环节点 {result.Nodes.Count(n => n is SolsticeWinter.Decompiler.Nodes.K2Node_Loop)}");
            log.AppendLine($"问题 {result.Problems.Count}");
            foreach (var problem in result.Problems.Take(10)) log.AppendLine("  " + problem);
            log.AppendLine();
            foreach (var node in result.Nodes.Take(15))
                log.AppendLine($"  {node.NodeType,-24} {node.Name,-28} in={node.Input.Count} out={node.Output.Count}");

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
            Write(path, log);
        }
    }

    /// <summary>
    /// 全量诊断：<c>--audit &lt;资产&gt;</c>。对每个函数跑走查器，按**问题类别**统计，
    /// 输出可直接读的报告到 <c>.cache\probe\audit.md</c>。
    /// <summary>
    /// 扫全资产的「宏模式」：For Each Loop 的字节码特征（LENGTH + GET + 比较 + 回边）。
    /// 用来判断宏还原值不值得做，而不是先做完再找证据。
    /// <summary>
    /// 统计「挂在函数入口节点上的边」按名字分类——用来判断入口脚是冗余还是承重。
    /// </summary>
    /// <summary>
    /// 统计未命名引脚（`argN`）出在哪些节点上——用来定位「哪些节点看着不对」。
    /// </summary>
    public static int ArgNames(string virtualPath)
    {
        var path = ProbePath("argn.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();
            var functionNames = ws.Functions.ToList();

            var byNodeType = new Dictionary<string, int>(StringComparer.Ordinal);
            var byFunction = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new List<string>();

            foreach (var functionName in functionNames)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : new List<uint>();
                uint? start = entries.Count > 0 ? entries[0] : null;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                    asset, function, entries, start, 4000, signatures, variableTypes);

                foreach (var node in result.Nodes)
                {
                    var argPins = node.Input.Concat(node.Output)
                        .Count(p => p.Name.Length > 3 && p.Name.StartsWith("arg", StringComparison.Ordinal)
                                    && char.IsDigit(p.Name[3]));
                    if (argPins == 0) continue;

                    byNodeType[node.NodeType] = byNodeType.GetValueOrDefault(node.NodeType) + argPins;
                    byFunction[functionName] = byFunction.GetValueOrDefault(functionName) + argPins;
                    if (samples.Count < 12) samples.Add($"{functionName} → {node.NodeType}「{node.Name}」{argPins} 个");
                }
            }

            report.AppendLine($"# {virtualPath} 未命名引脚（argN）分布");
            report.AppendLine();
            report.AppendLine("按节点类型：");
            foreach (var pair in byNodeType.OrderByDescending(p => p.Value))
                report.AppendLine($"- `{pair.Key}`：**{pair.Value}**");
            report.AppendLine();
            report.AppendLine("按函数（前 10）：");
            foreach (var pair in byFunction.OrderByDescending(p => p.Value).Take(10))
                report.AppendLine($"- `{pair.Key}`：{pair.Value}");
            report.AppendLine();
            report.AppendLine("样例：");
            foreach (var sample in samples) report.AppendLine($"- {sample}");
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>
    /// **图一致性校验**：不看节点数、不看孤立数，只看「图本身是不是自洽 / 像不像 UE 编辑器」。
    ///
    /// <para>
    /// 规则：
    /// 1. 函数入口只应有真参数（不能有 `Temp_*` / `CallFunc_*` / `K2Node_*` 编译器临时变量脚）
    /// 2. 纯节点（`Pure`）不能有 exec 脚
    /// 3. 每条连线的两端节点都必须存在于图里（悬空边）
    /// 4. 除入口外，有 exec 入脚的节点必须真的连上了 exec（执行链断点）
    /// 5. 节点不能一个引脚都没有
/// 6. 字节码里有 `Return` 的函数，图里必须有 Return 节点（执行链没被截断）
/// 7. **数据**输入脚不能接多个上游（exec 脚可以，分支汇合是常态）
/// 8. 连线两端类型必须兼容（数值族 / 对象族 / 枚举↔字节 之间互通，Unknown 放过）
    /// </para>
    /// </summary>
    public static int Validate(string virtualPath)
    {
        var path = ProbePath("validate.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();

            var rules = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var functions = 0;

            void Violate(string rule, string detail)
            {
                rules[rule] = rules.GetValueOrDefault(rule) + 1;
                if (!samples.TryGetValue(rule, out var list)) samples[rule] = list = [];
                if (list.Count < 6) list.Add(detail);
            }

            foreach (var functionName in ws.Functions)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : new List<uint>();
                uint? start = entries.Count > 0 ? entries[0] : null;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                    asset, function, entries, start, 4000, signatures, variableTypes);
                if (result.Nodes.Count == 0) continue;

                functions++;
                var set = new HashSet<SolsticeWinter.Decompiler.Model.BPNode>(result.Nodes);

                foreach (var entry in result.Nodes.OfType<SolsticeWinter.Decompiler.Nodes.K2Node_FunctionEntry>())
                {
                    foreach (var pin in entry.Output.Where(p => !p.IsHidden))
                    {
                        if (pin.Name.StartsWith("Temp_", StringComparison.Ordinal)
                            || pin.Name.StartsWith("CallFunc_", StringComparison.Ordinal)
                            || pin.Name.StartsWith("K2Node_", StringComparison.Ordinal))
                        {
                            Violate("入口挂着编译器临时变量脚", $"{functionName} → `{pin.Name}`");
                        }
                    }
                }

                foreach (var node in result.Nodes)
                {
                    if (node.Pure)
                    {
                        var execPins = node.Input.Concat(node.Output)
                            .Count(p => p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec && !p.IsHidden);
                        if (execPins > 0)
                            Violate("纯节点带 exec 脚", $"{functionName} → 「{node.Name}」{execPins} 个");
                    }

                    foreach (var pin in node.Output)
                    {
                        foreach (var consumer in pin.LinkedTo)
                        {
                            if (consumer.ParentNode is null || !set.Contains(consumer.ParentNode))
                                Violate("连线指向不存在的节点", $"{functionName} → 「{node.Name}」.{pin.Name}");
                        }
                    }

                    if (node.Input.Count == 0 && node.Output.Count == 0)
                        Violate("零引脚节点", $"{functionName} → 「{node.Name}」");

                    if (node is SolsticeWinter.Decompiler.Nodes.K2Node_FunctionEntry) continue;

                    var execIn = node.Input.FirstOrDefault(p =>
                        p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec && !p.IsNameHidden);
                    if (execIn is not null && execIn.LinkedTo.Count == 0 && node.Input.Count > 1)
                        Violate("执行链断点（exec 入未连）", $"{functionName} → 「{node.Name}」");
                }

                // 7) 一个输入脚接多个上游：UE 的数据脚只能接一个，多接说明连线被重复登记。
                // 8) 两端类型不兼容：孤立清零只证明「有边」，不能证明「边连对了」——
                //    类型对不上是连错线最直接的信号。
                foreach (var node in result.Nodes)
                {
                    foreach (var input in node.Input)
                    {
                        var producers = input.LinkedTo.Where(p => p.IsOutput).ToList();

                        if (producers.Count > 1
                            && input.Type.Category != SolsticeWinter.Decompiler.Model.PinType.Exec
                            && !functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
                        {
                            // 只约束**数据脚**：UE 里 exec 入脚可以接多条上游（分支汇合到同一个
                            // 节点是常态，`Branch.True/False → Return` 就是），ubergraph 更是多入口共享。
                            Violate("数据输入脚接了多个上游",
                                $"{functionName} → 「{node.Name}」.{input.Name}（{producers.Count} 个）");
                        }

                        foreach (var producer in producers)
                        {
                            if (TypesCompatible(producer.Type, input.Type)) continue;

                            // 只报**权威类型**之间的冲突。以下两类不是事实来源，拿它们当
                            // 「连错线」的证据是假阳性：
                            //   · 类型传播推断出来的值（`IsTypeInferred`）；
                            //   · 编译器临时变量节点（类型本来就是我们猜的）。
                            if (producer.IsTypeInferred || input.IsTypeInferred) continue;
                            if (producer.ParentNode is SolsticeWinter.Decompiler.Nodes.K2Node_TemporaryVariable) continue;

                            Violate("连线两端类型不兼容",
                                $"{functionName} → 「{producer.ParentNode?.Name}」.{producer.Name}" +
                                $"({producer.Type.Display()}) → 「{node.Name}」.{input.Name}({input.Type.Display()})");
                        }
                    }
                }

                // 6) 执行链被截断：字节码里有 `EX_Return`，图里却没有 Return 节点。
                // 这是唯一能机械发现「循环/分支后半段丢了」的检查——节点变少本身不可疑，
                // 丢了 Return 才是真的少了内容。
                var hasReturnInstruction = function.ScriptBytecode?.Any(expr => expr is UAssetAPI.Kismet.Bytecode.Expressions.EX_Return) == true;
                var hasReturnNode = result.Nodes.Any(n => n is SolsticeWinter.Decompiler.Nodes.K2Node_FunctionResult);
                if (hasReturnInstruction && !hasReturnNode
                    && !functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
                {
                    Violate("执行链截断（有 Return 指令但无 Return 节点）", functionName);
                }
            }

            report.AppendLine($"# {virtualPath} 图一致性校验");
            report.AppendLine();
            report.AppendLine($"函数 {functions}");
            report.AppendLine();

            if (rules.Count == 0)
            {
                report.AppendLine("**全部通过**：八条规则都没有违反。");
            }
            else
            {
                foreach (var pair in rules.OrderByDescending(p => p.Value))
                {
                    report.AppendLine($"## {pair.Key}：**{pair.Value}** 处");
                    foreach (var sample in samples[pair.Key]) report.AppendLine($"- {sample}");
                    report.AppendLine();
                }
            }

            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>
    /// 打印 **UI 层**（GraphAdapter 之后）的节点标题/正文——我看不了图，这是唯一能验证显示效果的入口。
    /// </summary>
    /// <summary>
    /// **全函数渲染自检**：对资产里每个函数跑一遍「构建 → 生成 VM → 布局」，
    /// 报告渲染阶段抛异常、节点数为 0、VM 数与模型数不一致的函数。
    ///
    /// <para>
    /// 存在的理由：`--vmdump` 只验证单个函数，而 UI 曾经因为线程问题**静默回退旧投影**——
    /// 那条路径 `--vmdump` 永远看不到。这个模式在进程内把 475 个函数一次跑完，
    /// 至少能抓住「渲染阶段对某些函数会炸」这一类问题。
    /// </para>
    /// </summary>
    public static int CheckAll(string virtualPath)
    {
        var path = ProbePath("checkall.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();

            var total = 0;
            GraphAdapter.ResetLayoutFallbacks();
            var empty = new List<string>();
            var failed = new List<(string Name, string Error)>();
            var mismatched = new List<(string Name, int Model, int Vm)>();

            foreach (var functionName in ws.Functions)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : new List<uint>();
                uint? start = entries.Count > 0 ? entries[0] : null;

                total++;
                try
                {
                    var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                        asset, function, entries, start, 4000, signatures, variableTypes);

                    if (result.Nodes.Count == 0) { empty.Add(functionName); continue; }

                    var (nodes, connections) = GraphAdapter.ToView(result.Nodes);
                    if (nodes.Count != result.Nodes.Count)
                        mismatched.Add((functionName, result.Nodes.Count, nodes.Count));

                    // `ToView` 内部已经跑了布局（`Layout`），布局失败会退回简单分列并写日志。
                }
                catch (Exception e)
                {
                    failed.Add((functionName, $"{e.GetType().Name}: {e.Message}"));
                }
            }

            report.AppendLine($"# {virtualPath} 全函数渲染自检");
            report.AppendLine();
            report.AppendLine($"函数 {total}　空节点 {empty.Count}　渲染失败 {failed.Count}　数量不一致 {mismatched.Count}　布局回退 {GraphAdapter.LayoutFallbacks}");
            report.AppendLine();

            foreach (var (name, error) in failed.Take(20)) report.AppendLine($"- 失败 `{name}`：{error}");
            foreach (var (name, model, vm) in mismatched.Take(20)) report.AppendLine($"- 不一致 `{name}`：模型 {model} / VM {vm}");
            foreach (var name in empty.Take(20)) report.AppendLine($"- 空图 `{name}`");

            if (failed.Count == 0 && mismatched.Count == 0 && empty.Count == 0)
                report.AppendLine("**全部通过**：每个函数都能构建、渲染、布局。");

            return failed.Count == 0 && mismatched.Count == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>
    /// 两个引脚类型是否**兼容**（可直连）。用于发现「边连错了」——孤立清零只证明有边，
    /// 不证明连对了，类型对不上是最直接的信号。
    ///
    /// <para>
    /// 允许的兼容：完全相同；数值族互通（Int / Int64 / Byte / Float / Double，UE 会自动转换）；
    /// 对象族互通（Object / Class / Interface / SoftObject / SoftClass）；任一端 Unknown 一律放过
    /// （类型缺失不是「连错」的证据）。
    /// </para>
    /// </summary>
    private static bool TypesCompatible(SolsticeWinter.Decompiler.Model.GraphPinType a,
        SolsticeWinter.Decompiler.Model.GraphPinType b)
    {
        const SolsticeWinter.Decompiler.Model.PinType Unknown = SolsticeWinter.Decompiler.Model.PinType.Unknown;
        if (a.Category == Unknown || b.Category == Unknown) return true;
        if (a.Category == b.Category) return true;

        static bool Numeric(SolsticeWinter.Decompiler.Model.PinType t) => t
            is SolsticeWinter.Decompiler.Model.PinType.Int
            or SolsticeWinter.Decompiler.Model.PinType.Int64
            or SolsticeWinter.Decompiler.Model.PinType.Byte
            or SolsticeWinter.Decompiler.Model.PinType.Float
            or SolsticeWinter.Decompiler.Model.PinType.Double;

        static bool Objectish(SolsticeWinter.Decompiler.Model.PinType t) => t
            is SolsticeWinter.Decompiler.Model.PinType.Object
            or SolsticeWinter.Decompiler.Model.PinType.Class
            or SolsticeWinter.Decompiler.Model.PinType.Interface
            or SolsticeWinter.Decompiler.Model.PinType.SoftObject
            or SolsticeWinter.Decompiler.Model.PinType.SoftClass;

        if (Numeric(a.Category) && Numeric(b.Category)) return true;
        if (Objectish(a.Category) && Objectish(b.Category)) return true;

        // Object ↔ Struct：索引里的类型字符串**分不出 struct 和 class**
        // （`/Script/SlateCore.SlateBrush` 这种一律落到 Object 兜底），
        // 所以这两类之间「不兼容」是数据局限，不是连错线（实测 1 处）。
        static bool ObjectOrStruct(SolsticeWinter.Decompiler.Model.PinType t) => Objectish(t)
            || t == SolsticeWinter.Decompiler.Model.PinType.Struct;
        if (ObjectOrStruct(a.Category) && ObjectOrStruct(b.Category)) return true;

        // 枚举 ↔ 字节/整数：枚举在 UE 里就是整数传输。
        // 必须包含 Int —— jmap 会把 map 的值类型抹成 `IntProperty`，于是
        // `Map_Find(...) -> Int` 接到期望 `Enum` 的参数上（实测 8 处全是这个模式），
        // 那是签名数据缺失，不是连错线。
        static bool EnumLike(SolsticeWinter.Decompiler.Model.PinType t) => t
            is SolsticeWinter.Decompiler.Model.PinType.Enum
            or SolsticeWinter.Decompiler.Model.PinType.Byte
            or SolsticeWinter.Decompiler.Model.PinType.Int
            or SolsticeWinter.Decompiler.Model.PinType.Int64;
        if (EnumLike(a.Category) && EnumLike(b.Category)) return true;

        return false;
    }

    /// <summary>
    /// **块覆盖诊断**：列出每条指令的偏移、操作码、是否被走查访问过。
    ///
    /// <para>
    /// 存在理由：图丢内容只有一个系统性来源——某些基本块没被走到。
    /// 之前只能看到「节点少了」，看不到「哪条指令没被执行到、上一条件是什么」。
    /// </para>
    /// </summary>
    public static int Blocks(string virtualPath, string functionName)
    {
        var path = ProbePath("blocks.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(report, $"没有函数 {functionName}", path);

            var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                : new List<uint>();
            uint? start = entries.Count > 0 ? entries[0] : null;

            var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                asset, function, entries, start, 4000, Signatures(), VariableTypes());

            var instructions = function.ScriptBytecode ?? [];
            uint running = 0;
            var reached = result.Reached.Count;
            var total = instructions.Length;
            var missed = 0;

            report.AppendLine($"# {functionName} 块覆盖");
            report.AppendLine();
            report.AppendLine($"指令 {total}　访问过 {reached}　未访问 {total - reached}");
            report.AppendLine();
            report.AppendLine("| 偏移 | 指令 | 状态 |");
            report.AppendLine("|---|---|---|");

            foreach (var instruction in instructions)
            {
                var offset = running;
                running += (uint)instruction.GetSize(asset);
                var hit = result.Reached.Contains(offset);
                if (!hit) missed++;
                report.AppendLine($"| {offset} | {instruction.GetType().Name.Replace("EX_", string.Empty)} | {(hit ? "✓" : "**✗ 未访问**")} |");
            }

            report.AppendLine();
            report.AppendLine($"未访问 {missed} 条。");
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>
    /// **表达式树诊断**：把指定偏移处那条指令的完整表达式树打出来。
    ///
    /// <para>
    /// 用来回答「UE 到底把赋值藏在了什么语法形态里」这类问题——
    /// `--trace` 只能看到我们**认识**的形态，看不到陌生的形态长什么样。
    /// </para>
    /// </summary>
    public static int Expr(string virtualPath, string functionName, uint targetOffset)
    {
        var path = ProbePath("expr.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(report, $"没有函数 {functionName}", path);

            var instructions = function.ScriptBytecode ?? [];
            uint running = 0;
            global::UAssetAPI.Kismet.Bytecode.KismetExpression? found = null;

            foreach (var instruction in instructions)
            {
                var size = (uint)instruction.GetSize(asset);
                if (running == targetOffset) { found = instruction; break; }
                if (running > targetOffset) break;
                running += size;
            }

            if (found is null) return Fail(report, $"偏移 {targetOffset} 不落在任何指令上", path);

            report.AppendLine($"# {functionName} @{targetOffset}");
            report.AppendLine();
            DumpExpression(found, asset, report, 0);
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>递归打印表达式树（反射读字段，不依赖已知类型清单）。</summary>
    private static void DumpExpression(object? value, global::UAssetAPI.UAsset asset,
        System.Text.StringBuilder report, int depth)
    {
        if (depth > 12 || value is null) return;

        var indent = new string(' ', depth * 2);
        var type = value.GetType();

        if (value is global::UAssetAPI.Kismet.Bytecode.KismetExpression expression)
        {
            report.AppendLine($"{indent}{type.Name}");
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var fieldValue = field.GetValue(expression);
                switch (fieldValue)
                {
                    case null:
                        break;
                    case global::UAssetAPI.Kismet.Bytecode.KismetExpression child:
                        DumpExpression(child, asset, report, depth + 1);
                        break;
                    case global::UAssetAPI.Kismet.Bytecode.KismetExpression[] children:
                        report.AppendLine($"{indent}  {field.Name}[{children.Length}]:");
                        foreach (var item in children) DumpExpression(item, asset, report, depth + 2);
                        break;
                    case string text:
                        report.AppendLine($"{indent}  {field.Name} = \"{text}\"");
                        break;
                    case global::UAssetAPI.Kismet.Bytecode.KismetPropertyPointer pointer:
                        // 指针里才是变量名的真正来源，必须打出来（只打 `KismetPropertyPointer` 等于没打）
                        var path = pointer.New?.Path is { } segments
                            ? string.Join(" / ", segments.Select(s => s.ToString()))
                            : "(旧式指针)";
                        report.AppendLine($"{indent}  {field.Name} → [{path}]");
                        break;
                    case int or uint or long or ulong or byte or bool or float or double:
                        report.AppendLine($"{indent}  {field.Name} = {fieldValue}");
                        break;
                    default:
                        // 名字类字段（FName / 指针）打 ToString 就够
                        report.AppendLine($"{indent}  {field.Name} = {fieldValue}");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// **属性表诊断**：列出函数的全部 `LoadedProperties`（参数 / 局部变量 / 返回值）及其标志位。
    ///
    /// <para>
    /// 用来回答「这个名字到底是函数的局部变量，还是别处的属性」——
    /// 如果字节码读了一个名字、而它**不在**这张表里，那就是读取侧的名字解析有问题，
    /// 不是写入侧丢了。
    /// </para>
    /// </summary>
    public static int Locals(string virtualPath, string functionName)
    {
        var path = ProbePath("locals.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(report, $"没有函数 {functionName}", path);

            var propertyList = function.LoadedProperties ?? [];
            var rowCount = 0;
            report.AppendLine($"# {functionName} 属性表");
            report.AppendLine();
            report.AppendLine("| 名字 | 标志 | 类型 |");
            report.AppendLine("|---|---|---|");

            foreach (var property in propertyList)
            {
                rowCount++;
                report.AppendLine($"| `{property.Name}` | {property.PropertyFlags} | {property.GetType().Name} |");
            }

            report.AppendLine();
            report.AppendLine($"共 {rowCount} 项。");

            // 扫**整棵表达式树**（含嵌套）里有多少 `EX_Let`：
            // 走查只处理顶层 Let，如果赋值藏在嵌套表达式里就会漏。
            var nestedLets = 0;
            var letTargets = new List<string>();
            uint scanOffset = 0;
            foreach (var instruction in function.ScriptBytecode ?? [])
            {
                try
                {
                    instruction.Visit(asset, ref scanOffset, (expr, _) =>
                    {
                        if (expr is not global::UAssetAPI.Kismet.Bytecode.Expressions.EX_Let let) return;
                        nestedLets++;
                        if (let.Variable is global::UAssetAPI.Kismet.Bytecode.Expressions.EX_LocalVariable local
                            && local.Variable?.New?.Path is { Length: > 0 } path)
                        {
                            letTargets.Add(path[^1].ToString());
                        }
                    });
                }
                catch
                {
                    // 单个指令访问失败不影响整体统计
                }
            }

            report.AppendLine();
            report.AppendLine($"## 表达式树里的 Let");
            report.AppendLine();
            report.AppendLine($"共 {nestedLets} 条（含嵌套）。");
            foreach (var target in letTargets.Where(t => t.Contains("BreakVector", StringComparison.Ordinal)))
                report.AppendLine($"- 写入 `{target}`");
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// <summary>
    /// **out 参数来源统计**：带 out 参数的调用里，原生函数（jmap）和蓝图函数（索引）各占多少。
    ///
    /// <para>
    /// 用途：判断「能不能只对蓝图函数启用精确的 out 判据」。
    /// 蓝图函数的签名方向来自真实的 `CPF_OutParm` / `CPF_ConstParm`，是准的；
    /// jmap 的 `dir` 把 const-ref 输入也标成 out，不可靠。数字决定这条路的收益面。
    /// </para>
    /// </summary>
    public static int OutStat(string virtualPath)
    {
        var path = ProbePath("outstat.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();
            SolsticeWinter.Decompiler.GraphCompiler.ResetOutParamStats();

            var functions = 0;
            foreach (var functionName in ws.Functions)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;
                functions++;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : new List<uint>();
                uint? start = entries.Count > 0 ? entries[0] : null;

                _ = SolsticeWinter.Decompiler.GraphCompiler.Build(
                    asset, function, entries, start, 4000, signatures, variableTypes);
            }

            var stats = SolsticeWinter.Decompiler.GraphCompiler.OutParamCallCounts;
            report.AppendLine($"# {virtualPath} out 参数来源统计");
            report.AppendLine();
            report.AppendLine($"扫描函数 {functions}");
            report.AppendLine();

            foreach (var key in new[] { "native", "blueprint", "no-out" })
            {
                report.AppendLine($"- `{key}`：{stats.GetValueOrDefault(key)}");
            }

            report.AppendLine();
            report.AppendLine("## 按 out 参数个数细分");
            foreach (var pair in stats.Where(p => p.Key.Contains("out=", StringComparison.Ordinal))
                         .OrderByDescending(p => p.Value))
            {
                report.AppendLine($"- `{pair.Key}` × {pair.Value}");
            }

            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    public static int VmDump(string virtualPath, string functionName)
    {
        var path = ProbePath("vmdump.txt");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(report, $"没有函数 {functionName}", path);

            var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                : new List<uint>();
            uint? start = entries.Count > 0 ? entries[0] : null;

            var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                asset, function, entries, start, 4000, Signatures(), VariableTypes());

            var (nodes, _) = GraphAdapter.ToView(result.Nodes);
            report.AppendLine($"函数 {functionName}　UI 节点 {nodes.Count}");

            // UI 侧布局跨度：验证「图是不是铺得离谱」的唯一数据口径
            var minX = nodes.Min(n => n.Location.X);
            var minY = nodes.Min(n => n.Location.Y);
            var maxX = nodes.Max(n => n.Location.X);
            var maxY = nodes.Max(n => n.Location.Y);
            var spanW = maxX - minX + 320;
            var spanH = maxY - minY + 120;
            var zoom = Math.Min(1200 / Math.Max(1, spanW), 700 / Math.Max(1, spanH));
            report.AppendLine($"布局跨度 {spanW:0}×{spanH:0}　估算 zoom≈{zoom:0.000}");
            report.AppendLine();
            foreach (var node in nodes)
            {
                var pins = string.Join(" / ", node.Inputs.Select(p => $"{p.Title}:{p.TypeName}").Concat(node.Outputs.Select(p => $"{p.Title}:{p.TypeName}")));
                report.AppendLine($"标题「{node.Title}」　正文「{node.Subtitle}」　引脚[{pins}]");
            }

            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// </summary>
    public static int EntryPins(string virtualPath)
    {
        var path = ProbePath("entrypins.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();
            var functionNames = ws.Functions.ToList();

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var totalPins = 0;
            var totalEdges = 0;

            foreach (var functionName in functionNames)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : new List<uint>();
                uint? start = entries.Count > 0 ? entries[0] : null;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                    asset, function, entries, start, 4000, signatures, variableTypes);

                var entry = result.Nodes.OfType<SolsticeWinter.Decompiler.Nodes.K2Node_FunctionEntry>().FirstOrDefault();
                if (entry is null) continue;

                foreach (var pin in entry.Output)
                {
                    totalPins++;
                    var consumers = pin.LinkedTo.Count(t => t.ParentNode is not null);
                    if (consumers == 0) continue;
                    totalEdges += consumers;

                    var kind = pin.Name.StartsWith("Temp_", StringComparison.Ordinal) ? "Temp_*"
                        : pin.Name.StartsWith("CallFunc_", StringComparison.Ordinal) ? "CallFunc_*"
                        : pin.Name.StartsWith("K2Node_", StringComparison.Ordinal) ? "K2Node_*"
                        : "参数/其它";
                    byKind[kind] = byKind.GetValueOrDefault(kind) + consumers;
                    byName[pin.Name] = byName.GetValueOrDefault(pin.Name) + consumers;
                }
            }

            report.AppendLine($"# {virtualPath} 入口脚统计");
            report.AppendLine();
            report.AppendLine($"入口输出脚总数 {totalPins}　挂出去的边 {totalEdges}");
            report.AppendLine();
            report.AppendLine("按名字前缀：");
            foreach (var pair in byKind.OrderByDescending(p => p.Value))
                report.AppendLine($"- `{pair.Key}`：**{pair.Value}** 条边");
            report.AppendLine();
            report.AppendLine("边最多的 15 个名字：");
            foreach (var pair in byName.OrderByDescending(p => p.Value).Take(15))
                report.AppendLine($"- `{pair.Key}`：{pair.Value}");
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// </summary>
    public static int Macros(string virtualPath)
    {
        var path = ProbePath("macros.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var signatures = Signatures();
            var variableTypes = VariableTypes();
            var functionNames = ws.Functions.ToList();

            int forEach = 0, whileLoop = 0, arrays = 0, total = 0, breakLike = 0, loopWithoutMacro = 0;
            var loopSamples = new List<string>();
            var breakExamples = new List<string>();
            var examples = new List<string>();

            foreach (var functionName in functionNames)
            {
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;
                total++;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(
                    asset, function, [], null, 4000, signatures, variableTypes);

                var titles = result.Nodes.Select(n => n.Name).ToList();
                var hasLength = titles.Any(t => t.Contains("LENGTH", StringComparison.Ordinal));
                var hasGet = titles.Any(t => t.Contains("GET", StringComparison.Ordinal));
                var hasCompare = result.Nodes.Any(n => n.Name is "<" or "<=");
                var hasLoop = result.Nodes.Any(n => n.NodeType == "K2Node_Loop");

                if (hasLength && hasGet && hasCompare)
                {
                    forEach++;
                    if (examples.Count < 8) examples.Add($"{functionName}（{result.Nodes.Count} 节点）");
                }
                if (hasLoop) whileLoop++;

                // 「有 Loop 节点但没折成宏」——这是还没覆盖的那一类
                var hasMacro = result.Nodes.Any(n => n.NodeType == "K2Node_MacroInstance");
                if (hasLoop && !hasMacro)
                {
                    loopWithoutMacro++;
                    if (loopSamples.Count < 10)
                        loopSamples.Add($"{functionName}（{result.Nodes.Count} 节点，LENGTH={hasLength}，GET={hasGet}，比较={hasCompare}）");
                }

                // 跳出特征：宏的 Completed 目标和「别的节点」的 exec 目标重合 → 可能是 break
                foreach (var macro in result.Nodes.OfType<SolsticeWinter.Decompiler.Nodes.K2Node_MacroInstance>())
                {
                    var completed = macro.Output.FirstOrDefault(p => p.Name == "Completed");
                    if (completed is null) continue;

                    var targets = completed.LinkedTo
                        .Where(t => t.ParentNode is not null)
                        .Select(t => t.ParentNode!)
                        .ToHashSet();
                    if (targets.Count == 0) continue;

                    var extraJump = result.Nodes.Any(node =>
                        !ReferenceEquals(node, macro)
                        && node.Output.Any(p => p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec
                                                && p.LinkedTo.Any(t => t.ParentNode is not null && targets.Contains(t.ParentNode))));

                    if (extraJump)
                    {
                        breakLike++;
                        if (breakExamples.Count < 8) breakExamples.Add($"{functionName}（{macro.Name}）");
                        break;
                    }
                }
                if (hasLength) arrays++;
            }

            report.AppendLine($"# {virtualPath} 宏模式扫描");
            report.AppendLine();
            report.AppendLine($"函数 {total}");
            report.AppendLine($"- 含 `Array_Length`：**{arrays}**");
            report.AppendLine($"- 含 `Loop` 节点：**{whileLoop}**");
            report.AppendLine($"- **For Each Loop 特征**（LENGTH + GET + 比较）：**{forEach}**");
            report.AppendLine($"- **疑似带 break 的循环**（别的节点也跳到循环出口）：**{breakLike}**");
            report.AppendLine($"- **有 Loop 节点但没折成宏**：**{loopWithoutMacro}**");
            if (loopSamples.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("未折样例：");
                foreach (var sample in loopSamples) report.AppendLine($"- {sample}");
            }
            if (breakExamples.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("break 候选：");
                foreach (var example in breakExamples) report.AppendLine($"- {example}");
            }
            report.AppendLine();
            report.AppendLine("样例：");
            foreach (var example in examples) report.AppendLine($"- {example}");
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }

    /// </summary>
    public static int Audit(string virtualPath)
    {
        var path = ProbePath("audit.md");
        var report = new System.Text.StringBuilder();
        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(report, "反射拿不到 _asset", path);

            var total = 0;
            var emptyFunctions = new List<string>();
            var isolatedFunctions = new List<(string Name, int Isolated, int Nodes)>();
            var unknownByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var problemByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var argPinFunctions = new List<(string Name, int ArgPins, int Pins)>();
            var rows = new List<string>();
            var totalNodes = 0;
            var totalEdges = 0;
            var totalIsolated = 0;
            var totalUnknown = 0;
            var totalArgPins = 0;
            var totalPins = 0;
            var totalTypedPins = 0;
            var totalUnknownTypePins = 0;
            var unknownTypeByKind = new Dictionary<string, int>(StringComparer.Ordinal);

            // 宏普查：`For Each Loop` 折不出来时节点会退回一堆 LENGTH / `<` / GET，
            // 但渲染和校验都不会报错——只有统计宏的数量才看得出退化。
            var macroByTitle = new Dictionary<string, int>(StringComparer.Ordinal);

            // 指令 vs 节点对账（见下）
            var totalJumpIfNot = 0;
            var totalBranchNodes = 0;
            var totalLostBranches = 0;
            var branchGaps = new List<(string Name, int Instructions, int Nodes)>();

            // 调用对账的计数器是静态的，扫描前清零
            SolsticeWinter.Decompiler.GraphCompiler.ResetOutParamStats();

            foreach (var functionName in ws.Functions)
            {
                total++;
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : [];
                uint? start = entries.Count > 0 ? entries[0] : null;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(asset, function, entries, start, 4000, Signatures(), VariableTypes());
                var nodeSet = new HashSet<SolsticeWinter.Decompiler.Model.BPNode>(result.Nodes);

                var edges = result.Nodes
                    .SelectMany(n => n.Output)
                    .SelectMany(p => p.LinkedTo)
                    .Count(p => p.ParentNode is not null && nodeSet.Contains(p.ParentNode));

                // 完全没有任何连线的节点（既没进也没出）——最直观的「节点有问题」信号
                var isolated = result.Nodes.Count(n =>
                    n.Input.All(p => p.LinkedTo.Count == 0) && n.Output.All(p => p.LinkedTo.Count == 0));

                var unknown = result.Nodes.Where(n => n is SolsticeWinter.Decompiler.Nodes.UnknownNode).ToList();
                foreach (var u in unknown) unknownByKind[u.Name] = unknownByKind.GetValueOrDefault(u.Name) + 1;

                var pins = result.Nodes.SelectMany(n => n.Input).ToList();
                var allPins = result.Nodes.SelectMany(n => n.Input.Concat(n.Output)).Where(p => p.Name.Length > 0).ToList();
                totalTypedPins += allPins.Count;
                totalUnknownTypePins += allPins.Count(p => p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Unknown);

                // 宏普查：折出来的宏按标题统计（退化时数量会掉到 0，但渲染/校验都不报错）。
                foreach (var macro in result.Nodes.OfType<SolsticeWinter.Decompiler.Nodes.K2Node_MacroInstance>())
                {
                    macroByTitle[macro.Name] = macroByTitle.GetValueOrDefault(macro.Name) + 1;
                }

                // **指令 vs 节点对账**：字节码里有多少个分支点，图上就该有多少个分支/宏节点。
                // 少了说明「执行链在某处被截断」——这是唯一能机械发现「图少了一层逻辑」的检查。
                var jumpIfNotCount = function.ScriptBytecode?
                    .Count(e => e is UAssetAPI.Kismet.Bytecode.Expressions.EX_JumpIfNot) ?? 0;
                var branchNodeCount = result.Nodes.Count(n =>
                    n is SolsticeWinter.Decompiler.Nodes.K2Node_IfThenElse
                    or SolsticeWinter.Decompiler.Nodes.K2Node_MacroInstance);

                totalJumpIfNot += jumpIfNotCount;
                totalBranchNodes += branchNodeCount;

                // ubergraph 单独排除：它有多個事件入口、共享块会被**多次**走进来，
                // 节点数天然可能多于/少于指令数，拿它判「截断」只会制造噪声。
                var isUbergraph = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal);
                if (!isUbergraph && jumpIfNotCount > branchNodeCount)
                {
                    var lost = jumpIfNotCount - branchNodeCount;
                    totalLostBranches += lost;
                    if (branchGaps.Count < 30) branchGaps.Add((functionName, jumpIfNotCount, branchNodeCount));
                }

                // 未知类型按**节点类型**分布：不定位来源就只能瞎猜（实测占 24~27%）。
                foreach (var node in result.Nodes)
                {
                    var unknownPins = node.Input.Concat(node.Output)
                        .Count(p => p.Name.Length > 0 && p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Unknown);
                    if (unknownPins == 0) continue;

                    var kind = node.GetType().Name;
                    unknownTypeByKind[kind] = unknownTypeByKind.GetValueOrDefault(kind) + unknownPins;
                }
                var argPins = pins.Count(p => p.Name.StartsWith("arg", StringComparison.Ordinal));

                foreach (var problem in result.Problems)
                {
                    // 分类键取到 `]` 或 `:` 为止：`[未处理语句] LetBool @12` 要能区分出具体指令类型，
                    // 否则只知道「有 44 条没处理」却不知道是什么。
                    var kind = problem.Split('@')[0].Trim();
                    problemByKind[kind] = problemByKind.GetValueOrDefault(kind) + 1;
                }

                totalNodes += result.Nodes.Count;
                totalEdges += edges;
                totalIsolated += isolated;
                totalUnknown += unknown.Count;
                totalArgPins += argPins;
                totalPins += pins.Count;

                if (result.Nodes.Count == 0) emptyFunctions.Add(functionName);
                if (isolated > 0) isolatedFunctions.Add((functionName, isolated, result.Nodes.Count));
                if (argPins > 0) argPinFunctions.Add((functionName, argPins, pins.Count));

                if (result.Problems.Count > 0 || unknown.Count > 0 || isolated > 0)
                    rows.Add($"| `{functionName}` | {result.Nodes.Count} | {edges} | {isolated} | {unknown.Count} | {argPins}/{pins.Count} | {result.Problems.Count} |");
            }

            report.AppendLine($"# {virtualPath} 节点诊断");
            report.AppendLine();
            report.AppendLine($"函数 {total}　节点 {totalNodes}　边 {totalEdges}");
            report.AppendLine();
            report.AppendLine($"- 空视图：**{emptyFunctions.Count}**");
            report.AppendLine($"- 孤立节点（无任何连线）：**{totalIsolated}** 个，涉及 {isolatedFunctions.Count} 个函数");
            report.AppendLine($"- 不透明节点：**{totalUnknown}** 个");
            report.AppendLine($"- 未命名引脚（argN）：**{totalArgPins}** / {totalPins}（{(totalPins > 0 ? 100.0 * totalArgPins / totalPins : 0):0.0}%）");
            report.AppendLine($"- **引脚类型未知**：**{totalUnknownTypePins}** / {totalTypedPins}（{(totalTypedPins > 0 ? 100.0 * totalUnknownTypePins / totalTypedPins : 0):0.0}%）");
            report.AppendLine();
            report.AppendLine("## 未知类型按节点类型");
            foreach (var kv in unknownTypeByKind.OrderByDescending(k => k.Value).Take(12))
            {
                report.AppendLine($"- `{kv.Key}` × {kv.Value}（占全部未知 {(totalUnknownTypePins > 0 ? 100.0 * kv.Value / totalUnknownTypePins : 0):0.0}%）");
            }

            report.AppendLine();
            report.AppendLine("## 指令 vs 节点对账");
            report.AppendLine();
            report.AppendLine($"- 分支点（`JumpIfNot`）指令：**{totalJumpIfNot}**");
            report.AppendLine($"- 分支/宏节点（`Branch` + `MacroInstance`）：**{totalBranchNodes}**");
            report.AppendLine($"- **少掉的分支：{totalLostBranches}**（>0 说明执行链有截断；ubergraph 已排除）");

            // 调用对账：指令数 / 建出的节点数 / 被当死节点删掉的数量。
            // 折叠（`Let X = 调用(...)` 只登记生产者）是正常的，所以节点数 < 指令数是常态；
            // 关键是看「删掉的」占多少——突然变大说明本该有消费者的连接断了。
            var callInstr = SolsticeWinter.Decompiler.GraphCompiler.CallInstructions;
            var callCreated = SolsticeWinter.Decompiler.GraphCompiler.CallNodesCreated;
            var callPruned = SolsticeWinter.Decompiler.GraphCompiler.CallNodesPruned;
            report.AppendLine();
            report.AppendLine("### 调用对账");
            report.AppendLine($"- 调用指令：**{callInstr}**");
            report.AppendLine($"- 建出的调用节点：**{callCreated}**（差 {callInstr - callCreated}，折叠进生产者是正常的）");
            report.AppendLine($"- 被当死节点删掉：**{callPruned}**（突然变大说明连接断了）");

            if (branchGaps.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("缺口最大的函数：");
                foreach (var gap in branchGaps.OrderByDescending(g => g.Instructions - g.Nodes).Take(10))
                {
                    report.AppendLine($"- `{gap.Name}`：指令 {gap.Instructions} / 节点 {gap.Nodes}（缺 {gap.Instructions - gap.Nodes}）");
                }
            }

            report.AppendLine();
            report.AppendLine("## 宏普查");
            if (macroByTitle.Count == 0)
            {
                report.AppendLine("- （一个宏都没折出来）");
            }
            else
            {
                foreach (var kv in macroByTitle.OrderByDescending(k => k.Value))
                    report.AppendLine($"- `{kv.Key}` × {kv.Value}");
            }

            report.AppendLine();
            report.AppendLine("## 不透明节点类型");
            foreach (var kv in unknownByKind.OrderByDescending(k => k.Value).Take(15))
                report.AppendLine($"- `{kv.Key}` × {kv.Value}");
            report.AppendLine();
            report.AppendLine("## 走查器问题分类");
            foreach (var kv in problemByKind.OrderByDescending(k => k.Value).Take(15))
                report.AppendLine($"- `{kv.Key}` × {kv.Value}");
            report.AppendLine();
            report.AppendLine("## 孤立节点最多的函数");
            foreach (var (name, iso, nodes) in isolatedFunctions.OrderByDescending(x => x.Isolated).Take(20))
                report.AppendLine($"- `{name}`：{iso} / {nodes} 个节点没有连线");
            report.AppendLine();
            report.AppendLine("## argN 引脚最多的函数");
            foreach (var (name, arg, all) in argPinFunctions.OrderByDescending(x => x.ArgPins).Take(20))
                report.AppendLine($"- `{name}`：{arg} / {all} 个引脚没有名字");
            report.AppendLine();
            report.AppendLine("## 全部有问题的函数");
            report.AppendLine("| 函数 | 节点 | 边 | 孤立 | 不透明 | argN/总引脚 | 问题 |");
            report.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var row in rows) report.AppendLine(row);
            return 0;
        }
        catch (Exception e)
        {
            report.AppendLine($"失败：{e.GetType().Name}: {e.Message}");
            report.AppendLine(e.StackTrace);
            return 1;
        }
        finally
        {
            Write(path, report);
        }
    }
    /// <summary>批量跑走查器：<c>--batch &lt;资产&gt;</c>，统计每个函数的节点/问题/argN/孤立边。</summary>
    public static int Batch(string virtualPath)
    {
        var path = ProbePath("graph.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(log, "反射拿不到 _asset", path);

            var empty = 0;
            var total = 0;
            var argPins = 0;
            var unknownNodes = 0;
            var dangling = 0;
            var rows = new List<string>();
            var unknownKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var danglingSamples = new List<string>();

            foreach (var functionName in ws.Functions)
            {
                total++;
                var function = AssetAccess.FindFunction(asset, functionName);
                if (function is null) continue;

                var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                    ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                    : [];
                uint? start = entries.Count > 0 ? entries[0] : null;

                var result = SolsticeWinter.Decompiler.GraphCompiler.Build(asset, function, entries, start, 4000, Signatures(), VariableTypes());
                var nodeSet = new HashSet<SolsticeWinter.Decompiler.Model.BPNode>(result.Nodes);
                var danglingList = result.Nodes
                    .SelectMany(n => n.Output)
                    .SelectMany(p => p.LinkedTo)
                    .Where(p => p.ParentNode is null || !nodeSet.Contains(p.ParentNode))
                    .ToList();
                var danglingEdges = danglingList.Count;
                if (danglingEdges > 0 && danglingSamples.Count < 6)
                {
                    danglingSamples.Add($"{functionName}: " + string.Join("; ",
                        danglingList.Take(3).Select(p => $"{p.ParentNode?.Name ?? "<无父>"}.{p.Name}({p.Type.Category})")));
                }

                var argCount = result.Nodes
                    .SelectMany(n => n.Input)
                    .Count(p => p.Name.StartsWith("arg", StringComparison.Ordinal));
                var unknown = result.Nodes.Count(n => n is SolsticeWinter.Decompiler.Nodes.UnknownNode);

                argPins += argCount;
                unknownNodes += unknown;
                foreach (var unknownNode in result.Nodes.Where(n => n is SolsticeWinter.Decompiler.Nodes.UnknownNode))
                {
                    unknownKinds[unknownNode.Name] = unknownKinds.TryGetValue(unknownNode.Name, out var c) ? c + 1 : 1;
                }
                dangling += danglingEdges;
                if (result.Nodes.Count == 0) empty++;

                if (result.Nodes.Count == 0 || argCount > 0 || danglingEdges > 0)
                    rows.Add($"{functionName,-44} 节点 {result.Nodes.Count,4}  问题 {result.Problems.Count,2}  argN {argCount,3}  悬空边 {danglingEdges,2}  不透明 {unknown,3}");
            }

            log.AppendLine($"函数 {total}  空视图 {empty}  argN 引脚 {argPins}  悬空边 {dangling}  不透明节点 {unknownNodes}");
            foreach (var sample in danglingSamples) log.AppendLine("悬空样例: " + sample);
            log.AppendLine($"不透明节点类型分布：{string.Join(", ", unknownKinds.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}"))}");
            log.AppendLine();
            foreach (var row in rows.Take(40)) log.AppendLine(row);
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
            Write(path, log);
        }
    }

    private static SolsticeWinter.Decompiler.Signatures.IFunctionSignatures? Signatures()
    {
        if (_signatures is not null) return _signatures;

        // 优先用索引（含全部 /Game 蓝图函数签名）；索引不存在才退回 jmap。
        var indexPath = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "index.json");
        if (File.Exists(indexPath))
        {
            var index = SolsticeWinter.Index.SymbolIndex.Load(indexPath);
            return _signatures = new IndexSignatures(index);
        }

        var jmap = @"D:\files\projects\jmap-out\functions.json";
        if (!File.Exists(jmap)) return null;
        return _signatures = new JmapSignatures(SolsticeWinter.Index.JmapCatalog.Load(jmap));
    }

    private static SolsticeWinter.Decompiler.Signatures.IFunctionSignatures? _signatures;

    /// <summary>变量类型源（索引里的资产变量表）。</summary>
    private static SolsticeWinter.Decompiler.Signatures.IVariableTypes? VariableTypes()
    {
        if (_variableTypes is not null) return _variableTypes;
        var indexPath = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "index.json");
        return File.Exists(indexPath)
            ? _variableTypes = new IndexVariableTypes(SolsticeWinter.Index.SymbolIndex.Load(indexPath), LoadUsmap())
            : null;
    }

    private static SolsticeWinter.Decompiler.Signatures.IVariableTypes? _variableTypes;

    private static UAssetAPI.Unversioned.Usmap? LoadUsmap()
    {
        try
        {
            var path = GameProfile.Kards().UsmapPath;
            return path is { Length: > 0 } && File.Exists(path) ? new UAssetAPI.Unversioned.Usmap(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 探针输出路径。沙箱会给每个进程不同的临时目录，所以优先写仓库下的 .cache\probe，
    /// 写不进去再退回 %TEMP%。
    /// </summary>
    private static string ProbePath(string fileName)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "probe");
        try
        {
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "solstice-winter-ui", fileName);
        }
    }

    /// <summary>
    /// 导出走查器产出的图：<c>--export &lt;资产&gt; &lt;函数&gt; [输出文件]</c>。
    /// 结构固定（节点类型/标题/坐标/引脚/边），用于与 UEBGV 的导出做对账。
    /// </summary>
    public static int Export(string virtualPath, string functionName, string? outputPath)
    {
        try
        {
            var profile = GameProfile.Kards();
            var ws = AssetAccess.OpenWorkspace(profile, virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return 1;
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return 1;

            var entries = functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)
                ? ws.UbergraphCallSites(functionName).Select(s => (uint)s.Offset).ToList()
                : new List<uint>();
            uint? start = entries.Count > 0 ? entries[0] : null;

            var result = SolsticeWinter.Decompiler.GraphCompiler.Build(asset, function, entries, start, 4000, Signatures(), VariableTypes());
            var nodes = result.Nodes;

            // 跑一遍真实布局，导出的坐标才有意义（与 UI 用的是同一套 Msagl）
            var sizes = nodes.ToDictionary(n => n, n => new System.Windows.Point(Math.Max(160, n.NodeWidth), Math.Max(80, n.NodeHeight)));
            MsaglLayout.Apply(nodes, sizes, (node, location) => node.SetPosition((float)location.X, (float)location.Y));

            var indexOf = new Dictionary<SolsticeWinter.Decompiler.Model.BPNode, int>();
            for (var i = 0; i < nodes.Count; i++) indexOf[nodes[i]] = i;

            var dto = new
            {
                tool = "Solstice-winter",
                asset = virtualPath,
                function = functionName,
                nodes = nodes.Select((n, i) => new
                {
                    id = i,
                    type = n.NodeType,
                    title = n.Name,
                    instr = n.Instruction?.GetType().Name.Replace("EX_", string.Empty) ?? string.Empty,
                    x = Math.Round(n.X, 1),
                    y = Math.Round(n.Y, 1),
                    pure = n.Pure,
                    inputs = n.Input.Where(p => !p.IsHidden).Select(p => new { name = p.FriendlyName, type = p.Type.Display(), exec = p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec }).ToList(),
                    outputs = n.Output.Where(p => !p.IsHidden).Select(p => new { name = p.FriendlyName, type = p.Type.Display(), exec = p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec }).ToList(),
                }).ToList(),
                problems = result.Problems.ToList(),
                edges = nodes.SelectMany((n, i) => n.Output.SelectMany(p => p.LinkedTo
                        .Where(t => t.ParentNode is not null && indexOf.ContainsKey(t.ParentNode))
                        .Select(t => new { from = i, fromPin = p.FriendlyName, to = indexOf[t.ParentNode!], toPin = t.FriendlyName, exec = p.Type.Category == SolsticeWinter.Decompiler.Model.PinType.Exec })))
                    .ToList(),
            };

            var path = outputPath ?? Path.Combine(Path.GetTempPath(), "solstice-winter-ui", "graph-export.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dto,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"已导出 {path}（{nodes.Count} 节点）");
            return 0;
        }
        catch (Exception e)
        {
            var errorPath = ProbePath("export-error.txt");
            File.WriteAllText(errorPath, $"{e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>追踪一个函数的走查过程：<c>--trace &lt;资产&gt; &lt;函数&gt;</c>。</summary>
    public static int TraceWalk(string virtualPath, string functionName)
    {
        var path = ProbePath("trace.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(log, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(log, $"没有函数 {functionName}", path);

            SolsticeWinter.Decompiler.GraphCompiler.Trace = message => log.AppendLine(message);
            var result = SolsticeWinter.Decompiler.GraphCompiler.Build(asset, function, [], null, 4000, Signatures(), VariableTypes());
            SolsticeWinter.Decompiler.GraphCompiler.Trace = null;
            log.AppendLine($"=== 节点 {result.Nodes.Count}  问题 {result.Problems.Count} ===");
            return 0;
        }
        catch (Exception e)
        {
            log.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, log);
        }
    }

    /// <summary>指令直方图：<c>--hist &lt;资产&gt; &lt;函数&gt;</c>，看分支/跳转的真实数量。</summary>
    public static int Histogram(string virtualPath, string functionName)
    {
        var path = ProbePath("hist.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var ws = AssetAccess.OpenWorkspace(GameProfile.Kards(), virtualPath);
            if (AssetAccess.GetAsset(ws) is not { } asset) return Fail(log, "反射拿不到 _asset", path);
            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return Fail(log, $"没有函数 {functionName}", path);

            var bytecode = function.ScriptBytecode ?? [];
            var counts = bytecode.GroupBy(e => e.GetType().Name)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key.Replace("EX_", string.Empty)}={g.Count()}");
            log.AppendLine($"函数 {functionName}  顶层指令 {bytecode.Length}");
            log.AppendLine(string.Join(", ", counts));

            // 递归统计所有表达式（含嵌套），看真实的 Context / 变量读数量
            var nested = new Dictionary<string, int>(StringComparer.Ordinal);
            void Visit(UAssetAPI.Kismet.Bytecode.KismetExpression? expression)
            {
                if (expression is null) return;
                var key = expression.GetType().Name.Replace("EX_", string.Empty);
                nested[key] = nested.TryGetValue(key, out var count) ? count + 1 : 1;

                switch (expression)
                {
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_Context context:
                        Visit(context.ObjectExpression);
                        Visit(context.ContextExpression);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_StructMemberContext member:
                        Visit(member.StructExpression);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_FinalFunction call:
                        foreach (var p in call.Parameters) Visit(p);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_Let let:
                        Visit(let.Variable);
                        Visit(let.Expression);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_Return ret:
                        Visit(ret.ReturnExpression);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_JumpIfNot jump:
                        Visit(jump.BooleanExpression);
                        break;
                    case UAssetAPI.Kismet.Bytecode.Expressions.EX_SetArray setArray:
                        Visit(setArray.AssigningProperty);
                        foreach (var e in setArray.Elements) Visit(e);
                        break;
                }
            }
            foreach (var instruction in bytecode) Visit(instruction);
            log.AppendLine("嵌套统计: " + string.Join(", ", nested.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}")));

            var flow = new SolsticeWinter.Decompiler.Infrastructure.ExecutionFlow();
            var offsets = SolsticeWinter.Decompiler.Infrastructure.StatementMap.Build(bytecode, asset);
            flow.DecompileFlow(bytecode, offsets);
            log.AppendLine($"基本块 {flow.Blocks.Count}  跳转 {flow.Blocks.Sum(b => b.Jumps.Count)}  序列 {flow.Blocks.Sum(b => b.Sequences.Count)}");

            // Pop* 指令分布在哪些块里、块内第几条
            foreach (var block in flow.Blocks)
            {
                var pops = block.Instructions
                    .Select((e, i) => (e, i))
                    .Where(t => t.e is UAssetAPI.Kismet.Bytecode.Expressions.EX_PopExecutionFlow
                                or UAssetAPI.Kismet.Bytecode.Expressions.EX_PopExecutionFlowIfNot)
                    .Select(t => $"{t.e.GetType().Name.Replace("EX_", "")}@{t.i}")
                    .ToList();
                if (pops.Count > 0)
                    log.AppendLine($"  块 {block.Start}（{block.Instructions.Count} 条，{block.Type}）: {string.Join(",", pops)}");
            }

            // 序列点
            foreach (var block in flow.Blocks)
                foreach (var seq in block.Sequences)
                    log.AppendLine($"  序列 src={block.Start}@{seq.SourceIndex} → {string.Join(",", seq.Points.Select(p => $"{p.Destination.Start}@{p.StartIndex}"))}");
            return 0;
        }
        catch (Exception e)
        {
            log.AppendLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Write(path, log);
        }
    }

    private static int Fail(System.Text.StringBuilder log, string message, string path)
    {
        log.AppendLine($"失败：{message}");
        Write(path, log);
        return 1;
    }

    private static void Write(string path, System.Text.StringBuilder log)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, log.ToString());
        }
        catch
        {
            // 写不进去也没别的办法
        }
    }
}

















































