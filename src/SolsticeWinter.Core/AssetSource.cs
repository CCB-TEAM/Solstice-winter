using System.IO;

namespace SolsticeWinter.Core;

/// <summary>
/// VFS 的「路径 → 字节」层。设计文档里的三层结构最底下那一层：
/// <c>PakSource（mmap + 路径表） | DirSource（散文件） | Overlay</c>。
///
/// <para>
/// 上层（<c>AssetHeaderReader</c> / <c>SymbolIndex</c> / UI）只认这个接口，
/// 因此「资产从 pak 来」还是「从 FModel 导出目录来」不会渗透到业务代码里。
/// </para>
///
/// <para>
/// **虚拟路径**统一是 <c>/Game/Blah/BP_Foo</c>（不带扩展名）——pak 条目的
/// <c>../../../kards/Content/Blueprints/Logic/BP_Logic.uasset</c> 和导出目录里的
/// <c>Blueprints\Logic\BP_Logic.uasset</c> 都映射到同一个虚拟路径，
/// 这样索引、最近列表、签名查询在两种数据源之间是同一套 key。
/// </para>
/// </summary>
public interface IAssetSource
{
    /// <summary>诊断用名字（`dir:D:\...` / `pak:xxx.pak` / `overlay(a,b)`）。</summary>
    string Name { get; }

    /// <summary>枚举全部虚拟路径（<c>/Game/...</c>，不含扩展名），顺序稳定。</summary>
    IReadOnlyList<string> EnumerateVirtualPaths();

    /// <summary>
    /// 读一个资产的字节。<paramref name="uexp"/> 可以为 null（存在但为空 / 条目本来就没有）。
    /// 找不到返回 false，不抛异常。
    /// </summary>
    bool TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp);
}

/// <summary>
/// 目录源：FModel 导出的散文件，或任何 <c>内容根\...\*.uasset</c> 目录。
///
/// <para>
/// 这是**现有行为**（改造前 UI 只有这一种）。它的问题不是实现，而是覆盖率：
/// KARDS 导出目录只有 2137 个 uasset，而 pak 里有 12738 个 `/Game` 资产。
/// </para>
/// </summary>
public sealed class DirAssetSource(string root) : IAssetSource
{
    private readonly string _root = Path.GetFullPath(root);

    public string Name => $"dir:{_root}";

    public IReadOnlyList<string> EnumerateVirtualPaths()
    {
        if (!Directory.Exists(_root)) return [];

        return Directory.EnumerateFiles(_root, "*.uasset", SearchOption.AllDirectories)
            .Select(file => "/Game/" + Path.GetRelativePath(_root, file)
                                              .Replace('\\', '/')[..^".uasset".Length])
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp)
    {
        uasset = [];
        uexp = null;

        var relative = virtualPath.StartsWith("/Game/", StringComparison.Ordinal)
            ? virtualPath["/Game/".Length..]
            : virtualPath;

        var path = Path.Combine(_root, relative.Replace('/', '\\') + ".uasset");
        if (!File.Exists(path)) return false;

        uasset = File.ReadAllBytes(path);
        var uexpPath = Path.ChangeExtension(path, ".uexp");
        if (File.Exists(uexpPath)) uexp = File.ReadAllBytes(uexpPath);
        return true;
    }
}

/// <summary>
/// 叠加源：按给顺序，**先给的赢**。
///
/// <para>
/// 典型用法是 <c>overlay(dir, pak)</c>——导出目录里的更新文件覆盖 pak 里的旧版本，
/// 这正是 FModel「导出后改、再读回来」的工作流需要的顺序。
/// </para>
///
/// <para>
/// 枚举取并集（去重）：只出现在 pak 里的资产也能被列出来，
/// 这正是补上「导出目录只有 17% 内容」那个缺口的关键。
/// </para>
/// </summary>
public sealed class OverlayAssetSource(params IAssetSource[] sources) : IAssetSource
{
    public string Name => $"overlay({string.Join(",", sources.Select(s => s.Name))})";

    public IReadOnlyList<string> EnumerateVirtualPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var source in sources)
        {
            foreach (var path in source.EnumerateVirtualPaths())
            {
                if (seen.Add(path)) result.Add(path);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public bool TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp)
    {
        foreach (var source in sources)
        {
            if (source.TryRead(virtualPath, out uasset, out uexp)) return true;
        }

        uasset = [];
        uexp = null;
        return false;
    }
}
