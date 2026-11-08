# SymbolIndex — 跨资产符号索引（构建与落盘）

**职责一句话**：扫一棵内容树，把每个资产的目录页收进一张表，落成 JSON 缓存，加载后重建查询字典——构建 O(n)，之后全是字典命中。

**对外入口**：`SymbolIndex.Build` / `Save` / `Load` / `RebuildLookups`，以及数据成员 `Assets` / `ByPath` / `FunctionsByName` / `Referencers`。查询方法（`Find` / `FindFunction` / `SearchFunctions` / `FindReferencers` / `Describe`）在 [SymbolIndex.query](SymbolIndex.query.md)。

**内部结构**：

```text
Build(root, usmap, limit, log)
 ├─ 复制 usmap → %TEMP%/solstice-winter-index-<8hex>/game.usmap
 ├─ 枚举 root 下全部 *.uasset（排序 → Take(limit)）
 ├─ 逐个复制 uasset(+uexp) → %TEMP%/.../assets/<相对路径>
 ├─ new Usmap(临时副本) → new AssetHeaderReader(usmap)
 ├─ 逐个 AssetHeaderReader.Read → Assets.Add；异常只记 failures
 ├─ RebuildLookups()                    内存表 → 三个查询字典
 └─ 删临时目录（失败吞掉）

落盘：Save(path)  → 单行 JSON（Version + BuiltFrom + BuiltAt + Assets）
加载：Load(path)  → 反序列化 → 校验 Version → RebuildLookups()
```

**依赖**：`UAssetAPI.Unversioned.Usmap`、`System.Text.Json`、`AssetHeaderReader`、`Models`。被 `SolsticeWinter.Cli.Program.IndexCommand`（`index build` / `stats` / `find` / `asset`）调用。

**取舍**：

- **JSON，不是二进制，也不是 SQLite**：结构还在变，二进制格式每次改字段都要写迁移；SQLite 要多一个依赖 + 一层映射，而查询只有四类、全是字典命中（没有 join、没有范围扫描）。代价：文件 7,326,426 字节，加载要全量反序列化（`设计-VFS与上下文索引` §4.2 记录「加载 + 建字典 296 ms，含进程启动」）。等真超 1 s 再换二进制。
- **先探测可写、只读才复制**：UAssetAPI 用 `File.Open(ReadWrite)` 打开 `.uexp` 与 usmap。所以 `Build` 先拿第一个资产对开一次 `FileAccess.ReadWrite` 试写权限：可写就**就地解析**（零复制），不可写才把整棵树复制到临时目录。否决了「无条件复制」——本机导出目录只读所以以前一直复制，但可写机器上那是纯浪费；也否决了「改成只读打开」——那是改库。代价：多一次探测（一次 `FileStream` 打开），日志里会写清走了哪条路。
- **`Build` 返回 `(Index, Failures)` 而不是抛**：一个坏资产不该让整个索引不可用。代价：调用方必须检查 `Failures`——`Cli` 在有失败时返回退出码 1。
- **所有字典都用 `Ordinal`（大小写敏感）**：UE 资产路径与函数名都区分大小写，按 `设计-VFS与上下文索引` §2.1 的规则不猜大小写。代价是用户手输路径必须完全一致——将来做「你是不是要找」提示时放在查询层，不在索引层做模糊匹配。
- **`Version` 常量 + 版本不符直接拒绝加载**：缓存可随时删掉重建，不需要迁移逻辑。代价：改 schema 必须手动重建，没有自动降级。
- **没有指纹、没有增量**：`BuiltFrom` + `BuiltAt` 只记录来源与时间，`Load` 不校验资产是否变过（`设计-VFS与上下文索引` §4.2 的「指纹」仍是待补项）。代价：资产改了必须手动 `index build`。
- **`ByClass` 只到「类名 → 资产列表」，没到「类 → 变量/函数」**：设计文档 §4.3 的 `MembersOfClass` 语义是后者。现在 `ByClass[类名]` 拿到 `AssetHeader` 后，`Variables` / `Functions` 就在里面，两步 O(1)；单独再建一张扁平成员表会重复存储，等 GraphModel 定下成员标识再做。

---

### `FormatVersion` / `Version` / `BuiltFrom` / `BuiltAt` / `Assets`

**状态**：✅ 已实现

**签名与契约**：`public const int FormatVersion = 1`；`Version` 可写、默认取 `FormatVersion`；`BuiltFrom`（内容根的绝对路径，可 null）、`BuiltAt`（`DateTimeOffset`）、`Assets`（`List<AssetHeader>`，初始化为空）都是可写属性，也是 JSON 的顶层字段。无 I/O。

**输入输出**：`BuiltFrom` 由 `Build` 填 `Path.GetFullPath(root)`，手写 JSON 时可以为 null（`WhenWritingNull` 会省略它）；`BuiltAt` 用 `DateTimeOffset.Now`（本地时区带偏移）。实测缓存：`"Version":1`、`"BuiltFrom":"D:\\devenv\\fmodel\\Output\\Exports\\kards\\Content"`、2137 个 `Assets` 元素。

**关键分支（伪代码）**：

```text
# 只有 Load 读 Version 做闸门：
if index.Version != FormatVersion: throw InvalidOperationException("索引版本 X 与当前 1 不符，请重建")
```

**边界与失败**：`Version` 可写 → 反序列化能被文件内容改写；`Assets` 可写 → 畸形 JSON 的 `"Assets": null` 会让 `RebuildLookups` NRE。`BuiltAt` 不参与任何判断，只是给人看的。

**调用者与被调用者**：`Build` 写 `BuiltFrom` / `BuiltAt` / `Assets`；`Describe` 读 `Assets` 与 `BuiltAt` / `BuiltFrom`；`Save` / `Load` 序列化与校验 `Version`。

---

### `ByPath` / `FunctionsByName` / `ByClass` / `Referencers`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：四个 `[JsonIgnore]` 只读字典（不进 JSON，`Load` 后由 `RebuildLookups` 重建）：

| 字典 | 键类型 | 值 | 键比较 |
|---|---|---|---|
| `ByPath` | `string` | `AssetHeader` | `Ordinal` |
| `FunctionsByName` | `string` | `List<FunctionHit>` | `Ordinal` |
| `ByClass` | `string` | `List<AssetHeader>` | `Ordinal` |
| `Referencers` | `string` | `List<AssetHeader>` | `Ordinal` |

**输入输出**：`ByPath` 的键是 `AssetHeader.VirtualPath`（`/Game/...`，无扩展名）；`FunctionsByName` 的键是函数短名（`GoHome`），值是全部同名候选；`Referencers` 的键是 `References` 里的原始字符串（`/Script/Engine.Actor`、`/Game/A/B`、`/Game/A/B.B` 三种形态混在一起）。实测：`ByPath` 2137 键、`FunctionsByName` 4856 键、`Referencers` 4611 键。

**关键分支（伪代码）**：

```text
# 填充逻辑在 RebuildLookups，这里只描述键的语义：
ByPath[asset.VirtualPath] = asset            # 同名（忽略大小写）后写覆盖先写
FunctionsByName[fn.Name] += (asset, fn)      # 多值：同名跨类是常态
Referencers[reference] += asset              # 一个资产对同一目标只算一条边（References 已去重）
```

**边界与失败**：

- ⛔ `ByPath` 忽略大小写，与 `设计-VFS与上下文索引` §2.1「UE 资产路径区分大小写，猜错会指向另一个资产」相反。后果：只差大小写的两个路径会互相覆盖（`ByPath[path] = asset` 是赋值，不是 `TryAdd`）。实测本批 2137 条无碰撞，所以键数仍是 2137；换到大小写敏感的文件系统上就可能踩。
- `FunctionsByName` 用 `Ordinal`：实测 4856 键；若改成 `OrdinalIgnoreCase` 会变成 4841（15 组仅大小写不同的函数名），会把两个不同函数混成一个候选组。`FindFunction` 因此是大小写敏感的。
- `Referencers` 忽略大小写：实测键数 4611，与区分大小写的计数相同 → 本批无大小写碰撞。
- 三个字典都是 `get`-only 属性，外部只能通过 `RebuildLookups` 重建；直接 `Clear()` 会让索引静默变成空查询。

**调用者与被调用者**：被 `RebuildLookups` 填充；被 `Find` / `FindFunction` / `SearchFunctions` / `FindReferencers` / `Describe` 读取。

---

### `FunctionHit(AssetHeader Asset, FunctionInfo Function)`

**状态**：✅ 已实现

**签名与契约**：`public readonly record struct`，两个字段：拥有该函数的资产、函数本身。值语义（struct + record 自动相等）。无方法、无 I/O。

**输入输出**：由 `RebuildLookups` 在遍历 `Assets[i].Functions` 时构造；`Asset` 与 `Function` 都非 null（来自同一个 `AssetHeader` 实例）。调用方常用 `hit.Asset.VirtualPath + "." + hit.Function.Name` 拼全名（`Cli index find` 就这么干）。

**关键分支（伪代码）**：

```text
# 无分支；产生点：
for asset in Assets:
    for fn in asset.Functions:
        FunctionsByName[fn.Name].Add(new FunctionHit(asset, fn))
```

**边界与失败**：`readonly record struct` 的默认值（`default`）两个字段都是 null，`FindFunction` 不会产出这种值，但手写代码 `new FunctionHit()` 会。不持有额外状态，序列化时不出现在 JSON 里。

**调用者与被调用者**：被 `RebuildLookups` 构造、被 `FindFunction` / `SearchFunctions` 返回；被 `SolsticeWinter.Cli` 的 `find` 分支读取字段。

---

### `Build(string root, string usmapPath, int limit = int.MaxValue, Action<string>? log = null): (SymbolIndex Index, List<string> Failures)`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态工厂，扫一棵内容树建索引。副作用：在 `%TEMP%` 建目录并复制整棵资产树、读源文件、结束前删临时目录（删不掉吞掉）。抛：`DirectoryNotFoundException`（root 不存在）、`FileNotFoundException`（usmap 不存在）、以及复制/枚举阶段的 I/O 异常（这些**没有**被兜住）。单资产解析异常不抛，进 `Failures`。

**输入输出**：`root` 是内容根（对应 `/Game/`），如 `D:\devenv\fmodel\Output\Exports\kards\Content`；`usmapPath` 是 `.usmap` 文件；`limit` 在排序之后 `Take`，所以是「按路径序的前 limit 个」，默认不限；`log` 每 250 个资产收到一行 `  已扫描 n/N`（前缀两个空格），可为 null。返回值：`Index` 只含解析成功的资产，`Failures` 每条是 `相对路径: 异常类型: 消息`。`VirtualPath` = `"/Game/" + 相对路径去掉 ".uasset"`（分隔符统一成 `/`）。实测：2137 个 `.uasset` → 2137 个资产、9303 个函数（全带字节码）、5678 个变量、69446 条引用边、唯一函数名 4856、唯一被引对象 4611，约 6.6 s，`Failures` 为空。

**关键分支（伪代码）**：

```text
if !Directory.Exists(root):  throw DirectoryNotFoundException
if !File.Exists(usmapPath):  throw FileNotFoundException
work = %TEMP%/solstice-winter-index-<8位 guid>
Directory.CreateDirectory(work); copy usmapPath → work/game.usmap
files = EnumerateFiles(root, "*.uasset", AllDirectories)
          .OrderBy(路径, OrdinalIgnoreCase).Take(limit).ToList()
foreach file in files:                                # 先全量复制
    relative = GetRelativePath(root, file)
    copy file → work/assets/relative
    if 同名 .uexp 存在: copy → work/assets/relative.uexp
usmap = new Usmap(work/game.usmap)                    # 可能抛
reader = new AssetHeaderReader(usmap); index = { BuiltFrom=GetFullPath(root), BuiltAt=Now }
foreach (workPath, relative) in 已复制列表:
    virtualPath = "/Game/" + relative 去掉 ".uasset"
    try:    index.Assets.Add(reader.Read(workPath, virtualPath))
    catch e: failures.Add($"{relative}: {e.GetType().Name}: {e.Message}")
    if 每 250 个: log?.Invoke($"  已扫描 n/N")
index.RebuildLookups()
try: Directory.Delete(work, recursive: true) catch { }   # 不在 finally 里
return (index, failures)
```

**边界与失败**：

- ⛔ 只复制 `.uasset` + `.uexp`，不复制 `.ubulk` / `.uptnl`：依赖 ubulk 的资产会解析失败（进 `Failures`）或读到不完整数据。
- ⛔ 临时目录清理不在 `finally` 里：`Usmap` 构造抛、或枚举/复制阶段抛时，`%TEMP%/solstice-winter-index-*` 残留。反复失败会累积垃圾。
- ⛔ 需要 %TEMP% 可写且空间 ≥ 内容树大小（实测 10.8 MB 级）；源目录只读是安全的（只读源、写临时）。
- 单线程顺序解析，无并发；`Usmap` 与 `AssetHeaderReader` 实例跨资产复用。
- `OrderBy(..., OrdinalIgnoreCase)` 让 `limit` 的截断顺序在 Windows / Linux 上都确定。
- 同一个 root 扫两次，结果一致（除 `BuiltAt`）；`Assets` 的顺序 = 复制列表的顺序 = 排序后的路径序。
- 失败后系统状态：临时目录可能残留、`Failures` 里有明细、`Index` 仍可用（少几个资产）。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `build` 分支调用；调用 `Directory.EnumerateFiles`、`File.Copy`、`new Usmap`、`new AssetHeaderReader`、`AssetHeaderReader.Read`、`RebuildLookups`。

---

### `RebuildLookups(): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：把 `Assets` 重建为三个查询字典。`void`，无 I/O。副作用：清空并重填 `ByPath` / `FunctionsByName` / `Referencers`（字典实例不变）。**加载后必须调一次**——`Load` 已代调，`Build` 结尾也调。

**输入输出**：依赖 `Assets` 已填；空 `Assets` → 三个字典都为空（合法状态）。重复调用幂等。实测产出：`ByPath` 2137、`FunctionsByName` 4856、`Referencers` 4611。

**关键分支（伪代码）**：

```text
ByPath.Clear(); FunctionsByName.Clear(); Referencers.Clear()
for asset in Assets:
    ByPath[asset.VirtualPath] = asset                       # 忽略大小写，后写覆盖
    for fn in asset.Functions:
        FunctionsByName[fn.Name] ??= []; FunctionsByName[fn.Name].Add(FunctionHit(asset, fn))
    for reference in asset.References:
        Referencers[reference] ??= []; Referencers[reference].Add(asset)
```

**边界与失败**：

- ⛔ `Assets` 为 null（JSON 里 `"Assets": null`）→ `foreach` 抛 `NullReferenceException`。`Load` 不校验，所以报错类型是 NRE 而不是 `InvalidOperationException`。
- ⛔ 大小写敏感：用户手输 `/game/blueprints/...` 查不到 `/Game/Blueprints/...`（按设计不做模糊匹配）。
- 复杂度 O(资产数 + 函数数 + 引用边数)；实测 6.6 s 里这一步占比极小。
- 不写盘；清空后若中途抛异常，三个字典处于半填状态（没有事务回滚）。

**调用者与被调用者**：被 `Build`（结尾一次）与 `Load`（反序列化后一次）调用；不调用别的函数。

---

### `Save(string path): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：把整个索引序列化成 UTF-8 文本写盘。副作用：创建父目录、覆盖写文件（非原子）。无返回值。抛：`IOException` / `UnauthorizedAccessException`（写失败时）。

**输入输出**：`path` 相对路径按当前工作目录解析；父目录不存在会被创建。输出是**单行** JSON（`WriteIndented = false`），`null` 属性被省略（`DefaultIgnoreCondition = WhenWritingNull`），`ByPath` / `FunctionsByName` / `Referencers` 因 `[JsonIgnore]` 不进文件，`FunctionHit` 也不出现。实测：2137 个资产 → 7,326,426 字节。

**关键分支（伪代码）**：

```text
dir = GetDirectoryName(GetFullPath(path))
if dir 非空: Directory.CreateDirectory(dir)
File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions))
# JsonOptions: WriteIndented=false, DefaultIgnoreCondition=WhenWritingNull
```

**边界与失败**：

- ⛔ 非原子写：`File.WriteAllText` 先截断再写，中途失败会留下 0 字节或不完整文件，而 `Load` 那边只会看到 JSON 解析错误。没有「写临时文件再改名」的保护。
- ⛔ 不写指纹：文件里没有源资产的 mtime / 大小，`Load` 无法判断缓存是否过期。
- 路径里的非 ASCII 与 `<`/`>` 会被 `System.Text.Json` 转义成 `\uXXXX`（未开 `UnsafeRelaxedJsonEscaping`），所以实测文件里 `Array<IntProperty>` 存成 `Array\u003CIntProperty\u003E`。中文标签不在 JSON 里（`Describe` 才输出中文）。
- 不写 AES key、不写字节码——`AssetHeader` 里本来就没有这些字段。
- 失败后可重试（覆盖写）。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `build` 分支调用；调用 `Directory.CreateDirectory`、`File.WriteAllText`、`JsonSerializer.Serialize`。

---

### `Load(string path): SymbolIndex`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态工厂。读文件 → 反序列化 → 校验 `Version` → `RebuildLookups`，返回可用索引（字典已建好）。抛：`FileNotFoundException` / `DirectoryNotFoundException`（路径不存在）、`JsonException`（JSON 畸形）、`InvalidOperationException`（反序列化结果为 null，或 `Version` 不符）。

**输入输出**：`path` 是 `Save` 写出的文件。版本不符的报错文本是 `索引版本 {index.Version} 与当前 {FormatVersion} 不符，请重建`。实测缓存 `Version=1` 可直接加载；`设计-VFS与上下文索引` §4.2 记录「加载 + 建字典 296 ms（含进程启动）」。

**关键分支（伪代码）**：

```text
text = File.ReadAllText(path)
index = JsonSerializer.Deserialize<SymbolIndex>(text, JsonOptions)
if index is null: throw InvalidOperationException($"索引文件解析失败：{path}")
if index.Version != FormatVersion: throw InvalidOperationException("索引版本 X 与当前 1 不符，请重建")
index.RebuildLookups()                     # 三个字典不在文件里，必须重建
return index
```

**边界与失败**：

- ⛔ 版本不符直接拒绝，不做迁移（刻意：缓存可删可重建）。
- ⛔ `Assets: null` 的畸形 JSON 会通过反序列化，然后在 `RebuildLookups` 里 NRE（不是 `InvalidOperationException`）。
- ⛔ 不校验内容新鲜度：源资产变了也照常加载旧缓存（没有指纹）。
- 全量反序列化 7.3 MB 一次；纯读，可重试。
- 跨平台：JSON 与 `DateTimeOffset` 都是文本，无差异。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `stats` / `find` / `asset` 分支调用；调用 `File.ReadAllText`、`JsonSerializer.Deserialize`、`RebuildLookups`。



---

## jmap 并入（2026-11-07）

索引现在同时装两类事实：

| 事实 | 来源 | 存放 |
|---|---|---|
| `/Game` 侧函数签名、变量、引用 | 资产本体（`AssetHeaderReader`） | `Assets` / `FunctionsByName` / `ByClass` / `Referencers` |
| `/Script` 侧函数签名 | jmap 产物（`JmapCatalog`） | `ScriptFunctions` / `ScriptByName` |

### `SymbolIndex.ScriptFunctions` / `ScriptByName`

**状态**：✅ 已实现

**签名与契约**：`ScriptFunctions` 是可序列化的 `List<ScriptFunction>`；`ScriptByName` 是 `[JsonIgnore]` 的短名 → 候选字典，由 `RebuildLookups` 重建。两者都不做去重——同名跨类是常态。

**输入输出**：实测并入 KARDS 的 `functions.json` 后：`ScriptFunctions` 13064 条、`ScriptByName` 11624 个短名；索引 JSON 从 7.3 MB 涨到 14.0 MB，构建从 6.6 s 涨到 15.5 s。

**关键分支（伪代码）**：

```text
RebuildLookups():
    ScriptByName.Clear()
    for fn in ScriptFunctions:                       # 空列表时什么也不做
        ScriptByName[fn.Name].Add(fn)
```

**边界与失败**：⛔ 只有短名索引，全路径查询要自己遍历；⛔ 与 `FunctionsByName`（`/Game`）**不合并**——同名时两边都有，调用方自己按「资产本体 > usmap > jmap > registry」的权威顺序取。

**调用者与被调用者**：被 `FindScriptFunction` 查询；由 `Build(jmapPath)` 填充。

### `SymbolIndex.FindScriptFunction(string): IReadOnlyList<ScriptFunction>`

**状态**：✅ 已实现

**签名与契约**：`public`。按短名查 `/Script` 签名，纯读、不抛；未命中返回空数组。

**输入输出**：`"PrintString"` → 1 条（`/Script/Engine.KismetSystemLibrary`）；`"NoSuchFn"` → 空。

**关键分支（伪代码）**：

```text
return ScriptByName.TryGetValue(name, out hits) ? hits : []
```

**边界与失败**：⛔ 只覆盖 `/Script`；`/Game` 侧的同名函数要用 `FindFunction`。

**调用者与被调用者**：被 `MainViewModel.BuildOptions` 的 `SignatureOf` 回调调用（图构建补引脚名）。

