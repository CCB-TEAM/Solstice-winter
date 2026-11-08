using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Signatures;
using SolsticeWinter.Index;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 把索引里的 jmap 签名（<c>/Script</c>）适配成 <see cref="IFunctionSignatures"/>。
///
/// <para>
/// 为什么不直接让 Decompiler 依赖 Index：反编译器是「字节码 → 图」，签名只是**可选增强**；
/// 让它依赖索引层会把两者绑死，也让 Decompiler 无法在 CLI/测试里单独用。
/// </para>
/// </summary>
internal sealed class IndexSignatures(SymbolIndex index) : IFunctionSignatures
{
    public IReadOnlyList<FunctionSignature> Find(string name)
    {
        // 1) jmap 的 /Script 签名（原生函数）
        var fromJmap = index.FindScriptFunction(name)
            .Select(function => new FunctionSignature(
                function.Owner,
                function.Name,
                function.Params.Select(parameter => new SignatureParam(
                    parameter.Name,
                    Map(parameter),
                    parameter.Direction == "return",
                    parameter.Direction == "out")).ToList()))
            .ToList();

        if (fromJmap.Count > 0) return fromJmap;

        // 注：试过「`GetCardFunctions` → 变量 `cardFunctions`」的 getter 签名合成，
        // 结果 argN 从 370 涨到 381——因为这类调用在字节码里**带参数**，而合成签名只有 1 个返回脚，
        // 对不上的参数就变成新的 `argN` 占位。取证不足，不做。

        // 2) 索引里的蓝图函数签名（/Game 资产）——这就是「直接读依赖蓝图取签名」：
        //    索引在构建时已经把 2137 个资产的函数参数表解析好了，这里是字典命中，不需要再开资产。
        return index.FindFunction(name)
            .Select(hit =>
            {
                var parameters = hit.Function.Params
                    .Select(p => new SignatureParam(p.Name, MapRendered(p.Type), p.Direction == "return", p.Direction == "out"))
                    .ToList();

                if (hit.Function.Return is { Length: > 0 } returnType && parameters.All(p => !p.IsReturn))
                    parameters.Add(new SignatureParam("ReturnValue", MapRendered(returnType), true, false));

                return new FunctionSignature(
                    hit.Asset.ClassName ?? hit.Asset.PackageName,
                    hit.Function.Name,
                    parameters);
            })
            .ToList();
    }

    /// <summary>
    /// 索引里存的是**渲染过的**类型字符串（`int` / `TArray<int>` / `Object` …），
    /// 这里反推回引脚类型。认不出的落 <see cref="PinType.Unknown"/>——名字仍然可用，只是没有颜色。
    /// </summary>
    internal static GraphPinType MapRendered(string type)
    {
        // 索引里结构体字段存的是原始属性类名（BoolProperty / IntProperty …），先单独映射
        if (MapPropertyClass(type) is { } fromProperty) return fromProperty;

        var text = type.Trim();
        if (text.EndsWith('*')) text = text[..^1].Trim();

        if (text.StartsWith("TArray<", StringComparison.Ordinal) && text.EndsWith('>'))
            return new GraphPinType(MapRendered(text[7..^1]).Category, Container: PinContainerType.Array);

        if (text.StartsWith("TSet<", StringComparison.Ordinal) && text.EndsWith('>'))
            return new GraphPinType(MapRendered(text[5..^1]).Category, Container: PinContainerType.Set);

        if (text.StartsWith("TMap<", StringComparison.Ordinal) && text.EndsWith('>'))
        {
            var inner = text[5..^1];
            var comma = inner.LastIndexOf(',');
            return new GraphPinType(
                MapRendered(comma > 0 ? inner[(comma + 1)..] : inner).Category,
                Container: PinContainerType.Map);
        }

        // 索引里结构体字段存的是原始属性类名（BoolProperty / IntProperty …），一并映射

        return text switch
        {
            "bool" => new GraphPinType(PinType.Bool),
            "int" or "int32" => new GraphPinType(PinType.Int),
            "int64" => new GraphPinType(PinType.Int64),
            "byte" or "uint8" => new GraphPinType(PinType.Byte),
            "float" => new GraphPinType(PinType.Float),
            "double" => new GraphPinType(PinType.Double),
            "string" or "FString" => new GraphPinType(PinType.String),
            "name" or "FName" => new GraphPinType(PinType.Name),
            "text" or "FText" => new GraphPinType(PinType.Text),
            "void" => new GraphPinType(PinType.Unknown),
            _ => new GraphPinType(PinType.Object, SubCategoryObject: text),
        };
    }

    /// <summary>原始属性类名 → 引脚类型（结构体字段、jmap 的 `class` 字段都用得上）。</summary>
    private static GraphPinType? MapPropertyClass(string text) => text switch
    {
        "BoolProperty" => new GraphPinType(PinType.Bool),
        "IntProperty" => new GraphPinType(PinType.Int),
        "Int64Property" => new GraphPinType(PinType.Int64),
        "ByteProperty" => new GraphPinType(PinType.Byte),
        "FloatProperty" => new GraphPinType(PinType.Float),
        "DoubleProperty" => new GraphPinType(PinType.Double),
        "NameProperty" => new GraphPinType(PinType.Name),
        "StrProperty" => new GraphPinType(PinType.String),
        "TextProperty" => new GraphPinType(PinType.Text),
        "ObjectProperty" => new GraphPinType(PinType.Object),
        "SoftObjectProperty" => new GraphPinType(PinType.SoftObject),
        "ClassProperty" => new GraphPinType(PinType.Class),
        "StructProperty" => new GraphPinType(PinType.Struct),
        "EnumProperty" => new GraphPinType(PinType.Enum),
        _ => null,
    };

    /// <summary>jmap 的类型字符串 → 引脚类型（`class` 字段带结构体/类的对象路径）。</summary>
    internal static GraphPinType Map(ScriptParam parameter) => parameter.Type switch
    {
        "ObjectProperty" => new GraphPinType(PinType.Object, SubCategoryObject: parameter.Class),
        "SoftObjectProperty" => new GraphPinType(PinType.SoftObject, SubCategoryObject: parameter.Class),
        "ClassProperty" => new GraphPinType(PinType.Class, SubCategoryObject: parameter.Class),
        "SoftClassProperty" => new GraphPinType(PinType.SoftClass, SubCategoryObject: parameter.Class),
        "StructProperty" => new GraphPinType(PinType.Struct, SubCategoryObject: parameter.Class),
        "EnumProperty" => new GraphPinType(PinType.Enum, SubCategoryObject: parameter.Class),
        "InterfaceProperty" => new GraphPinType(PinType.Interface, SubCategoryObject: parameter.Class),
        "DelegateProperty" or "MulticastDelegateProperty" => new GraphPinType(PinType.Delegate),
        "IntProperty" => new GraphPinType(PinType.Int),
        "Int64Property" => new GraphPinType(PinType.Int64),
        "ByteProperty" => new GraphPinType(PinType.Byte),
        "FloatProperty" => new GraphPinType(PinType.Float),
        "DoubleProperty" => new GraphPinType(PinType.Double),
        "BoolProperty" => new GraphPinType(PinType.Bool),
        "StrProperty" => new GraphPinType(PinType.String),
        "NameProperty" => new GraphPinType(PinType.Name),
        "TextProperty" => new GraphPinType(PinType.Text),
        _ => new GraphPinType(PinType.Unknown),
    };
}

/// <summary>直接用 jmap 目录做签名源（探针/CLI 用，不依赖 SymbolIndex）。</summary>
internal sealed class JmapSignatures(SolsticeWinter.Index.JmapCatalog catalog) : IFunctionSignatures
{
    public IReadOnlyList<FunctionSignature> Find(string name) =>
        catalog.Find(name)
            .Select(function => new FunctionSignature(
                function.Owner,
                function.Name,
                function.Params.Select(parameter => new SignatureParam(
                    parameter.Name,
                    IndexSignatures.Map(parameter),
                    parameter.Direction == "return",
                    parameter.Direction == "out")).ToList()))
            .ToList();

}



