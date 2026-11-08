# Signatures — 函数签名与引脚类型推断

**职责一句话**：把「函数名」变成「参数列表 + 类型 + 方向」，供调用节点造引脚；同时把 UAssetAPI 的 `FProperty` 映射成引脚类型。

**对外入口**：`IFunctionSignatures.Find(name)`、`FunctionSignature`、`SignatureParam`、`PropertyTypeMapper.From/Direction`。

**内部结构**：

```text
FunctionWalker.BuildPins(parameters, signature)
 ├─ 有签名 → 参数脚用签名里的 Name + Type；有返回 → 加一个 ReturnValue 输出脚
 └─ 无签名 → arg{i} + 按表达式形态推断

签名来源（FunctionWalker.ResolveSignature）
 ├─ 正索引（本资产导出）→ FunctionExport.LoadedProperties
 ├─ 本地虚调用（无 stackNode）→ 在本资产导出里按名字找
 └─ 其余（/Script 导入）→ IFunctionSignatures.Find（App 层用 jmap 实现）
```

**依赖**：`UAssetAPI.FieldTypes`（FProperty）、`Model.GraphPinType`。实现方在 App 层（`IndexSignatures` / `JmapSignatures`）。

**取舍**：

- **接口放在 Decompiler，实现在 App**：反编译器只做「字节码 → 图」，签名是可选增强；让它依赖索引层会把两者绑死。
- **本资产优先于 jmap**：资产本体是唯一「写回去会影响运行时」的事实源（D19 的权威顺序），jmap 只是补充 `/Script`。
- **容器类型只取内层类型**：`Array<Int>` 记成 `Array + Int`，`Map<K,V>` 记成 `Map + V`。完整键值类型要等类型推断里程碑。

---

### `PropertyTypeMapper.From(FProperty?, Func<FPackageIndex?, string>?): GraphPinType`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。纯映射，不抛。`resolvePath` 用来把结构体/类的包索引变成对象路径。

**输入输出**：`FIntProperty`（`SerializedType = "IntProperty"`）→ `Int`；`FArrayProperty` → `Array + 内层类型`；`FStructProperty` → `Struct + 路径`；`FObjectProperty` → `Object + 类路径`；`FEnumProperty` → `Enum + 路径`；`FDelegateProperty` → `Delegate`；未知 → `Unknown`。

**关键分支（伪代码）**：

```text
switch property:
  FArrayProperty  → (内层类型, Container=Array)
  FSetProperty    → (元素类型, Container=Set)
  FMapProperty    → (值类型,   Container=Map)
  FStructProperty → Struct + resolvePath(Struct)
  FEnumProperty   → Enum + resolvePath(Enum)
  FObjectProperty → Object/Class + resolvePath(PropertyClass)
  FInterfaceProperty → Interface + 路径
  FDelegateProperty  → Delegate
  其它 → 读 SerializedType 名字（"IntProperty" → Int，未知 → Unknown）
```

**边界与失败**：

- ⛔ **`Map` 丢掉键类型**：只留值类型（`Map<K,V>` 显示成 `Map + V`）。
- ⛔ 蓝图定义的结构体（`S_*`）不在 usmap，路径解析可能拿不到 → `Struct` 但无 `SubCategoryObject`。
- ⛔ 标量走 `SerializedType.Value.Value` 字符串匹配；遇到没列出的类型（如 `MulticastInlineDelegateProperty`）落 `Unknown`。

**调用者与被调用者**：被 `FunctionWalker.SignatureFromExport` 调用；不调用其它函数。

### `PropertyTypeMapper.Direction(EPropertyFlags): (bool IsReturn, bool IsOut)`

**状态**：✅ 已实现

**签名与契约**：`public static`。纯判定。`CPF_ReturnParm` → 返回；`out && !ref` → 真输出；其余入参。

**输入输出**：`CPF_ReturnParm` → `(true, false)`；`CPF_OutParm | CPF_ReferenceParm` → `(false, false)`（按引用入参）；`CPF_OutParm` → `(false, true)`。

**关键分支（伪代码）**：

```text
if flags 有 ReturnParm: return (true, false)
isOut = 有 OutParm 且 没有 ReferenceParm
return (false, isOut)
```

**边界与失败**：⛔ 不区分 `CPF_ConstParm`（常量入参）——对画布显示没有影响。

**调用者与被调用者**：被 `FunctionWalker.SignatureFromExport` 调用。
