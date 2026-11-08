using UAssetAPI;
using UAssetAPI.Kismet.Bytecode;

namespace SolsticeWinter.Decompiler.Infrastructure;

/// <summary>
/// 给每条顶层指令算「在函数字节码里的偏移」。
///
/// <para>
/// <b>为什么需要这一层</b>：参考项目直接读 CUE4Parse 的 <c>KismetExpression.StatementIndex</c>，
/// 而那个字段在 CUE4Parse 里就是**流的字节位置**（<c>expression.StatementIndex = index</c>，
/// index 是 <c>FKismetArchive.Index</c>）。UAssetAPI 没有这个字段，
/// 所以这里用 <c>GetSize</c> 累加补一份——跳转操作数（<c>CodeOffset</c>）也是字节偏移，两者才对得上。
/// </para>
///
/// <para>
/// 只映射**顶层**指令：跳转目标永远落在顶层指令边界上，嵌套表达式的偏移用不到。
/// </para>
/// </summary>
public static class StatementMap
{
    public static Dictionary<KismetExpression, uint> Build(
        IReadOnlyList<KismetExpression> bytecode, UAsset asset)
    {
        var map = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
        uint offset = 0;

        foreach (var expression in bytecode)
        {
            map[expression] = offset;
            offset += expression.GetSize(asset);
        }

        return map;
    }

    /// <summary>函数字节码的总长度（偏移上界）。</summary>
    public static uint Length(IReadOnlyList<KismetExpression> bytecode, UAsset asset)
    {
        uint total = 0;
        foreach (var expression in bytecode) total += expression.GetSize(asset);
        return total;
    }
}
