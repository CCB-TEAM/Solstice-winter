# AssetHeaderReader — 一个资产 → 一张目录页

**职责一句话**：从 `.uasset` + `.uexp` 里抽出「目录页」（`AssetHeader`）：类名、父类、变量、函数签名、引用，不读属性默认值、不反编译字节码。

**对外入口**：`new AssetHeaderReader(Usmap)` + `Read(assetPath, virtualPath)`。`ReparseClass` / `ResolveIndex` 是私有实现细节，但在诊断时是理解行为的关键。

**内部结构**：

```text
Read(assetPath, virtualPath)
 ├─ new UAsset(assetPath, VER_UE5_6, usmap)        打开资产对（含 .uexp）
 ├─ asset.GetClassExport()                         正常路径
 │    └─ null → ReparseClass(...)                  兜底：ConvertToChildExport + SerialOffset/Size 重读
 ├─ 类导出.LoadedProperties  → Variables           （每个属性一条 VariableInfo）
 ├─ Exports.OfType<FunctionExport>() → Functions   （按 CPF_Parm 分参数 / 局部变量）
 └─ Imports 全表 → References                      （每条 ResolveIndex，丢 None，Ordinal 去重）

ResolveIndex  包索引 → "包.外层.对象"；ReparseClass 也用它判断「这是不是生成类导出」
```

**依赖**：`UAssetAPI`（`UAsset`、`ClassExport`、`FunctionExport`、`FPackageIndex`、`Usmap`）、`TypeFormatter`、`Models`。被 `SymbolIndex.Build` 调用；间接被 `SolsticeWinter.Cli` 的 `index build` 驱动。

**取舍**：

- **只读结构、不读默认值、不反编译字节码**：索引的用途是「查签名 / 查成员 / 查引用」。注意省掉的是**内存与后续访问**，不是解析时间——`new UAsset(...)` 仍会把 `LoadedProperties` 的默认值一并解出来，我们只是不保留。
- **在 `GetClassExport()` 之上再加 `ReparseClass` 兜底**：实测 `BP_Logic`（199 个导出）的生成类导出在正常加载时**静默降级成 `RawExport`**，`GetClassExport()` 返回 null，于是变量与父类全丢。否决了「换 usmap / 换 UAssetAPI 版本」——问题在库的导出类型推断，不在 usmap。代价：兜底成功时该导出体被读两遍；兜底失败时**静默**返回 null（无变量、无父类、无日志），只能靠 `Cli index probe` 看出来。
- **必须用 `ConvertToChildExport<ClassExport>()`**：它会把 `Asset` 一起带过去。用 `new ClassExport(export)` 会留下 null 的 `Asset`，随后读属性时在 UAssetAPI 的 `GetClassTypeForAncestry` 里 NRE（实测踩过，`AssetProbe.TryReparse` 的失败诊断里专门打印「Asset 是否为空」）。
- **边界用 `SerialOffset + SerialSize`，不用下一个导出的偏移**：`SerialSize` 就是本导出体的序列化长度；用下一个导出的偏移会把导出之间的对齐/空洞算进来。实测这样能读出 `BP_Logic` 的 133 个属性 / 181 个 FuncMap 项。
- **导入链拼成 `包.外层.对象`**：否则所有导入都退化成同名短名，跨类同名函数/类型没法区分。代价：链一断（`OuterIndex` 为 null/0，或指向本包导出）就只剩短名——实测索引里有 5 处类型名是裸 `BP_BaseCard_C`。
- **不缓存 `UAsset`**：`Read` 每次新建、读完即弃。代价：同一资产被 `Build` 和 `probe` 各读一遍时重复解析；换来的是没有句柄泄漏与陈旧缓存。

---

### `AssetHeaderReader(Usmap usmap)`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：主构造函数，只把 `usmap` 存成字段。不打开文件、不做校验、不抛。

**输入输出**：`usmap` 由调用方构造（`SymbolIndex.Build` 用复制到临时目录的副本构造，因为 UAssetAPI 以 `File.Open(ReadWrite)` 打开 usmap，只读目录会抛 `UnauthorizedAccessException`）。传 null 不会在这里报错，会在 `Read` 里 `new UAsset(...)` 时抛。

**关键分支（伪代码）**：

```text
AssetHeaderReader(usmap):
    this.usmap = usmap          # 无分支、无 I/O
```

**边界与失败**：无状态（除 `usmap` 引用），所以一个实例可以连续扫多个资产——`Build` 就复用同一个实例扫了 2137 个。⛔ 并发未验证：是否线程安全取决于 `Usmap` 内部是否共享 Reader/缓存，本模块从未并发调用 `Read`。

**调用者与被调用者**：被 `SymbolIndex.Build` 构造一次；被 `Read` 使用。

---

### `Read(string assetPath, string virtualPath): AssetHeader`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：读一个资产对，返回填满的 `AssetHeader`。副作用：打开并关闭 `.uasset`/`.uexp`（UAssetAPI 自己按扩展名找同名文件）、分配 `UAsset` 对象（不缓存）。抛错：本函数**不 catch**，`UAsset` 构造/解析抛什么就往外抛什么（只有 `ReparseClass` 内部吞异常）。

**输入输出**：`assetPath` 是可读的资产路径，同目录必须有同名 `.uexp`；`virtualPath` 只做字符串赋值，不归一化、不校验（`Build` 传 `/Game/...`）。返回值的集合可能为空：数据型资产（无类导出）→ `Variables` 空；没有 `FunctionExport` → `Functions` 空。实测 `/Game/Blueprints/Logic/BP_Logic`：`ExportCount=199`、`ImportCount=661`、133 变量、181 函数（181 个全带字节码）、661 条引用。

**关键分支（伪代码）**：

```text
asset = new UAsset(assetPath, VER_UE5_6, usmap)              # 可能抛
resolve = index => ResolveIndex(asset, index)
classExport = asset.GetClassExport()
if classExport is null: classExport = ReparseClass(asset, assetPath, resolve)   # 吞异常，可能仍是 null

header = { VirtualPath=virtualPath, PackageName=virtualPath,
           ClassName=classExport?.ObjectName, SuperClass=classExport?.SuperStruct 解析,
           ExportCount=asset.Exports.Count, ImportCount=asset.Imports.Count }

if classExport 有 LoadedProperties: 每个属性 → VariableInfo（TypeFormatter.Of + PropertyFlags 文本）
for fn in asset.Exports.OfType<FunctionExport>():            # 只要转成 FunctionExport 的
    for p in fn.LoadedProperties ?? []:
        if !(p.PropertyFlags & CPF_Parm): locals++; continue  # 局部变量：只计数
        direction = TypeFormatter.Direction(...); 收进 Params；direction=="return" 时记 Return
    收进 Functions（HasBytecode = ScriptBytecode 非空，BytecodeSize，FunctionFlags 文本，locals）
for i in 0..Imports.Count-1:
    path = ResolveIndex(asset, FPackageIndex(-(i+1)))
    if path != "None": References.Add(path)                   # 0/null 索引解析成 None
References = References.Distinct(Ordinal)                     # 保序去重
```

**边界与失败**：

- ⛔ **类导出缺失不抛**：返回 `ClassName=null`、`SuperClass=null`、`Variables=[]`，`ExportCount`/`ImportCount` 仍正确。调用方无法从返回值区分「没有类」与「解析失败」。
- ⛔ **只认转成 `FunctionExport` 的导出**：UAssetAPI 转不出来的函数导出不会进 `Functions`（诊断用 `AssetProbe.Exports` 看实际类型）。
- 单资产解析异常不在这里处理，由 `SymbolIndex.Build` 捕获记 failure，其余资产继续。
- `.uexp` 缺失或被截断 → 抛（`Build` 记 failure）。路径含非 ASCII 走 .NET 文件 API，无编码问题。跨平台无差异（只用路径与流）。
- 不写任何文件，可重试；重复 `Read` 同一资产结果一致（纯读）。
- 交叉验证：`KismetProbe`（solstice-kismet 的独立工具）对 `BP_Logic` 报「181 个带字节码的函数」，本函数给出 181；类导出 133 个属性 = 133 个变量。

**调用者与被调用者**：被 `SymbolIndex.Build` 调用（每个资产一次）；调用 `asset.GetClassExport()`、`ReparseClass`、`TypeFormatter.Of`、`TypeFormatter.Direction`、`ResolveIndex`（经 `resolve` 闭包）、`Enumerable.OfType<FunctionExport>`。

---

### `ReparseClass(UAsset asset, string assetPath, Func<FPackageIndex?, string> resolve): ClassExport?`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：私有静态。`GetClassExport()` 返回 null 时的兜底：自己找出「类导出」，用 `ConvertToChildExport<ClassExport>()` 换类型、按 `SerialOffset` 定位、`Read` 到 `SerialOffset + SerialSize`。返回 `ClassExport` 或 null；**任何异常都被吞掉**并返回 null。

**输入输出**：遍历 `asset.Exports`，选中第一个满足 `resolve(export.ClassIndex).EndsWith("BlueprintGeneratedClass")` 的导出。返回值的 `LoadedProperties` 是类属性表、`SuperStruct` 是父类索引、`FuncMap` 是函数映射。实测 `BP_Logic` 读出 133 个属性 / 181 个 FuncMap 项（与 181 个函数吻合）。

**关键分支（伪代码）**：

```text
for i in 0..asset.Exports.Count-1:
    export = asset.Exports[i]
    if !resolve(export.ClassIndex).EndsWith("BlueprintGeneratedClass"): continue   # 只认生成类
    try:
        reader = asset.PathToReader(assetPath)              # 自己开一个 reader
        parsed = export.ConvertToChildExport<ClassExport>() # 必须用它：会带上 Asset
        reader.BaseStream.Seek(export.SerialOffset)
        parsed.Read(reader, (int)(export.SerialOffset + export.SerialSize))
        return parsed
    catch:
        return null                                         # 不记日志、不重试下一个
return null                                                 # 一个都没命中
```

**边界与失败**：

- ⛔ 只认类名以 `BlueprintGeneratedClass` 结尾的导出：`WidgetBlueprintGeneratedClass` / `AnimBlueprintGeneratedClass` 也满足（都以它结尾）；纯 C++ 类、`UserDefinedStruct`、`DataTable` 等资产不会命中 → 返回 null。
- ⛔ 只取第一个命中的导出：一个包里若有多个生成类导出，后面的被忽略（本批未遇到）。
- ⛔ 失败静默：异常被吞成 null，没有日志、没有异常类型。定位手段只有 `SolsticeWinter.Cli index probe <asset> --reparse <导出号>`。
- `(int)(SerialOffset + SerialSize)` 是 `long → int` 截断：单导出体超过 2 GB 会溢出（实际 5 KB 量级，不会发生）。
- 读的是同一个 `UAsset` 的 reader，但 `Seek` 后由 `ClassExport.Read` 自己消费；不影响后续 `Imports` 遍历（`Imports` 已在内存里）。
- 重试安全（纯读）。

**调用者与被调用者**：只被 `Read` 调用；调用 `asset.PathToReader`、`export.ConvertToChildExport<ClassExport>()`、`ClassExport.Read`，以及传入的 `resolve`（判断类名）。

---

### `ResolveIndex(UAsset asset, FPackageIndex? index): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：私有静态纯函数（只读 asset 的导入/导出表）。把 `FPackageIndex` 翻成对象路径字符串。不抛（表结构异常时退化成占位字符串）。

**输入输出**：`null` 或 `Index == 0` → `"None"`；正数 → 导出表 `[Index-1].ObjectName`（**只有短名，不含包路径**）；负数 → 导入表 `[-Index-1]`，沿 `OuterIndex` 链拼成 `外层.…​.对象`（点号连接）；越界 → `"<export N>"` / `"<import N>"`。实测两种形态：`/Script/Engine.Actor`（引擎侧导入）、`/Game/Assets/Materials/BackgroundCubeMaterial.BackgroundCubeMaterial`（游戏侧对象导入），以及包级导入 `/Game/Assets/Materials/BackgroundCubeMaterial`。

**关键分支（伪代码）**：

```text
if index is null or index.Index == 0: return "None"
if index.Index > 0:                                  # 本包导出
    i = index.Index - 1
    return i < Exports.Count ? Exports[i].ObjectName ?? "<export N>" : "<export N>"
i = -index.Index - 1                                 # 导入（1 基）
if i >= Imports.Count: return "<import N>"
parts = []; current = Imports[i]
for guard in 0..15:                                  # 最多爬 16 层
    parts.insert(0, current.ObjectName ?? "?")
    outer = current.OuterIndex
    if outer is null or outer.Index == 0: break      # 链到头
    if outer.Index < 0: current = Imports[-outer.Index-1]; continue    # 继续爬导入
    parts.insert(0, Exports[outer.Index-1].ObjectName ?? "?"); break   # 外层是本包导出
return join(".", parts)
```

**边界与失败**：

- ⛔ 正数索引只给短名：`SuperClass` 或 `Type` 指向本包导出时没有包前缀（实测索引里 5 处 `Type` 是裸 `BP_BaseCard_C`）。
- ⛔ 链最多爬 16 层（`guard < 16`）：更深的导入链被截断，返回部分路径而不报错。
- ⛔ 链断在 `OuterIndex == null` 或 `== 0` 时只剩已收集的部分（常见表现就是一段短名）。`AssetProbe.SuperStructDiag` 就是为扫 `OuterIndex == null` 的导入写的，而它正是 UAssetAPI `GetClassTypeForAncestry` 的 NRE 源头。实测索引里 5 处裸短名类型（`BP_BaseCard_C`）落在这条分支上——`ResolveIndex` 只有两个分支会产出裸名：索引指向本包导出，或导入链在第一步就断。
- `ObjectName` 为 null 的条目会插入 `"?"`。
- 不抛：坏索引退化成 `<export N>` / `<import N>` 这种可识别字符串。`Read` 只把 `"None"` 从 `References` 里剔除，`<export N>` 会留在 `References` 里（可用作坏数据的信号）。

**调用者与被调用者**：被 `Read`（变量类型、参数类型、父类、引用各一次）与 `ReparseClass`（判断类名）调用，经 `Resolve` 闭包传入；不调用别的函数。
