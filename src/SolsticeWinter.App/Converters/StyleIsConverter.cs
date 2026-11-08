using System.Globalization;
using System.Windows.Data;

namespace SolsticeWinter.App.Converters;

/// <summary>
/// 字符串属性 → 单选按钮的选中状态。
///
/// <para>
/// 设置里「标题风格」是字符串（<c>pretty</c> / <c>raw</c>），而界面上是单选按钮（布尔）。
/// 用 <c>ConverterParameter</c> 指定「这个按钮代表哪个值」：
/// 值相等则选中；选中的时候把参数写回去。
/// </para>
///
/// <para>
/// 单向也是安全的：未选中时 <c>ConvertBack</c> 返回 <see cref="Binding.DoNothing"/>，
/// 否则取消勾选会把它自己的值写成空。
/// </para>
/// </summary>
public sealed class StyleIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string text ? text : Binding.DoNothing;
}
