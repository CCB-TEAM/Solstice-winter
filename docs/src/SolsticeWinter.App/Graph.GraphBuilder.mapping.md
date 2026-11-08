# Graph.GraphBuilder.mapping — 类型与外观映射表

> 本文件是 [Graph.GraphBuilder](Graph.GraphBuilder.md) 的拆分（原 md 超过 400 行）：模块职责、布局常量、取舍见主文件；这里只有三张映射表（`Describe` / `ArgTypeName` / `BrushOf`）的五段。

这三张表决定了「节点长什么样、引脚什么颜色」——它们也是**新增 `KNode` / `KArg` 类型时唯一必须同步的地方**。

---

### `GraphBuilder.Describe(KNode node): (string Title, string Kind, string Tooltip)`

**状态**：✅ 已实现

**签名与契约**：`private static`。`switch` 表达式把节点映射成三元组（标题、种类、提示）。纯函数、不抛。

**输入输出**（实测出现过的映射，`Tooltip` 一律取 `node.Title`）：

| 节点 | Title | Kind |
|---|---|---|
| `CallNode` / `VirtualCallNode` / `LocalFinalCallNode` / `MathCallNode` | 函数名 | `Call` |
| `ContextCallNode` | `<ObjectVariable>.<Function>` | `Call` |
| `MulticastCallNode` | `Multicast <Function>` | `Call` |
| `IfNode` | `Branch` | `Branch` |
| `LoopNode` | `While` | `Branch` |
| `BottomLoopNode` | `DoWhile` | `Branch` |
| `AssignNode` / `AssignBoolNode` | `Set <VariableName>` | `Set` |
| `PersistentAssignNode` | `SetPersistent <Name>` | `Set` |
| `SetArrayNode` | `SetArray` | `Set` |
| `ReturnNode` | `Return` | `Return` |
| `ComputedJumpNode` | `ComputedJump` | `Opaque` |
| `PushFlowNode` / `PopFlowNode` / `PopFlowIfNotNode` | `PushFlow` / `PopFlow` / `PopFlowIfNot` | `Opaque` |
| `KismetDecompiler.RawNode` / `BlockGraphNode` / `BasicBlockNode` / `UberSectionNode` | `node.Title` | `Opaque` |
| 其它 | `类型名` 去掉 `Node` 后缀 | `Opaque` |

**关键分支（伪代码）**：

```text
switch node:
    CallNode n         => (n.Function, "Call", n.Title)
    ContextCallNode n  => ($"{n.ObjectVariable}.{n.Function}", "Call", n.Title)
    ...
    _                  => (node.GetType().Name.Replace("Node", ""), "Opaque", node.Title)
```

**边界与失败**：

- ⛔ `Replace("Node", "")` 会替换**所有**出现：`IfNode` 本该是 `If`，但它在前面被专门匹配了；未被匹配的类型若名字里带两处 `Node`（如未来的 `FooNodeNode`）会变成 `Foo`。
- ⛔ `KismetDecompiler.RawNode` 是 `KismetDecompiler` 的**嵌套类型**，必须写全 `KismetDecompiler.RawNode`；写成 `RawNode` 或 `using static` 之外的简写会编译不过（这个坑踩过）。
- ⛔ `Kind` 只被 `NodeCanvas` 用来选表头色，Nodify 视图忽略它；`Set` / `Return` / `Data` 的区别在当前实际 UI 里看不出来。
- 标题文案（`"Branch"` / `"While"` / `"DoWhile"` / `"Set X"`）是硬编码中文/英文混排，值可能随迭代调整。

**调用者与被调用者**：只被 `CreateNode` 调用；不调用其它函数。

---

### `GraphBuilder.ArgTypeName(KArg arg): string`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private static`。`switch` 表达式把参数值映射成一个**类型名**字符串（决定引脚颜色与 `IsUnknown`）。纯函数、不抛。

**输入输出**：

| `KArg` | 返回 |
|---|---|
| `IntArg` / `LongArg` / `ByteArg` / `SkipOffsetArg` | `"int"` |
| `BoolArg` | `"bool"` |
| `FloatArg` | `"float"` |
| `DoubleArg` | `"double"` |
| `StringArg` / `UnicodeStringArg` | `"string"` |
| `NameArg` | `"name"` |
| `TextArg` | `"text"` |
| `ObjectArg` / `NoObjectArg` / `SelfArg` / `DynamicCastArg` / `InterfaceCastArg` / `MetaCastArg` / `SoftObjectArg` / `ContextMemberArg` / `InstanceDelegateArg` | `"object"` |
| `PointerArg p` | `p.Name` 为空时 `"var"`，否则**变量名本身** |
| `VectorArg` / `RotationArg` / `TransformArg` / `StructConstArg` / `StructMemberArg` / `ArrayConstArg` / `MapConstArg` | `"struct"` |
| `ArrayGetByRefArg` / `PrimitiveCastArg` / `InterfaceContextArg` / `CallArg` | `"value"` |
| 其它（含 `LocalArg` / `RawArg`） | `"unknown"` |

**关键分支（伪代码）**：

```text
switch arg:
    IntArg or LongArg or ByteArg or SkipOffsetArg => "int"
    ...
    PointerArg p => string.IsNullOrEmpty(p.Name) ? "var" : p.Name    # 变量名当类型名
    ...
    _ => "unknown"
```

**边界与失败**：

- ⛔ `PointerArg` 返回**变量名**而不是类型：详情面板里「类型」一栏会显示 `CallFunc_Foo_ReturnValue` 这种名字，颜色落到默认灰。真正的类型要靠 usmap/jmap，这里没接。
- ⛔ `LocalArg`（局部变量）落到 `"unknown"`：这类引脚显示成灰色 + 描边，看起来像「类型推断失败」，其实是**没有映射**。
- ⛔ 类型名集合与 `Themes/Dark.xaml` 的 `Pin.*` 键名（`Pin.Exec` / `Pin.Bool` / …）不是同一套：XAML 那边大写、这边小写，两边没有共享常量。
- ⛔ 布尔 `bool` 与 `Boolean` 只有 `BoolArg` 一条来源；结构体统一成 `"struct"`，看不出具体是哪个结构。

**调用者与被调用者**：被 `CreateNode` / `CreateCallNode` 调用；被 `DataPin` 间接消费返回值；不调用其它函数。

---

### `GraphBuilder.BrushOf(string typeName): Brush`

**状态**：✅ 已实现

**签名与契约**：`private static`。类型名 → 颜色（每次新建 `SolidColorBrush`，不缓存、不冻结）。纯函数、不抛。

**输入输出**（RGB 值，与 `Themes/Dark.xaml` 的 `Pin.*` 同值）：

| `typeName` | 颜色 |
|---|---|
| `exec` | `#F0F0F0`（白） |
| `bool` | `#C0392B`（红） |
| `int` | `#1ABC9C`（青绿） |
| `float` / `double` | `#2ECC71`（绿） |
| `string` / `name` / `text` | `#E056A0`（品红） |
| `object` | `#3B8ED0`（蓝） |
| `struct` | `#2C5AA0`（深蓝） |
| 其它（`value` / `var` / `unknown` / 变量名…） | `#7F8C8D`（灰） |

**关键分支（伪代码）**：

```text
switch typeName:
    "exec" => #F0F0F0
    "bool" => #C0392B
    "int"  => #1ABC9C
    "float" or "double" => #2ECC71
    "string" or "name" or "text" => #E056A0
    "object" => #3B8ED0
    "struct" => #2C5AA0
    _ => #7F8C8D
```

**边界与失败**：

- ⛔ 每次调用都 `new SolidColorBrush`：一张图几十个引脚就是几十个画刷对象，且都**没有 `Freeze()`**（跨线程访问会抛 `InvalidOperationException`，当前只在 UI 线程用）。要优化就缓存 + 冻结。
- ⛔ 与 `Themes/Dark.xaml` 的 `Pin.*` 重复定义同一组颜色：改配色必须两处都改，而真正生效的只有这一处（XAML 的 `Pin.*` 没有任何消费者）。
- ⛔ 颜色语义是「类型」而不是「连接状态」：`IsConnected` 不影响颜色，悬空脚与已连脚同色。

**调用者与被调用者**：被 `ExecPin` / `DataPin` 调用；不调用其它函数。
