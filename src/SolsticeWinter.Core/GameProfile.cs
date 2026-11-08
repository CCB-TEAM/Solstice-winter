using System.Text.Json;

namespace SolsticeWinter.Core;

/// <summary>
/// 一个游戏的「事实来源」清单：pak 在哪、导出目录在哪、usmap/jmap/registry 在哪、AES key 是什么。
///
/// <para>
/// 设计取舍：<b>key 不落仓库</b>。KARDS 的 AES key 由 FModel 的配置持有
/// （<c>%APPDATA%\FModel\AppSettings.json</c> 里 <c>PerDirectory[&lt;游戏目录&gt;].AesKeys.mainKey</c>），
/// 这里只负责把它读出来用，不复制到任何提交物里。
/// </para>
/// </summary>
public sealed class GameProfile
{
    public required string Name { get; init; }
    public string? GameDirectory { get; init; }
    public string? PakDirectory { get; init; }
    public string? ExportDirectory { get; init; }
    public string? UsmapPath { get; init; }
    public string? JmapPath { get; init; }
    public string? AssetRegistryPath { get; init; }
    public string? AesKeyHex { get; init; }
    public string? AesKeySource { get; init; }

    public const string DefaultKardsGameDir = @"D:\files\projects\kards";
    public const string DefaultKardsExports = @"D:\devenv\fmodel\Output\Exports\kards\Content";
    public const string FmodelSettingsPath = @"%APPDATA%\FModel\AppSettings.json";

    /// <summary>
    /// 从 <see cref="StudioSettings.Sources"/> 组装 profile：字段填了就用，没填就按 KARDS 默认值补。
    /// 这是 UI 的唯一入口——配置改完重新调一次即可，不需要重启。
    /// </summary>
    public static GameProfile FromSettings(StudioSettings settings)
    {
        var sources = settings.Sources;
        var defaults = Kards(sources.GameDirectory);
        var pakDir = sources.PakDirectory ?? defaults.PakDirectory;

        var usmap = sources.UsmapPath;
        if (string.IsNullOrWhiteSpace(usmap) && pakDir is not null && Directory.Exists(pakDir))
            usmap = Directory.GetFiles(pakDir, "*.usmap").FirstOrDefault();

        var (key, keySource) = ResolveAesKey(sources);

        return new GameProfile
        {
            Name = "kards",
            GameDirectory = sources.GameDirectory ?? defaults.GameDirectory,
            PakDirectory = pakDir,
            ExportDirectory = sources.ExportDirectory ?? defaults.ExportDirectory,
            UsmapPath = usmap,
            JmapPath = sources.JmapPath ?? DefaultJmap(),
            AssetRegistryPath = sources.AssetRegistryPath,
            AesKeyHex = key,
            AesKeySource = keySource,
        };
    }

    /// <summary>jmap 产物（functions.json）的默认位置；没有就返回 null，不猜。</summary>
    private static string? DefaultJmap()
    {
        var candidate = @"D:\files\projects\jmap-out\functions.json";
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 按 <see cref="SourcesSettings.AesKeySource"/> 取 key：<c>fmodel</c> 走 FModel 配置，
    /// <c>env:NAME</c> 读环境变量。取不到返回 (null, 原因)，不猜。
    /// </summary>
    private static (string? Key, string? Source) ResolveAesKey(SourcesSettings sources)
    {
        var spec = sources.AesKeySource?.Trim() ?? "fmodel";
        if (spec.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = spec[4..].Trim();
            var value = Environment.GetEnvironmentVariable(name);
            return (string.IsNullOrWhiteSpace(value) ? null : value, $"环境变量 {name}");
        }

        return FindAesKey(sources.GameDirectory ?? DefaultKardsGameDir);
    }

    /// <summary>
    /// KARDS 的默认 profile：游戏目录、pak 目录、FModel 导出目录、usmap、jmap、registry 都按本机实测路径填，
    /// 缺哪个就把哪个留空（调用方自己决定是否必需）。
    /// </summary>
    public static GameProfile Kards(string? gameDirectory = null)
    {
        var dir = gameDirectory ?? DefaultKardsGameDir;
        var paks = Path.Combine(dir, "Content", "Paks");
        var usmap = Directory.Exists(paks)
            ? Directory.GetFiles(paks, "*.usmap").FirstOrDefault()
            : null;
        var jmap = Directory.Exists(paks)
            ? Directory.GetFiles(paks, "*.jmap").FirstOrDefault()
            : null;

        var (key, source) = FindAesKey(dir);

        return new GameProfile
        {
            Name = "kards",
            GameDirectory = dir,
            PakDirectory = Directory.Exists(paks) ? paks : null,
            ExportDirectory = Directory.Exists(DefaultKardsExports) ? DefaultKardsExports : null,
            UsmapPath = usmap,
            JmapPath = jmap,
            AssetRegistryPath = null,
            AesKeyHex = key,
            AesKeySource = source,
        };
    }

    /// <summary>
    /// 从 FModel 的配置里找某个游戏目录对应的 AES key。
    /// 找不到返回 (null, null)——不猜、不硬编码。
    /// </summary>
    public static (string? Key, string? Source) FindAesKey(string gameDirectory)
    {
        var settings = Environment.ExpandEnvironmentVariables(FmodelSettingsPath);
        if (!File.Exists(settings)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            if (!doc.RootElement.TryGetProperty("PerDirectory", out var perDir)) return (null, null);

            var wanted = Path.GetFullPath(gameDirectory).TrimEnd('\\', '/');
            foreach (var game in perDir.EnumerateObject())
            {
                var configured = game.Name.TrimEnd('\\', '/');
                if (!string.Equals(configured, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (!game.Value.TryGetProperty("AesKeys", out var keys)) continue;
                if (!keys.TryGetProperty("mainKey", out var mainKey)) continue;
                var value = mainKey.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                return (value, $"{settings} → PerDirectory[{game.Name}].AesKeys.mainKey");
            }
        }
        catch (JsonException)
        {
            return (null, null);
        }

        return (null, null);
    }
}

