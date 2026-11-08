# Converters.StyleIsConverter

字符串属性 → 单选按钮选中状态。对应源码 `src/SolsticeWinter.App/Converters/StyleIsConverter.cs`。

## 职责

让「一个字符串枚举值」能绑到一组 `RadioButton` 上：`ConverterParameter` 声明「这个按钮代表哪个值」。

## 对外入口

`IValueConverter` 实现，XAML 里以 `<conv:StyleIsConverter x:Key="StyleIs" />` 声明。

## 内部结构

无状态，两个方法。

## 依赖

`System.Globalization`、`System.Windows.Data`。

## 取舍

**为什么不用两个专用转换器**（`TitlePrettyIsChecked` / `TitleRawIsChecked`）：
那要写两个类且每个只能服务一个值；参数化后加新取值不用再写转换器。

**未选中返回 `DoNothing`**：
`RadioButton.IsChecked` 双向绑定时，取消勾选也会触发 `ConvertBack(false)`。
若无条件写回空串，值会被自己清掉。

### `Convert(object? value, Type targetType, object? parameter, CultureInfo culture)`

**签名与契约**
把字符串值和参数做**有序忽略大小写**比较，相等返回 `true`。
副作用：无。

**输入输出**
- `value` 不是字符串（含 `null`）→ `false`
- `parameter` 不是字符串 → `false`
- 返回 `bool`（WPF 会转成 `bool?` 给 `IsChecked`）

**关键分支（伪代码）**

```
return string.Equals(value as string, parameter as string, OrdinalIgnoreCase)
```

**边界与失败**
不会抛异常。`null` vs `null` 判为相等 —— 但用法上参数总是常量字符串，不会出现。

**调用者与被调用者**
- 调用者：`SettingsWindow.xaml` 里两个标题风格单选按钮
- 被调用者：无

### `ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)`

**签名与契约**
`value == true` 时把 `parameter` 写回模型；否则返回 `Binding.DoNothing`。

**输入输出**
- `value` 为 `true` 且 `parameter` 是字符串 → 返回该字符串
- 其它情况 → `Binding.DoNothing`（不写回）

**关键分支（伪代码）**

```
if value is true and parameter is string s: return s
return Binding.DoNothing
```

**边界与失败**
- `parameter` 为空 → 返回 `DoNothing`，模型不被改动。

**调用者与被调用者**
- 调用者：WPF 绑定引擎
- 被调用者：无
