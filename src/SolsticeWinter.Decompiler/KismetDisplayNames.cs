namespace SolsticeWinter.Decompiler;

/// <summary>
/// 函数名 → 画布上显示的短标题（`BooleanAND` → `AND`、`Array_Length` → `LENGTH`）。
///
/// <para>
/// 移植自 UEBGV 的三张表：<c>KismetMathLibrarySpecialNodes</c>、
/// <c>KismetArrayLibrarySpecialNodes</c>、<c>BlueprintMapLibrarySpecialNodes</c>。
/// 与 <see cref="PromotableOperators"/> 的区别：那张表把调用**变成运算符节点**（无标题栏、无 owner），
/// 这张表只是**改标题**，节点类型仍然是 <c>K2Node_CallFunction</c>。
/// </para>
/// </summary>
public static class KismetDisplayNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // 布尔运算（UEBGV 保留成 CallFunction，但标题是短名）
        ["BooleanAND"] = "AND",
        ["BooleanNAND"] = "NAND",
        ["BooleanNOR"] = "NOR",
        ["BooleanOR"] = "OR",
        ["BooleanXOR"] = "XOR",
        ["Not_PreBool"] = "NOT",

        // KismetArrayLibrary
        ["Array_Add"] = "ADD",
        ["Array_AddUnique"] = "ADDUNIQUE",
        ["Array_Shuffle"] = "SHUFFLE",
        ["Array_ShuffleFromStream"] = "SHUFFLE",
        ["Array_Identical"] = "==",
        ["Array_Append"] = "APPEND",
        ["Array_Insert"] = "INSERT",
        ["Array_Remove"] = "REMOVE INDEX",
        ["Array_RemoveItem"] = "REMOVE",
        ["Array_Clear"] = "CLEAR",
        ["Array_Resize"] = "RESIZE",
        ["Array_Reverse"] = "REVERSE",
        ["Array_Length"] = "LENGTH",
        ["Array_IsEmpty"] = "IS EMPTY",
        ["Array_IsNotEmpty"] = "IS NOT EMPTY",
        ["Array_LastIndex"] = "LAST INDEX",
        ["Array_Get"] = "GET",
        ["Array_Swap"] = "SWAP",
        ["Array_Find"] = "FIND",
        ["Array_Contains"] = "CONTAINS",
        ["Array_IsValidIndex"] = "IS VALID INDEX",
        ["Array_Random"] = "RANDOM",

        // BlueprintMapLibrary
        ["Map_Add"] = "ADD",
        ["Map_Contains"] = "CONTAINS",
        ["Map_Find"] = "FIND",
        ["Map_Remove"] = "REMOVE",
        ["Map_Clear"] = "CLEAR",
        ["Map_Length"] = "LENGTH",
        ["Map_IsEmpty"] = "IS EMPTY",
        ["Map_IsNotEmpty"] = "IS NOT EMPTY",
        ["Map_Keys"] = "KEYS",
        ["Map_Values"] = "VALUES",
    };

    /// <summary>函数名 → 显示标题；没有映射返回 null（调用方保持默认标题）。</summary>
    public static string? Resolve(string functionName) =>
        Names.TryGetValue(functionName, out var display) ? display : null;
}
