using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SolsticeWinter.App.Converters;

/// <summary>
/// 节点种类 → 标题栏颜色。
///
/// <para>
/// 颜色写死在这里而不是查资源：转换器在 XAML 解析期就要出值，
/// 查 <c>Application.Resources</c> 会在设计器/单元测试里拿到 null。
/// 与 <c>Themes/Studio.xaml</c> 里的 <c>Node.Header.*</c> 保持一致。
/// </para>
/// </summary>
public sealed class KindToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.Ordinal)
    {
        ["Call"] = Color.FromRgb(0x3B, 0x6E, 0xA5),
        ["Data"] = Color.FromRgb(0x4A, 0x7C, 0x8C),
        ["Branch"] = Color.FromRgb(0xA0, 0x6A, 0x1F),
        ["Set"] = Color.FromRgb(0x2F, 0x7D, 0x6B),
        ["Return"] = Color.FromRgb(0x9A, 0x4A, 0x4A),
        ["Opaque"] = Color.FromRgb(0x6E, 0x77, 0x81),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value as string ?? "Call";
        var color = Palette.TryGetValue(kind, out var found) ? found : Palette["Call"];
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
