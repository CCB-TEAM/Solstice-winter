using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolsticeWinter.Core;

/// <summary>
/// 第一版 UI 的全部可配置项，落在仓库根 <c>solstice-winter.settings.json</c>。
///
/// <para>
/// 取舍：配置放仓库而不是 <c>%APPDATA%</c>——这份工具的事实来源（导出目录、usmap、pak）
/// 是**项目级**的，跟仓库走才能「克隆下来就能跑」；代价是多人共用一份，改配置会进 git diff。
/// </para>
///
/// <para>
/// 加载策略：文件不存在就用默认值**并写出一份**（带注释性的默认值，方便手改）；
/// 文件存在但字段缺失，缺失字段取默认值（`System.Text.Json` 的默认行为）。
/// </para>
/// </summary>
public sealed class StudioSettings
{
    public const string FileName = "solstice-winter.settings.json";

    /// <summary>数据源：这一节决定「资产从哪来」。</summary>
    public SourcesSettings Sources { get; set; } = new();

    /// <summary>布局：改完立即重排，不需要重启。</summary>
    public LayoutSettings Layout { get; set; } = new();

    /// <summary>显示：标题风格、字号、主题。</summary>
    public DisplaySettings Display { get; set; } = new();

    /// <summary>最近打开的资产（虚拟路径，最多 5 个，最新的在前）。</summary>
    public List<string> RecentAssets { get; set; } = [];

    public static string DefaultPath(string repoRoot) => Path.Combine(repoRoot, FileName);

    /// <summary>加载；文件不存在时返回默认值并写盘（让用户有一份可改的模板）。</summary>
    public static StudioSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new StudioSettings();
            try { fresh.Save(path); } catch { /* 写不出模板不影响启动 */ }
            return fresh;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path), JsonOptions);
            return loaded ?? new StudioSettings();
        }
        catch (JsonException)
        {
            // 配置写坏了不该让工具打不开：用默认值继续，文件保持原样（用户自己能看出哪行坏了）
            return new StudioSettings();
        }
    }

    /// <summary>
    /// 深拷贝。设置窗口要在**副本**上编辑——点「取消」时原对象必须原封不动，
    /// 否则取消就变成了「只撤销一部分」（各子对象是引用类型，浅拷贝会把它们一起改掉）。
    /// </summary>
    public StudioSettings Clone() =>
        JsonSerializer.Deserialize<StudioSettings>(
            JsonSerializer.Serialize(this, JsonOptions), JsonOptions) ?? new StudioSettings();

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void RememberAsset(string virtualPath)
    {
        RecentAssets.RemoveAll(p => string.Equals(p, virtualPath, StringComparison.Ordinal));
        RecentAssets.Insert(0, virtualPath);
        if (RecentAssets.Count > 5) RecentAssets.RemoveRange(5, RecentAssets.Count - 5);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// 数据源。全部字段都可为空——空表示「这条路没配」，UI 会在需要时明确报错而不是静默降级。
/// <b>AES key 不写在这里</b>：只写「从哪读」（FModel 配置 / 环境变量 / 文件），key 本体不进仓库。
/// </summary>
public sealed class SourcesSettings
{
    /// <summary>FModel 导出目录的内容根，对应 <c>/Game/</c>。</summary>
    public string? ExportDirectory { get; set; } = @"D:\devenv\fmodel\Output\Exports\kards\Content";

    /// <summary>usmap 路径（结构体/类/枚举 schema）。</summary>
    public string? UsmapPath { get; set; }

    /// <summary>jmap 产物 <c>functions.json</c>（可选，用于 /Script 签名）。</summary>
    public string? JmapPath { get; set; } = @"D:\files\projects\jmap-out\functions.json";

    /// <summary><c>AssetRegistry.bin</c>（可选，用于资产目录）。</summary>
    public string? AssetRegistryPath { get; set; }

    /// <summary>pak 目录（可选；当前 UI 还没接 pak 直读）。</summary>
    public string? PakDirectory { get; set; }

    /// <summary>
    /// AES key 的来源：<c>fmodel</c>（从 FModel 的 AppSettings.json 读）或 <c>env:NAME</c>（环境变量）。
    /// 不存 key 本体。
    /// </summary>
    public string AesKeySource { get; set; } = "fmodel";

    /// <summary>游戏目录（FModel 配置按它查 key，也是 pak 目录的默认上级）。</summary>
    public string? GameDirectory { get; set; } = @"D:\files\projects\kards";
}

/// <summary>布局参数。改完调 <c>GraphBuilder.Build</c> 重排即可，不需要重启。</summary>
public sealed class LayoutSettings
{
    /// <summary>exec 链每步的横向间距。</summary>
    public double ColumnWidth { get; set; } = 300;

    /// <summary>分支上下分叉的行间距。</summary>
    public double RowHeight { get; set; } = 260;

    /// <summary>数据节点相对使用者的纵向偏移（负数 = 往上）。</summary>
    public double DataOffsetY { get; set; } = -170;
}

/// <summary>显示参数。</summary>
public sealed class DisplaySettings
{
    /// <summary><c>pretty</c> = 去常见前缀；<c>raw</c> = 反编译器原名。</summary>
    public string TitleStyle { get; set; } = "pretty";

    /// <summary>引脚上是否显示类型名（object/struct/bool…）。</summary>
    public bool ShowPinTypes { get; set; } = true;

    /// <summary>字号（节点标题与引脚共用）。</summary>
    public double FontSize { get; set; } = 12;


    /// <summary>
    /// 用新反编译器（移植的 UEBlueprintGraphViewer）还是旧的 AST 投影。
    /// 默认开：新的是 UE 式节点（Branch/Sequence/变量 Set），旧的是平铺链。
    /// </summary>
    public bool UseNewDecompiler { get; set; } = true;
}



