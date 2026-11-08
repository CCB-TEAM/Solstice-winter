using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace SolsticeWinter.Index;

/// <summary>
/// 从一个资产的 <c>.uasset</c>+<c>.uexp</c> 里抽出「目录页」（<see cref="AssetHeader"/>）。
///
/// <para>
/// 取舍：这里**只读结构，不读属性默认值、不反编译字节码**。属性默认值会让解析成本翻几倍，
/// 而索引的用途是「查签名 / 查成员 / 查引用」，用不到默认值。需要默认值时按需再开一次资产。
/// </para>
/// </summary>
public sealed class AssetHeaderReader(Usmap usmap)
{
    public AssetHeader Read(string assetPath, string virtualPath)
    {
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, usmap);

        string Resolve(FPackageIndex? index) => ResolveIndex(asset, index);

        var classExport = asset.GetClassExport() ?? ReparseClass(asset, assetPath, Resolve);
        var header = new AssetHeader
        {
            VirtualPath = virtualPath,
            PackageName = virtualPath,
            ClassName = classExport?.ObjectName?.ToString(),
            SuperClass = classExport is null ? null : Resolve(classExport.SuperStruct),
            ExportCount = asset.Exports.Count,
            ImportCount = asset.Imports.Count,
        };

        // 结构体资产（UserDefinedStruct / StructExport）：GetClassExport() 返回 null，
        // 字段表就在这个导出自己的 LoadedProperties 里。不处理的话结构体资产在索引里 vars=0。
        if (classExport is null)
        {
            var structExport = asset.Exports.OfType<UserDefinedStructExport>().FirstOrDefault()
                               ?? asset.Exports.OfType<StructExport>().FirstOrDefault();

            if (structExport?.LoadedProperties is { Length: > 0 } structFields)
            {
                header.ClassName = structExport.ObjectName?.ToString();
                foreach (var p in structFields)
                {
                    header.Variables.Add(new VariableInfo(
                        StripGuidSuffix(p.Name?.ToString() ?? "?"),
                        TypeFormatter.Of(p, Resolve),
                        p.PropertyFlags.ToString()));
                }
            }
        }

        if (classExport?.LoadedProperties is { Length: > 0 } properties)
        {
            foreach (var p in properties)
            {
                header.Variables.Add(new VariableInfo(
                    p.Name?.ToString() ?? "?",
                    TypeFormatter.Of(p, Resolve),
                    p.PropertyFlags.ToString()));
            }
        }

        foreach (var fn in asset.Exports.OfType<FunctionExport>())
        {
            var parameters = new List<ParamInfo>();
            var locals = 0;
            string? returnType = null;

            // 关键：函数的属性表里同时装着**参数**和**局部变量**（CallFunc_*、K2Node_*、Temp_* 都是局部变量）。
            // 判据是 CPF_Parm——只有带它的才是参数，否则算局部变量。不按这个过滤会把几十个临时变量当成参数。
            foreach (var p in fn.LoadedProperties ?? [])
            {
                if ((p.PropertyFlags & EPropertyFlags.CPF_Parm) == 0)
                {
                    locals++;
                    continue;
                }

                var direction = TypeFormatter.Direction(p.PropertyFlags);
                var type = TypeFormatter.Of(p, Resolve);
                parameters.Add(new ParamInfo(p.Name?.ToString() ?? "?", type, direction));
                if (direction == "return") returnType = type;
            }

            header.Functions.Add(new FunctionInfo(
                fn.ObjectName?.ToString() ?? "?",
                parameters,
                returnType,
                fn.ScriptBytecode is { Length: > 0 },
                fn.ScriptBytecodeSize,
                fn.FunctionFlags.ToString(),
                locals));
        }

        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var path = Resolve(new FPackageIndex(-(i + 1)));
            if (path != "None") header.References.Add(path);
        }

        header.References = header.References.Distinct(StringComparer.Ordinal).ToList();
        return header;
    }

    /// <summary>
    /// 兜底：某些资产（实测 `BP_Logic`，199 个导出）的生成类导出在正常加载时会**静默降级成 `RawExport`**，
    /// 于是 <c>GetClassExport()</c> 返回 null，变量与父类全部丢失。
    ///
    /// <para>
    /// 这里手动再读一次：<c>ConvertToChildExport</c>（必须用它，它会把 <c>Asset</c> 一起带过去，
    /// 用 <c>new ClassExport(export)</c> 会留下 null 的 Asset 并在读的时候 NRE）
    /// + 用 <c>SerialOffset + SerialSize</c> 作为边界（不用下一个导出的偏移）。
    /// 实测这样能读出来（`BP_Logic`：133 个属性 / 181 个 FuncMap 项，与 181 个函数吻合）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 结构体字段名在 cooked 资产里带 UE 生成的 `_&lt;序号&gt;_&lt;32位GUID&gt;` 后缀
    /// （`isSalvaged_1_D833...`），显示时要去掉。
    /// </summary>
    private static string StripGuidSuffix(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"_\d+_[0-9A-F]{32}$");
        return match.Success ? name[..match.Index] : name;
    }

    private static ClassExport? ReparseClass(UAsset asset, string assetPath, Func<FPackageIndex?, string> resolve)
    {
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            if (!resolve(export.ClassIndex).EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal)) continue;

            try
            {
                var reader = asset.PathToReader(assetPath);
                var parsed = export.ConvertToChildExport<ClassExport>();
                reader.BaseStream.Seek(export.SerialOffset, SeekOrigin.Begin);
                parsed.Read(reader, (int)(export.SerialOffset + export.SerialSize));
                return parsed;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 包索引 → 对象路径。
    /// <b>正数是本包导出，负数是导入</b>（1 基）。导入要沿 Outer 链拼成
    /// <c>包路径.外层.对象</c>，否则所有导入都会退化成同名短名，跨类同名函数就没法区分了。
    /// </summary>
    private static string ResolveIndex(UAsset asset, FPackageIndex? index)
    {
        if (index is null || index.Index == 0) return "None";

        if (index.Index > 0)
        {
            var exportIndex = index.Index - 1;
            return exportIndex < asset.Exports.Count
                ? asset.Exports[exportIndex].ObjectName?.ToString() ?? $"<export {index.Index}>"
                : $"<export {index.Index}>";
        }

        var importIndex = -index.Index - 1;
        if (importIndex >= asset.Imports.Count) return $"<import {index.Index}>";

        var parts = new List<string>();
        var current = asset.Imports[importIndex];
        for (var guard = 0; guard < 16; guard++)
        {
            parts.Insert(0, current.ObjectName?.ToString() ?? "?");

            var outer = current.OuterIndex;
            if (outer is null || outer.Index == 0) break;

            if (outer.Index < 0)
            {
                var next = -outer.Index - 1;
                if (next >= asset.Imports.Count) break;
                current = asset.Imports[next];
                continue;
            }

            var exportOuter = outer.Index - 1;
            if (exportOuter < asset.Exports.Count)
                parts.Insert(0, asset.Exports[exportOuter].ObjectName?.ToString() ?? "?");
            break;
        }

        return string.Join(".", parts);
    }
}

