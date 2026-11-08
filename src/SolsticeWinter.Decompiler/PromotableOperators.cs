namespace SolsticeWinter.Decompiler;

/// <summary>
/// 可折叠运算符表（移植自 UEBGV 的 <c>EngineBPData.PromotableOperators</c> 与
/// <c>KismetMathLibrarySpecialNodes</c>）。
///
/// <para>
/// UE 把 `+` 编成 `Add_IntInt` / `Add_DoubleDouble` 这类调用；画布上应该显示成 `+` 而不是函数名。
/// 规则：先按**完整函数名**查（`Not_PreBool` → `NOT`），再按**前缀**查（`Add_IntInt` → `Add` → `+`）。
/// </para>
/// </summary>
public static class PromotableOperators
{
    private static readonly Dictionary<string, string> ByPrefix = new(StringComparer.Ordinal)
    {
        ["Add"] = "+",
        ["Divide"] = "/",
        ["EqualEqual"] = "==",
        ["Greater"] = ">",
        ["GreaterEqual"] = ">=",
        ["Less"] = "<",
        ["LessEqual"] = "<=",
        ["Multiply"] = "*",
        ["NotEqual"] = "!=",
        ["Subtract"] = "-",
    };

    private static readonly Dictionary<string, string> ByName = new(StringComparer.Ordinal)
    {
        ["Min"] = "MIN",
        ["Max"] = "MAX",
        ["BMin"] = "MIN",
        ["BMax"] = "MAX",
        ["FMin"] = "MIN",
        ["FMax"] = "MAX",
        ["MinInt64"] = "MIN",
        ["MaxInt64"] = "MAX",
        ["Abs"] = "ABS",
        ["Abs_Int"] = "ABS",
        ["Abs_Int64"] = "ABS",
        ["Sin"] = "SIN",
        ["Asin"] = "ASIN",
        ["Cos"] = "COS",
        ["Acos"] = "ACOS",
        ["Tan"] = "TAN",
        ["Exp"] = "e",
        ["Sqrt"] = "SQRT",
        ["Square"] = "^2",
        ["GetPI"] = "PI",
        ["GetTAU"] = "TAU",
        ["DegreesToRadians"] = "D2R",
        ["RadiansToDegrees"] = "R2D",
        ["DegSin"] = "SINd",
        ["DegAsin"] = "ASINd",
        ["DegCos"] = "COSd",
        ["DegAcos"] = "ACOSd",
        ["DegTan"] = "TANd",
        ["EqualExactly_Vector2DVector2D"] = "===",
        ["NotEqualExactly_Vector2DVector2D"] = "!==",
        ["EqualExactly_VectorVector"] = "===",
        ["NotEqualExactly_VectorVector"] = "!==",
        ["Dot_VectorVector"] = "dot",
        ["Cross_VectorVector"] = "cross",
        ["Percent_ByteByte"] = "%",
        ["Percent_IntInt"] = "%",
        ["Percent_Int64Int64"] = "%",
        ["Percent_FloatFloat"] = "%",
        ["And_IntInt"] = "&",
        ["Xor_IntInt"] = "^",
        ["Or_IntInt"] = "|",
        ["Not_Int"] = "~",
        ["And_Int64Int64"] = "&",
        ["Xor_Int64Int64"] = "^",
        ["Or_Int64Int64"] = "|",
        ["Not_Int64"] = "~",
        ["ComposeTransforms"] = "*",
    };

    /// <summary>函数名 → 运算符符号；不是可折叠运算符返回 null。</summary>
    public static string? Resolve(string functionName)
    {
        if (ByName.TryGetValue(functionName, out var byName)) return byName;

        var separator = functionName.IndexOf('_');
        var prefix = separator > 0 ? functionName[..separator] : functionName;
        return ByPrefix.TryGetValue(prefix, out var byPrefix) ? byPrefix : null;
    }
}

