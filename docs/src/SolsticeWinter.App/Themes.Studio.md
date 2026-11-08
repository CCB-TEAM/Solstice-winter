# Themes.Studio — 应用级配色（Adonis Light 打底）

**职责一句话**：在 AdonisUI 的 Light 配色之上，补这一层应用自己的语义色：面板、画布、节点、引脚类型色。控件外观（按钮/输入框/列表/菜单/下拉）**不在这里写**，交给 AdonisUI 的隐式样式。

**对外入口**：`Themes/Studio.xaml` 作为 `Application.Resources` 的最后一个合并字典（`App.xaml`）。

**内部结构**：

```text
App.xaml
 ├─ AdonisUI/ColorSchemes/Light.xaml           ← 配色（必须先合并）
 ├─ AdonisUI.ClassicTheme/Resources.xaml       ← 控件样式（1.17.1 没有顶层 Resources.xaml）
 └─ Themes/Studio.xaml                         ← 本文件：语义色 + PanelHeader
```

**依赖**：AdonisUI 1.17.1 + AdonisUI.ClassicTheme 1.17.1（NuGet，MIT；nupkg 已放进 `.nuget-local/`）。

**取舍**：

- **为什么用 AdonisUI**：用户要求「Adonis/Light」。它是 MIT、纯 WPF、无第三方依赖，且自带一整套浅色控件样式（按钮/输入框/列表/菜单/下拉/滚动条），比手写省一大截。
- **为什么不用 1.1.0**：NuGet 上的 1.1.0 是 2020 年的包，**只有 net40 目标**；我们的 app 是 `net10.0-windows`，引用它会有 NU1701，且 `Resources.xaml` 路径在 1.1.0 与 1.17.1 之间变过（1.17.1 把主字典挪到了 `AdonisUI.ClassicTheme`）。用 1.17.1（`net5.0-windows7.0`）才能干净加载。
- **只补语义色，不重写控件模板**：重写等于把 Adonis 的样式抄一遍，两边都会漂。代价是：Adonis 升级换配色时，这里的手工色要跟着对一遍。
- **外壳浅色、画布深色**：外壳用 Adonis Light，画布（NodifyEditor + 节点 + 连线 + 引脚）用深色。理由：节点图是主要视觉焦点，浅底上彩色引脚和连线对比度不够；深底也让「节点」与「面板」在视觉上分层。代价是两套色板要分别维护，`Chrome.*` 的浅色值**不能**往画布上套。
- **画布不跟随 Adonis 的 `Layer` 刷子**：Nodify 的节点是自绘的，用 Adonis 的分层刷子会让节点和画布糊在一起，所以单独定义 `Canvas.*` / `Node.*` / `Edge.*`。

---

### 色板键（`Themes/Studio.xaml`）

**状态**：✅ 已实现

**签名与契约**：纯 `SolidColorBrush` 资源字典，无代码、无行为。键名被 `MainWindow.xaml`、`Views/GraphView.xaml`、`Controls/NodeCanvas.cs` 以 `StaticResource` 引用。

**输入输出**：

| 组 | 键 | 值 | 用在哪 |
|---|---|---|---|
| 外壳 | `Chrome.Background` | `#F3F4F6` | Window 背景 |
| | `Chrome.Panel` | `#FFFFFF` | 左右栏 |
| | `Chrome.PanelAlt` | `#E9ECEF` | 标题条 / 状态条 / 菜单 |
| | `Chrome.Border` | `#D6DBE1` | 分隔线 |
| | `Chrome.Foreground` | `#1F2328` | 正文 |
| | `Chrome.ForegroundDim` | `#6B7280` | 次要文字 |
| | `Chrome.Accent` | `#2563EB` | 强调 |
| 画布 | `Canvas.Background` | `#1E1E1E`（**深色**） | NodifyEditor 背景 |
| | `Edge.Exec` / `Edge.Data` | `#D0D0D0` / `#6A8FB8` | 连线（exec 粗、data 细） |
| 节点 | `Node.Background` / `Node.Border` / `Node.Foreground` | `#252526` / `#3F3F46` / `#E6E6E6` | 节点外观 |
| 引脚 | `Pin.Exec` … `Pin.Unknown` | 见文件 | 引脚圆点（深底上提亮一档） |

**关键分支（伪代码）**：无分支（纯资源字典）。

**边界与失败**：

- ⛔ 键名与 `MainWindow.xaml` / `GraphView.xaml` 里的 `StaticResource` 强耦合：改名必须同时改引用，否则**启动即 XamlParseException**（没有运行时兜底）。
- ⛔ `Chrome.*` 与 Adonis 的 `Layer*` 刷子是两套体系：不要在同一个控件上混用，否则深/浅切换时会花。
- ⛔ 没有深色版本：`ColorSchemes/Dark.xaml` 未接入，`Studio.xaml` 的色值是按浅色定的。

**调用者与被调用者**：被 `App.xaml` 合并；被 `MainWindow.xaml`、`Views/GraphView.xaml` 引用。

