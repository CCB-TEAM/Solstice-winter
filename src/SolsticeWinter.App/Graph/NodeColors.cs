using System.Windows.Media;
using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Nodes;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 节点标题栏配色。同类节点的颜色必须**稳定且唯一**——UE 里同一个蓝图类的调用节点同色，
/// 一眼就能看出「这几个节点来自同一个类」。
///
/// <para>
/// 做法：没有归属类的节点（Branch / Set / Get / Return）用固定调色板；
/// 有归属类的调用节点，把类名哈希成**色相**，饱和度和明度固定在偏暗的区间，
/// 保证深色画布上不刺眼、且不同类之间可分辨。
/// </para>
///
/// <para>
/// 用类名哈希而不是「按出现顺序分配」：顺序分配会随图的遍历顺序变化，
/// 同一个类在不同函数里颜色不一样，反而更乱。
/// </para>
/// </summary>
internal static class NodeColors
{
    /// <summary>
    /// 固定种类的标题栏颜色，按 **UE 编辑器的语义**取色：
    /// 红色 = 事件/函数入口，深蓝 = 流程控制（Branch / Sequence），
    /// 紫色 = 宏，青色 = 纯数据，绿 = 变量写，暗红 = 返回。
    /// </summary>
    private static readonly Dictionary<string, Color> Fixed = new(StringComparer.Ordinal)
    {
        ["Entry"] = Color.FromRgb(0xA0, 0x24, 0x24),
        ["Call"] = Color.FromRgb(0x3B, 0x6E, 0xA5),
        ["Data"] = Color.FromRgb(0x3F, 0x7A, 0x80),
        ["Branch"] = Color.FromRgb(0x1B, 0x3A, 0x5C),
        ["Macro"] = Color.FromRgb(0x5B, 0x3A, 0x8C),
        ["Set"] = Color.FromRgb(0x2F, 0x7D, 0x6B),
        ["Return"] = Color.FromRgb(0x9A, 0x4A, 0x4A),
        ["Opaque"] = Color.FromRgb(0x6E, 0x77, 0x81),
    };

    /// <summary>按节点算标题栏颜色：调用节点按归属类上色，其它按种类。</summary>
    public static Brush? For(BPNode node)
    {
        if (node is K2Node_CallFunction call && !call.Pure && call.OwnerPath.Length > 0)
            return ForOwner(call.OwnerPath);

        return ForKind(KindOf(node));
    }

    /// <summary>按种类取固定色（未知种类退回 `Call`）。</summary>
    public static Brush ForKind(string kind)
    {
        var color = Fixed.TryGetValue(kind, out var found) ? found : Fixed["Call"];
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>按归属类名取稳定色。</summary>
    public static Brush ForOwner(string ownerPath)
    {
        var dot = ownerPath.LastIndexOf('.');
        var shortName = dot >= 0 ? ownerPath[(dot + 1)..] : ownerPath;

        // FNV-1a：字符串哈希稳定（string.GetHashCode 在不同进程间可能不同，
        // 那会让同一个类在两次启动里颜色不一样）。
        var hash = 2166136261u;
        foreach (var ch in shortName)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        var hue = hash % 360;
        var brush = new SolidColorBrush(Hsv(hue, 0.42, 0.55));
        brush.Freeze();
        return brush;
    }

    private static string KindOf(BPNode node) => node switch
    {
        // UE 里事件/函数入口是**红色**的；之前没有这一项、落到了 `Call` 的蓝色。
        K2Node_FunctionEntry => "Entry",
        // 宏在 UE 里是紫色，和普通调用（按类上色）明确分开。
        K2Node_MacroInstance => "Macro",
        K2Node_IfThenElse => "Branch",
        K2Node_ExecutionSequence => "Branch",
        K2Node_VariableSet => "Set",
        K2Node_AssignmentStatement => "Set",
        K2Node_FunctionResult => "Return",
        K2Node_VariableGet => "Data",
        K2Node_TemporaryVariable => "Data",
        K2Node_CallFunction call => call.Pure ? "Data" : "Call",
        UnknownNode => "Opaque",
        _ => "Call",
    };

    private static Color Hsv(double hueDegrees, double saturation, double value)
    {
        var c = value * saturation;
        var h = hueDegrees / 60.0;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = value - c;

        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

