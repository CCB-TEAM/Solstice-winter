using SolsticeWinter.Decompiler.Model;
using SolsticeWinter.Decompiler.Signatures;
using SolsticeWinter.Index;
using UAssetAPI.Unversioned;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 用索引 + usmap 解析「变量/结构体字段 → 类型」。
///
/// <para>
/// 两个来源分工：类成员类型来自**索引**（构建索引时已把每个资产的变量表解析好，字典命中）；
/// 结构体字段类型来自 **usmap**（索引里只存了渲染后的字符串，没有结构体定义）。
/// </para>
/// </summary>
internal sealed class IndexVariableTypes(SymbolIndex index, Usmap? usmap = null) : IVariableTypes
{
    private readonly Dictionary<string, AssetHeader?> _classCache = new(StringComparer.OrdinalIgnoreCase);

    public GraphPinType? Find(string? ownerClass, string variableName)
    {
        if (string.IsNullOrEmpty(ownerClass) || string.IsNullOrEmpty(variableName)) return null;

        var header = ResolveHeader(ownerClass);
        var guard = 0;
        while (header is not null && guard++ < 12)
        {
            var variable = header.Variables.FirstOrDefault(v =>
                string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase));
            if (variable is not null) return IndexSignatures.MapRendered(variable.Type);

            header = header.SuperClass is { Length: > 0 } super ? ResolveHeader(super) : null;
        }

        return null;
    }

    /// <summary>结构体字段表来自 usmap（索引里没有结构体定义）。</summary>
    public IReadOnlyList<(string Name, GraphPinType Type)>? FieldsOf(string structName)
    {
        if (string.IsNullOrEmpty(structName)) return null;

        var shortName = structName[(structName.LastIndexOf('.') + 1)..];

        // 1) usmap 的原生结构体
        if (usmap is not null && usmap.Schemas.TryGetValue(shortName, out var schema))
        {
            return schema.Properties.Values
                .OrderBy(p => p.SchemaIndex)
                .Select(p => (p.Name, MapUsmap(p.PropertyData)))
                .ToList();
        }

        // 2) 索引里的蓝图自定义结构体（UserDefinedStruct 资产，字段 = 该资产的变量表）
        var asset = index.Assets.FirstOrDefault(a =>
            string.Equals(a.ClassName, shortName, StringComparison.OrdinalIgnoreCase) && a.Variables.Count > 0);

        return asset?.Variables
            .Select(v => (v.Name, IndexSignatures.MapRendered(v.Type)))
            .ToList();

    }

    private AssetHeader? ResolveHeader(string className)
    {
        if (_classCache.TryGetValue(className, out var cached)) return cached;

        var shortName = className[(className.LastIndexOf('.') + 1)..];
        if (shortName.EndsWith("_C", StringComparison.Ordinal)) shortName = shortName[..^2];

        var header = index.Assets.FirstOrDefault(a =>
                         string.Equals(a.ClassName, shortName, StringComparison.OrdinalIgnoreCase))
                     ?? index.Assets.FirstOrDefault(a =>
                         a.VirtualPath.EndsWith("/" + shortName, StringComparison.OrdinalIgnoreCase))
                     ?? index.Assets.FirstOrDefault(a =>
                         string.Equals(a.ClassName, className, StringComparison.OrdinalIgnoreCase));

        _classCache[className] = header;
        return header;
    }

    /// <summary>usmap 属性类型 → 引脚类型。</summary>
    private static GraphPinType MapUsmap(UsmapPropertyData? data)
    {
        if (data is null) return new GraphPinType(PinType.Unknown);

        switch (data)
        {
            case UsmapArrayData array:
            {
                var inner = MapUsmap(array.InnerType);
                return new GraphPinType(inner.Category,
                    Container: array.Type.ToString() == "SetProperty" ? PinContainerType.Set : PinContainerType.Array,
                    SubCategoryObject: inner.SubCategoryObject);
            }

            case UsmapMapData map:
            {
                var value = MapUsmap(map.ValueType);
                return new GraphPinType(value.Category, Container: PinContainerType.Map,
                    SubCategoryObject: value.SubCategoryObject);
            }

            case UsmapStructData structure:
                return new GraphPinType(PinType.Struct, SubCategoryObject: structure.StructType);

            case UsmapEnumData enumeration:
                return new GraphPinType(PinType.Enum, SubCategoryObject: enumeration.Name);
        }

        return data.Type.ToString() switch
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
            "SoftClassProperty" => new GraphPinType(PinType.SoftClass),
            "InterfaceProperty" => new GraphPinType(PinType.Interface),
            "DelegateProperty" => new GraphPinType(PinType.Delegate),
            "MulticastDelegateProperty" => new GraphPinType(PinType.Delegate),
            _ => new GraphPinType(PinType.Unknown),
        };
    }
}


