# MainWindow — 三栏骨架与代码后置

**职责一句话**：`Window` 子类，把 `MainViewModel` 挂成 `DataContext`，并用一张 XAML 摆出「左资产/函数 · 中画布 · 右详情 · 底状态条」的骨架。

**对外入口**：`MainWindow(string? indexPath = null)`；XAML 里的绑定与两个 `x:Name`（`AssetList` / `FunctionList`，代码后置未使用）。窗口标题固定为 `Solstice-winter — cooked 蓝图工坊`，`Height=860` / `Width=1480`，`WindowStartupLocation=CenterScreen`。

**内部结构**：

```text
Window (Background=Chrome.Background, Foreground=Chrome.Foreground)
 └─ Grid  rows = [ *, Auto ]
     ├─ Grid row0  cols = [ 300, *, 300 ]
     │   ├─ col0 (Chrome.Panel)
     │   │   rows = [ 表头"资产", TextBox, AssetList(*), 表头"函数（带字节码）", FunctionList(*) ]
     │   ├─ col1  rows = [ 标题条(资产 / 函数), GraphView(*) ]
     │   └─ col2 (Chrome.Panel) rows = [ 表头"详情", ScrollViewer(StackPanel, DataContext=SelectedNode) ]
     └─ Border row1 (Chrome.PanelAlt, 上边框)  Grid cols=[*, Auto]
         ├─ TextBlock Status（左，超长省略）
         └─ TextBlock Diagnostics（右，灰色）
```

**依赖**：`SolsticeWinter.App.MainViewModel`、`SolsticeWinter.App.Views.GraphView`、`Themes/Dark.xaml` 的资源键（`Chrome.Background` / `Chrome.Panel` / `Chrome.PanelAlt` / `Chrome.Border` / `Chrome.Foreground` / `Chrome.ForegroundDim` / `PanelHeader`）。被 [App.OnStartup](App.md) 实例化。

**取舍**：

- **固定 300/·/300 三栏而不是可拖动分栏**：骨架先按 UE 编辑器的比例摆好，拖动分隔条属于「顺手再加」。代价：宽屏浪费、窄屏挤压画布，且没有 `GridSplitter`。
- **`DataContext` 在构造函数里赋值，加载在 `Loaded` 里做**：`Loaded` 保证视觉树已就绪，弹 `MessageBox`/更新绑定都不会撞上半初始化的窗口。代价：窗口先出现一帧空列表（`Status` 是「启动中…」）。
- **详情面板靠 `DataContext = {Binding SelectedNode}` 整体切换**：一个 `StackPanel` 吃掉所有字段，不给每种节点做专门面板。代价：Branch 与 Set 的详情长得一样，也解释了为什么现在显示的是「未选择节点」——Nodify 视图不回写 `SelectedNode`（见 [Views.GraphView](Views.GraphView.md) 边界）。
- **两个 ListBox 开虚拟化（`VirtualizingPanel.IsVirtualizing` + `ScrollViewer.CanContentScroll`）**：`Assets` 最多 400 条、`Functions` 可达 181 条，不开虚拟化滚动会顿。代价：虚拟化下 `SelectedItem` 的时序更微妙（见 [MainViewModel](MainViewModel.md) 里 `SelectedFunction` 的边界）。

---

### `MainWindow(string? indexPath = null)`

**状态**：✅ 已实现

**签名与契约**：构造函数。三步：`InitializeComponent()`（解析 XAML）、`new MainViewModel()` 并赋给 `DataContext`、挂 `Loaded` 处理器：先往 `ui.log` 写一行「Loaded 触发」，再 `await _viewModel.InitializeAsync(indexPath)`，若它**之外**抛了异常则记日志并 `throw`。抛：XAML 解析异常（在 `InitializeComponent` 内），由 [App.OnStartup](App.md) 的 `catch` 兜住并弹框退出。

**输入输出**：`indexPath` 原样转交给 `InitializeAsync`；`null` 时由 `MainViewModel.FindIndex` 走四级回退（`<exe 目录>\.cache` → `<当前目录>\.cache` → 仓库根 `.cache`）。无返回值。

**关键分支（伪代码）**：

```text
InitializeComponent()                      # XAML 解析失败 → 异常冒泡到 App.OnStartup
_viewModel = new MainViewModel()           # 构造时读 GameProfile，不做 I/O
DataContext = _viewModel
Loaded += async (_,_) => {
    UiLog.Write($"Loaded 触发，indexPath={indexPath ?? "<null>"}")   # 「窗口真的起来了」的第一行证据
    try:    await _viewModel.InitializeAsync(indexPath)             # 内部已把异常变成 Status + ui.log
    catch e: UiLog.Write($"InitializeAsync 抛出：{类型}: {消息}\n{堆栈}"); throw
}
```

**边界与失败**：

- ⛔ `Loaded` 里的 `async void` 式处理器：`InitializeAsync` 内部自己吞异常，所以不会出现未观察的任务异常；但也意味着**加载失败只体现在状态条与 `ui.log`**，不会弹窗。
- ⛔ 那个 `catch` + `throw` 基本是死代码：`InitializeAsync` 已经把一切异常变成 `Status` + `ui.log`，能走到这里的只剩它之外的失败；而 `throw` 在 `async void` 里会交给 `DispatcherUnhandledException` → `App.Log` + 弹框 + `Shutdown(1)`（窗口直接被关掉）。
- ⛔ 窗口关闭后 `_viewModel` 里缓存的 `KismetWorkspace` 不会被释放（`Window` 没有 `Closed` 处理器）——进程退出时随进程一起回收。
- ⛔ 没有命令行开关能指定 profile / 导出目录：`GameProfile.Kards()` 的路径写死（见 `SolsticeWinter.Core`）。

**调用者与被调用者**：被 `App.OnStartup` 构造；调用 `MainViewModel()`、`MainViewModel.InitializeAsync`、`UiLog.Write`。

---

### `MainWindow.xaml`：三栏骨架与标题条

**状态**：✅ 已实现

**签名与契约**：纯 XAML 布局，无代码。左栏 300 宽、中栏 `*`、右栏 300 宽；中栏上方是一条标题：`{Binding SelectedAsset.VirtualPath, TargetNullValue=未选择资产}` + `"  /  "` + `{Binding SelectedFunction.Name, TargetNullValue=未选择函数}`（蓝色 `#7FB3D5`）。

**输入输出**：三个绑定源都是 `MainViewModel`。未选中时 `TargetNullValue` 显示中文占位；`SelectedAsset` 非空但 `SelectedFunction` 为空时，右侧显示「未选择函数」。

**关键分支（伪代码）**：

```text
col0 = 300(左栏)；col1 = *(画布)；col2 = 300(右栏)
标题条 = TextBlock(SelectedAsset.VirtualPath ?? "未选择资产") + "  /  " + TextBlock(SelectedFunction.Name ?? "未选择函数")
画布   = <views:GraphView Grid.Row="1" />            # 无显式 DataContext，继承自 Window
```

**边界与失败**：⛔ 三栏宽度固定，没有 `GridSplitter`；窗口缩到很窄时中栏被压到最小，画布几乎不可用。⛔ 标题条只显示两个字段，没有「字节码大小 / 是否结构化」等信息（那些在底部 `Diagnostics` 里）。

**调用者与被调用者**：被 `InitializeComponent` 解析；读 `MainViewModel.SelectedAsset` / `SelectedFunction`；实例化 `GraphView`。

---

### `AssetList` / `FunctionList`（两个 ListBox）

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：两个 `ListBox`，`ItemsSource` 绑 `Assets` / `Functions`，`SelectedItem` 双向绑 `SelectedAsset` / `SelectedFunction`，`DisplayMemberPath="Display"`，开虚拟化与 `CanContentScroll`。上方有一个 `TextBox` 绑 `SearchText`（`UpdateSourceTrigger=PropertyChanged`，`ToolTip="按路径过滤"`）。

**输入输出**：

- `AssetList` 每行 = `AssetItem.Display`（短名 + 带字节码函数数）；`FunctionList` 每行 = `FunctionItem.Display`（函数名 + 字节数）。
- 选中 → 写 VM → VM 触发加载（资产 → 函数列表；函数 → 出图）。
- 搜索框每敲一个字符写一次 `SearchText` → `RefreshAssets` → `Assets.Clear()` + 重填。

**关键分支（伪代码）**：

```text
TextBox:   Text={Binding SearchText, UpdateSourceTrigger=PropertyChanged}
AssetList: ItemsSource={Binding Assets}    SelectedItem={Binding SelectedAsset}     DisplayMemberPath=Display
FunctionList: ItemsSource={Binding Functions} SelectedItem={Binding SelectedFunction} DisplayMemberPath=Display
两者都：VirtualizingPanel.IsVirtualizing=True; ScrollViewer.CanContentScroll=True
```

**边界与失败**：

- ⛔ `Assets.Clear()` 会把 `SelectedAsset` 打回 `null`（`ListBox` 回写），而 `MainViewModel` 的 setter 对 `null` 不做任何事——所以**搜索后画布上还留着上一张图，左栏却没有任何选中项**，状态不一致。
- ⛔ `DisplayMemberPath` 用的是计算属性 `Display`（record 上没有变更通知）：record 是不可变的，所以不会有「显示不刷新」的问题；但这也意味着**行的文本不能更新**（例如想显示「已打开」标记就要换类型）。
- ⛔ 两个 ListBox 都没有右键菜单/键盘快捷键/分组：`Functions` 是一维平铺（181 个函数），没有按「事件图 / 函数」分组（设计文档 §1 的 My Blueprint 树没做）。
- ⛔ 搜索框没有占位提示与清空按钮，`ToolTip` 是唯一的说明。

**调用者与被调用者**：被 `InitializeComponent` 解析；读写 `MainViewModel` 的 `Assets` / `Functions` / `SelectedAsset` / `SelectedFunction` / `SearchText`。

---

### 详情面板（`SelectedNode` 绑定块）

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：右栏的 `ScrollViewer` → `StackPanel`（`DataContext="{Binding SelectedNode}"`）。内容：`Title`（14px，`TargetNullValue=未选择节点`）、`Kind`（灰）、「输入」标签 + `ItemsControl(Inputs)`、「输出」标签 + `ItemsControl(Outputs)`、`Tooltip`（灰、可换行）。每个引脚一行：8×8 圆点（`Fill={Binding Brush}`）+ `Title` + `TypeName`。

**输入输出**：`SelectedNode` 为 `null` → 标题显示「未选择节点」，`Kind` 空、两个 `ItemsControl` 空、`Tooltip` 空。非 `null` → 显示该节点的引脚清单。

**关键分支（伪代码）**：

```text
StackPanel.DataContext = SelectedNode
TextBlock(Title, TargetNullValue="未选择节点")
TextBlock(Kind)
ItemsControl(Inputs) → 每项：Ellipse(Brush) + Title + TypeName
ItemsControl(Outputs) → 同上
TextBlock(Tooltip)
```

**边界与失败**：

- ⛔ **在 Nodify 视图下 `SelectedNode` 永远是 `null`**：`GraphView.xaml` 没有把 `ItemContainer.IsSelected` 回写到 VM，所以这块面板当前恒显示「未选择节点」。只有换回 [Controls.NodeCanvas](Controls.NodeCanvas.md) 才会有点选效果。
- ⛔ 不显示参数值、不显示类型来源（`jmap 签名` / `usmap` / `推断`）、不显示调用者——设计文档 §3 要求的「来源」这一栏没做。
- ⛔ `Brush` 直接绑到 `Ellipse.Fill`：`ConnectorVm.Brush` 是 WPF `Brush`，所以这层没法脱离 WPF 复用（与 VM 的取舍一致）。

**调用者与被调用者**：被 `InitializeComponent` 解析；读 `MainViewModel.SelectedNode` 与 `GraphNodeVm` 的 `Title` / `Kind` / `Inputs` / `Outputs` / `Tooltip`。

---

### 状态条（`Status` / `Diagnostics`）

**状态**：✅ 已实现

**签名与契约**：底部 `Border`（`Chrome.PanelAlt`，上边框 1px），内部两列：左 `TextBlock` 绑 `Status`（`TextTrimming=CharacterEllipsis`，占满剩余宽度），右 `TextBlock` 绑 `Diagnostics`（灰色，`Auto` 宽，左外边距 12）。

**输入输出**：

- `Status` 的实测取值：`启动中…` / `加载索引…` / `索引已加载：2137 资产 / 4856 唯一函数名（296 ms）` / `打开 <路径>…` / `<路径>：181 个带字节码的函数` / `反编译 <函数>…` / `<函数>：52 节点 / 46 连线` / `反编译失败：<类型>: <消息>`。
- `Diagnostics` 的实测取值：`未加载`（初始）→ `index.Describe()` 的 7 行摘要 → 每次出图后变成单行 `函数 <名>  <字节> 字节  <参数> 参数  图 <N> 节点 / <M> 连线`。

**关键分支（伪代码）**：

```text
Border(row1) → Grid[ *, Auto ]
    col0: TextBlock{ Text=Status, TextTrimming=CharacterEllipsis }
    col1: TextBlock{ Text=Diagnostics, Foreground=Chrome.ForegroundDim, Margin=(12,0,0,0) }
```

**边界与失败**：

- ⛔ `Diagnostics` 的多行内容（`index.Describe()` 用 `Environment.NewLine` 拼 7 行）塞进一个**单行** `TextBlock`：行会被压成一行显示或直接裁掉，实际只能看到第一行。索引规模摘要因此几乎不可读。
- ⛔ `Status` 是「最后一条消息」而不是日志：切资产、切函数的消息互相覆盖，出问题后想回看前一步只能翻 `ui.log` / `crash.log`。
- ⛔ 没有进度指示（没有 `ProgressBar`、没有忙碌光标）：加载索引的几百毫秒里界面只显示一行字。

**调用者与被调用者**：被 `InitializeComponent` 解析；读 `MainViewModel.Status` / `Diagnostics`。

---

### 菜单结构（本轮改动）

```
文件  从 VFS 打开… (Ctrl+O)       → OnOpenFromVfs   （索引 / 导出目录，带签名）
      从本地打开… (Ctrl+Shift+O)  → OnOpenFromLocal （直接选 .uasset，不走索引）
      ────
      设置…                      → OnOpenSettingsWindow
      ────
      退出
资产  从 VFS 打开… / 从本地打开… / （最近 5 项，展开时重建）
视图  返回 (Alt+Left) / 重置视图
帮助  打开日志 / 打开配置文件 / 关于
```

**为什么把「字号」「标题风格」「重新加载设置」从视图菜单删掉**：
它们都是**配置项**，现在统一由设置窗口改。两处都能改会让人分不清哪个生效，
而设置窗口有「应用/取消」的明确语义。

**为什么文件菜单和资产菜单都放两个打开入口**：
「文件」是新用户会点的地方，「资产」是切资产时顺手的地方。
两处指向同一对处理器，不存在走不同代码的问题。

### `MainWindow.OnOpenFromLocal(object sender, RoutedEventArgs e)` ✅ 已实现

**签名与契约**
弹 `OpenFileDialog` 选 `.uasset`，检查同目录 `.uexp` 是否存在，缺了先确认，再调
`MainViewModel.OpenLocalAsset`。副作用：可能弹一个确认 `MessageBox`、切换当前资产。

**关键分支（伪代码）**

```
if 用户取消: return
if not File.Exists(同目录 .uexp):
    弹「缺少 .uexp，仍然尝试打开吗？」→ 选否就 return
_viewModel.OpenLocalAsset(dialog.FileName)
```

**边界与失败**
打开失败不在这里报错——`OpenLocalAsset` 把原因写进 `Status`，菜单层只负责选文件。

**调用者与被调用者**
- 调用者：XAML 两处菜单项
- 被调用者：`MainViewModel.OpenLocalAsset`

### `MainWindow.OnOpenSettingsWindow(object sender, RoutedEventArgs e)` ✅ 已实现

**签名与契约**
打开 `SettingsWindow`（模态）。订阅 `Applied`（「应用」不关窗，立即生效），
`ShowDialog() == true` 时再用 `Result` 应用一次。副作用：可能写配置文件并重排图。

**关键分支（伪代码）**

```
window = new SettingsWindow(_viewModel.Settings, _viewModel.SettingsPath)
window.Applied += (_, s) => _viewModel.ReplaceSettings(s.Clone())
if window.ShowDialog() == true and window.Result is { } result:
    _viewModel.ReplaceSettings(result)
```

**边界与失败**
「应用」已应用过一次后再点「确定」会再应用一次（幂等：只是重新落盘 + 重排）。

**调用者与被调用者**
- 调用者：文件菜单「设置…」
- 被调用者：`SettingsWindow`、`MainViewModel.ReplaceSettings`
