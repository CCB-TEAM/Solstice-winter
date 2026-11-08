using System.Text.Json;

namespace SolsticeWinter.Index;

/// <summary>一个 `/Script` 函数参数（来自 jmap）。</summary>
public sealed record ScriptParam(string Name, string Type, string Direction, string? Class);

/// <summary>
/// 一个 `/Script` 函数签名（jmap 产物）。<see cref="Flags"/> 是原始文本，
/// 判静态/纯函数时读它（`FUNC_Static` / `FUNC_BlueprintPure`）。
/// </summary>
public sealed record ScriptFunction(string Owner, string Name, string Flags, IReadOnlyList<ScriptParam> Params)
{
    public bool IsStatic => Flags.Contains("FUNC_Static", StringComparison.Ordinal);
    public bool IsPure => Flags.Contains("FUNC_BlueprintPure", StringComparison.Ordinal);

    /// <summary>返回参数（`dir == "return"`），没有则 null。</summary>
    public ScriptParam? Return => Params.FirstOrDefault(p => p.Direction == "return");

    public string FullPath => $"{Owner}.{Name}";
}

/// <summary>
/// jmap 产物（<c>functions.json</c>）的读取器。
///
/// <para>
/// 文件结构：<c>{ metadata, objects: { "/Script/Engine.KismetSystemLibrary": { type, super, source, functions: { "PrintString": { flags, params: [...] } } } } }</c>。
/// 只取 <c>/Script</c> 的函数签名——`/Game` 侧的签名由资产本体提供（jmap 里没有）。
/// </para>
///
/// <para>
/// 取舍：一次性 `JsonDocument` 读入（实测 5.3 MB / 13064 个函数，几百毫秒），
/// 不做流式解析——索引构建本来就是秒级的事，流式只会让代码复杂。
/// </para>
/// </summary>
public sealed class JmapCatalog
{
    private JmapCatalog(List<ScriptFunction> functions)
    {
        Functions = functions;
        ByName = functions
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScriptFunction>)g.ToList(), StringComparer.Ordinal);
    }

    public IReadOnlyList<ScriptFunction> Functions { get; }

    /// <summary>短名 → 候选（同名跨类是常态）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ScriptFunction>> ByName { get; }

    public static JmapCatalog Empty { get; } = new([]);

    public static JmapCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var functions = new List<ScriptFunction>(14000);
        if (!document.RootElement.TryGetProperty("objects", out var objects)) return new JmapCatalog(functions);

        foreach (var owner in objects.EnumerateObject())
        {
            if (!owner.Value.TryGetProperty("functions", out var functionMap)) continue;

            foreach (var function in functionMap.EnumerateObject())
            {
                var flags = function.Value.TryGetProperty("flags", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                var parameters = new List<ScriptParam>();

                if (function.Value.TryGetProperty("params", out var paramArray))
                {
                    foreach (var parameter in paramArray.EnumerateArray())
                    {
                        parameters.Add(new ScriptParam(
                            parameter.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                            parameter.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?",
                            parameter.TryGetProperty("dir", out var d) ? d.GetString() ?? "in" : "in",
                            parameter.TryGetProperty("class", out var c) ? c.GetString() : null));
                    }
                }

                functions.Add(new ScriptFunction(owner.Name, function.Name, flags, parameters));
            }
        }

        return new JmapCatalog(functions);
    }

    public IReadOnlyList<ScriptFunction> Find(string name) =>
        ByName.TryGetValue(name, out var hits) ? hits : [];

    public string Describe() =>
        $"jmap 函数 {Functions.Count} 条，唯一短名 {ByName.Count} 条";
}
