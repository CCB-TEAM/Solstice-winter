# SymbolIndex.query — 查询侧（`Find` / `FindFunction` / `SearchFunctions` / `FindReferencers` / `Describe`）

**职责一句话**：索引建好之后的所有读路径——按路径查资产、按短名查函数、按子串搜函数名、反查引用者、打印统计。全部是字典命中或一次线性扫描，不碰文件。

**对外入口**：`Find` / `FindFunction` / `SearchFunctions` / `FindReferencers` / `Describe`。构建与落盘（`Build` / `Save` / `Load` / `RebuildLookups`）在 [SymbolIndex](SymbolIndex.md)。

**内部结构**：

```text
Find(virtualPath)          → ByPath[virtualPath]                    O(1)，忽略大小写
FindFunction(name)         → FunctionsByName[name]                  O(1)，区分大小写，多候选
SearchFunctions(sub, lim)  → FunctionsByName 线性过滤 + 排序 + Take   O(名字数)
FindReferencers(objPath)   → Referencers[objPath]                   O(1)，忽略大小写
Describe()                 → 遍历 Assets 求和 + 两个字典的 Count       O(资产数)
```

**依赖**：只依赖 `SymbolIndex` 自己的三个字典与 `Assets`（见 [SymbolIndex](SymbolIndex.md)）。被 `SolsticeWinter.Cli.Program.IndexCommand` 调用。

**取舍**：

- **`FindFunction` 返回列表而不是单个**：同名函数跨类是常态（实测 9303 个函数只对应 4856 个唯一名），做「短名唯一」的假设会在第一次跨类调用时就撞车。代价：调用方要自己再按归属过滤。
- **`SearchFunctions` 线性扫而不是建子串索引**：4856 个键的 `Contains` 一次是微秒级，建倒排索引（前缀树/后缀数组）要额外内存与构建时间。代价：每次查询 O(名字数)，与资产规模无关（只与唯一名数有关）。
- **`SearchFunctions` 的 `limit` 截断在「函数名」而不是「命中条数」上**：先按名字排序取前 `limit` 个名字，再展开成候选，所以返回条数可能大于 `limit`。否决了「按命中截断」——那会在展开时把同一个名字的候选切开，候选面板会显示「一半的候选」。代价：`limit=50` 时最多可能吐出几百条。
- **`Describe` 用中文硬编码标签 + 空格对齐**：它是给人看的看板输出，不做 i18n、不做机器可读格式。代价：解析它要按中文标签匹配（`Cli` 也不解析，直接打印）。
- **`FindReferencers` 不归一化键**：`Referencers` 的键就是 `References` 的原始字符串，`/Game/A/B`（包级）与 `/Game/A/B.B`（对象级）是两条不同的键。代价：查询方必须知道这两种形态（见 `FindReferencers` 边界）。

---

### `Find(string virtualPath): AssetHeader?`

**状态**：✅ 已实现

**签名与契约**：`ByPath` 精确查。纯读、无副作用、不抛。找不到返回 `null`。

**输入输出**：`virtualPath` 期望形如 `/Game/Blueprints/Logic/BP_Logic`（无 `.uasset` 扩展名、无对象后缀），大小写敏感（`ByPath` 用 `Ordinal`）。命中 → 该 `AssetHeader`（同一个实例，改动它会影响索引）；未命中 → `null`。若从未调用 `RebuildLookups`（手工构造的索引），永远返回 `null`。

**关键分支（伪代码）**：

```text
if ByPath.TryGetValue(virtualPath, out asset): return asset
return null
```

**边界与失败**：传 `/Game/.../BP_Logic.uasset` 或 `/Game/.../BP_Logic.BP_Logic_C` 都**查不到**（键里没有扩展名与对象后缀）。`virtualPath` 为 null 会抛 `ArgumentNullException`（`Dictionary` 的行为）。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `asset` 分支调用；不调用别的函数。

---

### `FindFunction(string name): IReadOnlyList<FunctionHit>`

**状态**：✅ 已实现

**签名与契约**：`FunctionsByName` 精确查（`Ordinal`，区分大小写）。纯读、不抛（`name` 为 null 抛 `ArgumentNullException`）。未命中返回**空数组**（不是 null）。

**输入输出**：`name` 是函数短名，如 `GoHome`、`OnCardFinishedFlyingToLocation__DelegateSignature`。命中 → 全部同名候选（跨资产的同名函数都在里面，顺序 = `RebuildLookups` 的遍历顺序 = `Assets` 顺序）；未命中 → `[]`。

**关键分支（伪代码）**：

```text
if FunctionsByName.TryGetValue(name, out hits): return hits
return []
```

**边界与失败**：⛔ 大小写敏感：`gohome` 查不到 `GoHome`（`SearchFunctions` 才是忽略大小写的）。返回的是字典里的活列表（`List<FunctionHit>` 当 `IReadOnlyList` 返回），调用方若强转回 `List` 并修改会污染索引。当前没有调用者（`Cli` 用的是 `SearchFunctions`），这条路径给将来的「拖引脚按类型过滤」用。

**调用者与被调用者**：当前无调用者；不调用别的函数。

---

### `SearchFunctions(string substring, int limit = 50): IEnumerable<FunctionHit>`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：按子串搜函数名（忽略大小写），返回候选序列。**惰性** `IEnumerable`——LINQ 延迟执行，每次枚举都重新扫一遍字典；纯读、不抛。

**输入输出**：`substring` 为空串 `""` 时匹配所有名字（返回前 `limit` 个名字的全部候选）；`limit <= 0` → 空序列。排序：先按**名字长度升序**，再按名字 `Ordinal` 升序；`Take(limit)` 作用于名字，之后 `SelectMany` 展开成 `FunctionHit`。所以返回条数 = 前 `limit` 个匹配名字各自的候选数之和，**可能大于 `limit`**。

**关键分支（伪代码）**：

```text
FunctionsByName
  .Where(kv => kv.Key.Contains(substring, OrdinalIgnoreCase))   # 空串 → 全部
  .OrderBy(kv => kv.Key.Length)                                 # 短名优先
  .ThenBy(kv => kv.Key, Ordinal)                                # 同长度按名字定序
  .Take(limit)                                                  # 截断在「名字」上
  .SelectMany(kv => kv.Value)                                   # 展开成 hits（条数可超 limit）
```

**边界与失败**：

- ⛔ `limit` 的语义是「名字数」不是「命中数」（见取舍）。实测 `BP_Logic` 里 181 个函数，若某个短名有多个同名候选，展开后会超出。
- ⛔ 惰性求值：在 `RebuildLookups` 之后、`Clear` 之前枚举才有效；若枚举前索引被重建，结果可能变。
- 复杂度 O(4856 × 名字长度)，与资产数无关；实测 4856 个唯一函数名，够快。
- 不抛异常；未命中返回空序列。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `find` 分支调用（`index find <index.json> <子串>`）；不调用别的函数。

---

### `FindReferencers(string objectPath): IReadOnlyList<AssetHeader>`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`Referencers` 精确查（`Ordinal`，大小写敏感）。纯读、不抛（`objectPath` 为 null 抛 `ArgumentNullException`）。未命中返回空数组。

**输入输出**：键的形态有三种，来自 `AssetHeaderReader.ResolveIndex`：引擎侧对象 `/Script/Engine.Actor`、引擎侧包 `/Script/Engine`、游戏侧包 `/Game/A/B`、游戏侧对象 `/Game/A/B.B`。传 `AssetHeader.VirtualPath`（`/Game/A/B`）命中**包级**导入；传 `/Game/A/B.B` 命中对象级导入。实测 `BP_Logic`：35 个资产的 `References` 含包级键 `/Game/Blueprints/Logic/BP_Logic`，同样 35 个含对象级键 `/Game/Blueprints/Logic/BP_Logic.BP_Logic_C`——两批是同一组资产（引用一个类时包级与对象级导入都会出现），但两个键**不互推**，各自只能查到自己那一种。

**关键分支（伪代码）**：

```text
if Referencers.TryGetValue(objectPath, out list): return list
return []
```

**边界与失败**：

- ⛔ 不是规范化对象路径：没有 `/Script/Engine.Actor` ↔ `Engine.Actor` 的映射，也没有「类 → 该类所有实例」这种语义（那是 `MembersOfClass`，尚未实现）。
- ⛔ 包级与对象级是两个键：查 `/Game/A/B` 拿不到引用 `/Game/A/B.B` 的资产，反之亦然。要「谁用了这个资产」得查两次。
- ⛔ 只覆盖**导入表**能表达的直接引用；字节码里通过函数名/字符串间接引用的不算。
- 返回的是字典里的活列表；调用方修改会污染索引。

**调用者与被调用者**：当前无调用者（`Cli` 未接这条命令）；不调用别的函数。

---

### `Describe(): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：把索引规模拼成 7 行中文摘要。纯读、不抛（`Assets` 为 null 时 NRE）。用 `Environment.NewLine` 分隔（Windows 上是 `\r\n`）。

**输入输出**：固定 7 行，标签用中文 + 空格补到同一列：

```text
资产      : 2137
函数      : 9303（其中带字节码 9303）
变量      : 5678
引用边    : 69446
唯一函数名: 4856
唯一被引对象: 4611
构建时间  : 2026-11-07 20:52:57  来源 D:\devenv\fmodel\Output\Exports\kards\Content
```

数值全部现算：`Assets.Count`、`Functions.Count`（及其中 `HasBytecode` 为真的个数）、`Variables.Count`、`References.Count` 求和，加 `FunctionsByName.Count` 与 `Referencers.Count` 两个字典的键数。`BuiltAt` 格式 `yyyy-MM-dd HH:mm:ss`。

**关键分支（伪代码）**：

```text
withBytecode = Σ a.Functions.Count(f => f.HasBytecode)
functions    = Σ a.Functions.Count;  variables = Σ a.Variables.Count;  refs = Σ a.References.Count
join(NewLine,
     $"资产      : {Assets.Count}",
     $"函数      : {functions}（其中带字节码 {withBytecode}）",
     $"变量      : {variables}",
     $"引用边    : {refs}",
     $"唯一函数名: {FunctionsByName.Count}",          # 依赖 RebuildLookups
     $"唯一被引对象: {Referencers.Count}",            # 依赖 RebuildLookups
     $"构建时间  : {BuiltAt:yyyy-MM-dd HH:mm:ss}  来源 {BuiltFrom}")
```

**边界与失败**：

- ⛔ 两个「唯一」数字来自字典：没调 `RebuildLookups` 时恒为 0（手工构造的索引会打印 `唯一函数名: 0`，其余数字正常——这是个容易误判的坑）。
- ⛔ 不做单位换算、不做列宽自适应：中文标签宽度与 ASCII 混排，终端字体不是等宽中文时对不齐。
- `BuiltFrom` 为 null 时那一段是空的（不会打印 `null`）。
- 不抛异常（除 `Assets` 为 null）；无副作用。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `build`（构建后立刻打印）与 `stats` 分支调用；不调用别的函数（只读属性）。


