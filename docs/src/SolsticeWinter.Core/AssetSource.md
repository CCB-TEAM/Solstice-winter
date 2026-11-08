# Core.AssetSource

VFS 的「路径 → 字节」层。设计文档 `docs/设计-VFS与上下文索引.md` 里三层结构的最底下那一层：
`PakSource（mmap + 路径表） | DirSource（散文件） | Overlay`。

对应源码 `src/SolsticeWinter.Core/AssetSource.cs`。

## 职责

把「资产从哪来」抽象成 `IAssetSource`，让上层的索引构建 / UI 打开 / 签名查询只认虚拟路径，
不关心字节来自 pak 还是导出目录。

## 对外入口

| 类型 | 说明 |
|---|---|
| `IAssetSource` | `Name` / `EnumerateVirtualPaths()` / `TryRead(virtualPath, out uasset, out uexp)` |
| `DirAssetSource(string root)` | 目录源（FModel 导出目录或任何 `*.uasset` 树） |
| `OverlayAssetSource(params IAssetSource[])` | 叠加源，**先给的赢** |

## 内部结构

```
IAssetSource
├── DirAssetSource      目录枚举 + File.ReadAllBytes
├── ContentPakSource    （在 BpStudio.Pak 里，见 SolsticeWinter.Pak/ContentPakSource.md）
└── OverlayAssetSource  按顺序试每个源；枚举取并集去重
```

## 依赖

`System.IO`。**不依赖 UAssetAPI、不依赖 Pak**——所以 Core 仍是叶子模块，
Pak 反过来引用 Core 实现 `ContentPakSource`。

## 取舍

**1. 虚拟路径统一成 `/Game/...` 且不带扩展名。**
pak 条目是 `../../../kards/Content/Blueprints/Logic/BP_Logic.uasset`，
导出目录是 `Blueprints\Logic\BP_Logic.uasset`，两者必须映射到**同一个 key**，
否则索引、最近列表、签名查询在切换数据源后就全部失效。

**2. 接口放在 Core 而不是新开一个 VFS 项目。**
避免多一个只有一个接口的项目。代价是 Core 里混了 IO 代码，
但它本来就是「配置 + 领域模型」层，放这里不算跑偏。

**3. `Overlay` 的顺序是「导出目录在前、pak 在后」。**
导出目录里的文件是用户可能改过的版本，应该覆盖 pak 里的原始版本——
这正是 FModel「导出 → 改 → 读回来」的工作流需要的顺序。

---

### `IAssetSource.EnumerateVirtualPaths(): IReadOnlyList<string>`

**签名与契约**
枚举本源能看到的所有资产虚拟路径。纯查询，无副作用。
**不保证**顺序（实现者各自排序），调用方若要稳定顺序自己排。

**输入输出**
返回 `IReadOnlyList<string>`，元素形如 `/Game/Blueprints/Logic/BP_Logic`。
源为空（目录不存在 / pak 没有匹配条目）时返回空列表，**不抛异常**。

**关键分支（伪代码）**

```
Dir:   if not Directory.Exists(root): return 空列表
       遍历 root 下所有 *.uasset（含子目录）
       每个 → "/Game/" + 相对路径（去掉 .uasset）
       按 OrdinalIgnoreCase 排序

Pak:   构造时就建好了 虚拟路径 → PakFile 的字典
       返回字典的 key 副本并排序

Overlay: 依次取每个源的枚举结果，用 HashSet 去重（忽略大小写）
         最后统一排序
```

**边界与失败**
- 目录不存在 → 空列表。pak 前缀不匹配 → 空列表。
- 大源（KARDS pak 12738 个）每次调用都新建列表，**不要在循环里反复调**。

**调用者与被调用者**
- 调用者：`SymbolIndex.BuildFromSource`、CLI `pak source`
- 被调用者：`Directory.EnumerateFiles` / 构造期建好的字典

### `IAssetSource.TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp): bool`

**签名与契约**
按虚拟路径取字节。找不到返回 `false` 并把两个 out 参数置空，**不抛异常**。

**输入输出**
- `virtualPath`：`/Game/...`（不带扩展名）
- 成功：`true`，`uasset` 非空；`uexp` 可能为 `null`（纯结构体资产没有导出体）
- 失败：`false`，`uasset = []`，`uexp = null`

**关键分支（伪代码）**

```
Dir:   路径 = root/<相对路径>.uasset
       if not File.Exists: return false
       uasset = ReadAllBytes
       uexp   = 同目录同名 .uexp 存在则读，否则 null

Overlay: foreach 源（先给的先试）
           if 源.TryRead(...) 成功: return true
       return false

Pak:   查字典拿 PakFile；没有 → false
       解压 uasset（失败 → false）
       解压同名 .uexp（失败 → null，不算整体失败）
```

**边界与失败**
- pak 解压抛 `PakException`（条目已删 / 尺寸字段异常）→ 当作「本源没有」，
  由 `Overlay` 交给下一个源。这样「pak 坏了但导出目录有」时仍然能打开。
- 大资产（BP_Logic：uasset 107 KB + uexp 295 KB）会整份读进内存——可接受。

**调用者与被调用者**
- 调用者：`SymbolIndex.BuildFromSource`、CLI `pak source`
- 被调用者：`File.ReadAllBytes` / `PakArchive.Read(PakFile)`
