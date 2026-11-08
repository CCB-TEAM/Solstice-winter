using SolsticeWinter.Decompiler.Model;
using UAssetAPI.FieldTypes;

namespace SolsticeWinter.Decompiler.Signatures;

/// <summary>一个参数引脚的类型描述（来自 jmap 签名或资产本体）。</summary>
public sealed record SignatureParam(string Name, GraphPinType Type, bool IsReturn, bool IsOut);

/// <summary>一个函数的签名。</summary>
public sealed record FunctionSignature(string Owner, string Name, IReadOnlyList<SignatureParam> Parameters)
{
    public string ShortOwner => Owner.Length == 0 ? string.Empty : Owner[(Owner.LastIndexOf('.') + 1)..];
    public bool HasReturn => Parameters.Any(p => p.IsReturn);
}

/// <summary>
/// 签名来源。实现放在 App 层（jmap 在 <c>SolsticeWinter.Index</c>，Decompiler 不反向依赖它）。
/// </summary>
public interface IFunctionSignatures
{
    /// <summary>按短名查候选（同名跨类是常态）。</summary>
    IReadOnlyList<FunctionSignature> Find(string name);
}

/// <summary>
/// 把 UAssetAPI 的 <see cref="FProperty"/> 映射成引脚类型。
///
/// <para>
/// 标量在 UAssetAPI 里都是 <see cref="FGenericProperty"/>，真类型名在
/// <c>SerializedType.Value.Value</c>（`"IntProperty"` 等）；容器/对象/结构体/枚举才有专用子类。
/// 分派顺序因此是「先认子类，再退回 SerializedType」——与 <c>SolsticeWinter.Index.TypeFormatter</c> 同一套口径。
/// </para>
/// </summary>
public static class PropertyTypeMapper
{
    public static GraphPinType From(FProperty? property, Func<UAssetAPI.UnrealTypes.FPackageIndex?, string>? resolvePath = null)
    {
        if (property is null) return new GraphPinType(PinType.Unknown);

        return property switch
        {
            FArrayProperty a => new GraphPinType(From(a.Inner, resolvePath).Category,
                Container: PinContainerType.Array,
                SubCategoryObject: From(a.Inner, resolvePath).SubCategoryObject),

            FSetProperty s => new GraphPinType(From(s.ElementProp, resolvePath).Category,
                Container: PinContainerType.Set,
                SubCategoryObject: From(s.ElementProp, resolvePath).SubCategoryObject),

            FMapProperty m => new GraphPinType(From(m.ValueProp, resolvePath).Category,
                Container: PinContainerType.Map,
                SubCategoryObject: From(m.ValueProp, resolvePath).SubCategoryObject),

            FStructProperty st => new GraphPinType(PinType.Struct,
                SubCategoryObject: resolvePath?.Invoke(st.Struct)),

            FEnumProperty en => new GraphPinType(PinType.Enum,
                SubCategoryObject: resolvePath?.Invoke(en.Enum)),

            FObjectProperty o => new GraphPinType(
                property is FClassProperty ? PinType.Class : PinType.Object,
                SubCategoryObject: resolvePath?.Invoke(o.PropertyClass)),

            FInterfaceProperty it => new GraphPinType(PinType.Interface,
                SubCategoryObject: resolvePath?.Invoke(it.InterfaceClass)),

            FDelegateProperty => new GraphPinType(PinType.Delegate),

            _ => Scalar(property),
        };
    }

    private static GraphPinType Scalar(FProperty property)
    {
        var name = property.SerializedType?.Value?.Value ?? string.Empty;
        return new GraphPinType(name switch
        {
            "IntProperty" => PinType.Int,
            "Int64Property" => PinType.Int64,
            "ByteProperty" => PinType.Byte,
            "FloatProperty" => PinType.Float,
            "DoubleProperty" => PinType.Double,
            "BoolProperty" => PinType.Bool,
            "StrProperty" => PinType.String,
            "NameProperty" => PinType.Name,
            "TextProperty" => PinType.Text,
            "StructProperty" => PinType.Struct,
            "ObjectProperty" => PinType.Object,
            _ => PinType.Unknown,
        });
    }

    /// <summary>参数方向：`CPF_ReturnParm` → 返回；`out &amp;&amp; !ref` → 真输出；其余入参。</summary>
    public static (bool IsReturn, bool IsOut) Direction(UAssetAPI.UnrealTypes.EPropertyFlags flags)
    {
        const UAssetAPI.UnrealTypes.EPropertyFlags returnParm = UAssetAPI.UnrealTypes.EPropertyFlags.CPF_ReturnParm;
        const UAssetAPI.UnrealTypes.EPropertyFlags outParm = UAssetAPI.UnrealTypes.EPropertyFlags.CPF_OutParm;
        const UAssetAPI.UnrealTypes.EPropertyFlags refParm = UAssetAPI.UnrealTypes.EPropertyFlags.CPF_ReferenceParm;

        if ((flags & returnParm) != 0) return (true, false);
        var isOut = (flags & outParm) != 0 && (flags & refParm) == 0;
        return (false, isOut);
    }
}
