namespace SolsticeWinter.Decompiler.Model;

/// <summary>
/// 引脚类型（对应 UE 的 <c>FEdGraphPinType.PinCategory</c>）。
/// 名字与参考项目 <c>UEBlueprintGraphViewer</c> 保持一致，方便对照移植。
/// </summary>
public enum PinType : byte
{
    Unknown,
    Exec,
    Bool,
    Byte,
    Enum,
    Int,
    Int64,
    Float,
    Double,
    Name,
    String,
    Text,
    Vector,
    Rotator,
    Transform,
    Interface,
    Object,
    SoftObject,
    Class,
    SoftClass,
    Delegate,
    Struct,
    Wildcard,
}

/// <summary>容器类型（对应 <c>EPinContainerType</c>）。</summary>
public enum PinContainerType : byte
{
    None,
    Array,
    Set,
    Map,
}

/// <summary>引脚方向。</summary>
public enum PinDirection
{
    Input,
    Output,
}

/// <summary>
/// 一个引脚的完整类型。
/// <see cref="SubCategoryObject"/> 存结构体/类的对象路径（如 <c>/Script/CoreUObject.Vector</c>）。
/// </summary>
public readonly record struct GraphPinType(
    PinType Category,
    PinType SubCategory = PinType.Unknown,
    string? SubCategoryObject = null,
    PinContainerType Container = PinContainerType.None,
    bool IsReference = false)
{
    public static GraphPinType Of(PinType category) => new(category);

    public static readonly GraphPinType Exec = new(PinType.Exec);

    /// <summary>渲染成 UI 上看到的一行，例如 <c>Array&lt;Object(/Script/Engine.Actor)&gt;</c>。</summary>
    public string Display()
    {
        var inner = SubCategoryObject is { Length: > 0 } path
            ? $"{Category}({path[(path.LastIndexOf('.') + 1)..]})"
            : Category.ToString();

        return Container switch
        {
            PinContainerType.Array => $"Array<{inner}>",
            PinContainerType.Set => $"Set<{inner}>",
            PinContainerType.Map => $"Map<{inner}>",
            _ => inner,
        };
    }
}

/// <summary>变量/属性引用：名字 + 类型（移植时用来替代参考项目的 <c>PropertyData</c>）。</summary>
public sealed record PropertyRef(string Name, GraphPinType PinType)
{
    public override string ToString() => Name;
}
