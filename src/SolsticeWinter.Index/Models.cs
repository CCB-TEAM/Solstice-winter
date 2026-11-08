namespace SolsticeWinter.Index;

/// <summary>函数参数。<see cref="Direction"/> 取 <c>in</c> / <c>out</c> / <c>return</c>。</summary>
public sealed record ParamInfo(string Name, string Type, string Direction);

/// <summary>
/// 一个函数的签名摘要。
/// <para>
/// <see cref="HasBytecode"/> 是「这个函数能不能在画布上打开」的唯一判据；
/// <see cref="Flags"/> 保留原始 <c>EFunctionFlags</c> 文本（判断 Static/BlueprintEvent 时用）。
/// </para>
/// </summary>
public sealed record FunctionInfo(
    string Name,
    IReadOnlyList<ParamInfo> Params,
    string? Return,
    bool HasBytecode,
    int BytecodeSize,
    string Flags,
    int Locals = 0);

/// <summary>一个成员变量。<see cref="Type"/> 是渲染过的可读类型（见 <see cref="TypeFormatter"/>）。</summary>
public sealed record VariableInfo(string Name, string Type, string Flags);

/// <summary>
/// 一个资产的「目录页」：类名、父类、变量、函数签名、引用。
///
/// <para>
/// 这是索引的存储单位，<b>不含属性默认值、不含字节码</b>——那些按需从资产本体读。
/// 目的只有一个：让「按路径 / 按函数名 / 按类」的查询变成字典命中，而不是遍历。
/// </para>
/// </summary>
public sealed class AssetHeader
{
    /// <summary>归一化虚拟路径，形如 <c>/Game/Blueprints/Logic/BP_Logic</c>。</summary>
    public string VirtualPath { get; set; } = string.Empty;

    /// <summary>包名（与 <see cref="VirtualPath"/> 同值，保留字段名以便将来区分对象路径）。</summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>生成类名，如 <c>BP_Logic_C</c>；数据型资产为 null。</summary>
    public string? ClassName { get; set; }

    /// <summary>父类对象路径，如 <c>/Script/Engine.Actor</c>；解析不出时为 null。</summary>
    public string? SuperClass { get; set; }

    public List<VariableInfo> Variables { get; set; } = [];
    public List<FunctionInfo> Functions { get; set; } = [];
    public List<string> References { get; set; } = [];

    public int ExportCount { get; set; }
    public int ImportCount { get; set; }

    public override string ToString() =>
        $"{VirtualPath}  class={ClassName ?? "-"}  super={SuperClass ?? "-"}  " +
        $"vars={Variables.Count}  funcs={Functions.Count}  refs={References.Count}";
}
