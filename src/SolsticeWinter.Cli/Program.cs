using System.Security.Cryptography;
using SolsticeWinter.Core;
using SolsticeWinter.Index;
using SolsticeWinter.Pak;

namespace SolsticeWinter.Cli;

/// <summary>
/// 命令行驱动器。这一层只做「把库能力摊开给人看 + 给出可复现的验证数字」，
/// 不放业务逻辑（业务在 SolsticeWinter.Core / SolsticeWinter.Pak）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (PakException e)
        {
            Console.Error.WriteLine($"[pak] {e.Message}");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[错误] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();

        return args[0] switch
        {
            "pak" => Pak(args[1..]),
            "index" => IndexCommand(args[1..]),
            "profile" => Profile(args[1..]),
            "-h" or "--help" or "help" => Usage(),
            _ => Unknown(args[0]),
        };
    }

    private static int IndexCommand(string[] args)
    {
        if (args.Length < 2) return Usage();
        var action = args[0];
        var options = ParseOptions(args[2..]);

        switch (action)
        {
            case "build":
            {
                var root = args[1];
                var usmap = options.GetValueOrDefault("usmap")
                            ?? GameProfile.Kards(options.GetValueOrDefault("game")).UsmapPath
                            ?? throw new InvalidOperationException("没给 --usmap，profile 里也没有");
                var output = options.GetValueOrDefault("out") ?? "index.json";
                var limit = int.TryParse(options.GetValueOrDefault("limit"), out var n) ? n : int.MaxValue;
                var jmap = options.GetValueOrDefault("jmap")
                           ?? GameProfile.Kards(options.GetValueOrDefault("game")).JmapPath;

                Console.WriteLine($"扫描 {root}");
                Console.WriteLine(jmap is null ? "jmap：未提供（只索引资产本体）" : $"jmap：{jmap}");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var (index, failures) = SymbolIndex.Build(root, usmap, limit, m => Console.WriteLine(m), jmap);
                watch.Stop();

                index.Save(output);
                Console.WriteLine(index.Describe());
                Console.WriteLine($"耗时      : {watch.ElapsedMilliseconds} ms");
                Console.WriteLine($"写出      : {Path.GetFullPath(output)}（{new FileInfo(output).Length:N0} 字节）");

                if (failures.Count > 0)
                {
                    Console.WriteLine($"失败      : {failures.Count} 个");
                    foreach (var f in failures.Take(10)) Console.WriteLine("  " + f);
                    return 1;
                }

                return 0;
            }

            case "stats":
            {
                var index = SymbolIndex.Load(args[1]);
                Console.WriteLine(index.Describe());
                var top = index.Assets.OrderByDescending(a => a.Functions.Count).Take(5);
                Console.WriteLine("函数最多的资产：");
                foreach (var a in top) Console.WriteLine($"  {a.Functions.Count,4}  {a.VirtualPath}");
                return 0;
            }

            case "find":
            {
                var index = SymbolIndex.Load(args[1]);
                var needle = args[2];
                foreach (var hit in index.SearchFunctions(needle))
                {
                    var fn = hit.Function;
                    Console.WriteLine($"{hit.Asset.VirtualPath}.{fn.Name}  ({fn.Params.Count} 参数" +
                                      (fn.Return is null ? "" : $", 返回 {fn.Return}") + $")  {(fn.HasBytecode ? "有字节码" : "")}");
                }

                return 0;
            }

            case "probe":
            {
                var assetPath = args[1];
                var usmapPath = options.GetValueOrDefault("usmap")
                                ?? GameProfile.Kards(options.GetValueOrDefault("game")).UsmapPath
                                ?? throw new InvalidOperationException("没给 --usmap，profile 里也没有");
                var (workAsset, workUsmap, workDir) = AssetProbe.Prepare(assetPath, usmapPath);
                try
                {
                    var usmap = new UAssetAPI.Unversioned.Usmap(workUsmap);
                    foreach (var line in AssetProbe.Exports(workAsset, usmap)) Console.WriteLine(line);

                    if (int.TryParse(options.GetValueOrDefault("reparse"), out var exportIndex))
                    {
                        var error = AssetProbe.TryReparse(workAsset, usmap, exportIndex);
                        Console.WriteLine($"重解析导出 #{exportIndex}: {error}");
                    }
                }
                finally
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { }
                }

                return 0;
            }

            case "asset":
            {
                var index = SymbolIndex.Load(args[1]);
                var asset = index.Find(args[2]);
                if (asset is null)
                {
                    Console.Error.WriteLine($"索引里没有 {args[2]}");
                    return 1;
                }

                Console.WriteLine(asset);
                Console.WriteLine("变量：");
                foreach (var v in asset.Variables) Console.WriteLine($"  {v.Type,-40} {v.Name}");
                Console.WriteLine("函数：");
                foreach (var f in asset.Functions)
                {
                    var sig = string.Join(", ", f.Params.Select(p => $"{p.Type} {p.Name}"));
                    Console.WriteLine($"  {f.Name}({sig}){(f.Return is null ? "" : $" -> {f.Return}")}  {(f.HasBytecode ? $"[{f.BytecodeSize}B]" : "[无字节码]")}");
                }

                return 0;
            }

            default:
                return Usage();
        }
    }

    private static int Pak(string[] args)
    {
        if (args.Length < 2) return Usage();
        var action = args[0];
        var pakPath = args[1];
        var options = ParseOptions(args[2..]);

        var profile = GameProfile.Kards(options.GetValueOrDefault("game"));
        var key = options.GetValueOrDefault("key") ?? profile.AesKeyHex;
        var keySource = options.ContainsKey("key") ? "--key" : profile.AesKeySource;

        var openOptions = new PakOpenOptions
        {
            AesKeyHex = key,
            OodleLibraryPath = options.GetValueOrDefault("oodle"),
        };

        switch (action)
        {
            case "info":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                Console.WriteLine($"pak        : {pakPath}");
                Console.WriteLine($"版本       : {pak.Info.Version}   magic 0x{pak.Info.Magic:X8}");
                Console.WriteLine($"尾部       : {pak.Info.FooterSize} 字节 @ {pak.Info.FooterOffset}");
                Console.WriteLine($"索引       : {pak.Info.IndexSize} 字节 @ {pak.Info.IndexOffset}  加密={pak.Info.EncryptedIndex}");
                Console.WriteLine($"AES key    : {(key is null ? "无" : $"已提供（来源 {keySource}）")}");
                Console.WriteLine($"压缩方法   : {string.Join(", ", pak.Info.CompressionMethods.Select((m, i) => $"[{i}]{m}"))}");
                Console.WriteLine($"挂载点     : {pak.MountPoint}");
                Console.WriteLine($"条目数     : {pak.EntryCount}");
                Console.WriteLine($"Oodle      : {Oodle.Describe()}");
                foreach (var w in pak.Warnings) Console.WriteLine($"[警告] {w}");
                return 0;
            }

            case "list":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                var filter = options.GetValueOrDefault("filter");
                var limit = int.TryParse(options.GetValueOrDefault("limit"), out var n) ? n : int.MaxValue;

                var rows = pak.Files.Values
                    .Where(f => filter is null || f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();

                foreach (var f in rows)
                {
                    var method = pak.Info.MethodName(f.CompressionMethodIndex);
                    Console.WriteLine($"{f.UncompressedSize,10}  {method,-6} {(f.Encrypted ? "E" : " ")} {f.Path}");
                }

                Console.WriteLine($"--- 共 {rows.Count} / {pak.EntryCount} 条 ---");
                return 0;
            }

            case "find":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                var file = pak.Find(args[2]);
                Console.WriteLine(file);
                return 0;
            }

            case "extract":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                var file = pak.Find(args[2]);
                var outDir = options.GetValueOrDefault("out") ?? ".";
                var written = pak.Extract(file, outDir);
                Console.WriteLine($"已写出 {written}（{new FileInfo(written).Length} 字节）");
                foreach (var w in pak.Warnings) Console.WriteLine($"[警告] {w}");
                return 0;
            }

            case "verify":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                var limit = int.TryParse(options.GetValueOrDefault("limit"), out var n) ? n : 50;
                var sample = pak.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

                var ok = 0;
                var fail = 0;
                long total = 0;
                foreach (var f in sample)
                {
                    try
                    {
                        var data = pak.Read(f);
                        if (data.Length != f.UncompressedSize)
                            throw new PakException($"长度 {data.Length} != 声明 {f.UncompressedSize}");
                        ok++;
                        total += data.Length;
                    }
                    catch (Exception e)
                    {
                        fail++;
                        Console.WriteLine($"[失败] {f.Path}: {e.Message}");
                    }
                }

                Console.WriteLine($"解压校验：成功 {ok}，失败 {fail}，共 {total:N0} 字节");
                return fail == 0 ? 0 : 1;
            }

            case "compare":
            {
                using var pak = PakArchive.Open(pakPath, openOptions);
                var file = pak.Find(args[2]);
                var reference = args[3];
                var extracted = pak.Read(file);
                var expected = File.ReadAllBytes(reference);

                var a = Convert.ToHexString(SHA256.HashData(extracted));
                var b = Convert.ToHexString(SHA256.HashData(expected));
                Console.WriteLine($"pak 条目 : {file.Path}");
                Console.WriteLine($"  {extracted.Length,10} 字节  SHA256 {a}");
                Console.WriteLine($"参考文件 : {reference}");
                Console.WriteLine($"  {expected.Length,10} 字节  SHA256 {b}");
                Console.WriteLine(a == b ? "结论：逐字节一致 ✅" : "结论：不一致 ❌");
                return a == b ? 0 : 1;
            }

            default:
                return Usage();
        }
    }

    private static int Profile(string[] args)
    {
        var profile = GameProfile.Kards(args.Length > 0 ? args[0] : null);
        Console.WriteLine($"profile    : {profile.Name}");
        Console.WriteLine($"游戏目录   : {profile.GameDirectory}");
        Console.WriteLine($"pak 目录   : {profile.PakDirectory}");
        Console.WriteLine($"导出目录   : {profile.ExportDirectory}");
        Console.WriteLine($"usmap      : {profile.UsmapPath}");
        Console.WriteLine($"jmap       : {profile.JmapPath}");
        Console.WriteLine($"AES key    : {(profile.AesKeyHex is null ? "未找到" : "已找到")}（{profile.AesKeySource ?? "无来源"}）");
        return 0;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var name = args[i][2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }

            options[name] = value;
        }

        return options;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        return Usage();
    }

    private static int Usage()
    {
        Console.WriteLine("""
            solstice-winter — cooked 蓝图工坊（命令行驱动）

            用法：
              solstice-winter pak info    <pak> [--key HEX] [--oodle DLL] [--game DIR]
              solstice-winter pak list    <pak> [--filter S] [--limit N]
              solstice-winter pak find    <pak> <路径后缀>
              solstice-winter pak extract <pak> <路径后缀> --out <目录>
              solstice-winter pak verify  <pak> [--limit N]          逐条解压并核对长度
              solstice-winter pak compare <pak> <路径后缀> <参考文件>  与外部文件逐字节比对
              solstice-winter index build <内容根> [--usmap P] [--out F] [--limit N]
              solstice-winter index stats <索引文件>
              solstice-winter index find  <索引文件> <函数名子串>
              solstice-winter index asset <索引文件> <虚拟路径>
              solstice-winter index probe <资产> [--reparse 导出号]    诊断：导出表 / 重读导出体
              solstice-winter profile     [游戏目录]                  看数据源与 AES key 来源

            AES key 默认从 FModel 的 AppSettings.json 读，不在仓库里存。
            """);
        return 0;
    }
}



