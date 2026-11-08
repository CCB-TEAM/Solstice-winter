# MainViewModel — 索引 → 资产 → 函数 → 图

**职责一句话**：把 `SymbolIndex`（磁盘上的 JSON 缓存）与 `KismetWorkspace`（反编译）串成 UI 能绑定的四条集合，并在启动时自动打开一张有内容的图。

**对外入口**：`InitializeAsync(string?)`；六组可绑定属性（`SearchText` / `SelectedAsset` / `SelectedFunction` / `SelectedNode` / `Status` / `Diagnostics`）；四条集合（`Assets` / `Functions` / `Nodes` / `Connections`）；两个行记录类型 `AssetItem` / `FunctionItem`；同文件内的 `UiLog`。

> 本模块的 md 按职责拆成两份：本文件（构造 / 索引加载 / 资产过滤 / 属性 / 自动选中）、[MainViewModel.load](MainViewModel.load.md)（函数列表 / 出图 / workspace 缓存 / `UiLog`）。

**内部结构**：

```text
MainViewModel
 ├─ FindIndex(indexPath) : string                     索引在哪：显式 → exe/.cache → cwd/.cache → 仓库根/.cache
 ├─ _profile   : GameProfile.Kards()                构造时读一次，不随索引变化
 ├─ _index     : SymbolIndex?                         InitializeAsync 里 Load（路径来自 FindIndex）
 ├─ _loadVersion: int                                 切函数时丢弃过期反编译结果
 ├─ _workspaces: Dictionary<虚拟路径, KismetWorkspace> 每资产一个，永不失效
 │
 ├─ Assets ──────── RefreshAssets()      ← 索引加载完 / SearchText 变化
 ├─ Functions ───── LoadFunctionsAsync() ← SelectedAsset 变化
 ├─ Nodes / Connections ── LoadGraphAsync() ← SelectedFunction 变化
 └─ Status / Diagnostics / UiLog            给人看的两个通道
```

**依赖**：`SolsticeWinter.Core.GameProfile`（`Kards()` → `ExportDirectory` / `UsmapPath`）、`SolsticeWinter.Index.SymbolIndex`（`Load` / `Find` / `Assets` / `FunctionsByName` / `Describe`）、`SolsticeWinter.App.Graph.AssetAccess`（`OpenWorkspace`）、`SolsticeWinter.App.Graph.GraphBuilder`（`Build`）、`Solstice.Kismet`（`KismetWorkspace.Decompile`、`UberGraphNode`、`KNode`）。被 `MainWindow` 创建并绑定。

**取舍**：

- **ViewModel 自己 `new GameProfile.Kards()`**：现在只有一个游戏，把 profile 提成构造参数只会让 `MainWindow` 多一行转发。代价：换游戏要改 `MainViewModel` 构造函数（`GameProfile.Kards()` 读的是硬编码的本机路径，见 [GameProfile](../../../src/SolsticeWinter.Core/GameProfile.cs)）。
- **索引路径用四级回退（`FindIndex`）而不是写死 `<exe>/.cache`**：双击 `bin\Debug\net10.0-windows\SolsticeWinter.App.exe` 时 `AppContext.BaseDirectory` 在 `bin` 深处，索引却在仓库根的 `.cache\` 里，写死就等于「必须手写命令行参数才能跑」。否决了「把索引复制到输出目录」——7 MB 的构建产物没必要跟着 exe 走。代价：`FindIndex` 会从 exe 目录**往上最多爬 8 层**找 `Solstice-winter.slnx`，目录结构变了（或 exe 被拷到别处）就要靠显式参数。
- **重活在 `Task.Run` 里，结果回 UI 线程填 `ObservableCollection`**：`ObservableCollection` 不能在后台线程改（WPF 会抛 `NotSupportedException`），所以只有「反编译」在后台，填集合在 `await` 之后。代价：`LoadFunctionsAsync` 实际上**没有**后台工作（见该小节），类注释里「所有重活都放线程池」这句只对 `LoadGraphAsync` 成立。
- **用 `_loadVersion` 而不是取消令牌**：反编译是一次性的同步调用，`CancellationToken` 传不进 `KismetWorkspace.Decompile`，能做的只有「回来时发现版本不对就丢掉」。代价：快速连点会白做几次反编译（CPU 浪费），但 UI 不会被旧图覆盖。
- **`_workspaces` 缓存永不失效**：同一资产切函数时不必重复读三份字节（uasset + uexp + usmap）。代价：改了磁盘上的导出文件后必须重启进程才能看到；缓存也是无界增长（点过多少个资产就留多少个 `KismetWorkspace`）。
- **`Status` 与 `Diagnostics` 两条文本通道**：`Status` 是「刚才发生了什么」（一句话、会互相覆盖），`Diagnostics` 是「现在这张图是什么」（规模、字节数）。否决了「一条消息队列」——状态条上滚动历史没人看。代价：两条都可能过时，`Diagnostics` 在索引加载后会被图的信息覆盖。
- **索引加载与出图的成败都另写一份 `ui.log`**：状态条只留最后一条，翻旧账只能看日志；`ui.log` 因此同时承担「成功证据」与「失败证据」。代价：日志行会重复（同一信息在 `Status` 与 `ui.log` 各一份），两处的措辞要各自维护。
- **自动选中写死 `BP_Logic`**：第一眼必须是「有内容的图」，空画布看不出这工具长什么样。代价：这是本机样本资产的硬编码，见 `AutoSelect` 的边界。

---

### `AssetItem(string VirtualPath, string? ClassName, int FunctionCount)`

**状态**：✅ 已实现

**签名与契约**：`sealed record`，三个位置参数即三个只读属性；唯一的行为成员是 `Display`（计算属性，无副作用）。

**输入输出**：

- `VirtualPath`：索引里的资产虚拟路径，形如 `/Game/Blueprints/Logic/BP_Logic`（无扩展名、无对象后缀）。
- `ClassName`：`AssetHeader.ClassName` 原样搬运，**当前 UI 不显示**（`Display` 里没用）。
- `FunctionCount`：`RefreshAssets` 统计的「带字节码的函数数」，不是 `AssetHeader.Functions.Count`（后者含无字节码的委托签名）。
- `Display` → `"<最后一段路径>   (<FunctionCount>)"`，例如 `BP_Logic   (181)`。路径里没有 `/` 时 `LastIndexOf` 返回 `-1`，切片 `[0..]` 得到整串，不会抛。

**关键分支（伪代码）**：

```text
Display:
    cut = VirtualPath.LastIndexOf('/')        # 没有 '/' 时 cut = -1
    return VirtualPath[cut+1..] + "   (" + FunctionCount + ")"
```

**边界与失败**：`VirtualPath` 为 `null` 时 `Display` 抛 `NullReferenceException`（record 不校验）；`FunctionCount` 为 0 的行不会出现（`RefreshAssets` 先按 `HasBytecode` 过滤）。

**调用者与被调用者**：由 `MainViewModel.RefreshAssets` 构造；被 `AutoSelect`（读 `VirtualPath` / `FunctionCount`）、`LoadFunctionsAsync` / `LoadGraphAsync` / `GetWorkspace`（读 `VirtualPath`）、`MainWindow.xaml` 的两个 `TextBlock`（读 `VirtualPath` / `Name` 的是 `SelectedAsset.VirtualPath`）与 `ListBox` 的 `DisplayMemberPath="Display"` 使用。不调用其它函数。

---

### `MainViewModel()`

**状态**：✅ 已实现

**签名与契约**：无参构造。做两件事：`_profile = GameProfile.Kards()`、`Status = "启动中…"`。不做 I/O（`GameProfile.Kards` 只探测目录是否存在、读 FModel 配置找 AES key，不打开资产）。不抛（`GameProfile.Kards` 内部对 `JsonException` 有兜底）。

**输入输出**：无入参。产出对象本身：四条空集合 + `Diagnostics = "未加载"` + `Status = "启动中…"`。

**关键分支（伪代码）**：

```text
_profile = GameProfile.Kards()      # 本机路径写死在 GameProfile 里；找不到导出目录时 ExportDirectory = null
Status   = "启动中…"
# 集合与 _index 保持空：一切加载都在 InitializeAsync
```

**边界与失败**：⛔ `GameProfile.Kards()` 若返回 `ExportDirectory == null`（本机没有 `D:\devenv\fmodel\Output\Exports\kards\Content`），构造函数仍然成功，失败推迟到 `AssetAccess.ExportPathFor` 抛 `InvalidOperationException("profile 里没有导出目录")`——即索引能加载、点函数才炸。

**调用者与被调用者**：被 `MainWindow` 构造函数调用；调用 `GameProfile.Kards`。

---

### `SearchText` / `SelectedAsset` / `SelectedFunction` / `SelectedNode` / `Status` / `Diagnostics` / 四条集合

**状态**：✅ 已实现

**签名与契约**：六个带 `INotifyPropertyChanged` 的属性 + 四个只读 `ObservableCollection`。其中三个选择型属性的 setter **带副作用**：`SelectedAsset` → 触发 `LoadFunctionsAsync`，`SelectedFunction` → 触发 `LoadGraphAsync`，`SearchText` → 触发 `RefreshAssets`。`SelectedNode` / `Status` / `Diagnostics` 是纯数据。所有 setter 都先赋值再 `OnPropertyChanged`，且**不做相等判断**（重复赋同一个值也会通知）。

**输入输出**：

| 成员 | 类型 | 谁写 | 谁读 |
|---|---|---|---|
| `SearchText` | `string`（默认 `""`） | `MainWindow.xaml` 的 `TextBox`（`UpdateSourceTrigger=PropertyChanged`，每敲一个字符就写） | `RefreshAssets` |
| `SelectedAsset` | `AssetItem?` | `ListBox.SelectedItem` 双向绑定 + `AutoSelect` | `LoadFunctionsAsync` / `LoadGraphAsync` / 标题行 |
| `SelectedFunction` | `FunctionItem?` | `ListBox.SelectedItem` + `LoadFunctionsAsync` 的自动挑选 | `LoadGraphAsync` / 标题行 |
| `SelectedNode` | `GraphNodeVm?` | 只有 `NodeCanvas`（回退视图）会写；Nodify 视图**不写** | 详情面板 |
| `Status` | `string` | 各加载步骤 | 状态条左 |
| `Diagnostics` | `string`（默认 `"未加载"`） | 索引加载 + 每次出图 | 状态条右 |
| `Assets` / `Functions` / `Nodes` / `Connections` | `ObservableCollection<T>` | 只在本类内部 `Clear()` + `Add` | `ListBox.ItemsSource` / `NodifyEditor.ItemsSource` / `NodifyEditor.Connections` |

- `SearchText` 在索引加载前变化：`RefreshAssets` 里 `_index is null` 直接返回，安全。
- `SelectedAsset` / `SelectedFunction` 设为 `null`（例如 `ListBox` 因 `Clear()` 回写 null）→ **不触发**加载（`if (value is not null)`）。

**关键分支（伪代码）**：

```text
set SearchText v:      _searchText = v; notify; RefreshAssets()          # 索引未加载则空转
set SelectedAsset v:   _selectedAsset = v; notify
                       if v != null: _ = LoadFunctionsAsync(v)           # 火并忘，异常在方法内被 catch
set SelectedFunction v:_selectedFunction = v; notify
                       if v != null: _ = LoadGraphAsync(v)               # 同上
set SelectedNode v:    _selectedNode = v; notify
set Status/Diagnostics:v; notify
```

**边界与失败**：

- ⛔ `SelectedFunction` 的「清空 → 自动挑选」依赖 WPF 的时序：`LoadFunctionsAsync` 先 `Functions.Clear()`，`ListBox` 会同步把 `SelectedItem = null` 回写进 `SelectedFunction`，于是后面的 `if (SelectedFunction is null)` 成立、自动挑第一个中等函数。**若绑定尚未建立**（窗口还没渲染完）这条回写不会发生，第二次切资产就不会自动选函数——此时要靠用户手点。当前实测的首次启动路径是成立的。
- ⛔ `SearchText` 每敲一个字符就重填一次 `Assets`（`Clear` + 最多 400 次 `Add`），列表会闪一下；没有防抖。
- ⛔ 两个选择属性都是「火并忘」的 `async void` 式调用（`_ = Task`），调用方拿不到完成信号，也没有失败回调——失败只体现在 `Status` 文本里。

**调用者与被调用者**：setter 被 WPF 绑定与 `AutoSelect` / `LoadFunctionsAsync` 写；getter 被 `MainWindow.xaml`、`LoadGraphAsync`（读 `_selectedAsset`）、`RefreshAssets`（读 `SearchText`）读。`OnPropertyChanged` 是它们共同的被调用者。

---

### `FindIndex(string? explicitPath): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private static`。决定「索引文件在哪」，返回一个**绝对路径**（即使该文件不存在）。会做 `File.Exists` 探测与向上遍历目录（最多 8 层），属于轻量磁盘 I/O。抛：`explicitPath` 非法时 `Path.GetFullPath` 抛（`ArgumentException` / `NotSupportedException` / `PathTooLongException`），由 `InitializeAsync` 的 `catch` 接住。

**输入输出**：

- `explicitPath` 非空白 → 直接 `Path.GetFullPath(explicitPath)` 返回，**不检查是否存在**。
- 否则按顺序攒候选：① `<exe 目录>\.cache\index.json`；② `<当前工作目录>\.cache\index.json`；③ 从 `<exe 目录>` 往上最多 8 层，遇到含 `Solstice-winter.slnx` 的目录就追加 `<该目录>\.cache\index.json` 并停止。
- 返回「第一个存在的候选」；一个都不存在时返回 `candidates[0]`（即 `<exe 目录>\.cache\index.json`），用于错误提示。

**关键分支（伪代码）**：

```text
if not string.IsNullOrWhiteSpace(explicitPath):
    return Path.GetFullPath(explicitPath)              # 触发条件：命令行给了路径；不验证存在性
candidates = [ <exe>/.cache/index.json, <cwd>/.cache/index.json ]
dir = DirectoryInfo(<exe 目录>)
for i in 0..7 while dir != null:                        # 触发条件：往上找到含 Solstice-winter.slnx 的仓库根
    if File.Exists(Path.Combine(dir, "Solstice-winter.slnx")):
        candidates.Add(Path.Combine(dir, ".cache", "index.json")); break
    dir = dir.Parent
return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? candidates[0]
```

**边界与失败**：

- ⛔ 显式参数**不校验存在性**：`SolsticeWinter.App.exe --graph /Game/A`（少一个参数）会把 `--graph` 当索引路径，`Path.GetFullPath("--graph")` 得到 `<cwd>\--graph`，随后报「找不到索引」——不会崩，但错误信息与真实原因（参数写错）无关。
- ⛔ 向上最多 8 层：`bin\Debug\net10.0-windows` 到仓库根正好 3 层，够用；exe 被拷到更深的目录、或仓库里没有 `Solstice-winter.slnx` 时，只剩前两个候选。
- ⛔ 只认 `Solstice-winter.slnx` 这一个标记文件：换名或改回 `.sln` 后回退链退化成「exe 目录 / 当前目录」。
- ⛔ 兜底返回 `candidates[0]`：错误提示永远指向 exe 目录下的 `.cache\index.json`，即使真正该看的是仓库根那个。
- ⛔ 不缓存结果：每次 `InitializeAsync` 都重新探测（一次调用，无实际开销）。

**调用者与被调用者**：被 `InitializeAsync` 调用；调用 `Path.GetFullPath`、`File.Exists`、`DirectoryInfo.Parent`。

---

### `InitializeAsync(string? indexPath = null): Task`

**状态**：✅ 已实现

**签名与契约**：`public async Task`。加载索引 → 填 `Assets` → 写 `Status` / `Diagnostics` / `ui.log` → 触发自动选中。**不抛**：所有异常转成 `Status` 文本与一行 `ui.log`。副作用：`_index` 被赋值、`Assets` 被重填、`SelectedAsset` 可能被设置（进而连锁触发函数列表与图的反编译）。

**输入输出**：

- `indexPath` 为 `null` → 交给 `FindIndex` 走四级回退（exe 目录 → 当前目录 → 仓库根），不再固定为 `<exe 目录>\.cache\index.json`。
- 文件不存在 → `Status = "找不到索引 <path>——先跑 `solstice-winter index build`"` + 一行 `ui.log`（带 `BaseDirectory` 与 `CWD`，用来定位四级回退为什么没命中），**直接返回**，不抛、不弹窗。
- 文件存在 → 后台线程 `SymbolIndex.Load(path)`（含版本校验与 `RebuildLookups`），用 `Stopwatch` 计毫秒。
- 成功 → 先 `UiLog.Write("索引已加载 <资产数> 资产，来自 <path>")`，再 `_index = index` → `RefreshAssets()` → `Status = "索引已加载：<资产数> 资产 / <唯一函数名数> 唯一函数名（<ms> ms）"` → `Diagnostics = index.Describe()`（7 行规模摘要）→ `AutoSelect()`。
- 失败（JSON 坏、版本不符、路径非法）→ `Status = "加载索引失败：<异常类型>: <消息>"` + 同文本写 `ui.log`。

**关键分支（伪代码）**：

```text
try:
    path = FindIndex(indexPath)                      # 显式参数 → exe/.cache → cwd/.cache → 仓库根/.cache
    if not File.Exists(path):                        # 触发条件：索引还没建
        Status = "找不到索引 …"
        UiLog.Write($"找不到索引：{path}（BaseDirectory=…，CWD=…）")
        return
    Status = "加载索引…"
    (index, elapsed) = await Task.Run(load + stopwatch)
    UiLog.Write($"索引已加载 {index.Assets.Count} 资产，来自 {path}")   # 写在赋值之前
    _index = index
    RefreshAssets()                                   # 用当前 SearchText（可能非空）过滤
    Status = $"索引已加载：{index.Assets.Count} 资产 / {index.FunctionsByName.Count} 唯一函数名（{elapsed} ms）"
    Diagnostics = index.Describe()
    AutoSelect()                                      # 设置 SelectedAsset → 连锁 LoadFunctionsAsync
catch e:                                              # 版本闸门 / 解析异常 / 非法路径
    Status = $"加载索引失败：{e.GetType().Name}: {e.Message}"
    UiLog.Write($"加载索引失败：{e.GetType().Name}: {e.Message}")
```

**边界与失败**：

- ⛔ 版本闸门在 `SymbolIndex.Load` 里：索引 `Version` 与 `FormatVersion` 不符会抛 `InvalidOperationException`，这里只把它变成一行 `Status` + 一行日志，UI 不会提示「该重建」的具体命令。
- ⛔ `Status` 里的资产数是 `Assets.Count`（受 `RefreshAssets` 的 400 条上限与 `SearchText` 过滤影响），**不是**索引里的资产总数；唯一函数名数才是全量。
- ⛔ `ui.log` 的「索引已加载」行写在 `_index = index` **之前**：所以日志证明的是「索引文件读成功了」，不代表后面的 `RefreshAssets` / `AutoSelect` / 出图也成功。
- ⛔ 全流程无进度反馈：7 MB 索引加载约 300 ms（见 INDEX 实测），期间 UI 只显示「加载索引…」。

**调用者与被调用者**：被 `MainWindow` 的 `Loaded` 事件调用（`await _viewModel.InitializeAsync(indexPath)`）；调用 `FindIndex`、`SymbolIndex.Load`、`SymbolIndex.Describe`、`RefreshAssets`、`AutoSelect`、`UiLog.Write`。

---

### `AutoSelect(): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。挑一个资产并赋给 `SelectedAsset`（从而连锁触发函数列表与出图）。无返回值、不抛。**只在 `InitializeAsync` 末尾调用一次**。

**输入输出**：

- `_index is null || Assets.Count == 0` → 直接返回（不改选中）。
- 否则：`Assets.FirstOrDefault(a => a.VirtualPath.EndsWith("/BP_Logic", Ordinal))` 优先；没有则 `Assets.OrderByDescending(a => a.FunctionCount).First()`。
- 结果写进 `SelectedAsset` → 触发 `LoadFunctionsAsync` → 自动挑函数 → 触发 `LoadGraphAsync`。

**关键分支（伪代码）**：

```text
if _index == null or Assets.Count == 0: return        # 索引没加载或过滤后为空
preferred = Assets.FirstOrDefault(a => a.VirtualPath.EndsWith("/BP_Logic"))   # 大小写敏感
preferred ??= Assets.OrderByDescending(a => a.FunctionCount).First()          # 兜底：字节码函数最多
SelectedAsset = preferred
```

**边界与失败**：

- ⛔ `BP_Logic` 是**本机样本资产的硬编码**：换游戏或换导出目录后，这段逻辑等价于「取函数最多的资产」。
- ⛔ `EndsWith("/BP_Logic")` 用 `Ordinal`（区分大小写），且是「路径以 `/BP_Logic` 结尾」——`/Game/X/BP_Logic2` 不会命中，`/Game/X/bp_logic` 也不会。
- ⛔ 兜底排序用的 `FunctionCount` 是「带字节码的函数数」，不是节点数；`BP_Logic` 之外函数最多的资产不一定是图最漂亮的。
- 值可能随迭代调整（当前阈值/候选见上）。

**调用者与被调用者**：被 `InitializeAsync` 调用；调用 `RefreshAssets` 产出的 `Assets`，写 `SelectedAsset`（其 setter 调 `LoadFunctionsAsync`）。

---

### `RefreshAssets(): void`

**状态**：✅ 已实现

**签名与契约**：`private`。把 `_index.Assets` 过滤、排序、截断、投影成 `AssetItem` 并重填 `Assets` 集合。无返回值、不抛（`_index` 为 `null` 时直接返回）。**不碰 `Functions` / `Nodes` / `Connections`**。

**输入输出**：

- 过滤条件 1：资产至少有一个 `HasBytecode` 的函数（纯数据资产、接口资产不上列表）。
- 过滤条件 2：`SearchText.Trim()` 为空 → 不过滤；否则 `VirtualPath.Contains(filter, OrdinalIgnoreCase)`（**只搜路径，不搜函数名**）。
- 排序：`VirtualPath` 的 `Ordinal` 升序（区分大小写，所以 `/Game/A` 排在 `/Game/a` 前面）。
- 截断：`Take(400)`。
- 投影：`new AssetItem(a.VirtualPath, a.ClassName, a.Functions.Count(f => f.HasBytecode))`。
- 结果：`Assets.Clear()` 后逐条 `Add`（每次 `Add` 都会让 `ListBox` 更新一次）。

**关键分支（伪代码）**：

```text
if _index == null: return
filter = SearchText.Trim()
rows = _index.Assets
        .Where(a => a.Functions.Any(f => f.HasBytecode))              # 至少要有一个能上画布的函数
        .Where(a => filter.Length == 0 || a.VirtualPath.Contains(filter, OrdinalIgnoreCase))
        .OrderBy(a => a.VirtualPath, Ordinal)
        .Take(400)                                                    # 硬上限，防止 ListBox 卡死
        .Select(a => new AssetItem(a.VirtualPath, a.ClassName, count(HasBytecode)))
Assets.Clear(); foreach row: Assets.Add(row)
```

**边界与失败**：

- ⛔ 400 条硬上限：实测索引有 2137 个资产，**大部分资产在默认空搜索下根本看不到**；要靠 `SearchText` 过滤才能访问后面的。截断发生在排序之后，所以看到的是路径字典序前 400 个。
- ⛔ 只按路径搜，不按函数名搜：想「按函数名找资产」现在没有入口。
- ⛔ 重填时 `Assets.Clear()` 会把 `SelectedAsset` 打回 `null`（`ListBox` 回写），但因为 `if (value is not null)` 的守卫，清空不会触发加载——所以搜完字之后旧图还留在画布上，与左栏选中态不一致。
- 值可能随迭代调整（400 这个上限、过滤条件都可能变）。

**调用者与被调用者**：被 `InitializeAsync` 与 `SearchText` 的 setter 调用；读 `_index.Assets`、`SearchText`；产出 `AssetItem`（`Assets` 集合的元素）。

---

### `FunctionItem(string Name, int Size, int Params)`

**状态**：✅ 已实现

**签名与契约**：`sealed record`，三个位置参数；唯一行为成员 `Display`（计算属性）。

**输入输出**：

- `Name`：函数短名，直接来自 `FunctionInfo.Name`。
- `Size`：`FunctionInfo.BytecodeSize`（**声明的字节码字节数**，不是反编译后的节点数，也不是表达式条数）。
- `Params`：`FunctionInfo.Params.Count`（只算 `CPF_Parm`，不含局部变量）。
- `Display` → `"<Name>   (<Size> B)"`，例如 `DrawTopCardFromDeck   (612 B)`。

**关键分支（伪代码）**：

```text
Display:
    return Name + "   (" + Size + " B)"
```

**边界与失败**：`Size` 为 0 的函数不会进入列表（列表只收 `HasBytecode == true`，而它由 `ScriptBytecode.Length > 0` 推出）；委托签名函数（`*__DelegateSignature`）会出现，`Size` 通常是 3。

**调用者与被调用者**：由 `MainViewModel.LoadFunctionsAsync` 构造；被 `AutoSelect` 之后的自动挑选（读 `Size`）、`LoadGraphAsync`（读 `Name` / `Size` / `Params`）、`MainWindow.xaml`（`DisplayMemberPath="Display"`、标题行 `SelectedFunction.Name`）使用。不调用其它函数。
