# AssetProbe — 导出表诊断与导出体重读

**职责一句话**：把一个资产的导出表原样打出来，并手动重读某个导出体，用来定位「UAssetAPI 为什么把它降级成 `RawExport`」。

**对外入口**：`Prepare(assetPath, usmapPath)`、`Exports(assetPath, usmap)`、`TryReparse(assetPath, usmap, exportIndex)`。私有：`SuperStructDiag` / `StepByStep` / `DescribeIndex` / `ClassNameOf`。

**内部结构**：

```text
Prepare(asset, usmap)                 先探测可写：可写就就地返回；否则复制到 %TEMP%/solstice-winter-probe-<8hex>
Exports(assetPath, usmap)             new UAsset → 逐条打印 导出类型名 / ObjectName / 类名 / SerialSize
                                        └─ ClassNameOf 把 ClassIndex 翻成短名
TryReparse(assetPath, usmap, i)       new UAsset → Seek(SerialOffset) → ConvertToChildExport<ClassExport> → Read
  ├─ 成功 → "成功 → ClassExport  super=… 属性=N  FuncMap=M"
  └─ 失败 → 异常类型/消息 + DescribeIndex + Asset 是否为 null + 导入表条数
             + StepByStep（复现 GetClassTypeForAncestry）+ SuperStructDiag（扫 OuterIndex=null）+ 栈
```

**依赖**：`UAssetAPI`（`UAsset`、`ClassExport`、`FPackageIndex`、`Usmap`）。**不依赖** `Models` / `TypeFormatter` / `AssetHeaderReader`——诊断要绕开正常解析路径，否则看不到现场。被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `probe` 分支调用。

**取舍**：

- **单独构造 `UAsset`，不复用 `AssetHeaderReader` 的解析结果**：要回答的是「库实际把导出转成了什么类型」，必须在兜底改写之前看原始导出表；`AssetHeaderReader` 只交出 `AssetHeader`，看不到导出类型。代价：`probe` 一次跑下来资产被解析两遍（`Exports` 一遍、`TryReparse` 一遍）。
- **自己 `Seek` + `Read`，不用 `asset.ParseExport`**：`ParseExport` 内部 try/catch 会把异常吞掉并降级成 `RawExport`，外面永远看不到原因——这正是要诊断的东西。代价：必须自己算边界（`SerialOffset + SerialSize`）。
- **失败时打印六段诊断而不是抛**：这个类的用途就是「在坏资产上也要出结论」，抛异常会把现场丢掉。代价：返回值是给人看的字符串，不可程序化消费、不可断言。
- **必须用 `ConvertToChildExport<ClassExport>()`**：它会把 `Asset` 一起带过去；`new ClassExport(export)` 会留下 null 的 `Asset`，随后在 `GetClassTypeForAncestry` NRE。`TryReparse` 的失败分支专门打印「Asset 是否为空」就是为了验证这条。
- **临时目录复制由 `Prepare` 做、清理由调用方做**：UAssetAPI 以 `File.Open(ReadWrite)` 打开 `.uexp`/usmap，只读目录抛 `UnauthorizedAccessException`。清理放在 `Cli` 的 `finally` 里，因为 `Prepare` 之后还有多步操作，它自己没法知道什么时候用完。代价：调用方忘了删就残留 `%TEMP%/solstice-winter-probe-*`。
- **输出是 `string` 行而不是结构化记录**：诊断结果直接贴进报告/终端，没有机器消费者。代价：没法写断言测试。

---

### `Prepare(string assetPath, string usmapPath): (string Asset, string Usmap, string WorkDir)`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态。先探测源文件是否可写（`FileAccess.ReadWrite` 开一次）：可写 → 原样返回路径（`WorkDir` 为空串，不复制）；不可写 → 建临时目录、复制资产对与 usmap 并返回新路径。副作用：仅在需要时建目录、复制文件。抛：源文件不存在（`FileNotFoundException`）、目标不可写（`UnauthorizedAccessException`/`IOException`）。

**输入输出**：`assetPath` 是 `.uasset`（扩展名不校验，按原样复制并用 `Path.GetFileName` 定目标名）；同名 `.uexp` 存在则一起复制，不存在则跳过（后续解析可能抛）。`usmapPath` 复制成固定的 `game.usmap`。返回值 `WorkDir` 是 `%TEMP%/solstice-winter-probe-<8 位 guid>`，`Asset` 在 `WorkDir` 下、`Usmap` 是 `WorkDir/game.usmap`。

**关键分支（伪代码）**：

```text
work = %TEMP%/solstice-winter-probe-<8位 guid>
Directory.CreateDirectory(work)
asset = work/GetFileName(assetPath);  File.Copy(assetPath, asset, overwrite: true)
uexp = ChangeExtension(assetPath, ".uexp")
if File.Exists(uexp): File.Copy(uexp, ChangeExtension(asset, ".uexp"), overwrite: true)
usmap = work/game.usmap;  File.Copy(usmapPath, usmap, overwrite: true)
return (asset, usmap, work)
```

**边界与失败**：

- ⛔ 只复制 `.uasset` + `.uexp`，不复制 `.ubulk` / `.uptnl`：依赖 ubulk 的资产在这里就注定读不全。
- ⛔ 不清理：每次 `probe` 留一个 `%TEMP%/solstice-winter-probe-*` 目录，靠 `Cli` 的 `finally` 删；`Cli` 里删除失败也被吞掉。
- 同名多资产会冲突：目标名只用文件名，所以一次只能探一个资产（调用约定如此）。
- 源目录只读是安全的（只读源、写临时）；需要 %TEMP% 可写。
- 失败后重试安全（每次新 guid，不覆盖旧目录）。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `probe` 分支调用（随后把 `workAsset` / `workUsmap` 交给 `Exports` 与 `TryReparse`，并在 `finally` 里删 `WorkDir`）；调用 `Directory.CreateDirectory`、`File.Copy`、`Path.ChangeExtension`。

---

### `Exports(string assetPath, Usmap usmap): IReadOnlyList<string>`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态。解析资产并把导出表逐行格式化。副作用：读文件、分配 `UAsset`（不缓存）。抛：`UAsset` 构造/解析的异常**不 catch**，原样上抛。

**输入输出**：第一行是 `包名 {FolderName}  导出 N  导入 M`；之后每个导出一行：`[{序号,3}] {导出类型名,-22} {ObjectName,-42} class={类名,-28} size={SerialSize}`。导出类型名是 `export.GetType().Name`——`NormalExport` / `FunctionExport` / `ClassExport` / `StructExport` / **`RawExport`**（降级的信号）。类名由 `ClassNameOf` 从 `ClassIndex` 翻成短名。实测 `BP_Logic`：第一行 `包名 … 导出 199 导入 661`，能看到哪些导出被降级成 `RawExport`。

**关键分支（伪代码）**：

```text
asset = new UAsset(assetPath, VER_UE5_6, usmap)          # 可能抛，不 catch
lines = [$"包名 {asset.FolderName}  导出 {asset.Exports.Count}  导入 {asset.Imports.Count}"]
for i in 0..Exports.Count-1:
    e = Exports[i]
    lines.Add($"[{i+1,3}] {e.GetType().Name,-22} {e.ObjectName,-42} " +
              $"class={ClassNameOf(asset, e.ClassIndex),-28} size={e.SerialSize}")
return lines
```

**边界与失败**：

- ⛔ 坏资产在这里就抛（`UAsset` 构造失败），连第一行都拿不到——那种情况下只能先修资产或换 usmap。
- `size` 是 `SerialSize`（序列化字节数），不是解压后大小、不是字节码大小。
- `ObjectName` 为 null 时该列打印空；`ClassNameOf` 越界返回 `?`。
- 行宽用负数对齐（`-22` / `-42` / `-28`），名字超长会把列挤歪，不截断。
- 纯读，可重试。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `probe` 分支调用；调用 `new UAsset`、`ClassNameOf`。

---

### `TryReparse(string assetPath, Usmap usmap, int exportIndex): string?`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态。手动按 `ClassExport` 重读第 `exportIndex` 个导出（1 基）。返回人类可读的结果字符串：成功分支一行，失败分支多行（含栈）。**不抛**——所有异常都变成返回值的一部分。返回类型是 `string?` 但两个分支都返回非 null。

**输入输出**：`exportIndex < 1` 或 `> Exports.Count` → `"导出下标越界"`。成功 → `成功 → ClassExport  super=index {N}  属性={K}  FuncMap={M}`（`super` 在 `SuperStruct` 为 null 时是 `?`）。失败 → `失败 → {异常类型}: {消息}` + 5 段诊断：

```text
  ClassIndex={N} → {DescribeIndex 的结果}
  Asset 是否为空: 是/否
  导入表条数: {M}
  {StepByStep 的结论}
  {SuperStructDiag 的结论}
{异常栈}
```

**关键分支（伪代码）**：

```text
asset = new UAsset(assetPath, VER_UE5_6, usmap)
if exportIndex < 1 or exportIndex > asset.Exports.Count: return "导出下标越界"
try:
    reader = asset.PathToReader(assetPath)
    e = asset.Exports[exportIndex-1]
    end = (int)(e.SerialOffset + e.SerialSize)          # 边界 = 本导出体结束
    reader.BaseStream.Seek(e.SerialOffset)
    parsed = e.ConvertToChildExport<ClassExport>()      # 不能用 new ClassExport(e)：会丢 Asset
    parsed.Read(reader, end)
    return $"成功 → ClassExport  super=…  属性={parsed.LoadedProperties?.Length ?? 0}  FuncMap={parsed.FuncMap?.Count ?? 0}"
catch e:
    failed = asset.Exports[exportIndex-1]
    return "失败 → …" + DescribeIndex(failed.ClassIndex) + Asset是否为null + 导入表条数
           + StepByStep(asset, failed.ClassIndex) + SuperStructDiag(asset) + e.StackTrace
```

**边界与失败**：

- ⛔ 成功分支只给**数量**（属性数 / FuncMap 数），不给属性名；要名字得看 `Exports` 或走 `AssetHeaderReader.Read`。
- ⛔ 失败分支的 `Asset 是否为空` 直接验证「`new ClassExport(export)` 留下 null Asset → `GetClassTypeForAncestry` NRE」这个坑：是 → 说明导出实例没带上资产引用。
- ⛔ 每次调用都新建一个 `UAsset`：`Exports` 之后再来一次，等于解析两遍。
- `(int)` 截断：单导出体 > 2 GB 会溢出（实际 5 KB 量级）。
- 失败分支把 `e.StackTrace` 一起塞进返回值，多行且不稳定（不同运行时行号不同），别拿它做断言。
- 纯读，可重试。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program.IndexCommand` 的 `probe` 分支在给了 `--reparse N` 时调用；调用 `new UAsset`、`asset.PathToReader`、`export.ConvertToChildExport<ClassExport>()`、`ClassExport.Read`、`DescribeIndex`、`StepByStep`、`SuperStructDiag`。

---

### `SuperStructDiag(UAsset asset): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：私有静态。扫导入表里 `OuterIndex` 为 null 的条目，返回一行诊断（以两个空格开头、以 `\n` 结尾）。无副作用。

**输入输出**：没有这种条目 → `"  导入表里没有 OuterIndex=null 的条目\n"`；有 N 个 → `"  导入表里 OuterIndex=null 的条目 {N} 个（NRE 源头）：{前 3 个}\n"`，每个条目格式 `#{序号} idx={负索引} '{ObjectName}' class='{ClassName}'`。

**关键分支（伪代码）**：

```text
nulls = []
for i in 0..Imports.Count-1:
    if Imports[i].OuterIndex is null: nulls.Add($"#{i+1} idx={-(i+1)} '{ObjectName}' class='{ClassName}'")
if nulls.Count == 0: return "  导入表里没有 OuterIndex=null 的条目\n"
return $"  导入表里 OuterIndex=null 的条目 {nulls.Count} 个（NRE 源头）：{join("; ", 前3个)}\n"
```

**边界与失败**：⛔ 参数 `exportIndex` **没有被使用**（签名遗留），传什么都一样；只报前 3 个条目（更多的不打印）；只扫 `OuterIndex is null`，不扫 `OuterIndex.Index == 0` 这种「非 null 但为空」的形态。不抛。

**调用者与被调用者**：只被 `TryReparse` 的失败分支调用；不调用别的函数。

---

### `StepByStep(UAsset asset, FPackageIndex? classIndex): string`

**状态**：✅ 已实现

**签名与契约**：私有静态。复现 UAssetAPI `GetClassTypeForAncestry` 的每一步，指出到底是哪一步为空/越界。返回一行（两个空格开头、`\n` 结尾）。无副作用、不抛。

**输入输出**：可能的返回值（按分支）：

| 条件 | 输出 |
|---|---|
| `classIndex` 为 null | `  classIndex 为 null` |
| `Index >= 0` | `  classIndex={N} 不是导入` |
| `importIndex` 越界 | `  importIndex={N} 越界（共 {M}）` |
| `imp.OuterIndex` 为 null | `  import.OuterIndex 为 null` |
| `OuterIndex >= 0` | `  import.OuterIndex={N} 不是导入` |
| `outerIndex` 越界 | `  outerIndex={N} 越界` |
| 走到头 | `  outer import='{名}' class='{类}' ObjectName 为 null: 是/否` |

**关键分支（伪代码）**：

```text
if classIndex is null: return "  classIndex 为 null\n"
if classIndex.Index >= 0: return $"  classIndex={i} 不是导入\n"
importIndex = -classIndex.Index - 1
if importIndex >= Imports.Count: return "  importIndex 越界…\n"
imp = Imports[importIndex]; outer = imp.OuterIndex
if outer is null: return "  import.OuterIndex 为 null\n"
if outer.Index >= 0: return "  import.OuterIndex=N 不是导入\n"
outerIndex = -outer.Index - 1
if outerIndex >= Imports.Count: return "  outerIndex 越界\n"
return $"  outer import='…' class='…' ObjectName 为 null: …\n"
```

**边界与失败**：只走「类索引 → 导入 → 它的外层导入」这一支，不递归、不检查外层外层的 Outer；不判断 `ClassName` 是否可解析。返回值只给人看。

**调用者与被调用者**：只被 `TryReparse` 的失败分支调用；不调用别的函数。

---

### `DescribeIndex(UAsset asset, FPackageIndex? index): string`

**状态**：✅ 已实现

**签名与契约**：私有静态。把一个 `FPackageIndex` 描述成单行短文本（不带换行）。无副作用、不抛。

**输入输出**：`null` → `<null>`；`0` → `<0>`；正数 → `export '{ObjectName}'`（越界 `<导出越界>`）；负数 → `import '{ObjectName}' class='{ClassName}' outer={N}`（`OuterIndex` 为 null 时 `outer=null`；越界 `<导入越界>`）。

**关键分支（伪代码）**：

```text
if index is null: return "<null>"
if index.Index == 0: return "<0>"
if index.Index > 0:
    i = index.Index-1; return i < Exports.Count ? $"export '{Exports[i].ObjectName}'" : "<导出越界>"
j = -index.Index-1
if j >= Imports.Count: return "<导入越界>"
return $"import '{Imports[j].ObjectName}' class='{Imports[j].ClassName}' outer={Imports[j].OuterIndex?.Index 或 null}"
```

**边界与失败**：只给短名（不带包路径），所以两个同名导入在这行输出里分不开——它只用于失败现场定位，不用于标识。不抛。

**调用者与被调用者**：只被 `TryReparse` 的失败分支调用；不调用别的函数。

---

### `ClassNameOf(UAsset asset, FPackageIndex? classIndex): string`

**状态**：✅ 已实现

**签名与契约**：私有静态。把导出的 `ClassIndex` 翻成类名短名。无副作用、不抛。

**输入输出**：`null` 或 `0` → `None`；正数 → `Exports[i].ObjectName`（越界或名字为 null → `?`）；负数 → `Imports[j].ObjectName`（越界或名字为 null → `?`）。

**关键分支（伪代码）**：

```text
if classIndex is null or classIndex.Index == 0: return "None"
if classIndex.Index > 0:
    i = classIndex.Index-1; return i < Exports.Count ? Exports[i].ObjectName?.ToString() ?? "?" : "?"
j = -classIndex.Index-1
return j < Imports.Count ? Imports[j].ObjectName?.ToString() ?? "?" : "?"
```

**边界与失败**：只给短名；`BlueprintGeneratedClass` 与 `WidgetBlueprintGeneratedClass` 都显示原名，不做后缀归类。不抛。

**调用者与被调用者**：只被 `Exports` 调用；不调用别的函数。



