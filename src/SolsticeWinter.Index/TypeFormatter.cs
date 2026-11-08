using UAssetAPI.FieldTypes;
using UAssetAPI.UnrealTypes;

namespace SolsticeWinter.Index;

/// <summary>
/// 把 <see cref="FProperty"/> 渲染成可读类型名。
///
/// <para>
/// 实测（`BP_Logic`）：标量类型在 UAssetAPI 里**没有专用子类**，都是
/// <see cref="FGenericProperty"/>，真实类型名在 <c>SerializedType.Value.Value</c>（`"IntProperty"` 等）；
/// 容器 / 对象 / 结构体 / 枚举才有专用子类。所以分派顺序必须是「先认子类，再退回 SerializedType」。
/// </para>
/// </summary>
public static class TypeFormatter
{
    /// <summary>
    /// <paramref name="resolve"/> 负责把包索引解析成对象路径（由 <see cref="AssetHeaderReader"/> 提供，
    /// 因为它要读该资产自己的 Import/Export 表）。
    /// </summary>
    public static string Of(FProperty? property, Func<FPackageIndex?, string> resolve)
    {
        if (property is null) return "?";

        return property switch
        {
            FArrayProperty a => $"Array<{Of(a.Inner, resolve)}>",
            FSetProperty s => $"Set<{Of(s.ElementProp, resolve)}>",
            FMapProperty m => $"Map<{Of(m.KeyProp, resolve)}, {Of(m.ValueProp, resolve)}>",
            FStructProperty st => resolve(st.Struct),
            FEnumProperty en => resolve(en.Enum),
            FInterfaceProperty it => resolve(it.InterfaceClass),
            FDelegateProperty d => resolve(d.SignatureFunction),
            FObjectProperty o => resolve(o.PropertyClass),
            _ => Scalar(property)
        };
    }

    private static string Scalar(FProperty property) =>
        property.SerializedType?.Value?.Value
        ?? property.GetType().Name.Replace("Property", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// 参数方向判定。实测口径（见 skills/blueprint-read-modify 的边界说明）：
    /// <c>out &amp;&amp; !ref</c> → 真输出；<c>out &amp;&amp; ref</c> → 按引用入参；<c>return</c> 看 ReturnParm。
    /// </summary>
    public static string Direction(EPropertyFlags flags)
    {
        if ((flags & EPropertyFlags.CPF_ReturnParm) != 0) return "return";
        var isOut = (flags & EPropertyFlags.CPF_OutParm) != 0;
        var isRef = (flags & EPropertyFlags.CPF_ReferenceParm) != 0;
        return isOut && !isRef ? "out" : "in";
    }
}
