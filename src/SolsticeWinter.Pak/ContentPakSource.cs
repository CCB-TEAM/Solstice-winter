using SolsticeWinter.Core;

namespace SolsticeWinter.Pak;

/// <summary>
/// pak 源：直接从 <c>.pak</c> 读 <c>.uasset</c> + <c>.uexp</c>，不依赖 FModel 先导出。
///
/// <para>
/// 这是设计文档里 <c>PakSource（mmap + 路径表）</c> 那一条。KARDS 实测：
/// pak 里有 **12738** 个 <c>kards/Content</c>（= <c>/Game</c>）资产，
/// 而 FModel 导出目录只导出了 **2137** 个——只走目录源等于只看见 17% 的游戏内容。
/// </para>
///
/// <para>
/// 虚拟路径映射：条目 <c>../../../kards/Content/Blueprints/Logic/BP_Logic.uasset</c>
/// 在给定 <c>contentPrefix = "kards/Content"</c> 时映射为
/// <c>/Game/Blueprints/Logic/BP_Logic</c>，与目录源完全一致——索引和最近列表可以互认。
/// </para>
///
/// <para>
/// **查表是 O(1)**：构造时把「虚拟路径 → pak 条目」建成字典。不这么做的话，
/// 每次读都要线性扫 3 万多条目——建索引 12738 个资产会退化成 4 亿次字符串比较。
/// </para>
///
/// <para>
/// **性能**：<see cref="PakArchive.Read(PakFile)"/> 会按块解压整份文件。建索引要把
/// 约 6.8 GB 解压一遍，属于一次性成本；单次打开一个资产是逐文件几十毫秒级。
/// </para>
/// </summary>
public sealed class ContentPakSource : IAssetSource
{
    private readonly PakArchive _archive;
    private readonly string _contentPrefix;
    private readonly string _pakPath;

    /// <summary>虚拟路径 → pak 条目（大小写不敏感，与 <see cref="PakArchive.Files"/> 一致）。</summary>
    private readonly Dictionary<string, PakFile> _byVirtualPath;

    /// <param name="archive">已打开的 pak。**调用方负责生命周期**（本类不 Dispose 它）。</param>
    /// <param name="contentPrefix">
    /// pak 内对应 <c>/Game</c> 的前缀，如 <c>kards/Content</c>；传空串表示不剥前缀。
    /// </param>
    /// <param name="pakPath">仅用于诊断显示（<see cref="Name"/>）。</param>
    public ContentPakSource(PakArchive archive, string contentPrefix, string pakPath)
    {
        _archive = archive;
        _contentPrefix = contentPrefix.Replace('\\', '/').Trim('/');
        _pakPath = pakPath;

        _byVirtualPath = new Dictionary<string, PakFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in archive.Files.Values)
        {
            if (file.Deleted) continue;
            if (!file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

            if (TryNormalize(file.Path, out var virtualPath)) _byVirtualPath[virtualPath] = file;
        }
    }

    public string Name => $"pak:{_pakPath}";

    /// <summary>本源能看到的资产数（诊断用，等价于 <see cref="EnumerateVirtualPaths"/> 的条数）。</summary>
    public int Count => _byVirtualPath.Count;

    public IReadOnlyList<string> EnumerateVirtualPaths()
    {
        var result = new List<string>(_byVirtualPath.Keys);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>pak 内路径 → 虚拟路径；不在 <paramref name="contentPrefix"/> 下返回 false。</summary>
    private bool TryNormalize(string pakPath, out string virtualPath)
    {
        virtualPath = string.Empty;

        var path = pakPath.Replace('\\', '/');
        while (path.StartsWith("../", StringComparison.Ordinal)) path = path[3..];
        path = path.TrimStart('/');

        if (_contentPrefix.Length > 0)
        {
            if (!path.StartsWith(_contentPrefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
            path = path[(_contentPrefix.Length + 1)..];
        }

        if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return false;

        virtualPath = "/Game/" + path[..^".uasset".Length];
        return true;
    }

    public bool TryRead(string virtualPath, out byte[] uasset, out byte[]? uexp)
    {
        uasset = [];
        uexp = null;

        if (!_byVirtualPath.TryGetValue(virtualPath, out var entry)) return false;

        try
        {
            uasset = _archive.Read(entry);
        }
        catch (PakException)
        {
            // 已删除 / 尺寸字段异常：当作「这个源没有」，让 overlay 交给下一个源
            return false;
        }

        // .uexp 可选：纯结构体资产没有导出体。按同名条目找，找不到就是 null。
        var uexpPath = System.IO.Path.ChangeExtension(entry.Path, ".uexp").Replace('\\', '/');
        try
        {
            uexp = _archive.Read(uexpPath);
        }
        catch (PakException)
        {
            uexp = null;
        }

        return true;
    }
}
