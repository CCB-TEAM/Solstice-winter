# Pak.ContentPakSource

pak 源：直接从 `.pak` 读 `uasset` / `uexp`，不依赖 FModel 先导出。
对应源码 `src/SolsticeWinter.Pak/ContentPakSource.cs`。

## 职责

实现 `IAssetSource`，把 pak 的原始条目路径（`../../../kards/Content/...`）映射成
统一的虚拟路径（`/Game/...`），并按需解压出字节。

## 对外入口

`ContentPakSource(PakArchive archive, string contentPrefix, string pakPath)`

## 内部结构

```
构造时（一次性）
  archive.Files.Values
    → 过滤 .uasset 且未删除
    → TryNormalize：剥 `../` 挂载点前缀 → 剥 contentPrefix → 去扩展名 → /Game/...
    → 字典[虚拟路径] = PakFile        ← 查表 O(1)

TryRead(虚拟路径)
  → 字典命中 → archive.Read(PakFile)     ← 整份解压
  → 同名 .uexp 再读一次（失败视为无）
```

## 依赖

`SolsticeWinter.Core`（`IAssetSource`）、`SolsticeWinter.Pak`（`PakArchive`）。
**调用方拥有 `PakArchive` 的生命周期**，本类不 `Dispose` 它。

## 取舍

**1. 构造时就建字典，不在 `TryRead` 里线性扫。**
`PakArchive.Files` 是 3.3 万条。每次读都扫一遍的话，建索引 12738 个资产会退化成
4 亿次字符串比较（实测这条优化是把「不可用」变成「3.7 秒/200 个」的关键）。

**2. 文件名用 `one.uasset` 复用，不按资产名建目录。**
`SymbolIndex.BuildFromSource` 每次解析完立刻覆盖。这样磁盘峰值是**单个资产**，
而不是整棵 6.8 GB 的解压树。

**3. 不硬编码 `../../../` 前缀。**
挂载点在不同 pak 上会变（KARDS 是 `../../../`）。`TryNormalize` 用循环剥 `../`，
所以换游戏也能用。

**4. `uexp` 读失败不算整体失败。**
纯结构体资产没有导出体；把「没有 uexp」和「读 pak 失败」分开，前者返回 `null`、后者返回 `false`。

---

### `ContentPakSource(PakArchive archive, string contentPrefix, string pakPath)`

**签名与契约**
构造。副作用：遍历 `archive.Files` 并建字典（3.3 万条，毫秒级）。

**输入输出**
- `archive`：已打开的 pak，不可为 null。
- `contentPrefix`：pak 内对应 `/Game` 的前缀，如 `kards/Content`；空串表示不剥前缀。
- `pakPath`：仅用于 `Name` 显示。

**关键分支（伪代码）**

```
foreach file in archive.Files.Values:
    if file.Deleted: continue
    if not file.Path.EndsWith(".uasset", 忽略大小写): continue
    if TryNormalize(file.Path, out vpath): _byVirtualPath[vpath] = file
```

**边界与失败**
- 前缀写成 `kards\\Content`（反斜杠）会被 `Replace('\\','/')` 归一化。
- 前缀不匹配任何条目 → 字典为空，`Count == 0`，不抛异常。

**调用者与被调用者**
- 调用者：CLI `pak source` / `index build pak`、（后续）UI 的数据源工厂
- 被调用者：`IAssetSource` 无；`PakArchive.Files`

### `ContentPakSource.TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp): bool`

**签名与契约**
按虚拟路径解压出字节。副作用：从 pak 读盘 + Oodle 解压。

**输入输出**
- 字典命中且解压成功 → `true`，`uasset` 为完整字节，`uexp` 可能为 `null`
- 路径不在本 pak → `false`，两个 out 置空
- 解压抛 `PakException` → `false`

**关键分支（伪代码）**

```
if not _byVirtualPath.TryGetValue(vpath, out entry): return false
try: uasset = _archive.Read(entry)                 // 整份解压，按块
catch PakException: return false
uexpPath = ChangeExtension(entry.Path, ".uexp")
try: uexp = _archive.Read(uexpPath) catch PakException: uexp = null
return true
```

**边界与失败**
- 已删除条目、尺寸字段为负 → `PakException` → `false`（交给 overlay 下一个源）。
- 单文件 >2 GB → `PakException`（`Read` 里限制），本项目资产没有这么大的。

**调用者与被调用者**
- 调用者：`SymbolIndex.BuildFromSource`、`OverlayAssetSource`
- 被调用者：`PakArchive.Read(PakFile)`、`Path.ChangeExtension`

### `ContentPakSource.Count: int`

**签名与契约**
本源可见的资产数（= 字典条目数）。纯查询。

**调用者与被调用者**
- 调用者：CLI 打印、后续 UI 显示「当前数据源有多少资产」
- 被调用者：无

## 实测数据（KARDS）

| 项 | 值 |
|---|---|
| pak 总条目 | 33,808 |
| 本源可见（`kards/Content` 下 `.uasset`） | **12,738** |
| FModel 导出目录的 `.uasset` | 2,137（**17%**） |
| 建索引速度 | 200 个 / 3.7 秒 ≈ 18 ms/个 |
| 虚拟路径映射验证 | `/Game/Blueprints/Logic/BP_Logic` → 107,140 字节，SHA256 与导出文件**完全一致** |
