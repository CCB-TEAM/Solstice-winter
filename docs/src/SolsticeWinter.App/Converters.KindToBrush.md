# Converters.KindToBrush — 节点种类 → 标题栏颜色

**职责一句话**：把 `GraphNodeVm.Kind`（Call / Data / Branch / Set / Return / Opaque）映射成一个 `SolidColorBrush`，给节点标题栏上色。

**对外入口**：`KindToBrushConverter.Convert(...)`（`IValueConverter`，XAML 里 `{Binding Kind, Converter={StaticResource KindToBrush}}`）。

**内部结构**：一张静态字典（6 个 `Color`）→ `SolidColorBrush`。无状态、无 I/O。

**依赖**：`System.Windows.Media`。被 `Views/GraphView.xaml` 的 `Node.HeaderTemplate` 使用。

**取舍**：

- **颜色写死在转换器里，不查 `Application.Resources`**：转换器在 XAML 解析期就要出值，查应用资源在设计器/单元测试里会拿到 null。代价是 `Themes/Studio.xaml` 里的 `Node.Header.*` 与这里各存一份，改色要改两处（已在两处都写了注释指向对方）。
- **未知 Kind 落到 `Call` 的颜色**：不抛异常——标题栏配色错一下不影响功能，抛异常会让整张图渲染失败。

---

### `KindToBrushConverter.Convert(object?, Type, object?, CultureInfo): object`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public`。`value` 期望是 `string`（`GraphNodeVm.Kind`）；返回非冻结的 `SolidColorBrush`。无副作用。

**输入输出**：

- `"Branch"` → `#A06A1F`；`"Set"` → `#2F7D6B`；`"Return"` → `#9A4A4A`；`"Data"` → `#4A7C8C`；`"Opaque"` → `#6E7781`；`"Call"` → `#3B6EA5`。
- `null` / 非字符串 / 未知 Kind → `"Call"` 的颜色。

**关键分支（伪代码）**：

```text
kind = value as string ?? "Call"
color = Palette.TryGetValue(kind) ? found : Palette["Call"]    # 未知种类兜底成 Call
return new SolidColorBrush(color)
```

**边界与失败**：

- ⛔ 每次调用都 `new` 一个 Brush：节点多时会有额外分配（实测 39 节点无感；上千节点要改成缓存/冻结）。
- ⛔ `ConvertBack` 抛 `NotSupportedException`：这个转换器只单向。

**调用者与被调用者**：被 `GraphView.xaml` 的 `Node.HeaderTemplate` 绑定调用；不调用其它函数。
