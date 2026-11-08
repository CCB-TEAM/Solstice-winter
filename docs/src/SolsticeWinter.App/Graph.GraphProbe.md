# Graph.GraphProbe — 无窗口自检（结果写 `graph.txt`）

**职责一句话**：命令行驱动的自检：打开一个资产的一个函数、投影成图，把「图的数据形状」（节点数、连线数、每个节点的种类/坐标/引脚）写进 `%TEMP%\solstice-winter-ui\graph.txt`，并用退出码表示成败。

**对外入口**：唯一静态方法 `Run(virtualPath, functionName, limit = 40)`，由 [App.OnStartup](App.md) 在 `--graph` 分支调用。

**内部结构**：

```text
Run(virtualPath, functionName, limit)
 ├─ profile = GameProfile.Kards()                 每次新建，不读命令行/配置
 ├─ ws = AssetAccess.OpenWorkspace(...)            重新打开（不复用 MainViewModel 的缓存）
 ├─ top = ws.Decompile(functionName)
 ├─ 若 top 里有 UberGraphNode → 取一段（第一个有 Label 的，否则段最大者）
 ├─ (nodes, connections) = GraphBuilder.Build(roots)
 ├─ 文本：资产 / 函数（+ 表达式条数）/ 说明 / 规模 / 节点行 / 连线行
 └─ finally: 整份文本覆盖写 %TEMP%\solstice-winter-ui\graph.txt
     return 0（成功）/ 1（异常）
```

输出样例（形态，非真实数据）：

```text
资产     : /Game/Blueprints/Logic/BP_Logic
函数     : AddCardStats  表达式 61 条
说明     : ubergraph 170 段，取「ObjectConst.GetDSession」
图       : 16 节点 / 15 连线

  (     0,     0)  [Call  ] CallFunc_Foo                      in=[exec:exec,arg0:int] out=[exec:exec]
  …

连线：
  CallFunc_Foo .exec -> Set Bar .exec  [exec]
```

**依赖**：`SolsticeWinter.Core.GameProfile`、`SolsticeWinter.App.Graph.AssetAccess`、`SolsticeWinter.App.Graph.GraphBuilder`、`Solstice.Kismet`（`KismetWorkspace.Decompile` / `ExpressionCount`、`UberGraphNode`、`KNode`）、`System.Text.StringBuilder` + `System.IO`。被 `App.OnStartup` 调用。

**取舍**：

- **写文件而不是 `stdout`**：`SolsticeWinter.App` 是 `WinExe`（`OutputType=WinExe`），进程没有控制台，`Console.WriteLine` 写进虚空。所以自检的产物只能是文件 + 退出码。代价：要看结果得去 `%TEMP%` 翻文件；`graph.txt` 每次运行被**覆盖**，没有历史。
- **自检只验证「数据形状」，不验证像素**：模型不支持读图，能自动判断的只有「节点/连线的数量与结构对不对」。真正的「UI 渲染了」由 [MainViewModel.UiLog](MainViewModel.md) 的 `ui.log` 行证明。代价：画错颜色、线画歪、节点重叠这些问题自检一律发现不了。
- **`limit` 默认 40，且对节点与连线**各自**截断**：一次自检的输出量可控，不必把 52 个节点全打出来。代价：看完整图要手动传 `limit`（命令行目前没暴露这个参数，`App.OnStartup` 只传两个参数）。
- **每次都重新打开资产**（不复用 `MainViewModel._workspaces`）：自检与 UI 的生命周期无关，能独立跑才是它的价值。代价：一次自检要重读 uasset + uexp + usmap，比在 UI 里点一下慢。

---

### `GraphProbe.Run(string virtualPath, string functionName, int limit = 40): int`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static int`。**不抛**：所有异常被内部 `catch` 转成 `graph.txt` 里的「失败：<类型>: <消息>」+ 堆栈，返回 1。`finally` 里无论如何都写文件（写失败也吞掉）。副作用：读三份资产字节、建 `%TEMP%/solstice-winter-ui/` 目录、覆盖写 `graph.txt`。

**输入输出**：

- `virtualPath`：索引里的虚拟路径（形如 `/Game/Blueprints/Logic/BP_Logic`），经 `AssetAccess.ExportPathFor` 映射到导出目录。
- `functionName`：函数短名。找不到函数时 `KismetWorkspace.Decompile` 抛 `InvalidOperationException("找不到函数: X")` → 返回 1。
- `limit`：节点行与连线行**各自**最多打印多少条，超出时补一行 `… 其余 N 个节点省略` / `… 其余 N 条连线省略`。`limit <= 0` 时打印 0 条明细（汇总行仍在）。
- 返回 `0` = 全流程成功（**不代表图正确**，只代表没抛异常）；`1` = 任一步抛异常。

文本字段来源：

| 行 | 来源 |
|---|---|
| `资产` | 入参 `virtualPath` 原样 |
| `函数` | 入参 + `ws.ExpressionCount(functionName)`（**当前字节码的表达式条数**） |
| `说明` | 仅 ubergraph 有：`"ubergraph <段数> 段，取「<Label>」"` |
| `图` | `nodes.Count` / `connections.Count` |
| 节点行 | `({Location.X:0},{Location.Y:0})  [Kind] Title  in=[名字:类型,…] out=[…]` |
| 连线行 | `<上游节点 Title> .<出脚 Title> -> <下游节点 Title> .<入脚 Title>`（exec 边追加 `[exec]`） |

**关键分支（伪代码）**：

```text
log = StringBuilder(); path = %TEMP%/solstice-winter-ui/graph.txt
try:
    profile = GameProfile.Kards()
    ws   = AssetAccess.OpenWorkspace(profile, virtualPath)     # 抛 → catch
    top  = ws.Decompile(functionName)                          # 抛 → catch
    roots = top; note = ""
    if top.OfType<UberGraphNode>().FirstOrDefault() is uber:    # 触发条件：函数是 ubergraph
        section = uber.Sections.FirstOrDefault(s => s.Label 非空)      # 优先「有名字的段」
                  ?? uber.Sections.OrderByDescending(s => s.Nodes.Count).FirstOrDefault()
        roots = section?.Nodes ?? []; note = $"ubergraph {uber.Sections.Count} 段，取「{section?.Label ?? "最大段"}」"
    (nodes, connections) = GraphBuilder.Build(roots)
    写汇总行；for node in nodes.Take(limit): 写节点行；超出补省略行
    for edge in connections.Take(limit): 反查两端节点 Title 后写连线行；超出补省略行
    return 0
catch e:
    log.AppendLine($"失败：{e.GetType().Name}: {e.Message}"); log.AppendLine(e.StackTrace); return 1
finally:
    try: Directory.CreateDirectory(dir); File.WriteAllText(path, log)     # 覆盖写
    catch: # 写不进去也没别的办法了
```

**边界与失败**：

- ⛔ **`Decompile` 对 ubergraph 返回的是 `[UberGraphNode, ReturnNode]` 这样的多元素列表**，不是单元素；所以判断必须用 `OfType<UberGraphNode>().FirstOrDefault()`。用 `top[0] is UberGraphNode` 之类写法会漏掉「头节点不是第一个」的情况。
- ⛔ ubergraph 只取**一段**：`BP_Logic` 实测 170 段，自检只看「第一个有 `Label` 的段」。`BP_Logic / ExecuteUbergraph_BP_Logic` 取到的是 `ObjectConst.GetDSession` 那段（6 节点 / 4 连线），不是整张事件图。
- ⛔ 连线的两端靠 `nodes.FirstOrDefault(n => n.Outputs.Contains(edge.Output))` **反查**：如果某个引脚所属节点没被加进 `nodes`（例如将来引入「只连不显」的引脚），这行会打成 `?`，不会报错。反查是 O(节点数) 每边，整体 O(节点数 × 边数)——当前规模（几十个）无感。
- ⛔ `limit` 无法从命令行调整：`App.OnStartup` 只传 `args[1]` / `args[2]`，`limit` 恒为默认 40。
- ⛔ 文本里 `Kind` 用 `-6` 宽度对齐、`Title` 用 `-32`：超过宽度的长标题会把列推歪（不影响解析，只影响观感）。
- ⛔ 自检不覆盖「图是否与字节一致」：`Decompile` 内部做过字节校验（退化时会退到块图），但 `Run` 不打印「是否结构化成功」（`DecompileDetailed` 的 `Structured` 字段在这里被丢掉了）。
- 节点数/连线数会随 `GraphBuilder` 的布局与投影规则变化而变化，不是资产的不变量。值可能随迭代调整。

**调用者与被调用者**：被 `App.OnStartup`（`--graph` 分支）调用；调用 `GameProfile.Kards`、`AssetAccess.OpenWorkspace`、`KismetWorkspace.Decompile`、`KismetWorkspace.ExpressionCount`、`GraphBuilder.Build`。
