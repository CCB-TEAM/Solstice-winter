using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace SolsticeWinter.Index;

/// <summary>
/// 诊断用：把资产的导出表原样打出来（类型名 + 对象名 + 类名）。
///
/// <para>
/// 存在理由：UAssetAPI 只有在「导出类名」匹配已知后缀时才把导出转成专用子类
/// （`ClassExport` / `StructExport` / `FunctionExport`…），
/// 转不出来时 <c>OfType&lt;ClassExport&gt;()</c> 就是空。这个命令用来确认「到底转成了什么」，
/// 而不是靠猜。
/// </para>
/// </summary>
public static class AssetProbe
{
    /// <summary>
    /// 把资产对（uasset+uexp）与 usmap 复制到可写临时目录。
    /// UAssetAPI 用 <c>File.Open(ReadWrite)</c> 打开 <c>.uexp</c> 与 usmap，只读目录会直接抛。
    /// </summary>
    public static (string Asset, string Usmap, string WorkDir) Prepare(string assetPath, string usmapPath)
    {
        // 先假设源目录可写：能就地打开就不复制；否则才把资产对与 usmap 复制到临时目录。
        if (IsWritable(assetPath) && IsWritable(usmapPath))
            return (assetPath, usmapPath, string.Empty);

        var work = Path.Combine(Path.GetTempPath(), "solstice-winter-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        var asset = Path.Combine(work, Path.GetFileName(assetPath));
        File.Copy(assetPath, asset, overwrite: true);
        var uexp = Path.ChangeExtension(assetPath, ".uexp");
        if (File.Exists(uexp)) File.Copy(uexp, Path.ChangeExtension(asset, ".uexp"), overwrite: true);

        var usmap = Path.Combine(work, "game.usmap");
        File.Copy(usmapPath, usmap, overwrite: true);
        return (asset, usmap, work);
    }

    private static bool IsWritable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Exports(string assetPath, Usmap usmap)
    {
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, usmap);
        var lines = new List<string>
        {
            $"包名 {asset.FolderName}  导出 {asset.Exports.Count}  导入 {asset.Imports.Count}"
        };

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            lines.Add($"[{i + 1,3}] {export.GetType().Name,-22} {export.ObjectName,-42} " +
                      $"class={ClassNameOf(asset, export.ClassIndex),-28} size={export.SerialSize}");
        }

        return lines;
    }

    /// <summary>
    /// 手动按 <see cref="UAssetAPI.ExportTypes.ClassExport"/> 读一次导出体。
    ///
    /// <para>
    /// 不能用 <c>asset.ParseExport</c>：它内部有 try/catch，异常会被吞掉并降级成 <c>RawExport</c>，
    /// 外面永远看不到原因。这里用 <c>ConvertToChildExport</c> 转成 <c>ClassExport</c> 再 <c>Read</c>，异常原样返回。
    /// </para>
    /// </summary>
    public static string? TryReparse(string assetPath, Usmap usmap, int exportIndex)
    {
        var asset = new UAsset(assetPath, EngineVersion.VER_UE5_6, usmap);
        if (exportIndex < 1 || exportIndex > asset.Exports.Count) return "导出下标越界";

        try
        {
            var reader = asset.PathToReader(assetPath);
            var i = exportIndex - 1;
            var export = asset.Exports[i];
            var nextStarting = (int)(export.SerialOffset + export.SerialSize);

            reader.BaseStream.Seek(export.SerialOffset, SeekOrigin.Begin);
            var parsed = export.ConvertToChildExport<UAssetAPI.ExportTypes.ClassExport>();
            parsed.Read(reader, nextStarting);

            var super = parsed.SuperStruct is null ? "?" : $"index {parsed.SuperStruct.Index}";
            return $"成功 → ClassExport  super={super}  属性={parsed.LoadedProperties?.Length ?? 0}  " +
                   $"FuncMap={parsed.FuncMap?.Count ?? 0}";
        }
        catch (Exception e)
        {
            var failed = asset.Exports[exportIndex - 1];
            return $"失败 → {e.GetType().Name}: {e.Message}\n" +
                   $"  ClassIndex={failed.ClassIndex?.Index} → {DescribeIndex(asset, failed.ClassIndex)}\n" +
                   $"  Asset 是否为空: {(failed.Asset is null ? "是" : "否")}\n" +
                   $"  导入表条数: {asset.Imports.Count}\n" +
                   StepByStep(asset, failed.ClassIndex) +
                   SuperStructDiag(asset) +
                   e.StackTrace;
        }
    }

    /// <summary>
    /// 扫导入表里 <c>OuterIndex</c> 为 null 的条目。
    /// UAssetAPI 的 <c>GetClassTypeForAncestry</c> 直接对 <c>imp.OuterIndex</c> 调 <c>IsImport()</c>，
    /// OuterIndex 为 null 时会 NRE——这是库的隐患，不是我们读错了。
    /// </summary>
    private static string SuperStructDiag(UAsset asset)
    {
        var nulls = new List<string>();
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var imp = asset.Imports[i];
            if (imp.OuterIndex is null) nulls.Add($"#{i + 1} idx={-(i + 1)} '{imp.ObjectName}' class='{imp.ClassName}'");
        }

        return nulls.Count == 0
            ? "  导入表里没有 OuterIndex=null 的条目\n"
            : $"  导入表里 OuterIndex=null 的条目 {nulls.Count} 个（NRE 源头）：{string.Join("; ", nulls.Take(3))}\n";
    }

    /// <summary>复现 <c>GetClassTypeForAncestry</c> 的每一步，指出到底是哪一步为空。</summary>
    private static string StepByStep(UAsset asset, FPackageIndex? classIndex)
    {
        if (classIndex is null) return "  classIndex 为 null\n";
        var idx = classIndex.Index;
        if (idx >= 0) return $"  classIndex={idx} 不是导入\n";

        var importIndex = -idx - 1;
        if (importIndex >= asset.Imports.Count) return $"  importIndex={importIndex} 越界（共 {asset.Imports.Count}）\n";

        var imp = asset.Imports[importIndex];
        var outer = imp.OuterIndex;
        if (outer is null) return $"  import.OuterIndex 为 null\n";
        if (outer.Index >= 0) return $"  import.OuterIndex={outer.Index} 不是导入\n";

        var outerIndex = -outer.Index - 1;
        if (outerIndex >= asset.Imports.Count) return $"  outerIndex={outerIndex} 越界\n";

        var outerImp = asset.Imports[outerIndex];
        return $"  outer import='{outerImp.ObjectName}' class='{outerImp.ClassName}' " +
               $"ObjectName 为 null: {(outerImp.ObjectName is null ? "是" : "否")}\n";
    }

    private static string DescribeIndex(UAsset asset, FPackageIndex? index)
    {
        if (index is null) return "<null>";
        if (index.Index == 0) return "<0>";

        if (index.Index > 0)
        {
            var i = index.Index - 1;
            return i < asset.Exports.Count ? $"export '{asset.Exports[i].ObjectName}'" : "<导出越界>";
        }

        var j = -index.Index - 1;
        if (j >= asset.Imports.Count) return "<导入越界>";
        var imp = asset.Imports[j];
        var outer = imp.OuterIndex is null ? "null" : imp.OuterIndex.Index.ToString();
        return $"import '{imp.ObjectName}' class='{imp.ClassName}' outer={outer}";
    }

    private static string ClassNameOf(UAsset asset, FPackageIndex? classIndex)
    {
        if (classIndex is null || classIndex.Index == 0) return "None";
        if (classIndex.Index > 0)
        {
            var idx = classIndex.Index - 1;
            return idx < asset.Exports.Count ? asset.Exports[idx].ObjectName?.ToString() ?? "?" : "?";
        }

        var importIndex = -classIndex.Index - 1;
        return importIndex < asset.Imports.Count ? asset.Imports[importIndex].ObjectName?.ToString() ?? "?" : "?";
    }
}






