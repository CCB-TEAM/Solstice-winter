using SolsticeWinter.Decompiler.Model;

namespace SolsticeWinter.Decompiler.Signatures;

/// <summary>
/// 变量类型来源。反编译器本身不读资产，只把「类名 + 变量名」交给实现方查。
///
/// <para>
/// 为什么需要：`EX_InstanceVariable` / `EX_StructMemberContext` 的读写在字节码里**不带类型**，
/// 只带名字。不查表的话引脚类型全是 <c>Unknown</c>（灰色），而索引里其实存了每个资产的变量表。
/// </para>
/// </summary>
public interface IVariableTypes
{
    /// <summary>按「归属类名 + 变量名」查类型；查不到返回 null。</summary>
    GraphPinType? Find(string? ownerClass, string variableName);

    /// <summary>
    /// 结构体字段表（字段名 + 类型）。用于 <c>MakeStruct</c> 的字段引脚——
    /// 字节码里的 <c>EX_StructConst</c> 只带结构体名和值，字段名/类型要靠 usmap。
    /// </summary>
    IReadOnlyList<(string Name, GraphPinType Type)>? FieldsOf(string structName);
}

