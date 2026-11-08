# MainViewModel.load — 函数列表与出图（含 `UiLog`）

> 本文件是 [MainViewModel](MainViewModel.md) 的拆分（原 md 超过 400 行）：模块职责、取舍、索引加载与资产过滤见主文件；这里只有「选中资产 → 函数列表 → 反编译出图」这条链的三个方法，以及同文件内的 `UiLog`。

链路：`SelectedAsset` setter → `LoadFunctionsAsync` → 自动挑函数（写 `SelectedFunction`）→ `LoadGraphAsync` → `GetWorkspace` → `GraphBuilder.Build` → 填 `Nodes` / `Connections` + 写 `UiLog`。

---

### `LoadFunctionsAsync(AssetItem asset): Task`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private async Task`。清空 `Functions` / `Nodes` / `Connections`，按资产填函数列表，必要时自动挑一个函数（赋 `SelectedFunction`，进而触发出图）。不抛：异常转 `Status`。

**输入输出**：

- 入参：一个 `AssetItem`（来自 `Assets` 集合）。
- 先 `Functions.Clear()` + `Nodes.Clear()` + `Connections.Clear()`（画布立刻空掉）。
- `_index.Find(asset.VirtualPath)` → `AssetHeader?`；`Functions` 只收 `HasBytecode == true` 的，投影成 `FunctionItem(Name, BytecodeSize, Params.Count)`。
- `Status = "<虚拟路径>：<N> 个带字节码的函数"`。
- 自动挑函数（仅当 `SelectedFunction is null` 且列表非空）：`Size` 落在 `(250, 1500)` **开区间**里最大的那个；一个都没有则 `Functions[0]`。
- 方法体最后 `await Task.CompletedTask`——**没有真正的异步工作**，全部在 UI 线程同步完成。

**关键分支（伪代码）**：

```text
Functions.Clear(); Nodes.Clear(); Connections.Clear()        # 先清，避免旧图配新函数名
try:
    Status = $"打开 {asset.VirtualPath}…"
    header = _index.Find(asset.VirtualPath)                  # 字典命中；_index 为 null 时这里会 NRE 被 catch
    functions = header?.Functions.Where(HasBytecode) ?? []
    foreach fn: Functions.Add(FunctionItem(fn.Name, fn.BytecodeSize, fn.Params.Count))
    Status = $"…：{functions.Count} 个带字节码的函数"
    if SelectedFunction == null and Functions.Count > 0:     # 触发条件：切资产后选中被 Clear 清掉
        pick = Functions.Where(f => f.Size > 250 and f.Size < 1500)
                        .OrderByDescending(f => f.Size).FirstOrDefault()
        SelectedFunction = pick ?? Functions[0]               # 区间内没有 → 退到第一个
    await Task.CompletedTask                                  # 占位，无后台线程
catch e:
    Status = $"读取函数列表失败：{e.GetType().Name}: {e.Message}"
```

**边界与失败**：

- ⛔ 名字叫 `Async` 但**没有 `await` 真实工作**：类注释说「所有重活放线程池」，这里并不成立。索引查询是字典命中，所以目前不卡；一旦这里加上磁盘操作就会卡 UI。
- ⛔ `_index` 为 `null` 时 `_index!.Find(...)` 抛 `NullReferenceException`，被 `catch` 变成 `Status = "读取函数列表失败：NullReferenceException: …"`。
- ⛔ 自动挑选的阈值是开区间 `(250, 1500)`：`Size == 250` 或 `== 1500` 的函数**不会**被自动选中（会退到 `Functions[0]`）。值可能随迭代调整。
- ⛔ `Functions.Clear()` 写了两遍（第 162 行与第 173 行），第二遍是多余的历史残留，无副作用。
- ⛔ 切资产时不清 `SelectedFunction` 以外的东西：`_workspaces` 缓存保留，`Diagnostics` 保留上一张图的信息直到新图出来。

**调用者与被调用者**：被 `SelectedAsset` 的 setter（含 `AutoSelect` 间接调用）以「火并忘」方式调用；调用 `SymbolIndex.Find`、写 `SelectedFunction`（其 setter 调 `LoadGraphAsync`）。

---

### `LoadGraphAsync(FunctionItem function): Task`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private async Task`。在后台线程打开资产 + 反编译 + 投影成图，回 UI 线程填 `Nodes` / `Connections` 并写 `Status` / `Diagnostics` / `ui.log`。不抛：异常转 `Status`。用 `_loadVersion` 丢弃过期结果。

**输入输出**：

- 入参：一个 `FunctionItem`。
- 版本号：`var version = ++_loadVersion`（每次调用自增，全局唯一）。
- 后台（`Task.Run`）：`GetWorkspace(asset)` → `ws.Decompile(function.Name)` → 若是 ubergraph，取段 → `GraphBuilder.Build(roots)` → 返回 `(nodes, connections, note)`。
- ubergraph 取段规则：`top.OfType<UberGraphNode>().FirstOrDefault()`；段选 `Sections.FirstOrDefault(s => !string.IsNullOrEmpty(s.Label))`，否则 `OrderByDescending(s => s.Nodes.Count).First()`；`note = "ubergraph <N> 段，显示第 1 段（<section.Title>）"`。
- 回到 UI：`version != _loadVersion` → 静默丢弃（不填集合、不写状态）。
- 成功 → `Status = "<函数名>：<节点数> 节点 / <连线数> 连线"`（有 `note` 时追加 `（<note>）`），`Diagnostics = "函数 <名>  <字节> 字节  <参数数> 参数  图 <N> 节点 / <M> 连线"`，`UiLog.Write("已渲染 <函数名>: <N> 节点 / <M> 连线")`。
- 失败（版本未过期）→ `Status = "反编译失败：<类型>: <消息>"`，并同文本写一行 `UiLog.Write("反编译失败：<函数名>: <类型>: <消息>")`。

**关键分支（伪代码）**：

```text
version = ++_loadVersion
asset = _selectedAsset
if asset == null: return                                  # 没有资产（例如刚 Clear 完）直接放弃
try:
    Status = $"反编译 {function.Name}…"
    (nodes, connections, note) = await Task.Run(() =>
        ws   = GetWorkspace(asset)                        # 可能首次读 3 份字节，见 AssetAccess
        top  = ws.Decompile(function.Name)                # 返回 [UberGraphNode, ReturnNode] 之类
        if top.OfType<UberGraphNode>().FirstOrDefault() is uber:   # 触发条件：这是 ubergraph
            section = uber.Sections.First(s => s.Label 非空) ?? 段最大者
            roots = section?.Nodes ?? []
            note  = $"ubergraph {uber.Sections.Count} 段，显示第 1 段（{section?.Title}）"
        else: roots = top
        return GraphBuilder.Build(roots) + note)
    if version != _loadVersion: return                    # 期间又点了别的函数 → 丢弃
    Nodes.Clear(); Connections.Clear(); 逐条 Add
    Status/Diagnostics/UiLog.Write(...)
catch e:
    if version != _loadVersion: return                    # 过期失败也不覆盖新状态
    Status = $"反编译失败：{e.GetType().Name}: {e.Message}"
    UiLog.Write($"反编译失败：{function.Name}: {e.GetType().Name}: {e.Message}")   # 状态条会互相覆盖，日志留证
```

**边界与失败**：

- ⛔ ubergraph 只显示**一段**：一个 ubergraph 是 N 段（实测 `BP_Logic` 170 段），画布只画「第一个有 `Label` 的段」，没有段切换 UI。`note` 文案写「显示第 1 段」而实际是「第一个有标签的段」，措辞与行为不完全一致。
- ⛔ `Decompile` 对 ubergraph 返回的是 `[UberGraphNode, ReturnNode]` 这样的**多元素列表**，不是单个节点——所以判断必须用 `OfType<UberGraphNode>().FirstOrDefault()`，用 `[0]` 或 `is UberGraphNode` 判断都会漏。
- ⛔ 模型是「每资产一个 `KismetWorkspace`」且缓存在 `Dictionary` 里：`GetWorkspace` 在后台线程执行，快速连点两个不同资产时两个 `Task.Run` 可能**并发写同一个 `Dictionary`**，`Dictionary` 非线程安全（可能抛或损坏内部结构）。目前靠 `_loadVersion` 丢掉结果，但写入本身没有被串行化。
- ⛔ `Task.Run` 里没有任何取消机制：点 10 个函数就会并行反编译 10 次（每次几百毫秒），只有最后一个的结果会被采用。
- ⛔ `Status` 的节点/连线数是 `GraphBuilder.Build` 的产物，与「界面上真画出来的东西」不一定一一对应（例如不透明节点零引脚，见 [GraphBuilder](Graph.GraphBuilder.md) 的边界）。
- 值可能随迭代调整（节点数阈值、`note` 文案、`Diagnostics` 格式都可能变）。

**调用者与被调用者**：被 `SelectedFunction` 的 setter 以「火并忘」方式调用；调用 `GetWorkspace`、`KismetWorkspace.Decompile`、`GraphBuilder.Build`、`UiLog.Write`。

---

### `GetWorkspace(AssetItem asset): KismetWorkspace`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。按虚拟路径缓存 `KismetWorkspace`；未命中则 `AssetAccess.OpenWorkspace(_profile, asset.VirtualPath)` 并写入缓存。副作用：读磁盘（首次）、建 `%TEMP%/solstice-winter-ui/<资产名>/` 工作目录、写缓存字典。

**输入输出**：

- 命中缓存 → 直接返回同一个实例（含它内部的「编辑前字节码快照」状态）。
- 未命中 → 打开成功则缓存并返回；打开失败（文件不存在、没有 usmap、uexp 缺失）→ 异常向 `LoadGraphAsync` 的 `catch` 冒泡，`Status` 变「反编译失败：…」。

**关键分支（伪代码）**：

```text
if _workspaces.TryGetValue(asset.VirtualPath, out cached): return cached
ws = AssetAccess.OpenWorkspace(_profile, asset.VirtualPath)   # 读 uasset/uexp/usmap 三份字节
_workspaces[asset.VirtualPath] = ws
return ws
```

**边界与失败**：

- ⛔ 无失效机制：磁盘上的资产被替换/重新导出后，进程内仍用旧字节，直到重启。
- ⛔ 非线程安全（见 `LoadGraphAsync` 边界）；也没有上限，点得越多占用越大（每个 workspace 持有整份 uasset + usmap）。
- ⛔ 缓存键是虚拟路径字符串，大小写敏感：索引里同时存在 `/Game/A/B` 与 `/Game/A/b` 时会各开一个 workspace（实测数据里没有这种情况）。

**调用者与被调用者**：被 `LoadGraphAsync` 在后台线程调用；调用 `AssetAccess.OpenWorkspace`。

---

### `UiLog.Write(string message): void`

**状态**：✅ 已实现

**签名与契约**：`internal static class UiLog` 的唯一成员。向 `%TEMP%\solstice-winter-ui\ui.log` **追加**一行 `[HH:mm:ss.fff] <message>`。吞掉所有异常。不返回值、不阻塞。

**输入输出**：`message` 是任意文本；实测写入的形态是 `已渲染 DrawTopCardFromDeck: 52 节点 / 46 连线`。文件不存在时自动建目录与文件；从不清理、无轮转。

**关键分支（伪代码）**：

```text
try:
    Directory.CreateDirectory(dirname(path))       # 已存在不抛
    AppendAllText(path, $"[{Now:HH:mm:ss.fff}] {message}\n")
catch:
    # 日志失败不影响 UI
```

**边界与失败**：

- ⛔ 这是「UI 真的渲染了」的**唯一机器可读证据**（模型不支持读图，测试也只能读这个文件）。它证明的是「ViewModel 把 N 个节点/M 条连线交给了绑定」，**不能**证明像素画对了。
- ⛔ 每次出图写一行，连点会写很多行；判断「最新状态」要取最后一行。
- ⛔ 与 `crash.log` 是同一个目录下的两个文件，但两者互不引用。

**调用者与被调用者**：被 `LoadGraphAsync` 调用；不调用其它函数。

