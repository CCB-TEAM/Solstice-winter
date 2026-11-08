# ViewModels.GraphNodeVm — 画布的三种数据形状（引脚 / 连线 / 节点）

**职责一句话**：定义画布层的三个纯数据对象：`ConnectorVm`（一个引脚）、`ConnectionVm`（一条连线）、`GraphNodeVm`（一个节点），它们同时就是 Nodify 与自绘 `NodeCanvas` 共用的契约。

**对外入口**：三个类型本身。可写的只有 `ConnectorVm.Anchor` / `ConnectorVm.IsConnected`、`GraphNodeVm.Location`；其余是 `init` 只读属性。集合 `Inputs` / `Outputs` 是 `ObservableCollection`，构造后可增删。

**内部结构**：

```text
GraphNodeVm
 ├─ Title / Subtitle / Kind / Tooltip / IsOpaque     init 只读（Kind ∈ Exec|Call|Data|Branch|Set|Return|Opaque）
 ├─ Location : Point                                可写，绑到 Nodify 的 ItemContainer.Location
 ├─ Inputs   : ObservableCollection<ConnectorVm>    输入脚（exec 排前面由 AllInputs 保证）
 └─ Outputs  : ObservableCollection<ConnectorVm>    输出脚

ConnectionVm(Output, Input)                           Output = 上游引脚，Input = 下游引脚
 └─ IsExec : bool                                    线型（exec 粗实线 / 数据细虚线）

ConnectorVm
 ├─ Title / IsInput / IsExec / TypeName / Brush      init 只读
 ├─ IsUnknown                                        TypeName ∈ {"unknown",""} 的派生属性
 ├─ Anchor : Point                                   由视图回填（Nodify 的 Connector.Anchor，OneWayToSource）
 └─ IsConnected : bool                               由 ConnectionVm 构造时置 true
```

**依赖**：`System.Windows.Point` / `System.Windows.Media.Brush`（WPF 类型泄漏进 VM，见取舍）、`System.ComponentModel.INotifyPropertyChanged`。被 [GraphBuilder](Graph.GraphBuilder.md) 生产，被 [Views.GraphView](Views.GraphView.md) 与 [Controls.NodeCanvas](Controls.NodeCanvas.md) 消费。

**取舍**：

- **VM 里直接用 `Point` / `Brush`**：这两个是 WPF 类型，纯 MVVM 会把它们换成自定义结构再由视图转换。这里不换——`NodeCanvas` 与 Nodify 都需要坐标和颜色，中间加一层转换只会多两处映射。代价：这一层 VM 不可移植到非 WPF 宿主。
- **`Anchor` 由视图回填（`OneWayToSource`）而不是图构建时算**：图构建阶段不知道节点实际尺寸与引脚排布，算出来的坐标一定和渲染结果对不上。让控件把自己真实的锚点写回 VM，连线就永远对。代价：**首次布局完成前 `Anchor` 是 `(0,0)`**，连线会先画到原点一次；也让「换画布库」变成「换一个会回填 Anchor 的视图」，这正是 `NodeCanvas` 能与 Nodify 共存的原因。
- **`IsConnected` 在 `ConnectionVm` 构造时置 true**：Nodify 的 `Connector.IsConnected` 是只读的视觉状态，用它画「已连/未连」的差别（实心点 vs 空心点）。代价：断开连线必须重新构造对象，不能就地改——现在也没有断开操作。
- **`ConnectionVm` 有两个构造函数**：2 参版（`IsExec` 恒 `false`、不动 `IsConnected`）是给「只关心两端」的调用方留的，3 参版才是生产路径。代价：误用 2 参版会得到一条「数据线 + 两端都显示未连接」，是个静默的坑。
- **`Kind` 是字符串而不是枚举**：XAML 里要靠它做触发器/配色，字符串省一层转换器。代价：拼错不报错，拼错的表现是「配色退回默认」。

---

### `ConnectorVm`

**状态**：✅ 已实现

**签名与契约**：`sealed class : INotifyPropertyChanged`。`Title` / `IsInput` 是 `required init`（不写就编译不过）；`IsExec` 默认 `false`；`TypeName` 默认 `"unknown"`；`Brush` 默认 `Brushes.Gray`。`Anchor`（默认 `(0,0)`）与 `IsConnected`（默认 `false`）可写并触发通知。`IsUnknown` 是派生属性，**不触发通知**（`TypeName` 是 init 只读，本来也不会变）。

**输入输出**：

- `Title`：引脚显示名。exec 脚叫 `"exec"`、分支的两个出脚叫 `"true"` / `"false"`、循环叫 `"body"` / `"done"`、条件叫 `"cond"`、参数叫 `"arg0"` / `"arg1"`…、返回值叫 `"value"` / `"return"`（见 `GraphBuilder`）。
- `TypeName`：`"exec"` / `"bool"` / `"int"` / `"float"` / `"double"` / `"string"` / `"name"` / `"text"` / `"object"` / `"struct"` / `"value"` / `"var"` / 变量名 / `"unknown"`。由 `GraphBuilder.ArgTypeName` 决定。
- `Brush`：由 `GraphBuilder.BrushOf(TypeName)` 给出；`NodeCanvas` 画点时 `IsExec` 优先于 `Brush`（exec 恒白）。
- `Anchor`：屏幕坐标（相对画布），由视图回填；写入即通知，`CircuitConnection.Source/Target` 直接绑它。

**关键分支（伪代码）**：

```text
IsUnknown => TypeName is "unknown" or ""          # 派生属性，无通知
set Anchor v:    _anchor = v; notify              # 视图 OneWayToSource 写进来
set IsConnected v: _isConnected = v; notify
```

**边界与失败**：

- ⛔ `Anchor` 在视图布局完成前恒为 `(0,0)`：所有连线的起点/终点会先在原点重合一次（Nodify 随后刷新）。不是错误，但截图时可能看到「一团线在左上角」。
- ⛔ `IsUnknown` 为 `true` 只影响画法（灰色点 + 描边），**不阻止连线**——设计文档 §8 说「unknown 不可连」，当前只读渲染没有拦截逻辑（也没有连线操作可拦）。
- ⛔ `Brush` 是 `init` 且由 `GraphBuilder` 硬编码 RGB，不读 `Themes/Dark.xaml` 的 `Pin.*` 资源（见 [Themes.Dark](Themes.Dark.md) 边界）。

**调用者与被调用者**：由 `GraphBuilder.ExecPin` / `GraphBuilder.DataPin` 构造；`GraphBuilder.Connect` 读它构造 `ConnectionVm` 并把 `IsConnected` 置 `true`；`GraphBuilder.BuildChain` / `BuildArg` 读 `IsExec` / `Title`；Nodify 的 `NodeInput` / `NodeOutput` 样式绑定 `Anchor` / `IsConnected`；`NodeCanvas.AnchorOf` / `CreatePinRow` 读它算坐标与画点。不调用其它函数。

---

### `ConnectionVm(ConnectorVm output, ConnectorVm input)`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：主构造函数。只存两个引脚，`IsExec` 保持默认 `false`，**不改两端引脚的 `IsConnected`**。无 I/O、无通知。

**输入输出**：`output` = 上游引脚，`input` = 下游引脚（语义约定，不做校验：传反了不会报错，只会画出一条反向的线）。`IsExec` 恒 `false`。

**关键分支（伪代码）**：

```text
# 无分支
Output = output; Input = input; IsExec = false（默认值）
```

**边界与失败**：⛔ 这是「只给两端」的简化入口：用它建出的连线在视觉上是数据线（细虚线），并且两端引脚仍显示「未连接」。当前生产路径**没有**用它（`GraphBuilder.Connect` 一律走 3 参版）。

**调用者与被调用者**：当前无生产调用者（仅被 3 参构造函数链式调用）；不调用其它函数。

---

### `ConnectionVm(ConnectorVm output, ConnectorVm input, bool isExec)`

**状态**：✅ 已实现

**签名与契约**：生产路径用的构造函数。先链到 2 参版，再设置 `IsExec`，并把 `output.IsConnected` / `input.IsConnected` 都置 `true`（会触发这两个引脚的通知）。无 I/O。

**输入输出**：`isExec == true` → 画成粗实线（`CircuitConnection` 默认样式 / `NodeCanvas` 的 2.2px 白线）；`false` → 细虚线（1.4px 蓝灰 + `StrokeDashArray=[4,3]`）。副作用：两个引脚的 `IsConnected` 从 `false` 变 `true`。

**关键分支（伪代码）**：

```text
this(output, input)                # 链到 2 参版
IsExec = isExec
output.IsConnected = true          # 触发 PropertyChanged → Nodify 的 Connector 变实心
input.IsConnected  = true
```

**边界与失败**：

- ⛔ 一个引脚可以被多条 `ConnectionVm` 引用（`IsConnected` 只置 `true`、从不置回 `false`）：删线时必须自己保证「没有别的线还连着它」，否则会显示成已连接而实际悬空。
- ⛔ `output` / `input` 为 `null` 时在 `output.IsConnected = true` 处抛 `NullReferenceException`（`GraphBuilder.Connect` 因此先做了空判断）。

**调用者与被调用者**：被 `GraphBuilder.Connect` 调用；调用 `ConnectorVm` 的 `IsConnected` setter；被 `GraphView.xaml` 的 `ConnectionTemplate`（读 `Output.Anchor` / `Input.Anchor`）与 `NodeCanvas.CreateEdge`（读 `IsExec`）消费。

---

### `GraphNodeVm`

**状态**：✅ 已实现

**签名与契约**：`sealed class : INotifyPropertyChanged`。`Title` 是 `required init`；`Subtitle` / `Kind` / `Tooltip` / `IsOpaque` 有默认值（`""` / `"Call"` / `""` / `false`）；`Location` 可写并通知；`Inputs` / `Outputs` 是构造时就存在的空 `ObservableCollection`。无 I/O。

**输入输出**：

- `Title`：节点标题。`CallNode` → 函数名；`ContextCallNode` → `"<对象>.<函数>"`；`AssignNode` → `"Set <变量名>"`；`IfNode` → `"Branch"`；`LoopNode` → `"While"`；`BottomLoopNode` → `"DoWhile"`；`ReturnNode` → `"Return"`；不认识的一律 `类型名去掉 Node 后缀`。
- `Kind`：`"Call"` / `"Data"` / `"Branch"` / `"Set"` / `"Return"` / `"Opaque"`。`NodeCanvas` 用它选表头颜色；Nodify 视图**不用**它（模板只显示标题）。
- `Subtitle`：只有 `CreateCallNode` 会填（被调函数的归属路径），其余节点恒为空——当前两个视图都不显示它。
- `IsOpaque`：`true` 表示「还原不出结构的节点」（`RawNode` / `BlockGraphNode` / `BasicBlockNode` / `UberSectionNode`）。这类节点**没有引脚**（`GraphBuilder.CreateNode` 提前返回）。`NodeCanvas` 会在标题前加 `? `。
- `Location`：图坐标（像素），由 `GraphBuilder` 算出（cooked 资产里没有位置）。绑到 Nodify `ItemContainer.Location`。

**关键分支（伪代码）**：

```text
set Location v:  _location = v; notify        # Nodify 拖动节点会写回（当前 AllowDrop=False，拖拽仍可改位置）
# 其余属性均 init 只读，构造后不变
```

**边界与失败**：

- ⛔ `Location` 是 `init` 之后唯一可变的结构属性：拖动节点会改它，但**没有持久化**（设计文档 §6 的位置侧车文件还没做），切换函数后位置全部重算。
- ⛔ `IsOpaque` 的节点没有 `Inputs` / `Outputs`，视图上是「孤立无连线的方块」；它们出现在图里纯粹是为了不隐藏信息。
- ⛔ `Subtitle` / `Kind` / `IsOpaque` 在 Nodify 视图里没有任何视觉表现——同一份 VM 在两个视图下的观感不同（见 [Views.GraphView](Views.GraphView.md) 与 [Controls.NodeCanvas](Controls.NodeCanvas.md) 的取舍）。

**调用者与被调用者**：由 `GraphBuilder.CreateNode` / `CreateCallNode` 构造；被 `GraphBuilder.BuildChain`（读 `Inputs` / `Outputs` 找 exec 脚）、`GraphProbe.Run`（读 `Kind` / `Title` / `Location` / 引脚列表）、Nodify 的 `NodeTemplate` 与 `ItemContainerStyle`、`NodeCanvas.Render` / `CreateNodeVisual` 消费。

---

### `GraphNodeVm.AllInputs`

**状态**：✅ 已实现

**签名与契约**：只读计算属性，`IEnumerable<ConnectorVm>`。返回 `Inputs` 按「exec 优先」重排的序列（`IsExec ? 0 : 1` 稳定排序）。每次访问都重新 LINQ 求值，不缓存、不通知。

**输入输出**：输入 `Inputs` 的当前内容；输出一个惰性序列。`Inputs` 为空时返回空序列（不是 `null`）。

**关键分支（伪代码）**：

```text
AllInputs => Inputs.OrderBy(p => p.IsExec ? 0 : 1)      # 稳定排序：同类保持插入顺序
```

**边界与失败**：

- ⛔ 当前**没有任何视图或代码使用它**（Nodify 直接绑 `Inputs`，`NodeCanvas` 用 `Inputs.ToList()`）。它是为「模板里想先画 exec 脚」留的口子，属于未接线的便利属性。
- ⛔ 每次访问都重新排序：绑到频繁刷新的模板上会有多余分配（现在没绑，无实际开销）。

**调用者与被调用者**：当前无调用者；读取 `Inputs` 与 `ConnectorVm.IsExec`。
