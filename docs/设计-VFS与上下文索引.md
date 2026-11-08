# 设计：VFS、懒解析与上下文索引

状态：`PROPOSED`（设计已定，代码未落地；落地顺序见 §7）
依据：2026-11-07 的四项决策（见 [可行性评估](可行性评估.md) §5 的 D5–D8）

---

## 0. 要解决的问题

「超强上下文」听起来是「数据源多」，实际是两个性能问题：

1. **按路径拿字节**要 O(1)，不能每次都遍历目录树或重新挂载 pak。
2. **按符号查签名**要 O(1)，不能每次拖引脚都全量扫资产。

如果这两条不成立，节点编辑器会变成「拖一下卡两秒」，再强的数据也白搭。

---

## 1. 三层结构

```
┌─ VFS（路径 → 字节）──────────────────────────────────┐
│  PakSource（mmap + 路径表）  DirSource（散文件）  Overlay │
└──────────────────────────────────────────────────────┘
                       ↓ 只读 Header（summary + import/export 表）
┌─ PackageHeader 缓存（LRU，常驻 ~10 MB）──────────────┐
│  类名 / 父类 / 变量 / 函数签名 / 引用列表              │
└──────────────────────────────────────────────────────┘
                       ↓ 一次性 O(n) 扫描，结果落盘
┌─ SymbolIndex（缓存文件 + 内存多值字典）──────────────┐
│  函数名 → 候选（含归属类、参数类型、返回、flags）        │
│  类 → 变量/函数；资产 → 引用；被引用 → 引用者          │
└──────────────────────────────────────────────────────┘
```

**关键取舍：mount 是 O(n)，lookup 是 O(1)。**

mount 必须把目录索引读完（实测 KARDS：索引 633,072 字节，几万条目，几十毫秒级），
这是格式决定的，没法省。能省的是**解析资产本体**——mount 阶段一个 `.uasset` 都不读。
用户说的「只扫文件表」就是这个意思。

---

## 2. VFS：路径到字节

### 2.1 路径归一化（第一个坑）

pak 里的路径长这样（实测）：

```
../../../kards/Content/Blueprints/Logic/BP_Logic.uasset
```

而 FModel 导出目录长这样：

```
D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic\BP_Logic.uasset
```

引擎侧引用又是第三种：

```
/Game/Blueprints/Logic/BP_Logic.BP_Logic
```

所以 VFS 的键**统一成 `/Game/...` 形式**，挂载时把三种写法都折进去：

| 来源 | 原始 | 归一化 |
|---|---|---|
| pak | `../../../kards/Content/Blueprints/...` | 去掉 mount point + 映射 `Content/` → `/Game/` |
| 散目录 | `<导出根>/kards/Content/Blueprints/...` | 同上，按相对导出根算 |
| 资产内引用 | `/Game/Blueprints/...` | 已是目标形式 |

归一化函数只写一次（`GamePath.Normalize`），三处都调它。
**不做大小写不敏感猜测**：UE 资产路径区分大小写，猜错会指向另一个资产。

### 2.2 覆盖顺序

```
Overlay > 散目录 > pak
```

理由：mod 工作流就是「原版 pak 里改一份，导出散文件」。散文件代表用户的当前编辑，
pak 是基线。这条顺序写死在 `OverlaySource`，不靠调用方自觉。

### 2.3 mmap

`PakArchive` 现在用 `FileStream.Seek + Read`。换成 `MemoryMappedFile` 的理由不是「更快」，
而是**避免大文件反复 seek 的系统调用开销 + 允许零拷贝切片**（1.17 GB 的 pak，一次读一个 800 KB 的 uexp）。

约束：

- Windows x64 下 mmap 1.17 GB 没问题；但要处理「文件被游戏占用」的情况（`FileShare.ReadWrite`）。
- mmap 只解决**未压缩**条目的读；Oodle 压缩的条目仍然要解压到托管数组（不可避免）。
- 不要一次性 `ReadAllBytes` 整个 pak（1.17 GB 会直接把内存吃掉）。

`ReadAt(offset, length)` 是唯一入口，mmap 与 FileStream 两种实现互换不影响上层——
这也是先保留现有实现、后换 mmap 的原因（上层零改动）。

---

## 3. PackageHeader：只读「目录」，不读「正文」

UAssetAPI 的 `UAsset` 会把**属性值**也解析出来（`LoadedProperties` 里的 `FProperty` 带默认值），
对索引来说是浪费。索引只需要：

| 需要 | 从哪来 | 成本 |
|---|---|---|
| 资产类名 | `PackageSummary` / Export 的 `ClassIndex` | 便宜 |
| 蓝图父类 | `ClassExport.SuperStruct` | 便宜 |
| 变量名与类型 | `StructExport.LoadedProperties` 的**类型部分** | 中等（要跳过默认值） |
| 函数签名 | `FunctionExport` 的参数属性表 | 中等 |
| 是否带字节码 | `FunctionExport.ScriptBytecode != null` | 便宜 |
| 引用（Import 表） | `UAsset.Imports` 的 `ObjectName` + `OuterIndex` | 便宜 |

**实测规模**：2137 个蓝图 uasset 合计 10.8 MB，平均 5 KB。全部 Header 常驻内存上限约 10 MB，
完全可以全驻留，不需要 LRU 淘汰；但接口仍按「按需解析 + 缓存」写，以便将来扩到全 pak。

---

## 4. SymbolIndex：一次 O(n)，之后 O(1)

### 4.1 键的设计

```
/Script/Engine.KismetMathLibrary.Greater_IntInt          ← jmap 提供（13064 条）
/Game/Blueprints/Logic/BP_Logic.BP_Logic_C:GoHome        ← 自己扫资产补（jmap 没有）
```

函数短名 → 候选列表（多值），因为同名函数跨类很常见（`ResolveFunction` 已经会报歧义并列出候选）。
**索引存全路径，查询时按短名 + 可选归属过滤**，不做「短名唯一」的假设。

### 4.2 缓存文件

| 项 | 决定 |
|---|---|
| 位置 | `<项目目录>/.cache/index.json` |
| 格式 | **JSON**（实测 7,326,426 字节；加载 + 建字典 **296 ms**，含进程启动） |
| 指纹 | 待补：`(pak 大小, footer indexHash, 散文件 mtime 最大值)`——当前版本靠手动重建 |
| 失效 | 版本号不符直接拒绝加载；指纹落地后改为自动重建 |
| 增量 | 待补：单资产 mtime 变化只重建该条目 |

**为什么不用 SQLite**：查询只有四类（按路径、按短名、按类、按被引用），全是字典命中，
没有 join、没有范围扫描。SQLite 要多一个依赖 + 一层映射，换不来任何东西。

**为什么先用 JSON 而不是自定义二进制**：结构还在变，二进制会让每次改字段都要写迁移；
缓存可随时删掉重建，不需要版本兼容。实测 296 ms 的加载时间离「不可接受」还很远，
等真超了 1 s 再换二进制。

### 4.3 四张表

| 表 | 键 | 值 | 用途 |
|---|---|---|---|
| `ByPath` | `/Game/...` | 资产条目 | 打开资产、候选面板过滤 |
| `FunctionsByName` | 短名 | 候选列表 | 拖引脚时按类型过滤 |
| `MembersOfClass` | 类全路径 | 变量/函数列表 | 属性面板、`this.` 补全 |
| `Referencers` | 资产路径 | 引用者列表 | 「谁在用这个函数」 |

`Referencers` 是反向索引，构建时顺带填，查询 O(1)。它解决现有工具链的一个老问题：
`objects.json` 的 `super` 恒为 null、`functions.json` 只有 `/Script`——这些缺口都靠本索引补。

---

## 5. 拖拽模型：宽松 + 编译校验（D8）

选了「任意重连 + 编译校验」，就必须把「校验到底校验什么」写死，否则宽松会变成放任。

三层，只有前两层拦人：

| 层 | 检查什么 | 不通过时 |
|---|---|---|
| 类型层 | 引脚类型是否匹配（jmap 签名 / usmap / 资产属性表） | **UI 直接不让落线**，并说明期望类型 |
| 结构层 | 编译 → 回编译 → 与编辑后字节逐字节一致；ubergraph 入口点仍在原位 | 拒绝提交，回滚到编辑前，报出具体节点路径 |
| 语义层 | 不检查 | —（这是宽松模式的代价，明写在 UI 上） |

配套硬要求：

- **每个编辑是一个事务**：进入编辑时留字节快照，提交失败自动回滚。没有 undo 的宽松模式不能用。
- **差异视图**：提交前显示「exec 链顺序 + 跳转目标」的前后对比。语义层不校验，人得能看出来。
- **不确定就标 `unknown`**：类型推断不出来时引脚标灰、只读，不猜。

---

## 6. 回写：散文件默认，pak 导出可选（D7）

```
改动的资产集合（脏集）
   ├─ LooseSink（默认）：写到 <输出目录>/<资产虚拟路径>，同时写 .uasset 与 .uexp
   └─ PakSink（可选）：v3 + store 模式，mount point ../../../kards/Content/
```

依据（实测）：游戏目录里两个真实 mod pak（`BP_AsyncLoader.pak` 49 KB、`wide-mount.pak` 66 KB）
都是 **v3 / 不压缩 / 不加密 / 一对 uasset+uexp**，而 `solstice-pak` 的写路径正好覆盖这个形态
（它对一份真实 pak 做过逐字节复现）。所以不需要 v11 writer。

pak 内路径由资产虚拟路径反推：`/Game/Blueprints/Logic/BP_Logic` → `Blueprints/Logic/BP_Logic.uasset`。

---

## 7. 落地顺序（每一层都能单独验）

| # | 层 | 验收 |
|---|---|---|
| 1 | PakSource（v11 读 + AES + Oodle） | `pak compare` 与 FModel 导出逐字节一致（子代理在跑） |
| 2 | GamePath 归一化 + OverlaySource | 同一资产的三种写法映射到同一个键；散文件覆盖 pak |
| 3 | PackageHeader 解析 + 缓存 | 2137 个资产全扫一遍不崩；单资产解析耗时可测 |
| 4 | SymbolIndex + 缓存文件 | 冷启动建索引 vs 热启动加载，两个数字都要报 |
| 5 | mmap 替换 FileStream | 同一条 `ReadAt` 语义，逐字节结果不变 |
| 6 | ubergraph 重定位 spike | `BP_Logic`（170 段 / 50 入口）压测通过才继续 |

---

## 8. 明确的边界

- **不索引贴图/音频/网格**：它们是 1.17 GB 的主体，对蓝图编辑没有上下文价值。
- **不做跨游戏索引**：profile 决定路径与密钥，索引格式不承诺跨版本兼容（指纹不符就重建）。
- **不猜大小写**：UE 资产路径区分大小写。
- **mmap 不解决压缩**：Oodle 条目仍要解压到内存。
