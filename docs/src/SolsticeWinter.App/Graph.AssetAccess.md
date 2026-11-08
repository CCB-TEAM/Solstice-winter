# Graph.AssetAccess — 虚拟路径 → 磁盘文件 → `KismetWorkspace`

**职责一句话**：把索引里的虚拟路径（`/Game/...`）翻译成 FModel 导出目录里的 `.uasset` 路径，并把三份字节（uasset / uexp / usmap）喂给 `KismetWorkspace.Open`。

**对外入口**：三个静态成员——`ExportPathFor`（纯路径推导）、`OpenWorkspace`（真正打开）、`CleanStaleWorkDirs`（清理死实例留下的工作目录）。无状态、无缓存（缓存由 `MainViewModel._workspaces` 负责）。

**内部结构**：

```text
OpenWorkspace(profile, virtualPath)
 ├─ ExportPathFor(profile, virtualPath)                 /Game/A/B → <导出目录>\A\B.uasset
 ├─ File.Exists(uassetPath)?            否 → FileNotFoundException
 ├─ profile.UsmapPath != null?          否 → InvalidOperationException
 ├─ 读三份字节（uasset / uexp / usmap）到内存
 └─ 依次尝试两个工作根：
      %TEMP%\solstice-winter-ui\p<pid>\<资产名>-<路径哈希>           ← 首选
      %LOCALAPPDATA%\solstice-winter-ui\p<pid>\<资产名>-<路径哈希>   ← 首选写不进去时
      每个候选：CreateDirectory → 写探针文件 .writable → 删掉 → KismetWorkspace.Open(...)
      两个都失败 → IOException，消息里列出两条失败原因
```

**依赖**：`SolsticeWinter.Core.GameProfile`（`ExportDirectory` / `UsmapPath`）、`Solstice.Kismet.KismetWorkspace.Open(byte[], byte[], byte[], string)`、`System.IO`、`System.Diagnostics.Process`（清理用）。被 [MainViewModel.GetWorkspace](MainViewModel.md)、[GraphProbe.Run](Graph.GraphProbe.md)、`App.OnStartup` 调用。

**取舍**：

- **UI 先吃 FModel 导出目录，不在这里做 pak 直读**：索引与 pak 读取已在各自模块里验证过，但把它们接进 UI 会让「画布能不能显示」和「pak 解析对不对」两件事耦合在一起。先读导出目录最快，也方便直接改文件看效果。代价：**UI 依赖一个事先存在的导出目录**（本机是 `D:\devenv\fmodel\Output\Exports\kards\Content`），没有它就只能看索引、点函数必炸；等 VFS 层落地后只换这一个类。
- **工作目录按进程隔离（`p<pid>`）**：`KismetWorkspace.Open` 会把字节写到磁盘再按路径构造资产，两个实例共用一个目录时，后开的那个写 `a.uasset` 会撞文件锁，表现为 `Access to the path ... is denied`（实测踩过）。代价：临时目录多一层 PID。
- **首选目录失败自动退到 `%LOCALAPPDATA%`**：临时目录被安全软件拦、或 `%TEMP%` 不可写时，至少还有一条路。代价：错误信息要同时报两个位置。
- **写探针文件再开资产**：`Directory.CreateDirectory` 在目录存在但不可写时不报错，要等真正写文件才炸；提前写一个 `.writable` 能让错误发生在「我们知道要报哪个目录」的时候。
- **三份字节整读进内存**：`KismetWorkspace` 的入口就是 `byte[]`，绕不过去。代价：一个 `BP_Logic` 级别资产加 usmap 常驻；每个资产都重读一次 usmap。
- **不在这里做缓存与错误恢复**：缓存放 `MainViewModel`，错误直接抛给上层变 `Status` 文本。否决了「返回 `null` 表示失败」——那样调用方要靠 `Status` 猜原因。

---

### `AssetAccess.ExportPathFor(GameProfile profile, string virtualPath): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。纯字符串运算 + 一次 `profile` 判空。**不检查文件是否存在**、不碰磁盘。抛：`profile.ExportDirectory` 为 `null` 时抛 `InvalidOperationException("profile 里没有导出目录")`。

**输入输出**：

- 输入 `/Game/Blueprints/Logic/BP_Logic` → `"<ExportDirectory>\Blueprints\Logic\BP_Logic.uasset"`。
- 不以 `/Game/` 开头时**整串**参与拼接（只去掉开头的 `/Game/` 这一条规则），例如 `/Script/Engine` → `"<ExportDirectory>\Script\Engine.uasset"`（实际不存在，是无效路径）。
- `/` 全部换成 `\`；末尾无条件追加 `.uasset`（传进来已带 `.uasset` 会得到 `X.uasset.uasset`）。

**关键分支（伪代码）**：

```text
root = profile.ExportDirectory
if root == null: throw InvalidOperationException("profile 里没有导出目录")   # 触发条件：本机没有导出目录
relative = virtualPath.StartsWith("/Game/") ? virtualPath[6..] : virtualPath  # 只剥这一种前缀
return Path.Combine(root, relative.Replace('/', '\\') + ".uasset")
```

**边界与失败**：

- ⛔ 只认 `/Game/` 前缀；`/Engine/`、`/Script/`、`/Game`（无尾斜杠）都会拼出无效路径。`/Game` 恰好等于根目录名时会被当成相对路径 `/Game` → 换成 `\Game` → `Path.Combine(root, "\Game.uasset")`，得到一个**以根路径开头的绝对路径**（`Path.Combine` 遇到以分隔符开头的第二段会丢弃第一段）。
- ⛔ 大小写敏感：索引里的路径必须与磁盘上的目录名完全一致（Windows 上文件系统不区分大小写，所以这一条实际不会失败）。
- ⛔ 不处理「虚拟路径带对象后缀」（`/Game/A/B.B_C`）：会拼出 `B.B_C.uasset`。

**调用者与被调用者**：被 `AssetAccess.OpenWorkspace` 调用；不调用其它函数。

---

### `AssetAccess.OpenWorkspace(GameProfile profile, string virtualPath): KismetWorkspace`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。读三份文件 → 在**两个候选工作根**里挑一个可写的 → `KismetWorkspace.Open`。副作用：建目录、写探针文件（写完即删）、写三个临时文件（`a.uasset` / `a.uexp` / `kards.usmap`）、把整个资产载入内存。抛：`InvalidOperationException`（没有导出目录 / 没有 usmap）、`FileNotFoundException`（uasset 或 uexp 不存在）、`IOException`（两个工作根都不可写，消息里列出两条原因）、以及 `KismetWorkspace.Open` 内部 UAssetAPI 的任何解析异常。

**输入输出**：

- 成功 → 一个可用的 `KismetWorkspace`（持有 `UAsset` + `Usmap`）。
- 失败 → 异常向调用方冒泡；`MainViewModel` 会把它变成 `Status = "反编译失败：…"`。
- 工作目录 = `<根>\solstice-winter-ui\p<pid>\<uasset 文件名去扩展名>`，根依次是 `%TEMP%` 与 `%LOCALAPPDATA%`。

**关键分支（伪代码）**：

```text
uassetPath = ExportPathFor(profile, virtualPath)
if not File.Exists(uassetPath): throw FileNotFoundException("资产不存在：" + uassetPath)
usmapPath = profile.UsmapPath
if usmapPath == null: throw InvalidOperationException("profile 里没有 usmap")
读 uasset / uexp / usmap 三份字节

for root in [%TEMP%\solstice-winter-ui\p<pid>, %LOCALAPPDATA%\solstice-winter-ui\p<pid>]:   # 两个候选
    work = root\<资产名>
    try:
        CreateDirectory(work)
        写 .writable 再删掉                       # 提前暴露「目录存在但不可写」
        return KismetWorkspace.Open(uasset, uexp, usmap, work)
    catch UnauthorizedAccessException | IOException:
        记下原因，试下一个候选

throw IOException("工作目录不可写，两个候选都失败了：…")   # 消息里带两条原因
```

**边界与失败**：

- ✅ **多实例安全**：工作目录带 PID，同时开两个实例不会互相锁 `a.uasset`。
- ✅ **同名资产不再撞**：工作目录名 = `<资产名>-<虚拟路径哈希>`，`/Game/A/BP_Logic` 与 `/Game/B/BP_Logic` 各占一个目录。
- ⛔ 每个资产都重新读一遍 usmap（几 MB）：切资产时有一次可感知的延迟，没有复用。
- ⛔ 只支持「导出目录」这一种数据源：pak 直读（`SolsticeWinter.Pak`）没有接进来，`GameProfile.PakDirectory` / `AesKeyHex` 在这个类里完全没用到。
- ⛔ 不做资产指纹/版本校验：磁盘上的 uexp 与 uasset 不配套（例如只重新导出了一半）时，错误会以 UAssetAPI 的解析异常形式出现，报错信息与「文件损坏」无法区分。

**调用者与被调用者**：被 `MainViewModel.GetWorkspace`（后台线程，首次打开某资产时）与 `GraphProbe.Run`（每次自检都重新打开，不复用缓存）调用；调用 `ExportPathFor`、`KismetWorkspace.Open`、`File.ReadAllBytes`、`File.WriteAllText`。

---

### `AssetAccess.CleanStaleWorkDirs(): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。扫**工作根的父目录**（`%TEMP%\solstice-winter-ui`、`%LOCALAPPDATA%\solstice-winter-ui`）下的 `p<pid>` 目录，把「PID 不存在」或「PID 被别的进程复用」的删掉。**不抛**（每个删除都单独 try/catch）。副作用：删除临时目录。\n\n> 修过一次：最初写成在工作根**内部**找 `p*`，而工作根本身就是 `p<pid>`，所以永远找不到子目录——清理从未生效，实测堆了 43 个目录（每个含资产副本 + 1.8 MB usmap）。改成扫父目录后 44 → 1。

**输入输出**：无返回值。没有目录、目录不可枚举、删除失败都静默跳过。由 `App.OnStartup` 在窗口显示前调一次。

**关键分支（伪代码）**：

```text
for root in 两个工作根:
    for dir in GetDirectories(root, "p*"):
        pid = 解析 dir 名字里的数字；解析失败 → 跳过
        if pid == 当前进程: 跳过
        try: p = GetProcessById(pid); if p.ProcessName == "SolsticeWinter.App": 跳过   # 活实例
        catch ArgumentException: 无此进程 → 往下删
        try: Delete(dir, recursive) catch: 忽略
```

**边界与失败**：

- ⛔ **只看进程名判活**：PID 被复用给另一个 `SolsticeWinter.App`（理论上可能）时会漏删；目录留着不影响正确性。
- ⛔ 删除是尽力而为：文件被占用时静默失败，下次启动再试。
- ⛔ 不清理工作根之外的任何东西（`ui.log` / `crash.log` / `graph.txt` 保留，方便排查）。

**调用者与被调用者**：被 `App.OnStartup` 调用；调用 `Directory.GetDirectories`、`Process.GetProcessById`、`Directory.Delete`。

