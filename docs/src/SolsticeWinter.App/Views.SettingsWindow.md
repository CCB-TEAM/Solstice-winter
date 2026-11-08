# Views.SettingsWindow

设置窗口。形态参考 FModel 的 Settings：**顶部标签页分组 + 底部 应用 / 确定 / 取消**。
入口在主窗口「文件 → 设置…」。对应源码 `src/SolsticeWinter.App/Views/SettingsWindow.xaml(.cs)`。

## 职责

把 `StudioSettings` 的所有字段做成可编辑的表单，并保证「取消」不留下任何改动。

## 对外入口

| 成员 | 说明 |
|---|---|
| `SettingsWindow(StudioSettings current, string settingsPath)` | 构造时**深拷贝** `current`，`DataContext` 指向副本 |
| `event EventHandler<StudioSettings> Applied` | 点「应用」时触发，参数是当前编辑副本 |
| `StudioSettings? Result` | 点「确定」后的结果；取消时是 `null` |

## 内部结构

```
Window
├── DockPanel
│   ├── 底部 Border：StatusText + [应用] [确定] [取消]
│   └── TabControl
│       ├── 通用   数据源七项（导出目录 / usmap / jmap / registry / pak / 游戏目录 / AES 来源）
│       ├── 显示   标题风格（两个单选）/ 显示引脚类型 / 字号 / 新反编译器开关
│       └── 布局   旧投影的列宽 / 行高 / 数据偏移 + 配置文件路径
└── Window.Resources：FieldBox / FieldCheck / FieldRadio / Hint / FieldLabel / SectionTitle / DialogButton / PathRow
```

## 依赖

- `SolsticeWinter.Core.StudioSettings` / `Sub*Settings`：被编辑的模型
- `SolsticeWinter.App.Converters.StyleIsConverter`：字符串属性 ↔ 单选按钮
- `Microsoft.Win32.OpenFolderDialog` / `OpenFileDialog`：路径浏览

## 取舍

**1. 编辑副本，不直接改原对象。**
`StudioSettings` 的子对象（`Sources` / `Display` / `Layout`）都是引用类型，浅拷贝会把它们一起改掉，
「取消」就变成了「撤销一部分」。所以用 JSON 往返做深拷贝（`StudioSettings.Clone`）。

**2. 直接 `DataContext = 副本`，不写 ViewModel 包装属性。**
XAML 里写 `{Binding Sources.ExportDirectory}`。代价是这窗口只能做「字段编辑」，
将来要有联动（比如选 pak 目录自动找 usmap）就得补一个 ViewModel。

**3. 数值框用 `UpdateSourceTrigger=PropertyChanged`。**
默认 `LostFocus` 时，「点应用按钮」的瞬间焦点还在输入框里，最后编辑的那个数字会被吞掉。
这是 WPF 的经典坑，改触发器比补一套焦点提交更省事。

**4. 没显示 `Display.ShowGrid`。**
它原本在配置里但**代码中零引用**（网格由 `GraphView.xaml` 的 `Background` 决定），
已在这次改动里从 `StudioSettings` 删掉——留一个无效开关比少一个开关更容易误导。

**5. 布局页标注「仅旧投影生效」。**
`Layout.*` 只被旧的 `GraphBuilder` 消费；新反编译器（默认）用 Sugiyama 分层布局，
参数固定在 `MsaglLayout` 里。不写清楚会让人以为调了没用。

---

### `SettingsWindow(StudioSettings current, string settingsPath)`

**签名与契约**
构造窗口。`current` 被 `Clone()` 后作为 `DataContext`；`settingsPath` 只用于在「布局」页显示配置文件位置。
副作用：触发 XAML 解析与资源解析（`InitializeComponent`）。资源键写错会在这里抛异常。

**输入输出**
- `current`：任意（不可为 null）。各字段可以为 null / 空串，表单照样显示为空。
- `settingsPath`：仅显示用，不校验存在性。
- 返回：无。`Result` 初始为 `null`。

**关键分支（伪代码）**

```
_editing = current.Clone()
DataContext = _editing
SettingsPathText.Text = settingsPath
StatusText.Text = "改完点「应用」立即生效"
```

**边界与失败**
- `current` 为 null → NRE（构造签名不接受 null，调用方保证）。
- XAML 资源缺失 → `XamlParseException`，窗口构造失败；`--check-settings` 冒烟专门抓这个。

**调用者与被调用者**
- 调用者：`MainWindow.OnOpenSettingsWindow`
- 被调用者：`StudioSettings.Clone`、XAML 内 `StyleIsConverter.Convert`

---

### `OnOk / OnCancel / OnApply(object sender, RoutedEventArgs e)`

**签名与契约**
三个按钮的点击处理。
- `OnOk`：`Result = _editing; DialogResult = true`（关闭窗口）
- `OnCancel`：`DialogResult = false`（关闭窗口，`Result` 保持 `null`）
- `OnApply`：触发 `Applied` 事件，**不关闭窗口**，并把状态文字改成「已应用 时:分:秒」

**输入输出**
`DialogResult` 只在窗口以 `ShowDialog()` 打开时才能赋值，否则抛 `InvalidOperationException`。

**关键分支（伪代码）**

```
OnOk:     Result = _editing; DialogResult = true
OnCancel: DialogResult = false
OnApply:  Applied?.Invoke(this, _editing); StatusText = 已应用 <时间>
```

**边界与失败**
- 以 `Show()`（非模态）打开时 `DialogResult` 会抛异常 —— 本窗口只以 `ShowDialog` 使用。

**调用者与被调用者**
- 调用者：XAML 按钮
- 被调用者：`Applied` 订阅者（`MainWindow` → `MainViewModel.ReplaceSettings`）

---

### `OnBrowseFolder / OnBrowseFile(object sender, RoutedEventArgs e)`

**签名与契约**
路径浏览。按钮的 `Tag` 绑定到目标 `TextBox`，选完把结果写回 `box.Text`（再由绑定写回模型）。
- `OnBrowseFolder`：`OpenFolderDialog`，起始目录取当前值（存在时）
- `OnBrowseFile`：`OpenFileDialog`，过滤 `*.usmap;*.json;*.bin`

**输入输出**
- `sender.Tag` 不是 `TextBox` → 直接返回（防御性：改错 XAML 不至于崩）
- 用户取消 → 不改任何值

**关键分支（伪代码）**

```
if sender.Tag is not TextBox box: return
dialog.InitialDirectory = 当前值（存在才设）
if dialog.ShowDialog(this) == true: box.Text = 选中路径
```

**边界与失败**
- 当前值指向不存在的路径 → 不设 `InitialDirectory`（设了会抛，对话框打不开）。
- 文件夹对话框在 .NET 8+ 才有（`OpenFolderDialog`），本项目 `net10.0-windows` 满足。

**调用者与被调用者**
- 调用者：XAML 里七处「浏览…」按钮
- 被调用者：`Microsoft.Win32.OpenFolderDialog` / `OpenFileDialog`
