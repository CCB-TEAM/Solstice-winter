# Controls.NodeCanvas — 自绘节点画布（回退实现，当前未被使用）

**职责一句话**：用 WPF `Canvas` + 贝塞尔曲线自己画一张节点图（节点框、引脚点、连线），作为 Nodify 不可用时的**回退视图**。

> **当前状态：没有被任何 XAML 引用。** `MainWindow.xaml` 的中栏用的是 `views:GraphView`（Nodify 版），`Controls` 命名空间在整个工程里除了本文件自身之外没有任何引用（`grep -r "controls:"` 与 `NodeCanvas` 都只命中本文件）。保留它是为了「Nodify 拿不到」时的退路：本机 NuGet 源曾经连不上，Nodify 装不上，那时全靠这个控件；现在 Nodify 7.3.0 可用，它退居备胎。**不要因为「没人用」就删掉**——它是「换画布库只换视图层」这条设计的证据。

**对外入口**：三个依赖属性 `Nodes` / `Connections` / `SelectedNode`，形状与 Nodify 视图完全一致（都吃 `IEnumerable<GraphNodeVm>` / `IEnumerable<ConnectionVm>` / `GraphNodeVm?`）。在 XAML 里的用法等价于：

```xml
<controls:NodeCanvas Nodes="{Binding Nodes}" Connections="{Binding Connections}"
                     SelectedNode="{Binding SelectedNode}" />
```

**内部结构**：

```text
NodeCanvas : UserControl
 └─ Grid(root, ClipToBounds, #1E1E1E)
     ├─ Rectangle(填 GridBrush 15×15 网格, IsHitTestVisible=false)
     └─ Canvas(_world)
         ├─ Canvas(_edges)   RenderTransform = Scale × Translate   ← 先画，被节点盖住
         └─ Canvas(_nodes)   RenderTransform = 同一个 TransformGroup
             每个节点 = Border(宽 240, 高 = 26 + 12 + max(输入数,输出数)×18)
             每条线   = Path(三次贝塞尔)

坐标：输入脚 x = 节点左边缘，输出脚 x = 左边缘 + 240；
      第 i 个脚 y = 节点顶 + 26 + 6 + i×18 + 9
```

常量（与节点视觉强耦合，改一个就要改另一个）：

| 常量 | 值 | 用途 |
|---|---|---|
| `NodeWidth` | `240` | 节点宽，也是输出脚相对节点左边缘的 x 偏移 |
| `HeaderHeight` | `26` | 表头高（标题行） |
| `PinRowHeight` | `18` | 每行引脚高 |
| `Padding` | `6` | 引脚区内边距（上下各一次） |

**依赖**：`SolsticeWinter.App.ViewModels`（`GraphNodeVm` / `ConnectorVm` / `ConnectionVm`）、`System.Windows.Shapes`（`Rectangle` / `Ellipse` / `Path`）、`System.Windows.Media`（`SolidColorBrush` / `PathGeometry` / `BezierSegment` / `DrawingBrush` / `ScaleTransform` / `TranslateTransform`）。不依赖 Nodify。**当前无调用者**（没有 XAML 实例化它）。

**取舍**：

- **为什么存在**：本机 NuGet 源曾经 SSL/认证失败，Nodify 装不上，而「画布能显示」是这条链路唯一能被人看到的结果，所以只能自己画。否决了「等 Nodify 可用再开工」——那会把整条链路的验证一起卡住。代价：现在有两套画布代码，改视觉要改两处（`NodeCanvas` 的 `HeaderBrush` 与 Nodify 的模板），而只有一套在跑。
- **VM 契约与 Nodify 一致（这是关键）**：`ConnectorVm.Anchor` 在 Nodify 版里由 `Connector.Anchor` 回填（`OneWayToSource`），本控件**不用**它——自己按常量算坐标。两条路径吃同一份 VM，所以换库只换视图层，`GraphBuilder` 与 VM 不动。代价：两套坐标算法（算的 vs 回填的）可能不一致，本控件的算法假设「节点宽 240、行高 18」，与 Nodify 模板（`MinWidth=160`、行高由内容决定）不同，两边的线位置会有差异。
- **引脚坐标算出来而不是量出来**：不依赖布局完成，`Render()` 一次就能画对。代价：字号/DPI 变化时引脚点与实际文字行会轻微错位（没有测量回填）。
- **没有节点拖拽/框选/连线交互**：只做「看」+「平移缩放」+「点选」。编辑相关的交互留给 Nodify 版。代价：它不能当编辑器用，只是渲染器。

---

### `NodeCanvas()`

**状态**：✅ 已实现

**签名与契约**：构造函数。搭视觉树、装变换、挂鼠标事件。无 I/O、不抛。

**输入输出**：无入参。产出一个空画布（`_edges` / `_nodes` 为空），背景为 `#1E1E1E` + 15×15 网格。事件：`MouseWheel`→`OnWheel`、`MouseLeftButtonDown`→`OnLeftDown`、`MouseMove`→`OnMove`、`MouseLeftButtonUp`→`OnLeftUp`、`MouseRightButtonDown`→ 取消平移。

**关键分支（伪代码）**：

```text
transform = TransformGroup[ _scale(1,1), _translate(0,0) ]     # 先缩放后平移
_edges.RenderTransform = _nodes.RenderTransform = transform     # 两个层共用一个变换
_world.Children = [ _edges, _nodes ]                            # 顺序决定层级：线在下、节点在上
root = Grid{ ClipToBounds=true, Background=#1E1E1E }
root.Children = [ 网格 Rectangle, _world ]
Content = root
挂 5 个鼠标事件；SizeChanged += (_,_) => {}                     # 空处理器，历史残留
```

**边界与失败**：⛔ `SizeChanged` 的空 lambda 是死代码（不做事）。⛔ 网格 `Rectangle` 没有尺寸绑定，靠 `Grid` 拉伸铺满，缩放/平移时**网格不动**（只有 `_world` 里的东西动），所以拖远之后背景会变成纯色。

**调用者与被调用者**：只由 XAML 实例化（当前无实例化点）；不调用其它函数。

---

### `Nodes` / `Connections` / `SelectedNode`（依赖属性）

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：三个 `DependencyProperty` + 三个 CLR 包装。`Nodes` / `Connections` 的类型是 `IEnumerable<T>`，注册时带 `PropertyChangedCallback` → 调 `Render()`；`SelectedNode` 是 `GraphNodeVm`，`FrameworkPropertyMetadataOptions.BindsTwoWayByDefault`，**没有回调**（改选中不会自动重画）。

**输入输出**：

- `Nodes` / `Connections` 赋 `null` → `Render()` 里 `Nodes?.ToList() ?? []`，画成空画布。
- `SelectedNode` 赋任意 `GraphNodeVm`（含不在 `Nodes` 里的）→ 只影响**下一次** `Render()` 时哪个节点描蓝边（`#0E639C`、2px）。
- 三个属性都是「换引用才触发」：往同一个 `ObservableCollection` 里 `Clear()` + `Add` **不会**触发 `Render()`。

**关键分支（伪代码）**：

```text
NodesProperty       : PropertyMetadata(null, (d,_) => ((NodeCanvas)d).Render())    # 引用变化才重画
ConnectionsProperty : PropertyMetadata(null, (d,_) => ((NodeCanvas)d).Render())
SelectedNodeProperty: FrameworkPropertyMetadata(null, BindsTwoWayByDefault)        # 无回调
```

**边界与失败**：

- ⛔ **这是它当前不可用的根因之一**：`MainViewModel` 的 `Nodes` / `Connections` 是**同一批集合实例**，只做 `Clear()` + `Add`，依赖属性引用不变 → 回调不触发 → 绑上去只会画出第一次（空）的样子。要用它必须改成「每次出图换新集合」或订阅 `CollectionChanged`。
- ⛔ `SelectedNode` 无回调：`MainViewModel` 里点选别的节点时，旧节点的高亮不会消失，直到某次 `Render()`（只有点击节点自己会触发）。

**调用者与被调用者**：被 WPF 绑定系统读写；`Render()` 读 `Nodes` / `Connections` / `SelectedNode`；`CreateNodeVisual` 与节点点击处理器读/写 `SelectedNode`。

---

### `Render(): void`

**状态**：✅ 已实现

**签名与契约**：`private`。清空两个画布层，按当前 `Connections` 画线（先）、按 `Nodes` 画框（后），并把视图平移到「左上角留 60px 边距」。无返回值、不抛（空集合时什么都不画）。**不做增量更新**：每次全量重建视觉树。

**输入输出**：

- 输入：`Nodes` / `Connections` / `SelectedNode` 的当前值。
- 输出：`_edges` / `_nodes` 的子元素被整体替换；`_translate` 被设为 `(60 - minX, 60 - minY)`。
- 只画「两端都能找到所属节点」的连线：`AnchorOf` 返回 `null` 的边被**静默跳过**。

**关键分支（伪代码）**：

```text
_nodes.Children.Clear(); _edges.Children.Clear()
nodeList = Nodes?.ToList() ?? []; connectionList = Connections?.ToList() ?? []
for connection in connectionList:                       # 先线后节点 → 节点盖住线的端点
    from = AnchorOf(connection.Output, isOutput: true,  nodeList)
    to   = AnchorOf(connection.Input,  isOutput: false, nodeList)
    if from == null or to == null: continue             # 触发条件：引脚不在任何已知节点里
    _edges.Children.Add(CreateEdge(from, to, connection.IsExec))
for node in nodeList:
    visual = CreateNodeVisual(node); Canvas.SetLeft/Top(visual, node.Location); _nodes.Children.Add(visual)
if nodeList.Count > 0:                                  # 触发条件：有节点才归位
    _translate.X = 60 - min(Location.X); _translate.Y = 60 - min(Location.Y)
```

**边界与失败**：

- ⛔ 全量重建：52 个节点 / 46 条线的图会重建 98 个视觉对象，拖动一次就一次；没有虚拟化（设计文档 §7 要求「只渲染视口内」）。
- ⛔ 平移会把**整个图**塞到左上角（`60 - minX`）：用户手动平移的位置在下次 `Render()` 时被重置。
- ⛔ 缩放级别不重置（`_scale` 保留），但平移会重置——「放大后平移、再重画」会跳回左上。
- ⛔ 跳过找不到所属节点的边是静默的：连线少了不会报错（`GraphBuilder` 造出的边理论上都能找到）。

**调用者与被调用者**：被 `Nodes` / `Connections` 的 DP 回调与节点点击处理器调用；调用 `AnchorOf`、`CreateEdge`、`CreateNodeVisual`。

---

### `AnchorOf(ConnectorVm pin, bool isOutput, IReadOnlyList<GraphNodeVm> nodes): Point?`

**状态**：✅ 已实现

**签名与契约**：`private static`。算出某个引脚在画布坐标系里的锚点。**算的，不是量的**：只用节点 `Location` + 常量 + 引脚序号。找不到所属节点返回 `null`。

**输入输出**：

- `pin`：要定位的引脚；`isOutput` 决定去 `owner.Outputs` 还是 `owner.Inputs` 里找它。
- `nodes`：候选节点集合（`Render` 传的是当前快照）。
- 返回：`X = owner.Location.X + (isOutput ? 240 : 0)`，`Y = owner.Location.Y + 26 + 6 + index×18 + 9`。`index` 是该引脚在集合里的位置（找不到时按 `0`）。

**关键分支（伪代码）**：

```text
owner = nodes.FirstOrDefault(n => (isOutput ? n.Outputs : n.Inputs).Contains(pin))
if owner == null: return null                            # 触发条件：引脚不属于任何节点
index = (isOutput ? owner.Outputs : owner.Inputs).IndexOf(pin)
if index < 0: index = 0
return (owner.Location.X + (isOutput ? 240 : 0),
        owner.Location.Y + 26 + 6 + index*18 + 9)
```

**边界与失败**：

- ⛔ 完全依赖常量：节点高度由 `CreateNodeVisual` 算出（`26 + 12 + rows×18`），这里只用到前 3 行的高度；行数超过 `max(输入,输出)` 时不会错位（两处公式一致），但**改字号/改行高必须两处一起改**。
- ⛔ 只用 `Contains` + `IndexOf` 做线性查找：每条边两次 O(节点数) 扫描，整体 O(边数 × 节点数)；当前规模无感。
- ⛔ 输出脚锚点固定在右边缘（`+240`），与 Nodify 版（锚点由控件回填，可能落在引脚圆圈中心）不是同一个点，两边线形会有几个像素的差。

**调用者与被调用者**：只被 `Render` 调用；读 `GraphNodeVm.Location` / `Inputs` / `Outputs`。

---

### `CreateEdge(Point from, Point to, bool isExec): Path`

**状态**：✅ 已实现

**签名与契约**：`private static`。造一条三次贝塞尔 `Path`：控制点水平外推，`dx = max(40, |to.X - from.X| × 0.5)`。纯构造、不抛。

**输入输出**：

- `isExec == true` → 实线，`#D0D0D0`，粗 `2.2`。
- `isExec == false` → 虚线（`StrokeDashArray=[4,3]`），`#6A8FB8`，粗 `1.4`。
- 两种都 `IsHitTestVisible = false`（线不吃鼠标事件，所以不能点选连线）。

**关键分支（伪代码）**：

```text
dx = max(40, |to.X - from.X| * 0.5)                      # 保证短距离也有弯度
figure = PathFigure(from, IsFilled=false)
figure.Segments += BezierSegment((from.X+dx, from.Y), (to.X-dx, to.Y), to)
return Path{ Data=geometry, Stroke = isExec ? #D0D0D0 : #6A8FB8,
             StrokeThickness = isExec ? 2.2 : 1.4,
             StrokeDashArray = isExec ? null : [4,3], IsHitTestVisible=false }
```

**边界与失败**：

- ⛔ 逆向边（`to.X < from.X`，例如循环回边）会画出「S 形回勾」：`dx` 用绝对值，控制点仍按 `from.X+dx` / `to.X-dx` 外推，视觉上绕一圈，没有专门的回边走线。
- ⛔ 自环（`from == to`，循环体为空时会出现）退化成 `dx = 40` 的一条几乎看不到的小回环。
- ⛔ 颜色/线型与 `GraphBuilder.BrushOf` 无关：exec 白线在这里是 `#D0D0D0`（不是引脚那种 `#F0F0F0`），两处不一致。

**调用者与被调用者**：只被 `Render` 调用；不调用其它函数。

---

### `CreateNodeVisual(GraphNodeVm node): Border`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。把一个 `GraphNodeVm` 变成一个 `Border`（表头 + 左右两列引脚）。会**挂鼠标事件**（点击 → 写 `SelectedNode` + `Render()`）。无 I/O。

**输入输出**：

- 尺寸：`Width = 240`，`Height = 26 + 6×2 + max(输入数, 输出数)×18`。
- 配色：表头由 `HeaderBrush(node.Kind)`；正文 `#2D2D30`；边框 `#3F3F46`（1px），`node == SelectedNode` 时换成 `#0E639C`（2px）。
- 文本：`node.IsOpaque` 为真时标题前加 `"? "`；`TextTrimming = CharacterEllipsis`（长标题截断）。
- 其他：`ToolTip = node.Tooltip`（空则 `null`）、`Cursor = Hand`、`Tag = node`。
- 点击：`SelectedNode = node; e.Handled = true; Render()`。

**关键分支（伪代码）**：

```text
rows = max(Inputs.Count, Outputs.Count)
height = 26 + 12 + rows*18
body = Grid[ row0=26px(表头), row1=*(引脚区) ]
表头 = Border{ Background=HeaderBrush(Kind), CornerRadius=(4,4,0,0), Padding=(8,0) }
       内放 TextBlock( IsOpaque ? "? "+Title : Title, 白色, SemiBold, Ellipsis )
引脚区 = Grid[ 两列各 * ]：左列 StackPanel(输入脚，靠左)，右列 StackPanel(输出脚，靠右)
border = Border{ Width=240, Height=height, Background=#2D2D30,
                 BorderBrush = node==SelectedNode ? #0E639C : #3F3F46,
                 BorderThickness = node==SelectedNode ? 2 : 1, CornerRadius=4,
                 ToolTip, Cursor=Hand, Tag=node, Child=body }
border.MouseLeftButtonDown += (_,e) => { SelectedNode = node; e.Handled = true; Render() }
```

**边界与失败**：

- ⛔ 选中态靠**重建整张图**来体现（点击里直接 `Render()`）：52 个节点时点一下要重建 98 个视觉对象，会有可感知的卡顿。
- ⛔ `Height` 是硬算的：`Subtitle` 不显示、引脚文字换行被裁掉（`StackPanel` 高 18 固定），长引脚名会被截断（`TextBlock` 没有设 `TextTrimming`，直接溢出/被裁）。
- ⛔ 不可拖动：`MouseLeftButtonDown` 只做选中，没有拖拽逻辑——节点位置完全由 `GraphBuilder` 决定（这与「手动位置进侧车」的设计一致，但连临时拖动都没有）。
- ⛔ `Kind` 里的 `"Call"` / `"Exec"` 没有专门配色，落到默认蓝灰（`#2E3E50`），和 `Data`（`#2A4A6A`）几乎看不出区别。

**调用者与被调用者**：只被 `Render` 调用；调用 `CreatePinRow`、`HeaderBrush`；读 `GraphNodeVm` 的全部可视属性与 `SelectedNode`。

---

### `CreatePinRow(ConnectorVm pin, bool isInput): UIElement`

**状态**：✅ 已实现

**签名与契约**：`private static`。造一行引脚：一个 8×8 的圆点 + 一段 11px 的文字，输入脚「点在左、字在右」，输出脚「字在左、点在右」。纯构造、不抛。

**输入输出**：

- 圆点填充：`IsExec` → `#F0F0F0`（白），否则 `pin.Brush`；`IsUnknown` 时加 `#9A9A9A` 1px 描边。
- 文字：`IsExec` → 恒显示 `"exec"`（忽略 `pin.Title`！）；否则显示 `pin.Title`。颜色 `#C8C8C8`，`IsUnknown` 时 `#8A8A8A`。
- 行高恒 `18`，圆点用负外边距（输入 `(-4,0,4,0)`、输出 `(4,0,-4,0)`）压在节点边框上。

**关键分支（伪代码）**：

```text
panel = StackPanel{ Orientation=Horizontal, Height=18, 对齐 = isInput ? 左 : 右 }
dot   = Ellipse{ 8×8, Fill = IsExec ? #F0F0F0 : pin.Brush,
                 Stroke = IsUnknown ? #9A9A9A : null, Margin = 负边距 }
label = TextBlock{ Text = IsExec ? "exec" : pin.Title, FontSize=11,
                   Foreground = IsUnknown ? #8A8A8A : #C8C8C8 }
panel.Children = isInput ? [dot, label] : [label, dot]
```

**边界与失败**：

- ⛔ **exec 脚的文字被硬写成 `"exec"`**：`GraphBuilder` 造的 exec 出脚叫 `"true"` / `"false"` / `"body"` / `"done"`，在这个视图里全显示成 `exec`——分支的两条出脚因此**无法区分**。这是本控件与 Nodify 版的观感差异之一（Nodify 版显示真实 `Title`）。
- ⛔ `IsUnknown` 的灰色文字 + 描边是「类型没推出来」的唯一提示，没有 tooltip 说明。
- ⛔ 圆点没有 `ToolTip`，悬停看不到类型名（设计文档 §3 要求 hover 显示类型来源）。

**调用者与被调用者**：只被 `CreateNodeVisual` 调用；读 `ConnectorVm` 的 `IsExec` / `IsUnknown` / `Brush` / `Title`。

---

### `HeaderBrush(GraphNodeVm node): Brush`

**状态**：✅ 已实现

**签名与契约**：`private static`。`node.Kind` → 表头颜色。纯函数、不抛。

**输入输出**：

| `Kind` | 颜色 |
|---|---|
| `Branch` | `#8A6D1F`（暗金） |
| `Set` | `#1F6F6A`（暗青） |
| `Return` | `#6A2F2F`（暗红） |
| `Data` | `#2A4A6A`（暗蓝） |
| `Opaque` | `#4A4A4A`（灰） |
| 其它（含 `Call` / `Exec`） | `#2E3E50`（蓝灰） |

**关键分支（伪代码）**：

```text
switch node.Kind:
    "Branch" => #8A6D1F
    "Set"    => #1F6F6A
    "Return" => #6A2F2F
    "Data"   => #2A4A6A
    "Opaque" => #4A4A4A
    _        => #2E3E50
```

**边界与失败**：⛔ 字符串 `Kind` 拼错不报错，直接落到默认色（`GraphBuilder.Describe` 是唯一生产者，拼写风险低但存在）。⛔ 与 Nodify 视图完全无关：Nodify 版的节点表头用的是 Nodify 默认样式，这里的一套配色在真正跑的 UI 里看不到。

**调用者与被调用者**：只被 `CreateNodeVisual` 调用；读 `GraphNodeVm.Kind`。

---

### `GridBrush(): Brush`

**状态**：✅ 已实现

**签名与契约**：`private static`。造 15×15 的平铺网格画刷：`GeometryDrawing(null, Pen(#2A2A2A, 1), RectangleGeometry(0,0,15,15))`，`TileMode = Tile`、`Viewport = (0,0,15,15)`、`ViewportUnits = Absolute`、`Stretch = None`。每次调用新建一个（不缓存、不冻结）。

**输入输出**：无入参，返回一个 `DrawingBrush`。只被 `NodeCanvas()` 用于背景 `Rectangle` 的 `Fill`。

**关键分支（伪代码）**：

```text
pen = Pen(#2A2A2A, 1)
drawing = GeometryDrawing(null, pen, RectangleGeometry(0,0,15,15))
return DrawingBrush{ Drawing=drawing, TileMode=Tile, Viewport=(0,0,15,15),
                     ViewportUnits=Absolute, Stretch=None }
```

**边界与失败**：⛔ 网格不随缩放/平移变化（画在 `_world` 之外），放大后格子看起来变密/变疏是与视图脱节的。⛔ 未 `Freeze()`，每次构造一个新画刷（只调一次，无实际开销）。

**调用者与被调用者**：只被 `NodeCanvas()` 调用；不调用其它函数。

---

### `OnWheel(MouseWheelEventArgs e): void` / `OnLeftDown` / `OnMove` / `OnLeftUp`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：四个鼠标处理器。`OnWheel`：以光标为锚点缩放，比例夹在 `[0.2, 3.0]`。`OnLeftDown`：记录平移起点。`OnMove`：按位移改 `_translate`。`OnLeftUp`：结束平移。`MouseRightButtonDown`：把 `_panning` 置 `false`（取消平移）。副作用：改 `_scale` / `_translate` 与 `_panning`；`OnWheel` 里 `e.Handled = true`。

**输入输出**：

- 缩放：`factor = e.Delta > 0 ? 1.1 : 1/1.1`；夹取后按「光标位置不动」重算平移：`t' = p - (p - t) × factor'`。
- 平移：`_translate = _panOrigin + (当前鼠标 - _panStart)`。
- 左键按下即进入平移（**与「点选节点」冲突**，见边界）。

**关键分支（伪代码）**：

```text
OnWheel:
    p = e.GetPosition(this); factor = e.Delta > 0 ? 1.1 : 1/1.1
    newScale = clamp(_scale.ScaleX * factor, 0.2, 3.0)
    factor = newScale / _scale.ScaleX                       # 夹取后重算，避免越界跳变
    _translate = p - (p - _translate) * factor              # 以光标为锚
    _scale = newScale; e.Handled = true
OnLeftDown: _panning = true; _panStart = pos; _panOrigin = (_translate.X, _translate.Y)
OnMove:     if !_panning: return; _translate = _panOrigin + (pos - _panStart)
OnLeftUp:   _panning = false
右键按下:   _panning = false                                # 中途放弃平移
```

**边界与失败**：

- ⛔ 左键既用于平移又用于选中节点：`CreateNodeVisual` 的 `MouseLeftButtonDown` 里 `e.Handled = true` 能挡住冒泡，所以在节点上按下不会开始平移；在空白处按下会同时「开始平移」并清掉选中？——不会清选中（空白处没有处理器），但会平移。
- ⛔ 没有边界约束：可以平移到无限远，网格又不动，最后是一片纯色。
- ⛔ 没有「缩放到适应」/「复位」操作，只能手动滚回去。
- ⛔ 缩放上限 3.0 / 下限 0.2 是硬编码，值可能随迭代调整。

**调用者与被调用者**：由构造函数挂到 `root` 的鼠标事件上；读写 `_scale` / `_translate` / `_panning` / `_panStart` / `_panOrigin`。
