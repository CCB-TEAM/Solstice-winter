using System.IO;
using SolsticeWinter.Core;
using Solstice.Kismet;

namespace SolsticeWinter.App.Graph;

/// <summary>
/// 资产打开的唯一入口：虚拟路径 → 磁盘文件 → <see cref="KismetWorkspace"/>。
///
/// <para>
/// 取舍：不在这里做 pak 直读。索引与 pak 读取已经在各自模块里验证过，
/// UI 先吃 FModel 导出目录（写起来最简单，也方便你直接改文件看效果）；
/// 等 VFS 层落地后只换这一个类。
/// </para>
///
/// <para>
/// <b>工作目录按进程隔离</b>：<see cref="KismetWorkspace.Open"/> 会把字节写到磁盘再按路径构造资产，
/// 两个实例共用一个目录时，后开的那个写 <c>a.uasset</c> 会撞上文件锁，
/// 表现为 <c>Access to the path ... is denied</c>。所以路径里带 PID，
/// 且第一个位置写不进去时自动退到 <c>%LOCALAPPDATA%</c>。
/// </para>
/// </summary>
public static class AssetAccess
{
    /// <summary>
    /// 工作根候选，按顺序尝试。
    ///
    /// <para>
    /// <b>首选仓库内的 <c>.cache\work</c></b>：实测 <c>%TEMP%</c> 与 <c>%LOCALAPPDATA%</c> 下的
    /// <c>solstice-winter-ui</c> 目录会被沙箱/安全软件打上拒绝 ACE，直接报
    /// <c>Access to the path ... is denied</c>（用户看到的「文件夹被占用」就是它）。
    /// 仓库目录是确定可写的，也方便清理。
    /// </para>
    /// </summary>
    private static readonly string[] WorkRoots = BuildWorkRoots();

    private static string[] BuildWorkRoots()
    {
        var roots = new List<string>();
        if (FindRepoRoot() is { } repo)
            roots.Add(Path.Combine(repo, ".cache", "work", $"p{Environment.ProcessId}"));

        roots.Add(Path.Combine(Path.GetTempPath(), "solstice-winter-ui", $"p{Environment.ProcessId}"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "solstice-winter-ui", $"p{Environment.ProcessId}"));
        return [.. roots];
    }

    /// <summary>从程序目录向上找 <c>Solstice-winter.slnx</c>，找不到返回 null。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Solstice-winter.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>诊断用：当前进程解析出的工作根候选。</summary>
    public static string DescribeWorkRoots() => string.Join(" | ", WorkRoots);

    public static string ExportPathFor(GameProfile profile, string virtualPath)
    {
        var root = profile.ExportDirectory
                   ?? throw new InvalidOperationException("profile 里没有导出目录");
        var relative = virtualPath.StartsWith("/Game/", StringComparison.Ordinal)
            ? virtualPath["/Game/".Length..]
            : virtualPath;
        return Path.Combine(root, relative.Replace('/', '\\') + ".uasset");
    }

    /// <summary>
    /// 清掉已经退出的进程留下的工作目录（<c>p&lt;pid&gt;</c>）。
    ///
    /// <para>
    /// <b>注意枚举的是工作根的父目录</b>：<see cref="WorkRoots"/> 本身就是 <c>...\solstice-winter-ui\p&lt;pid&gt;</c>，
    /// 之前写成在它<b>里面</b>找 <c>p*</c>，所以一个都找不到、清理从来没生效过（实测堆了 43 个目录）。
    /// </para>
    /// </summary>
    public static void CleanStaleWorkDirs()
    {
        var parents = WorkRoots
            .Select(Path.GetDirectoryName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var parent in parents)
        {
            if (!Directory.Exists(parent)) continue;

            foreach (var dir in Directory.GetDirectories(parent, "p*"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 2 || !int.TryParse(name[1..], out var pid)) continue;
                if (pid == Environment.ProcessId) continue;

                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    // PID 可能被别的进程复用：只有「同名进程」才算本工具的活实例
                    if (process.ProcessName.Equals("SolsticeWinter.App", StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch (ArgumentException)
                {
                    // PID 不存在 → 死实例，往下删
                }

                try { Directory.Delete(dir, recursive: true); } catch { /* 删不掉就算了 */ }
            }
        }
    }

    /// <summary>反射拿 <c>KismetWorkspace._asset</c>（库没暴露 UAsset）。拿不到返回 null。</summary>
    public static UAssetAPI.UAsset? GetAsset(KismetWorkspace workspace) =>
        typeof(KismetWorkspace)
            .GetField("_asset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(workspace) as UAssetAPI.UAsset;

    /// <summary>按名字找函数导出。</summary>
    public static UAssetAPI.ExportTypes.FunctionExport? FindFunction(UAssetAPI.UAsset asset, string name) =>
        asset.Exports.OfType<UAssetAPI.ExportTypes.FunctionExport>()
            .FirstOrDefault(e => e.ObjectName?.ToString() == name);

    public static KismetWorkspace OpenWorkspace(GameProfile profile, string virtualPath)
    {
        var uassetPath = ExportPathFor(profile, virtualPath);
        if (!File.Exists(uassetPath)) throw new FileNotFoundException($"资产不存在：{uassetPath}");

        var usmapPath = profile.UsmapPath ?? throw new InvalidOperationException("profile 里没有 usmap");
        var uasset = File.ReadAllBytes(uassetPath);
        var uexp = File.ReadAllBytes(Path.ChangeExtension(uassetPath, ".uexp"));
        var usmap = File.ReadAllBytes(usmapPath);
        // 同名资产（不同目录）会撞同一个工作目录，加一段路径哈希区分
        var assetName = Path.GetFileNameWithoutExtension(uassetPath) + "-" +
                        (uint)virtualPath.GetHashCode(StringComparison.Ordinal);

        var errors = new List<string>();

        foreach (var root in WorkRoots)
        {
            // 同一个资产目录可能被上次同 PID 的实例留下锁（PID 复用）——先试原名，失败再试带随机后缀的。
            foreach (var suffix in new[] { string.Empty, "-" + Guid.NewGuid().ToString("N")[..6] })
            {
                var work = Path.Combine(root, assetName + suffix);
                try
                {
                    Directory.CreateDirectory(work);
                    // 先探一次可写：目录存在但被锁/无权限时，Directory.CreateDirectory 不会报错，
                    // 要等真正写文件才炸，那时错误信息已经离开现场了。
                    var probe = Path.Combine(work, ".writable");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);

                    return KismetWorkspace.Open(uasset, uexp, usmap, work);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    errors.Add($"{work}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        throw new IOException(
            "工作目录不可写，所有候选都失败了：\n  " + string.Join("\n  ", errors) +
            "\n（通常是另一个实例正占着同一目录，或安全软件拦了临时目录写入）");
    }
}




