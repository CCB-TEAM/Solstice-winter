namespace SolsticeWinter.Decompiler;

/// <summary>
/// 判断一个 Kismet 函数是不是**纯函数**（UE 里没有 exec 脚）。
///
/// <para>
/// 为什么需要：字节码里 `EX_FinalFunction` / `EX_LocalFinalFunction` 出现在顶层时，
/// 我们原本一律当成 impure（发射 exec 脚）。但 `Array_Get`、`Array_Length`、`Conv_*`、
/// 所有 `KismetMathLibrary` / `KismetStringLibrary` 的函数在 UE 里都是 BlueprintPure，
/// 画到画布上就变成「静态节点长了 exec 口」——明显不对。
/// </para>
///
/// <para>
/// 数据来自 UE 引擎的 Kismet 库声明：纯函数只读不写世界，impure 的会改状态或需要执行顺序。
/// 表里没有的函数返回 <c>null</c>（未知），调用方保持原有判定。
/// </para>
/// </summary>
public static class KismetPurity
{
    /// <summary>会修改容器 / 世界，必须带 exec。</summary>
    private static readonly HashSet<string> Impure = new(StringComparer.Ordinal)
    {
        "Array_Add", "Array_AddUnique", "Array_Insert", "Array_Remove", "Array_RemoveItem",
        "Array_Clear", "Array_Resize", "Array_Append", "Array_Shuffle", "Array_Reverse",
        "Array_Swap", "Array_Set", "Array_Empty",
        "Map_Add", "Map_Remove", "Map_Clear",
        "Set_Add", "Set_AddItems", "Set_Remove", "Set_Clear",
    };

    /// <summary>只做计算、没有 exec 的库函数。</summary>
    private static readonly HashSet<string> Pure = new(StringComparer.Ordinal)
    {
        "Array_Get", "Array_Length", "Array_IsValidIndex", "Array_Contains", "Array_Find",
        "Array_LastIndex", "Array_Random", "Array_IsEmpty", "Array_IsNotEmpty", "Array_Identical",
        "Map_Find", "Map_Contains", "Map_Length", "Map_Keys", "Map_Values", "Map_IsEmpty", "Map_IsNotEmpty",
        "Set_Length", "Set_Contains", "Set_IsEmpty", "Set_IsNotEmpty", "Set_ToArray",
    };

    /// <summary>整库都是纯函数的模块（按 owner 路径匹配）——这些库里没有会改世界的东西。</summary>
    private static readonly string[] PureLibraries =
    [
        "KismetMathLibrary", "KismetStringLibrary", "KismetTextLibrary", "KismetNodeHelperLibrary",
        "BlueprintPathsLibrary", "KismetGuidLibrary", "KismetInputLibrary",
    ];

    /// <summary>
    /// 返回 <c>true</c>（纯）/ <c>false</c>（impure）/ <c>null</c>（不认识，调用方自己决定）。
    /// </summary>
    public static bool? Of(string functionName, string ownerPath)
    {
        if (Impure.Contains(functionName)) return false;
        if (Pure.Contains(functionName)) return true;

        foreach (var library in PureLibraries)
        {
            if (ownerPath.Contains(library, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return null;
    }
}
