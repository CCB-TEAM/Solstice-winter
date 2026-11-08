# TypeFormatter — `FProperty` → 可读类型名，以及参数方向

**职责一句话**：把 UAssetAPI 的 `FProperty` 渲染成可读类型字符串，并按属性标志判定参数方向（`in` / `out` / `return`）。

**对外入口**：`TypeFormatter.Of(FProperty?, Func<FPackageIndex?, string>)`、`TypeFormatter.Direction(EPropertyFlags)`。`Scalar` 是私有。

**内部结构**：

```text
Of(property, resolve)
 ├─ null            → "?"
 ├─ 容器子类         → "Array<内层>" / "Set<元素>" / "Map<键, 值>"（递归 Of）
 ├─ 结构/枚举/接口/委托/对象 → resolve(对应 FPackageIndex)   ← 只有短名或全路径，由回调决定
 └─ 其余            → Scalar(property)                      ← 标量走这里
Direction(flags)   → CPF_ReturnParm? "return" : (OutParm && !ReferenceParm ? "out" : "in")
```

**依赖**：`UAssetAPI.FieldTypes`（`FProperty` 家族）、`UAssetAPI.UnrealTypes.EPropertyFlags`。被 `AssetHeaderReader.Read` 调用（变量一次、每个参数一次）；`resolve` 回调由 `AssetHeaderReader.ResolveIndex` 提供。

**取舍**：

- **先认子类，再退回 `SerializedType`**：实测（`BP_Logic`）标量类型在 UAssetAPI 里**没有专用子类**，都是 `FGenericProperty`，真实类型名只在 `SerializedType.Value.Value`；容器 / 对象 / 结构体 / 枚举才有专用子类。否决了「全用 `GetType().Name`」——那会把 4951 个 `IntProperty` 全渲染成 `Generic`。代价：出现新的专用子类时必须显式加分支，否则悄悄退化（见 `Of` 边界）。
- **`resolve` 用回调而不是把 `UAsset` 传进来**：包索引要靠该资产自己的 Import/Export 表解析，但本类不该持有资产。回调让本类保持纯函数、可单测。代价：每层递归多一次委托调用（可忽略），且必须由调用方保证 `resolve` 不抛。
- **方向只分三态，`out && ref` 归到 `in`**：实测口径是 `out && !ref` 才是真输出，`out && ref` 是「按引用传入并被写回」，在蓝图里表现为 in 引脚。否决了四态（in/out/ref/return）——画布上只有 in/out 两种引脚。代价：丢掉「这是 ref」这一位；需要时回查 `VariableInfo.Flags` 原文。
- **容器类型不缓存字符串**：每次渲染都重新拼。代价：`Array<IntProperty>` 这种字符串在 2137 个资产里重复 287 次，但内存压力可忽略，换来的是没有跨资产共享状态。

---

### `Of(FProperty? property, Func<FPackageIndex?, string> resolve): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：静态纯函数。不读文件、不写状态、不抛（除非 `resolve` 抛）。`property` 为 null 返回 `"?"`；否则按子类分派，容器递归调自己。

**输入输出**：

| 输入子类 | 输出 |
|---|---|
| `FArrayProperty` | `Array<` + `Of(Inner)` + `>` |
| `FSetProperty` | `Set<` + `Of(ElementProp)` + `>` |
| `FMapProperty` | `Map<` + `Of(KeyProp)` + `, ` + `Of(ValueProp)` + `>`（逗号后一个空格） |
| `FStructProperty` | `resolve(Struct)` |
| `FEnumProperty` | `resolve(Enum)` |
| `FInterfaceProperty` | `resolve(InterfaceClass)` |
| `FDelegateProperty` | `resolve(SignatureFunction)` |
| `FObjectProperty` | `resolve(PropertyClass)` |
| 其余 | `Scalar(property)` |

`resolve` 返回 `"None"`（索引为 0 或 null）时原样返回 `"None"`，不会替换成 `"?"`。实测输出形态：`IntProperty`、`Array<IntProperty>`、`Map<StrProperty, IntProperty>`、`/Script/Engine.Actor`、`/Game/Assets/Materials/BackgroundCubeMaterial.BackgroundCubeMaterial`。

**关键分支（伪代码）**：

```text
if property is null: return "?"
switch property:
    FArrayProperty  a → "Array<" + Of(a.Inner, resolve) + ">"
    FSetProperty    s → "Set<"   + Of(s.ElementProp, resolve) + ">"
    FMapProperty    m → "Map<" + Of(m.KeyProp) + ", " + Of(m.ValueProp) + ">"
    FStructProperty st→ resolve(st.Struct)
    FEnumProperty   en→ resolve(en.Enum)
    FInterfaceProperty it → resolve(it.InterfaceClass)
    FDelegateProperty  d  → resolve(d.SignatureFunction)
    FObjectProperty    o  → resolve(o.PropertyClass)
    default            → Scalar(property)      # 标量：SerializedType 里才有真名
```

**边界与失败**：

- ⛔ **软/弱/懒对象引用会退化**：`FSoftObjectProperty` / `FWeakObjectProperty` / `FLazyObjectProperty` 是 `FObjectPropertyBase` 的兄弟类，**不匹配** `FObjectProperty` 分支，于是走 `Scalar`，输出 `SoftObjectProperty` 这种没有目标名的字符串。触发条件：资产里有软引用属性。本批 2137 个资产未出现该类型，所以未实测。
- ⛔ **裸短名**：`resolve` 对「本包导出索引」只返回 `ObjectName`，对「导入链断在第一步」的也只返回短名。实测索引里 5 处 `Type` 是裸 `BP_BaseCard_C`（`cardBuffContainer_Widget` 的变量与几个委托参数），从类型名看不出它属于哪个包，同名跨包类型无法区分。
- 递归深度不受限：畸形资产可以造出深嵌套，本批实测深度 < 5。
- 不抛异常：失败一律表现为字符串退化，不会中断 `AssetHeaderReader.Read`。
- `FClassProperty` 继承 `FObjectProperty`，所以类引用会被正确解析成类路径；`FByteProperty` / `FBoolProperty` 等标量无专用子类，走 `Scalar`。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 调用（每个变量、每个参数各一次）；递归调用 `Of` 自己；调用 `Scalar` 与传入的 `resolve`（实际是 `AssetHeaderReader.ResolveIndex`）。

---

### `Scalar(FProperty property): string`

**状态**：✅ 已实现

**签名与契约**：私有静态纯函数。给 `Of` 的 `default` 分支兜底：先取 `SerializedType`，取不到再用 CLR 类型名。

**输入输出**：返回 `property.SerializedType?.Value?.Value`；为 null 时返回 `property.GetType().Name.Replace("Property", "")`。实测标量输出：`IntProperty`（4951 处）、`BoolProperty`（3057）、`StrProperty`（1717）、`DoubleProperty`（251）、`FloatProperty`（234）、`ByteProperty`（159）、`NameProperty`（112）、`TextProperty`（59）——注意 `SerializedType` 分支返回的名字**自带 `Property` 后缀**，与兜底分支的形态不同。

**关键分支（伪代码）**：

```text
v = property.SerializedType?.Value?.Value
if v != null: return v                      # 正常路径：usmap 给了类型名
return property.GetType().Name.Replace("Property", "")   # 兜底：去掉后缀
```

**边界与失败**：两个来源都可能给出空字符串（`SerializedType.Value` 为 `""` 时直接返回空串，不会走到兜底），于是类型名渲染成空白——`VariableInfo.Type` 会是空字符串，不是 `"?"`。usmap 里缺该属性类型时 `SerializedType` 为 null，走兜底得到 `Generic` / `Byte` 之类。

**调用者与被调用者**：只被 `Of` 的 `default` 分支调用；不调用别的函数。

---

### `Direction(EPropertyFlags flags): string`

**状态**：✅ 已实现

**签名与契约**：静态纯函数。把属性标志翻成参数方向三态。无 I/O、不抛。

**输入输出**：先判 `CPF_ReturnParm` → `"return"`；否则取 `CPF_OutParm` 与 `CPF_ReferenceParm`：`out && !ref` → `"out"`，其余（含 `out && ref`、两者都没有）→ `"in"`。实测全库 14343 个参数：`in` 10763、`out` 3538、`return` 42。

**关键分支（伪代码）**：

```text
if flags & CPF_ReturnParm:   return "return"     # 返回值优先，即使同时带 OutParm
isOut = flags & CPF_OutParm
isRef = flags & CPF_ReferenceParm
if isOut && !isRef:          return "out"        # 真输出
return "in"                                      # 含 out && ref（按引用入参）
```

**边界与失败**：

- ⛔ 本函数**不看 `CPF_Parm`**：调用方必须先按 `CPF_Parm` 过滤，否则 `CallFunc_*` / `K2Node_*` / `Temp_*` 这些局部变量会被判成参数（实测一个函数最多几十个临时变量）。
- ⛔ 畸形组合（同时 `ReturnParm` + `OutParm` + `ReferenceParm`）不会报错，`return` 胜出。
- 不抛、无状态，可任意重入。

**调用者与被调用者**：被 `AssetHeaderReader.Read` 调用（每个 `CPF_Parm` 属性一次）；不调用别的函数。
