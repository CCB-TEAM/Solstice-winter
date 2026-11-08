# Views.GraphView — Nodify 视图（当前真正在用的画布）

**职责一句话**：把 `MainViewModel.Nodes` / `Connections` 交给 Nodify 7 的 `NodifyEditor` 渲染，并用三个 `Style` / `DataTemplate` 把 VM 的形状对到 Nodify 的控件契约上。

**对外入口**：`GraphView : UserControl`（无参构造，只调 `InitializeComponent`）。对外**没有任何属性或方法**——它完全靠 `DataContext` 继承 `MainViewModel` 工作，在 `MainWindow.xaml` 里以 `<views:GraphView Grid.Row="1" />` 的形式使用。

**内部结构**：

```text
GraphView.Resources
 ├─ Style nodify:NodeInput   Header={Binding}  Anchor={Binding Anchor, OneWayToSource}
 │                           IsConnected={Binding IsConnected}  HeaderTemplate=11px 文本
 ├─ Style nodify:NodeOutput  同上（输出脚）
 ├─ DataTemplate NodeTemplate       nodify:Node Header=Title Input=Inputs Output=Outputs
 │                                 ToolTip=Tooltip MinWidth=160
 ├─ DataTemplate ConnectionTemplate nodify:CircuitConnection Source=Output.Anchor
 │                                 Target=Input.Anchor
 └─ Style ItemContainerStyle        Location={Binding Location} ContentTemplate=NodeTemplate
                                    BasedOn Nodify 默认 ItemContainer 样式

<nodify:NodifyEditor x:Name="Editor"
        Background=#1E1E1E GridCellSize=15 AllowDrop=False
        ItemsSource={Binding Nodes} Connections={Binding Connections}
        ConnectionTemplate / ItemContainerStyle 指向上面三个资源 />
```

**依赖**：Nodify 7.3.0（`NodifyEditor` / `Node` / `NodeInput` / `NodeOutput` / `CircuitConnection` / `ItemContainer`）、`SolsticeWinter.App.ViewModels`。被 `MainWindow.xaml` 使用；间接依赖 [MainViewModel](MainViewModel.md) 的集合与 [ViewModels.GraphNodeVm](ViewModels.GraphNodeVm.md) 的 `Anchor` 回填约定。

**取舍**：

- **Nodify 而不是自绘**：缩放、平移、框选、虚拟化、连线渲染都是现成的，自绘版（[Controls.NodeCanvas](Controls.NodeCanvas.md)）只保留了最基本的平移缩放。代价：引入第三方 UI 库与其样式体系，Nodify 大版本升级会连带改 XAML。
- **VM 形状按 Nodify 契约设计**（`ConnectorVm.Anchor` 由控件 `OneWayToSource` 回填）：这让「换画布库」只换视图层，图构建与 VM 不动——自绘版就是这么接进来的。代价：`Anchor` 在布局前是 `(0,0)`，连线会先闪一下。
- **用 `BasedOn="{StaticResource {x:Type nodify:NodeInput}}"` 继承 Nodify 默认样式**：不重写模板，只改 `Header` / `Anchor` / `IsConnected` 三个属性，样式跟库走。代价：Nodify 改默认模板时这里会跟着变（好处是免费获得新版视觉）。
- **`AllowDrop="False"`**：关掉 Nodify 的拖放建连。当前是只读渲染，任何编辑入口都不该被误触。代价：将来要做拖线建连必须显式打开。
- **不给节点做自定义模板**：`NodeTemplate` 只绑了 `Header` / `Input` / `Output` / `ToolTip`，所以 `GraphNodeVm` 的 `Kind` / `Subtitle` / `IsOpaque` 在这一版里**没有视觉表现**（自绘版有表头配色与 `?` 角标）。这是「先跑通再美化」的取舍，代价是两种视图观感不一致。

---

### `GraphView()`

**状态**：✅ 已实现

**签名与契约**：无参构造，只调 `InitializeComponent()`。副作用：解析本文件的 XAML（会实例化 `NodifyEditor` 与四个资源）。不抛（除非 XAML 写错——那时异常在 `MainWindow` 构造里被 [App.OnStartup](App.md) 的 `catch` 接住）。

**输入输出**：无入参、无返回值。构造完成后控件树就绪，但 `ItemsSource` 要等 `DataContext`（`MainViewModel`）传下来才有内容。

**关键分支（伪代码）**：

```text
InitializeComponent()      # 解析 XAML：建四个资源 + 一个 NodifyEditor；XAML 错误在这里抛
```

**边界与失败**：

- ⛔ XAML 解析期异常没有局部兜底：本文件里任何一个属性写错（例如历史上 `CircuitConnection.ArrowSize="8"`）都会让整个 `MainWindow` 构造失败，表现为「启动弹框后进程退出」，只有 `crash.log` 有线索。
- ⛔ 构造函数里不做任何绑定校验：`DataContext` 类型不对时不会报错，只是画布空白。

**调用者与被调用者**：被 `MainWindow.xaml` 实例化；调用 `InitializeComponent`（生成的 `GraphView.g.cs`）。

---

### `NodeInput` / `NodeOutput` 样式（引脚）

**状态**：✅ 已实现

**签名与契约**：两个 `Style`（隐式 TargetType 分别 `nodify:NodeInput` / `nodify:NodeOutput`），都 `BasedOn` Nodify 默认样式。设三个属性：`Header`（绑整个 `ConnectorVm`）、`Anchor`（`OneWayToSource` 回填到 `ConnectorVm.Anchor`）、`IsConnected`（单向绑 `ConnectorVm.IsConnected`）；另加 `HeaderTemplate` 显示 `Title`（11px、`#C8C8C8`）。

**输入输出**：

- `Header = {Binding}`：把 `ConnectorVm` 本身当 `Header`，再由 `HeaderTemplate` 取它的 `Title`。
- `Anchor = {Binding Anchor, Mode=OneWayToSource}`：**方向是控件 → VM**。Nodify 算完引脚位置写回 `ConnectorVm.Anchor`，`CircuitConnection` 的 `Source`/`Target` 再绑这个值。
- `IsConnected = {Binding IsConnected}`：单向 VM → 控件，决定 Nodify 画实心还是空心引脚。

**关键分支（伪代码）**：

```text
Style NodeInput:  Header={Binding}  Anchor={Binding Anchor, OneWayToSource}  IsConnected={Binding IsConnected}
Style NodeOutput: 同上
HeaderTemplate: DataTemplate(ConnectorVm) → TextBlock{ Text=Title, FontSize=11, Foreground=#C8C8C8 }
```

**边界与失败**：

- ⛔ `Anchor` 只在**布局完成后**才有值：首帧所有连线会画到 `(0,0)`，随后刷新。这是 Nodify 的正常行为，但截图/录屏时能看到一帧错位。
- ⛔ `HeaderTemplate` 只显示 `Title`：exec 脚的 `"true"` / `"false"` / `"body"` / `"done"` 都显示原名（比自绘版的「全叫 exec」更好），但**类型名不显示**，悬停也没有类型提示。
- ⛔ `IsConnected` 是单向的：用户手动拖线（当前 `AllowDrop=False`，做不到）不会回写 `IsConnected`，将来开编辑要改成双向或补事件。
- ⛔ 样式没有区分 exec 与数据引脚的形状：Nodify 默认模板用同一个圆形，`IsExec` 在视觉上无差别（自绘版用白色 vs 类型色区分）。

**调用者与被调用者**：被 `NodeTemplate` 里的 `nodify:Node` 通过 `Input` / `Output` 集合隐式使用；读写 `ConnectorVm.Anchor` / `IsConnected` / `Title`。

---

### `NodeTemplate`（`DataTemplate`）

**状态**：✅ 已实现

**签名与契约**：`DataTemplate`（`x:Key="NodeTemplate"`，`DataType = GraphNodeVm`），内容是一个 `nodify:Node`：`Header={Binding Title}`、`Input={Binding Inputs}`、`Output={Binding Outputs}`、`ToolTip={Binding Tooltip}`、`MinWidth=160`。

**输入输出**：一个 `GraphNodeVm` → 一个 Nodify 节点视觉。`Inputs` / `Outputs` 是 `ObservableCollection<ConnectorVm>`，Nodify 会为每个元素套上上面两个引脚样式。`Tooltip` 为空串时 `ToolTip` 是一个空提示框（不会变成 `null`）。

**关键分支（伪代码）**：

```text
DataTemplate(GraphNodeVm) →
    nodify:Node{ Header = Title, Input = Inputs, Output = Outputs, ToolTip = Tooltip, MinWidth = 160 }
    # 每个 Inputs/Outputs 元素自动套 NodeInput/NodeOutput 隐式样式
```

**边界与失败**：

- ⛔ **不显示 `Kind` / `Subtitle` / `IsOpaque`**：Branch 与普通调用看起来一样，不透明节点也没有 `?` 角标。设计文档 §3 的节点外观规则在这一版里只落实了「标题 + 引脚」。
- ⛔ `MinWidth=160` 但**没有 `MaxWidth`**：长函数名会把节点撑得很宽（`GraphBuilder` 的 `ColumnWidth=300` 假设节点不会更宽，撑破后会与右侧节点重叠）。
- ⛔ 节点不能拖动改位置？——能拖（Nodify 的 `ItemContainer` 支持拖动并写回 `Location`），但位置**不持久化**，切换函数就重算。
- 节点模板是「UI 还在迭代」的重点对象：`Kind` 配色、`IsOpaque` 角标、`Subtitle` 都可能后续补上。值可能随迭代调整。

**调用者与被调用者**：被 `ItemContainerStyle` 的 `ContentTemplate` 引用；读 `GraphNodeVm` 的 `Title` / `Inputs` / `Outputs` / `Tooltip`。

---

### `ConnectionTemplate`（`CircuitConnection`）

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`DataTemplate`（`x:Key="ConnectionTemplate"`，`DataType = ConnectionVm`），内容是一个 `nodify:CircuitConnection`：`Source={Binding Output.Anchor}`、`Target={Binding Input.Anchor}`。**只绑两个点，不绑任何视觉属性**。

**输入输出**：一条 `ConnectionVm` → 一条曲线（Nodify 的电路风格正交/折线）。线型（exec 粗实线 vs 数据细虚线）**没有**绑 `IsExec`，两条线看起来一样。

**关键分支（伪代码）**：

```text
DataTemplate(ConnectionVm) → nodify:CircuitConnection{ Source = Output.Anchor, Target = Input.Anchor }
# 无 IsExec 分支：线型不区分 exec / 数据
```

**边界与失败**：

- ⛔ **`CircuitConnection.ArrowSize` 是 `Size` 类型，不是 `double`**：写 `ArrowSize="8"` 会在 XAML 解析时抛 `XamlParseException`（`Size.Parse("8")` 失败，需要 `"8,8"` 这种 `宽,高` 形式）。当前模板**不设**该属性，所以现在不会炸；要加箭头必须写成 `ArrowSize="8,8"`。这个坑踩过一次，`crash.log` 里留下的是 `XamlParseException` + `SizeConverter`。
- ⛔ 不区分 exec / 数据线：`ConnectionVm.IsExec` 在这个视图里没有被任何东西读取（自绘版用它选线型与粗细）。
- ⛔ `Source` / `Target` 依赖 `Anchor` 已被回填：首帧两者都是 `(0,0)`，线会先叠在原点。
- ⛔ 线不可点选、不可删：没有 `IsSelected` / 命令绑定。

**调用者与被调用者**：被 `NodifyEditor.ConnectionTemplate` 引用；读 `ConnectionVm.Output.Anchor` / `Input.Anchor`。

---

### `ItemContainerStyle`（`ItemContainer.Location`）

**状态**：✅ 已实现

**签名与契约**：`Style`（`x:Key="ItemContainerStyle"`，TargetType `nodify:ItemContainer`，`BasedOn` Nodify 默认样式）。设 `Location={Binding Location}`（双向，Nodify 拖动时会写回 VM）与 `ContentTemplate={StaticResource NodeTemplate}`。

**输入输出**：`GraphNodeVm.Location` ↔ `ItemContainer.Location`。拖动节点 → VM 的 `Location` 变化（`GraphNodeVm.Location` 会触发 `PropertyChanged`）；程序改 `Location` → 节点移动。

**关键分支（伪代码）**：

```text
Style ItemContainer: Location = {Binding Location}         # 默认 TwoWay（Nodify 的 Location 是双向绑定的目标）
                     ContentTemplate = NodeTemplate
```

**边界与失败**：

- ⛔ 拖动结果不落盘：`Location` 改了但没有任何保存通道（设计文档 §6 的位置侧车文件未实现），下次出图重算。
- ⛔ 位置由 `GraphBuilder` 用固定常量算（`300` / `±130` / `-170`），与节点实际渲染尺寸无关：节点被 `MinWidth=160` 与长标题撑宽后，列间距可能不够，出现重叠。

**调用者与被调用者**：被 `NodifyEditor.ItemContainerStyle` 引用；读写 `GraphNodeVm.Location`。

---

### `Editor`（`NodifyEditor`）

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：XAML 里的根控件，`x:Name="Editor"`（代码后置没有用到这个名字）。属性：`Background=#1E1E1E`、`GridCellSize=15`、`ItemsSource={Binding Nodes}`、`Connections={Binding Connections}`、`ConnectionTemplate`、`ItemContainerStyle`、`AllowDrop=False`。

**输入输出**：

- `ItemsSource` 绑 `MainViewModel.Nodes`（`ObservableCollection<GraphNodeVm>`）：`Clear()` + `Add` 会正常增量刷新（Nodify 内部订阅了集合变更）。
- `Connections` 绑 `MainViewModel.Connections`：同上。
- 视图自身的缩放/平移由 Nodify 管，没有绑到 VM（切换函数后视图**不会**自动回到原点，也没有「适应窗口」命令）。

**关键分支（伪代码）**：

```text
NodifyEditor{ ItemsSource=Nodes, Connections=Connections,
              ConnectionTemplate=ConnectionTemplate, ItemContainerStyle=ItemContainerStyle,
              AllowDrop=False }        # 关掉拖放建连
```

**边界与失败**：

- ⛔ **`SelectedNode` 完全没有绑**：Nodify 的选中状态留在 `ItemContainer.IsSelected` 里，没有回写到 `MainViewModel.SelectedNode`，所以右侧「详情」面板在 Nodify 视图下**永远显示「未选择节点」**。这是当前 UI 最明显的缺口（自绘版 `NodeCanvas` 有这个回写）。
- ⛔ 没有虚拟化配置：`NodifyEditor` 默认不做视口虚拟化（节点多时会卡）；设计文档 §7 的「只渲染视口内」还没落实。
- ⛔ 没有右键菜单、没有快捷键、没有撤销：所有编辑入口都不存在（只读渲染）。
- ⛔ `GridCellSize=15` 与背景色是硬编码，与 `Themes/Dark.xaml` 的 `Chrome.Background`（同为 `#1E1E1E`）重复但没引用资源。
- 中栏画布的属性值（`GridCellSize`、`AllowDrop`、模板内容）都可能随迭代调整。

**调用者与被调用者**：由 `GraphView()` 的 `InitializeComponent` 实例化；读 `MainViewModel.Nodes` / `Connections`、`ItemContainerStyle`、`ConnectionTemplate`。
