# Models — 索引的数据形状

**职责一句话**：定义索引的唯一存储单位 `AssetHeader` 与它的三个叶子记录（`ParamInfo` / `FunctionInfo` / `VariableInfo`），它们同时就是 JSON 缓存的 schema。

**对外入口**：四个类型本身；唯一的行为成员是 `AssetHeader.ToString()`。

**内部结构**：

```text
AssetHeader                    一个资产 = 一条记录（JSON 顶层 Assets 数组的元素）
 ├─ Variables: VariableInfo[]   类导出属性表 → 渲染后的类型名 + CPF_ 标志文本
 ├─ Functions: FunctionInfo[]   每个 FunctionExport 一条
 │   └─ Params: ParamInfo[]     只装带 CPF_Parm 的属性；局部变量只计数，存在 Locals
 └─ References: string[]        导入表解析出的对象路径（保序、Ordinal 去重）
```

**依赖**：`TypeFormatter`（产出 `VariableInfo.Type` / `ParamInfo.Type`）；`System.Text.Json`（这些字段的形状就是落盘形状）。被 `AssetHeaderReader` 生产、被 `SymbolIndex` 持有与序列化。

**取舍**：

- **不含属性默认值、不含字节码**：默认值会让解析成本翻几倍，而索引的用途只有「查签名 / 查成员 / 查引用」。代价：`AssetHeader` 不能当「资产内容」用，画布仍要回读 `.uasset`。
- **`PackageName` 与 `VirtualPath` 同值**：现在没有「包 vs 对象」这一层，保留字段名是为了将来区分。代价：JSON 里多一份冗余（2137 条 × 约 40 字节）。
- **`Flags` 存 `ToString()` 文本而不是枚举值**：`EPropertyFlags` / `EFunctionFlags` 是 `[Flags]` 枚举，序列化成数字不可读，也挡不住 UAssetAPI 升级改值；存文本让缓存能直接 grep。代价：判断 Static / BlueprintEvent 要字符串 `Contains`，比位运算慢（量级可忽略）。
- **`Locals` 单独计数而不塞进 `Params`**：函数的属性表里参数与局部变量混在一起（`CallFunc_*` / `K2Node_*` / `Temp_*` 全是局部变量）。塞进去会让签名不可读，直接丢又看不出「这函数有多少临时量」。代价：属性表总条数 = `Params.Count + Locals`，读者要记住这条。
- **`AssetHeader` 用 `class` 而不是 `record`**：它要在 `AssetHeaderReader.Read` 里被逐字段填充（`Variables.Add(...)`），不可变 record 得写成几十行 `with`。代价：没有值相等语义（见 `AssetHeader` 小节）。
- **`HasBytecode` 与 `BytecodeSize` 都存**：前者是「能不能在画布上打开」的唯一判据，后者给面板显示；同一来源、无额外开销。

---

### `ParamInfo(string Name, string Type, string Direction)`

**状态**：✅ 已实现

**签名与契约**：`sealed record`，三个位置参数即三个只读属性。纯数据载体：无方法、无 I/O、无副作用、不校验入参。值相等 = 三个字符串逐个相等。

**输入输出**：`Name` 是属性名（生产端写 `p.Name?.ToString() ?? "?"`）；`Type` 是 `TypeFormatter.Of` 的渲染结果；`Direction` 只有 `"in"` / `"out"` / `"return"` 三态，唯一生产者是 `TypeFormatter.Direction`。非法值不会被拒绝——构造器不做检查，传 `"foo"` 也能存进去。实测全库 14343 个参数：`in` 10763、`out` 3538、`return` 42。

**关键分支（伪代码）**：

```text
# 本类型自身无分支；生产端（AssetHeaderReader.Read）的分支是：
for p in fn.LoadedProperties:
    if (p.PropertyFlags & CPF_Parm) == 0:  locals++; continue     # 局部变量，不算参数
    direction = TypeFormatter.Direction(p.PropertyFlags)          # return / out / in
    add ParamInfo(p.Name ?? "?", TypeFormatter.Of(p, resolve), direction)
    if direction == "return": returnType = 上面那个 type
```

**边界与失败**：不存在失败路径（不抛、不返回 null）。`Direction == "return"` 的参数仍在 `Params` 里，同时被复制到 `FunctionInfo.Return`——所以「返回值的类型」有两份，改一处不影响另一处。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 构造并塞进 `FunctionInfo.Params`；被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `asset` 分支打印。不调用任何函数。

---

### `FunctionInfo(string Name, IReadOnlyList<ParamInfo> Params, string? Return, bool HasBytecode, int BytecodeSize, string Flags, int Locals = 0)`

**状态**：✅ 已实现

**签名与契约**：`sealed record`，7 个位置参数，`Locals` 默认 0。`HasBytecode` 由 `fn.ScriptBytecode is { Length: > 0 }` 算出，`BytecodeSize` 取 `fn.ScriptBytecodeSize`，`Flags` 是 `fn.FunctionFlags.ToString()`。无 I/O。

**输入输出**：`Return` 为 null 表示没有 `CPF_ReturnParm` 属性；`BytecodeSize` 是 UAsset 声明的字节码字节数（不是反编译后的节点数）；`Flags` 形如 `FUNC_Public, FUNC_BlueprintCallable, FUNC_BlueprintEvent`，`FUNC_` 前缀是 `EFunctionFlags` 的原文。实测：9303 个函数 `HasBytecode` 全为 `true`；委托签名函数（`*__DelegateSignature`）的 `BytecodeSize` 为 3（一条 `EX_Return` 序列）。

**关键分支（伪代码）**：

```text
# 本类型自身无分支；生产端（AssetHeaderReader.Read）：
parameters = []; locals = 0; returnType = null
for p in fn.LoadedProperties ?? []:
    if !(p.PropertyFlags & CPF_Parm): locals++; continue
    ...
add FunctionInfo(fn.ObjectName ?? "?", parameters, returnType,
                 fn.ScriptBytecode is {Length: > 0},      # 不是看 BytecodeSize
                 fn.ScriptBytecodeSize, fn.FunctionFlags.ToString(), locals)
```

**边界与失败**：

- ⛔ 值相等是浅比较：`Params` 是 `IReadOnlyList<ParamInfo>`，record 生成的 `Equals` 对它走引用比较，所以「内容相同但列表实例不同」的两个 `FunctionInfo` **不相等**。不要拿它当去重键。
- ⛔ `HasBytecode` 只看 `ScriptBytecode` 数组长度，不看 `BytecodeSize`：若 UAssetAPI 只填了 size 没填数组，会被判成「无字节码」。反过来 size 为 0 而数组非空会显示 `[0B]`。
- `Locals` 的默认值 0 在当前链路上不会被依赖：`Save` 的 `DefaultIgnoreCondition = WhenWritingNull` 只忽略 null，`"Locals":0` 仍会写出（实测 9303 条全有该字段）。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 构造；被 `SymbolIndex.RebuildLookups` 收进 `FunctionsByName`；被 `SymbolIndex.Describe` 计数、被 `SolsticeWinter.Cli` 的 `find` / `asset` / `stats` 分支读取。

---

### `VariableInfo(string Name, string Type, string Flags)`

**状态**：✅ 已实现

**签名与契约**：`sealed record`，3 个位置参数。只描述**类导出**（`ClassExport.LoadedProperties`）上的属性，不含函数参数与局部变量。无 I/O。

**输入输出**：`Type` 是 `TypeFormatter.Of` 的渲染结果；`Flags` 是 `p.PropertyFlags.ToString()`，形如 `CPF_BlueprintVisible, CPF_InstancedReference, CPF_NonTransactional`。实测：`BP_Logic` 有 133 个 `VariableInfo`，与类导出的属性数一致；全库 5678 个。

**关键分支（伪代码）**：

```text
# 生产端（AssetHeaderReader.Read）：
if classExport?.LoadedProperties is { Length: > 0 } props:
    for p in props:
        add VariableInfo(p.Name ?? "?", TypeFormatter.Of(p, resolve), p.PropertyFlags.ToString())
# 没有类导出 → 不产生任何 VariableInfo（不抛）
```

**边界与失败**：⛔ 属性默认值不在记录里（这是设计，不是缺陷），要默认值得回读资产；⛔ `Type` 可能是裸短名（`BP_BaseCard_C`），原因见 `TypeFormatter.Of` 的边界；`Flags` 是文本，判断 `CPF_Edit` 之类要 `Contains`。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 构造；被 `SymbolIndex.Describe` 计数、被 `SolsticeWinter.Cli` 的 `asset` 分支打印。

---

### `AssetHeader`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：可变 `sealed class`，9 个公开可写属性——`VirtualPath` / `PackageName` / `ClassName` / `SuperClass` / `Variables` / `Functions` / `References` / `ExportCount` / `ImportCount`；三个集合初始化为空 `List<>`。无 I/O；落盘形状由 `SymbolIndex.Save` / `SymbolIndex.Load` 的 `JsonOptions` 决定。`ExportCount` / `ImportCount` 是 UAsset 导出/导入表的原始条数。

**输入输出**：`VirtualPath` 与 `PackageName` 恒等于 `"/Game/" + 相对路径去掉 .uasset`（由 `SymbolIndex.Build` 拼，本类型不校验、不归一化）；`ClassName` 是生成类名（`BP_Logic_C`），数据型资产或类导出降级失败时为 null；`SuperClass` 是父类对象路径（实测 `BP_Logic` → `/Script/kards.kardsGameMode`），解析不出为 null。实测 `BP_Logic`：`ExportCount=199`、`ImportCount=661`、`References.Count=661`（去重后仍 661）。

**关键分支（伪代码）**：

```text
# 生产端（AssetHeaderReader.Read）的两条路径：
classExport = asset.GetClassExport()
if classExport is null: classExport = ReparseClass(...)        # 兜底
header.ClassName  = classExport?.ObjectName?.ToString()        # 兜底失败 → null
header.SuperClass = classExport is null ? null : Resolve(classExport.SuperStruct)
if classExport 有 LoadedProperties: 填 Variables
填 Functions / References / ExportCount / ImportCount（无条件）
```

**边界与失败**：

- ⛔ 没有重写 `Equals`/`GetHashCode`：相等是引用相等。`SymbolIndex.Referencers` 里同一个资产会出现多次，靠的是同一个实例引用。
- ⛔ 类导出拿不到时**静默降级**：`ClassName` / `SuperClass` 为 null、`Variables` 为空，其余字段正常。调用方无法从 `AssetHeader` 本身区分「这个资产没有类」和「解析失败」。
- ⛔ 属性可写且集合可赋 null：JSON 里出现 `"Variables": null` 时 `ToString()` 与 `Describe()` 会 NRE（`Load` 不校验）。
- 实测 2137 条资产中 0 条 `References` 为空（连最简单的资产也引用 `/Script/CoreUObject.Object` 之类）。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 构造；被 `SymbolIndex.ByPath` / `SymbolIndex.Referencers` 持有、被 `SymbolIndex.Save` 序列化；被 `SolsticeWinter.Cli` 的 `asset` / `stats` 分支打印。

---

### `AssetHeader.ToString(): string`

**状态**：✅ 已实现

**签名与契约**：重写 `object.ToString()`，返回一行摘要，无副作用、不抛（除非集合为 null）。

**输入输出**：格式固定为 `"{VirtualPath}  class={ClassName ?? "-"}  super={SuperClass ?? "-"}  vars={Variables.Count}  funcs={Functions.Count}  refs={References.Count}"`，字段之间**两个空格**。示例（`BP_Logic`）：`/Game/Blueprints/Logic/BP_Logic  class=BP_Logic_C  super=/Script/kards.kardsGameMode  vars=133  funcs=181  refs=661`。

**关键分支（伪代码）**：

```text
s = VirtualPath
s += "  class=" + (ClassName ?? "-")
s += "  super=" + (SuperClass ?? "-")
s += "  vars="  + Variables.Count
s += "  funcs=" + Functions.Count
s += "  refs="  + References.Count
return s
```

**边界与失败**：不做对齐填充、不转义——路径长度不同时列不对齐；集合为 null 时 NRE（见 `AssetHeader` 边界）。只给人看，不要拿去解析。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `asset` 分支（`Console.WriteLine(asset)`）调用；不调用别的函数。
