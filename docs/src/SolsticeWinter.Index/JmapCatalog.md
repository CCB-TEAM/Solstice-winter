# JmapCatalog — `/Script` 函数签名（jmap 产物）

**职责一句话**：读 `functions.json`（jmap 产物），把 13k 条 `/Script` 函数签名变成可按短名查的表，供索引与图构建补「参数名 / 参数类型 / 返回类型」。

**对外入口**：`JmapCatalog.Load(string)`、`Find(string)`、`Describe()`、`ByName`、`Functions`、`Empty`。

**内部结构**：

```text
functions.json
 └─ objects: { "/Script/Engine.KismetSystemLibrary": { functions: { "PrintString": { flags, params[] } } } }
      ↓ Load()
    List<ScriptFunction>（Owner / Name / Flags / Params）
      ↓ 按短名分组
    ByName: 短名 → List<ScriptFunction>
```

**依赖**：`System.Text.Json`。被 `SymbolIndex.Build`（并入索引）与 `SolsticeWinter.App.GraphBuilder`（补引脚名）使用。

**取舍**：

- **只取 `/Script`**：jmap 里 `/Game` 侧没有函数签名（只有资产条目），`/Game` 的签名由资产本体提供。两者在索引里分开存，不混成一张表。
- **一次性 `JsonDocument`**：实测 5.3 MB / 13064 条，几百毫秒。否决了流式解析——索引构建本来就是秒级，流式只会让代码复杂。
- **保留原始 `flags` 文本**：`FUNC_Static` / `FUNC_BlueprintPure` 是判调用形态的依据，不预先解析成枚举（形态判定在 `Solstice.Kismet` 里已经有一份，重复建模会分叉）。

---

### `JmapCatalog.Load(string path): JmapCatalog`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。读整个文件、解析 `objects[*].functions[*]`，返回目录。副作用：只读文件。抛：`FileNotFoundException`（路径不存在）、`JsonException`（文件不是合法 JSON）。

**输入输出**：正常文件 → `Functions` 13064 条、`ByName` 11624 个短名（实测 KARDS 的 `functions.json`）。文件里没有 `objects` 键 → 返回空目录（不抛）。没有 `functions` 的对象（类、结构体、资产条目）直接跳过。

**关键分支（伪代码）**：

```text
doc = JsonDocument.Parse(stream)
if !doc.RootElement.TryGetProperty("objects"): return 空目录
for owner in objects:
    if !owner.functions: continue                      # 触发条件：该对象没有函数表
    for fn in owner.functions:
        flags = fn.flags ?? ""
        params = fn.params.Select(p => (p.name, p.type, p.dir ?? "in", p.class))
        add ScriptFunction(ownerName, fnName, flags, params)
return catalog
```

**边界与失败**：

- ⛔ 不校验 `metadata`（extractor 版本、引擎版本）；文件来自别的引擎也能读，可能对不上。
- ⛔ 不处理 `struct` / `inner` / `key` / `value` 这些容器字段（`functions.json` 里有，这里只读 `class`）。容器元素类型要等类型推断（D13 图 IR）再做。
- ⛔ 大文件整读进内存：5.3 MB 可接受，再大要改流式。

**调用者与被调用者**：被 `SymbolIndex.Build` 调用；调用 `JsonDocument.Parse`。

---

### `JmapCatalog.Find(string name): IReadOnlyList<ScriptFunction>`

**状态**：✅ 已实现

**签名与契约**：`public`。按短名查候选，纯读、不抛。未命中返回空数组（不是 null）。

**输入输出**：`"PrintString"` → 1 条（`/Script/Engine.KismetSystemLibrary.PrintString`）；`"Map_Add"` → 1 条；同名跨类时返回多条。

**关键分支（伪代码）**：

```text
return ByName.TryGetValue(name, out hits) ? hits : []
```

**边界与失败**：⛔ 只认短名，不认全路径（`/Script/Engine.KismetSystemLibrary.PrintString` 查不到）；需要全路径时自己遍历 `Functions`。

**调用者与被调用者**：被 `GraphBuilder.AddParamPins` 的 `SignatureOf` 回调、`GraphProbe` 使用；调用 `Dictionary.TryGetValue`。

---

### `JmapCatalog.Describe(): string`

**状态**：✅ 已实现

**签名与契约**：`public`。一行摘要，纯读、不抛。

**输入输出**：`"jmap 函数 13064 条，唯一短名 11624 条"`。

**关键分支（伪代码）**：无分支。

**边界与失败**：无。

**调用者与被调用者**：诊断用；调用 `Functions.Count` / `ByName.Count`。
