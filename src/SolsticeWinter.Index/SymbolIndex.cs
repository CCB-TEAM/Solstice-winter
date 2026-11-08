using System.Text.Json;
using System.Text.Json.Serialization;
using SolsticeWinter.Core;
using UAssetAPI.Unversioned;

namespace SolsticeWinter.Index;

/// <summary>
/// 跨资产符号索引：路径 → 资产目录页，函数短名 → 候选，类 → 成员，资产 → 引用者。
///
/// <para>
/// <b>构建是 O(n)，查询是 O(1)</b>：一次扫盘建表，之后全是字典命中。
/// 表本身落成 JSON（可再生的缓存，不是契约），加载后重建字典。
/// </para>
///
/// <para>
/// 取舍：先用 JSON 而不是自定义二进制。理由：结构还在变，二进制格式会让每次改字段都要写迁移；
/// 缓存可以随时删掉重建，不需要版本兼容。等实测加载时间不可接受（&gt;1 s）再换二进制。
/// </para>
/// </summary>
public sealed class SymbolIndex
{
    public const int FormatVersion = 1;

    public int Version { get; set; } = FormatVersion;
    public string? BuiltFrom { get; set; }
    public DateTimeOffset BuiltAt { get; set; }
    public List<AssetHeader> Assets { get; set; } = [];

    /// <summary>`/Script` 侧的函数签名（jmap 产物）。`/Game` 侧不在这里——那由资产本体提供。</summary>
    public List<ScriptFunction> ScriptFunctions { get; set; } = [];

    [JsonIgnore] public Dictionary<string, AssetHeader> ByPath { get; } = new(StringComparer.Ordinal);
    [JsonIgnore] public Dictionary<string, List<FunctionHit>> FunctionsByName { get; } = new(StringComparer.Ordinal);
    [JsonIgnore] public Dictionary<string, List<AssetHeader>> ByClass { get; } = new(StringComparer.Ordinal);
    [JsonIgnore] public Dictionary<string, List<AssetHeader>> Referencers { get; } = new(StringComparer.Ordinal);
    [JsonIgnore] public Dictionary<string, List<ScriptFunction>> ScriptByName { get; } = new(StringComparer.Ordinal);

    public readonly record struct FunctionHit(AssetHeader Asset, FunctionInfo Function);

    /// <summary>
    /// 扫一个目录建索引。<paramref name="root"/> 是内容根（对应 <c>/Game/</c>）。
    /// 单个资产解析失败只记录、不中断——一个坏资产不该让整个索引不可用。
    /// </summary>
    public static (SymbolIndex Index, List<string> Failures) Build(
        string root,
        string usmapPath,
        int limit = int.MaxValue,
        Action<string>? log = null,
        string? jmapPath = null)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"内容根不存在：{root}");
        if (!File.Exists(usmapPath)) throw new FileNotFoundException($"usmap 不存在：{usmapPath}");

        // UAssetAPI 用 File.Open(ReadWrite) 打开 .uexp 与 usmap：
        // **先假设目录可写、就地解析**；只有探测失败才把整棵树复制到临时目录。
        var files = Directory.EnumerateFiles(root, "*.uasset", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        var writable = IsWritable(root, files) && IsWritableFile(usmapPath);
        string? work = writable ? null : PickWorkRoot();
        if (work is not null) Directory.CreateDirectory(work);
        if (work is not null) Directory.CreateDirectory(work);

        log?.Invoke(writable
            ? "  数据源可写：就地解析（不复制）"
            : $"  数据源只读：复制到临时目录 {work}");

        try
        {
            var workUsmap = usmapPath;
            if (work is not null)
            {
                workUsmap = Path.Combine(work, "game.usmap");
                File.Copy(usmapPath, workUsmap, overwrite: true);
            }

            var workAssets = new List<(string WorkPath, string Relative)>(files.Count);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file);
                if (work is null)
                {
                    workAssets.Add((file, relative.Replace('\\', '/')));
                    continue;
                }

                var target = Path.Combine(work, "assets", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                var uexp = Path.ChangeExtension(file, ".uexp");
                if (File.Exists(uexp)) File.Copy(uexp, Path.ChangeExtension(target, ".uexp"), overwrite: true);
                workAssets.Add((target, relative.Replace('\\', '/')));
            }

            var usmap = new Usmap(workUsmap);
            var reader = new AssetHeaderReader(usmap);
            var index = new SymbolIndex
            {
                BuiltFrom = Path.GetFullPath(root),
                BuiltAt = DateTimeOffset.Now,
            };
            var failures = new List<string>();

            var done = 0;
            foreach (var (workPath, relative) in workAssets)
            {
                var virtualPath = "/Game/" + relative[..^".uasset".Length];

                try
                {
                    index.Assets.Add(reader.Read(workPath, virtualPath));
                }
                catch (Exception e)
                {
                    failures.Add($"{relative}: {e.GetType().Name}: {e.Message}");
                }

                done++;
                if (done % 250 == 0) log?.Invoke($"  已扫描 {done}/{workAssets.Count}");
            }

            if (!string.IsNullOrWhiteSpace(jmapPath) && File.Exists(jmapPath))
            {
                log?.Invoke($"  并入 jmap：{jmapPath}");
                index.ScriptFunctions = JmapCatalog.Load(jmapPath).Functions.ToList();
            }

            index.RebuildLookups();
            return (index, failures);
        }
        finally
        {
            // 只有复制过才清理；就地解析时 work 为 null
            if (work is not null)
            {
                try { Directory.Delete(work, recursive: true); } catch { /* 删不掉不影响结果 */ }
            }
        }
    }

    /// <summary>
    /// 探测内容根是否可写：拿第一个资产对试一次 <c>FileAccess.ReadWrite</c>。
    /// 只开一次句柄，不写任何字节——只读目录会在这里抛 <see cref="UnauthorizedAccessException"/>。
    /// </summary>
    /// <summary>
    /// 选一个能写的临时目录。优先仓库内的 <c>.cache</c>——这台机器的 <c>%TEMP%</c>
    /// 存在 ACL 问题（写入被拒），而沙箱下每个进程的 %TEMP% 还各不相同。
    /// </summary>
    private static string PickWorkRoot()
    {
        var suffix = "solstice-winter-index-" + Guid.NewGuid().ToString("N")[..8];

        var local = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "index-work", suffix);
        try
        {
            Directory.CreateDirectory(local);
            return local;
        }
        catch
        {
            // 落回系统临时目录
            var temp = Path.Combine(Path.GetTempPath(), suffix);
            Directory.CreateDirectory(temp);
            return temp;
        }
    }

    /// <summary>
    /// 从**任意 VFS 源**构建索引（pak / 目录 / 叠加）。这是设计文档里那条正确路径：
    /// 上层只认 <see cref="IAssetSource"/>，所以「资产从 pak 来」不会渗进索引逻辑。
    ///
    /// <para>
    /// 与 <see cref="Build"/> 的区别：不再要求内容根是目录。每个资产**逐个**取字节、落到
    /// 工作目录、解析完立刻覆盖——磁盘峰值只有一个资产，而不是整棵树。
    /// </para>
    ///
    /// <para>
    /// 代价：pak 是压缩存储的，取字节必须整份解压（Oodle 按块解压，拿不到「只解前 64 KB」）。
    /// KARDS 实测 <c>kards/Content</c> 有 12738 个 uasset、解压后约 6.8 GB，
    /// 这个成本是**一次性**的；建完之后查询全走字典。
    /// </para>
    /// </summary>
    public static (SymbolIndex Index, List<string> Failures) BuildFromSource(
        IAssetSource source,
        string usmapPath,
        int limit = int.MaxValue,
        Action<string>? log = null,
        string? jmapPath = null)
    {
        if (!File.Exists(usmapPath)) throw new FileNotFoundException($"usmap 不存在：{usmapPath}");

        var paths = source.EnumerateVirtualPaths();
        if (limit < paths.Count) paths = paths.Take(limit).ToList();

        log?.Invoke($"  数据源：{source.Name}");
        log?.Invoke($"  资产数：{paths.Count}");

        var work = PickWorkRoot();
        // UAssetAPI 用 File.Open(ReadWrite) 打开 .uexp 与 usmap，所以必须落在可写目录
        var workUsmap = Path.Combine(work, "game.usmap");
        File.Copy(usmapPath, workUsmap, overwrite: true);

        var usmap = new Usmap(workUsmap);
        var reader = new AssetHeaderReader(usmap);
        var index = new SymbolIndex
        {
            BuiltFrom = source.Name,
            BuiltAt = DateTimeOffset.Now,
        };
        var failures = new List<string>();

        // 所有资产复用同一个文件名：解析完立刻覆盖，磁盘峰值 = 单个资产
        var assetFile = Path.Combine(work, "one.uasset");
        var uexpFile = Path.Combine(work, "one.uexp");

        try
        {
            var done = 0;
            foreach (var virtualPath in paths)
            {
                done++;

                try
                {
                    if (!source.TryRead(virtualPath, out var uasset, out var uexp))
                    {
                        failures.Add($"{virtualPath}: 源里读不到字节");
                        continue;
                    }

                    File.WriteAllBytes(assetFile, uasset);
                    if (uexp is { Length: > 0 }) File.WriteAllBytes(uexpFile, uexp);
                    else if (File.Exists(uexpFile)) File.Delete(uexpFile);

                    index.Assets.Add(reader.Read(assetFile, virtualPath));
                }
                catch (Exception e)
                {
                    failures.Add($"{virtualPath}: {e.GetType().Name}: {e.Message}");
                }

                if (done % 500 == 0) log?.Invoke($"  已扫描 {done}/{paths.Count}（失败 {failures.Count}）");
            }

            if (!string.IsNullOrWhiteSpace(jmapPath) && File.Exists(jmapPath))
            {
                log?.Invoke($"  并入 jmap：{jmapPath}");
                index.ScriptFunctions = JmapCatalog.Load(jmapPath).Functions.ToList();
            }

            index.RebuildLookups();
            return (index, failures);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 删不掉不影响结果 */ }
        }
    }

    private static bool IsWritable(string root, IReadOnlyList<string> files)
    {
        var probe = files.FirstOrDefault(f => File.Exists(Path.ChangeExtension(f, ".uexp")))
                    ?? files.FirstOrDefault();
        return probe is not null && IsWritableFile(probe);
    }

    private static bool IsWritableFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>把内存表重建为查询字典。加载后必须调一次（<see cref="Load"/> 已代调）。</summary>
    public void RebuildLookups()
    {
        ByPath.Clear();
        FunctionsByName.Clear();
        ByClass.Clear();
        Referencers.Clear();
        ScriptByName.Clear();

        foreach (var fn in ScriptFunctions)
        {
            if (!ScriptByName.TryGetValue(fn.Name, out var list))
            {
                list = [];
                ScriptByName[fn.Name] = list;
            }

            list.Add(fn);
        }

        foreach (var asset in Assets)
        {
            ByPath[asset.VirtualPath] = asset;

            if (!string.IsNullOrEmpty(asset.ClassName))
            {
                if (!ByClass.TryGetValue(asset.ClassName, out var sameClass))
                {
                    sameClass = [];
                    ByClass[asset.ClassName] = sameClass;
                }

                sameClass.Add(asset);
            }

            foreach (var fn in asset.Functions)
            {
                if (!FunctionsByName.TryGetValue(fn.Name, out var list))
                {
                    list = [];
                    FunctionsByName[fn.Name] = list;
                }

                list.Add(new FunctionHit(asset, fn));
            }

            foreach (var reference in asset.References)
            {
                if (!Referencers.TryGetValue(reference, out var list))
                {
                    list = [];
                    Referencers[reference] = list;
                }

                list.Add(asset);
            }
        }
    }

    public AssetHeader? Find(string virtualPath) =>
        ByPath.TryGetValue(virtualPath, out var asset) ? asset : null;

    /// <summary>按短名查 `/Script` 签名（jmap）。与 <see cref="FindFunction"/> 分开：这里没有资产。</summary>
    public IReadOnlyList<ScriptFunction> FindScriptFunction(string name) =>
        ScriptByName.TryGetValue(name, out var hits) ? hits : [];

    /// <summary>按函数短名精确查候选（同名跨类是常态，所以返回列表）。</summary>
    public IReadOnlyList<FunctionHit> FindFunction(string name) =>
        FunctionsByName.TryGetValue(name, out var hits) ? hits : [];

    /// <summary>按子串搜函数名（候选面板用；线性扫，量级 4 万条，够快）。</summary>
    public IEnumerable<FunctionHit> SearchFunctions(string substring, int limit = 50) =>
        FunctionsByName
            .Where(kv => kv.Key.Contains(substring, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key.Length)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .SelectMany(kv => kv.Value);

    /// <summary>谁引用了这个资产/对象路径。</summary>
    public IReadOnlyList<AssetHeader> FindReferencers(string objectPath) =>
        Referencers.TryGetValue(objectPath, out var list) ? list : [];

    public string Describe()
    {
        var withBytecode = Assets.Sum(a => a.Functions.Count(f => f.HasBytecode));
        var functions = Assets.Sum(a => a.Functions.Count);
        var variables = Assets.Sum(a => a.Variables.Count);
        var refs = Assets.Sum(a => a.References.Count);
        return string.Join(Environment.NewLine,
            $"资产      : {Assets.Count}",
            $"函数      : {functions}（其中带字节码 {withBytecode}）",
            $"变量      : {variables}",
            $"引用边    : {refs}",
            $"唯一函数名: {FunctionsByName.Count}",
            $"唯一被引对象: {Referencers.Count}",
            $"脚本函数  : {ScriptFunctions.Count}（唯一短名 {ScriptByName.Count}）",
            $"构建时间  : {BuiltAt:yyyy-MM-dd HH:mm:ss}  来源 {BuiltFrom}");
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static SymbolIndex Load(string path)
    {
        var index = JsonSerializer.Deserialize<SymbolIndex>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidOperationException($"索引文件解析失败：{path}");
        if (index.Version != FormatVersion)
            throw new InvalidOperationException($"索引版本 {index.Version} 与当前 {FormatVersion} 不符，请重建");
        index.RebuildLookups();
        return index;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}



