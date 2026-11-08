using System.Reflection;
using Solstice.Kismet;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 按「入口偏移」切一段字节码再反编译。
///
/// <para>
/// <b>为什么必须有这一层</b>：cooked 的 ubergraph 是**段复用**的——
/// 段是连续块，事件可以从段中间进入，多个事件共享同一段尾。
/// 实测 `BP_Logic`：段起始 `[10,83,648,1267]`，入口 `[107,721,726,731,938,1295]`，
/// 两者根本对不上。直接渲染整段会让 A 事件看到 B 事件的代码。
/// </para>
///
/// <para>
/// 做法：从 <see cref="KismetWorkspace"/> 里反射取 <c>_asset</c> 与函数的
/// <c>ScriptBytecode</c>，按 <c>GetSize</c> 累加算出每个入口的表达式下标，
/// 切出 <c>[入口, 段尾)</c> 后用 <see cref="KismetDecompiler.Decompile"/> 的
/// <c>baseOffset</c> 重编——跳转是绝对字节偏移，传错起点结构就全错。
/// </para>
///
/// <para>
/// 边界：反射依赖 <c>KismetWorkspace</c> 的私有字段名 <c>_asset</c>（与 Diag 工具同一手法）。
/// 库改名就退化成「渲染整段」，并在状态栏说明，不静默出错。
/// </para>
/// </summary>
internal static class SectionSlicer
{
    private static readonly FieldInfo? AssetField =
        typeof(KismetWorkspace).GetField("_asset", BindingFlags.NonPublic | BindingFlags.Instance);

    public static bool IsAvailable => AssetField is not null;

    /// <summary>
    /// 切出 <paramref name="entryOffset"/> 到 <paramref name="endOffset"/>（不含）之间的节点。
    /// 失败返回 null，调用方退回「渲染整段」。
    /// </summary>
    public static IReadOnlyList<KNode>? Slice(
        KismetWorkspace workspace,
        string functionName,
        int entryOffset,
        int endOffset)
    {
        try
        {
            if (AssetField?.GetValue(workspace) is not UAsset asset) return null;

            var function = asset.Exports.OfType<FunctionExport>()
                .FirstOrDefault(e => e.ObjectName?.ToString() == functionName);
            if (function?.ScriptBytecode is not { Length: > 0 } bytecode) return null;

            var start = IndexAtOffset(bytecode, asset, entryOffset);
            if (start < 0) return null;

            var end = endOffset == int.MaxValue
                ? bytecode.Length
                : IndexAtOffset(bytecode, asset, endOffset);
            if (end < 0) end = bytecode.Length;
            if (end <= start) return null;

            var slice = new List<KismetExpression>(end - start);
            for (var i = start; i < end; i++) slice.Add(bytecode[i]);

            var names = ImportNames(asset);
            var options = new DecompileOptions(
                Measure: e => e.GetSize(asset),
                ResolveImport: idx => names.TryGetValue(idx?.Index ?? 0, out var n) ? n : null);

            return KismetDecompiler.Decompile(slice, options, (uint)entryOffset);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>诊断：把切片的每一步中间量打出来（入口下标、切片表达式数、节点数）。</summary>
    public static string Debug(KismetWorkspace workspace, string functionName, int entryOffset, int endOffset)
    {
        try
        {
            if (AssetField?.GetValue(workspace) is not UAsset asset) return "反射拿不到 _asset";
            var function = asset.Exports.OfType<FunctionExport>()
                .FirstOrDefault(e => e.ObjectName?.ToString() == functionName);
            if (function?.ScriptBytecode is not { Length: > 0 } bytecode) return $"没有函数 {functionName} 的字节码";

            var start = IndexAtOffset(bytecode, asset, entryOffset);
            var end = endOffset == int.MaxValue ? bytecode.Length : IndexAtOffset(bytecode, asset, endOffset);
            var nodes = Slice(workspace, functionName, entryOffset, endOffset);

            return $"函数 {functionName} 表达式 {bytecode.Length} 条\n" +
                   $"入口 @{entryOffset} → 表达式下标 {start}\n" +
                   $"段尾 @{(endOffset == int.MaxValue ? "末尾" : endOffset.ToString())} → 表达式下标 {end}\n" +
                   $"切片表达式 {Math.Max(0, end - start)} 条 → 反编译节点 {nodes?.Count ?? -1} 个\n" +
                   (nodes is { Count: > 0 } ? $"首节点：{nodes[0].GetType().Name} {nodes[0].Title}" : string.Empty);
        }
        catch (Exception e)
        {
            return $"诊断失败：{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>按字节偏移找表达式下标；找不到（偏移落在指令中间）返回 -1。</summary>
    private static int IndexAtOffset(IReadOnlyList<KismetExpression> bytecode, UAsset asset, int target)
    {
        var cursor = 0;
        for (var i = 0; i < bytecode.Count; i++)
        {
            if (cursor == target) return i;
            if (cursor > target) return -1;
            cursor += (int)bytecode[i].GetSize(asset);
        }

        return cursor == target ? bytecode.Count : -1;
    }

    private static Dictionary<int, string> ImportNames(UAsset asset)
    {
        var map = new Dictionary<int, string>(asset.Imports.Count);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var name = asset.Imports[i].ObjectName?.ToString();
            if (!string.IsNullOrEmpty(name)) map[-(i + 1)] = name;
        }

        return map;
    }
}


