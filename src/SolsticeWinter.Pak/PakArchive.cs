using System.IO.Compression;
using System.Security.Cryptography;

namespace SolsticeWinter.Pak;

public sealed class PakOpenOptions
{
    /// <summary>pak 索引/条目加密用的 AES key（十六进制 64 字符）。索引加密时必须给。</summary>
    public string? AesKeyHex { get; init; }

    /// <summary>Oodle 运行时 DLL 的显式路径（不填则按 <see cref="Oodle"/> 的候选顺序找）。</summary>
    public string? OodleLibraryPath { get; init; }
}

/// <summary>
/// 只读 pak 归档（v1..v11，含 v10+ 的 path-hash 索引与编码条目）。
///
/// <para>
/// 与 <c>solstice-pak</c> 的分工：那个库只做 v3 + 不压缩的<b>写</b>；
/// 这个库负责读真实游戏的 pak（实测 KARDS：v11 / 索引 AES 加密 / 条目 Oodle 压缩 + AES 加密）。
/// 两者互不依赖，写路径后续再补。
/// </para>
///
/// <para>
/// 解析顺序（v10+）：
/// <code>
/// footer → 主索引（挂载点 + path hash 索引头 + 目录索引头 + 编码条目池 + 非编码条目）
///        → 目录索引（目录 → 文件名 → 池内定位）
///        → 拼虚拟路径（挂载点 + 目录 + 文件名）
/// </code>
/// 每一步都做「刚好读完」自检：主索引与目录索引的残留字节超过 16 字节（AES 对齐填充的上界）
/// 就记进 <see cref="Warnings"/>，不静默吞掉——残留说明布局假设错了。
/// </para>
/// </summary>
public sealed class PakArchive : IDisposable
{
    /// <summary>加密索引/目录索引按 AES 块对齐写盘，明文尾部最多带 15 字节填充。</summary>
    private const int MaxIndexPadding = AesEcb.BlockSize - 1;

    private readonly FileStream _stream;

    private PakArchive(FileStream stream, PakInfo info)
    {
        _stream = stream;
        Info = info;
    }

    public PakInfo Info { get; }
    public string MountPoint { get; private set; } = string.Empty;
    public Dictionary<string, PakFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
    public int EntryCount => Files.Count;

    /// <summary>全部条目的解压后字节数之和（不需要真解压）。</summary>
    public long TotalUncompressedSize => Files.Values.Sum(f => f.UncompressedSize);

    public static PakArchive Open(string pakPath, PakOpenOptions? options = null)
    {
        options ??= new PakOpenOptions();
        if (!File.Exists(pakPath)) throw new PakException($"pak 不存在：{pakPath}");

        var stream = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var info = PakInfo.Read(stream);
            var archive = new PakArchive(stream, info);
            archive.SetAesKey(options.AesKeyHex);
            archive.Mount(options);
            return archive;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void Mount(PakOpenOptions options)
    {
        var rawIndex = ReadAt(Info.IndexOffset, checked((int)Info.IndexSize));
        var indexBytes = Info.EncryptedIndex ? DecryptIndex(rawIndex, "主索引") : rawIndex;

        // 自检：footer 的 IndexHash 是索引<b>明文</b>的 SHA-1（实测两者一致）
        var actual = SHA1.HashData(indexBytes);
        if (!actual.AsSpan().SequenceEqual(Info.IndexHash))
            Warnings.Add($"主索引 SHA-1 与 footer 记录不一致（记录 {Convert.ToHexString(Info.IndexHash)}，" +
                         $"实际 {Convert.ToHexString(actual)}）——key 错或文件被改过");

        if (Info.Version >= 10)
            MountPathHashIndex(indexBytes, options);
        else
            MountLegacyIndex(indexBytes, options);
    }

    /// <summary>
    /// 读挂载点 FString，并校验它「像不像挂载点」。
    /// 用错 AES key 解密索引时，这里读到的是一个随机 int32 长度——直接按它读会得到
    /// 「需要 1378850302 字节」这种莫名其妙的消息，所以先在这里拦下来并指向 key。
    /// </summary>
    private static string ReadMountPoint(ByteCursor cur, string what)
    {
        if (cur.Remaining < 4)
            throw new PakException($"{what}: 解密后连挂载点长度都读不出来，AES key 很可能不对（剩余 {cur.Remaining} 字节）");

        var declared = cur.PeekInt32();
        var plausible = declared > 0 && declared <= 4096 && declared <= cur.Remaining - 4;
        if (!plausible)
            throw new PakException(
                $"{what}: 解密后的挂载点长度是 {declared}，不可能是合法索引——AES key 不对，或这个 pak 的索引没按 AES-ECB 加密");

        var mountPoint = cur.ReadFString();
        if (mountPoint.Any(c => c < 0x20 || c > 0x7E))
            throw new PakException($"{what}: 解密后的挂载点 \"{mountPoint}\" 含不可打印字符——AES key 很可能不对");
        return mountPoint;
    }

    /// <summary>v10+：主索引 + 目录索引 + 编码条目池。</summary>
    private void MountPathHashIndex(byte[] indexBytes, PakOpenOptions options)
    {
        var cur = new ByteCursor(indexBytes, "主索引");
        MountPoint = ReadMountPoint(cur, "主索引");
        var declaredCount = cur.ReadInt32();
        cur.Skip(8); // PathHashSeed

        // 注意：这里的 bool 是 UE 的 4 字节 bool，按 1 字节读会整体错位 3 字节
        if (!cur.ReadUInt32Bool())
            throw new PakException("主索引没有 path hash index（本实现只支持带它的 v10+ 布局）");
        cur.Skip(36); // PathHashIndexOffset(8) + Size(8) + Hash(20)

        if (!cur.ReadUInt32Bool())
            throw new PakException("主索引没有 full directory index（本实现只支持带它的 v10+ 布局）");
        var dirOffset = cur.ReadInt64();
        var dirSize = cur.ReadInt64();
        cur.Skip(20); // DirectoryIndexHash

        var encodedSize = cur.ReadInt32();
        if (encodedSize < 0 || encodedSize > cur.Remaining)
            throw new PakException($"主索引的编码条目池长度 {encodedSize} 不合理（剩余 {cur.Remaining}）");
        var encoded = new ByteCursor(cur.ReadBytes(encodedSize), "编码条目池");

        var nonEncodedCount = cur.ReadInt32();
        if (nonEncodedCount < 0 || nonEncodedCount > 1 << 22)
            throw new PakException($"主索引的非编码条目数量 {nonEncodedCount} 不合理");

        var nonEncoded = new PakFile[nonEncodedCount];
        for (var i = 0; i < nonEncodedCount; i++)
            nonEncoded[i] = PakFile.ReadLegacy(cur, Info, $"<nonencoded {i}>");

        CheckResidual(cur, "主索引", indexBytes, cur.Position);

        // 目录索引是独立一段，与主索引同样加密
        var dirRaw = ReadAt(dirOffset, checked((int)dirSize));
        var dirBytes = Info.EncryptedIndex ? DecryptIndex(dirRaw, "目录索引") : dirRaw;

        var dirCur = new ByteCursor(dirBytes, "目录索引");
        if (dirCur.Remaining >= 4 && dirCur.PeekUInt32() == FlatDirectoryIndexMagic)
            throw new PakException("这个 pak 用的是扁平目录索引（'PFDQ'），本实现不支持（见 docs 的边界）");

        var dirCount = dirCur.ReadInt32();
        if (dirCount < 0 || dirCount > 1 << 22)
            throw new PakException($"目录索引的目录数量 {dirCount} 不合理");
        var mountEndsWithSlash = MountPoint.EndsWith('/');

        for (var d = 0; d < dirCount; d++)
        {
            var dir = dirCur.ReadFString();
            var fileCount = dirCur.ReadInt32();
            if (fileCount < 0 || fileCount > 1 << 24)
                throw new PakException($"目录 {dir} 的文件数量 {fileCount} 不合理");

            var trim = mountEndsWithSlash && dir.StartsWith('/');
            var dirPart = trim ? dir[1..] : dir;

            for (var f = 0; f < fileCount; f++)
            {
                var name = dirCur.ReadFString();
                var location = dirCur.ReadInt32();
                if (location == int.MinValue) continue; // 删除记录

                var path = MountPoint + dirPart + name;

                PakFile file;
                if (location >= 0)
                {
                    file = PakFile.ReadEncoded(encoded, location, Info, path);
                }
                else
                {
                    var idx = -location - 1;
                    if (idx < 0 || idx >= nonEncoded.Length)
                    {
                        Warnings.Add($"路径 {path} 指向不存在的非编码条目 {idx}");
                        continue;
                    }

                    file = nonEncoded[idx];
                    file.Path = path;
                }

                Files[path] = file;
            }
        }

        CheckResidual(dirCur, "目录索引", dirBytes, dirCur.Position);

        if (declaredCount != Files.Count)
            Warnings.Add($"主索引声明 {declaredCount} 个文件，目录索引实际给出 {Files.Count} 个");
    }

    /// <summary>v1..v9：挂载点 + 数量 + (名字, 条目) 直表。</summary>
    private void MountLegacyIndex(byte[] indexBytes, PakOpenOptions options)
    {
        var cur = new ByteCursor(indexBytes, "传统索引");
        MountPoint = ReadMountPoint(cur, "传统索引");
        var count = cur.ReadInt32();
        if (count < 0 || count > 1 << 24)
            throw new PakException($"传统索引的文件数量 {count} 不合理");

        for (var i = 0; i < count; i++)
        {
            var name = cur.ReadFString();
            var path = MountPoint + name;
            var file = PakFile.ReadLegacy(cur, Info, path);
            if (file.Deleted && file.UncompressedSize == 0) continue;
            Files[path] = file;
        }

        CheckResidual(cur, "传统索引", indexBytes, cur.Position);
    }

    /// <summary>
    /// 「刚好读完」自检。残留必须小于 16 字节（AES 对齐填充的上界，实测主索引 14、目录索引 1）。
    /// 实测那 14 字节正好是挂载点字符串的副本，1 字节是 0xE2——都不是结构字段，
    /// 所以这里按填充处理，只在残留过大或残留里还有可读结构时告警。
    /// </summary>
    private void CheckResidual(ByteCursor cur, string what, byte[] data, int position)
    {
        var remaining = data.Length - position;
        if (remaining <= 0) return;
        if (remaining <= MaxIndexPadding) return;
        Warnings.Add($"{what}解析后残留 {remaining} 字节（偏移 {position}/{data.Length}），布局假设可能不成立：" +
                     Convert.ToHexString(data, position, Math.Min(remaining, 32)));
    }

    /// <summary>按完整虚拟路径或路径后缀查条目。后缀必须唯一，否则抛异常并列候选。</summary>
    public PakFile Find(string pathOrSuffix)
    {
        if (Files.TryGetValue(pathOrSuffix, out var exact)) return exact;

        var normalized = pathOrSuffix.Replace('\\', '/').TrimStart('/');
        var matches = Files.Keys
            .Where(k => k.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();

        return matches.Count switch
        {
            1 => Files[matches[0]],
            0 => throw new PakException($"pak 里没有路径以 {pathOrSuffix} 结尾的条目（共 {Files.Count} 条）"),
            _ => throw new PakException($"路径后缀 {pathOrSuffix} 命中 {matches.Count} 个条目，请给更长的路径：\n  " +
                                        string.Join("\n  ", matches)),
        };
    }

    public byte[] Read(string pathOrSuffix) => Read(Find(pathOrSuffix));

    public byte[] Read(PakFile file)
    {
        if (file.Deleted) throw new PakException($"{file.Path} 是删除记录");
        if (file.UncompressedSize > int.MaxValue) throw new PakException($"{file.Path} 超过 2GB，暂不支持");
        if (file.UncompressedSize < 0 || file.CompressedSize < 0)
            throw new PakException($"{file.Path} 的尺寸字段为负（uncomp={file.UncompressedSize} comp={file.CompressedSize}）");

        if (!file.IsCompressed)
            return ReadRaw(file.Offset + file.StructSize, checked((int)file.CompressedSize), file.Encrypted);

        var method = Info.MethodName(file.CompressionMethodIndex);
        var output = new byte[file.UncompressedSize];

        // 单块条目直接以解压后长度为块大小（位域里的块大小字段可能是 0）
        var chunkSize = file.Blocks.Length == 1 ? file.UncompressedSize : file.BlockSize;
        if (chunkSize <= 0)
            throw new PakException($"{file.Path} 有 {file.Blocks.Length} 个压缩块但块大小是 {chunkSize}");

        long written = 0;
        for (var i = 0; i < file.Blocks.Length; i++)
        {
            var (start, end) = file.Blocks[i];
            var compressedLength = checked((int)(end - start));
            var raw = ReadRaw(start, compressedLength, file.Encrypted);
            var outLength = (int)Math.Min(chunkSize, file.UncompressedSize - written);
            if (outLength <= 0)
                throw new PakException($"{file.Path} 块 {i} 的目标长度 {outLength} 非法（已写 {written}）");

            switch (method)
            {
                case "Oodle":
                    var got = Oodle.Decompress(raw, compressedLength, output, (int)written, outLength);
                    if (got != outLength)
                        Warnings.Add($"{file.Path} 块 {i} Oodle 解出 {got} 字节，期望 {outLength}");
                    break;
                case "Zlib":
                    Inflate(raw, output, (int)written, outLength, zlib: true);
                    break;
                case "Gzip":
                    Inflate(raw, output, (int)written, outLength, zlib: false);
                    break;
                default:
                    throw new PakException(
                        $"{file.Path} 用 {method} 压缩（方法下标 {file.CompressionMethodIndex}），本实现只支持 None/Oodle/Zlib/Gzip");
            }

            written += outLength;
        }

        if (written != file.UncompressedSize)
            throw new PakException($"{file.Path} 解出 {written} 字节，头部声明 {file.UncompressedSize}");
        return output;
    }

    /// <summary>把条目写到磁盘，返回写出的完整路径。</summary>
    public string Extract(PakFile file, string outputDirectory)
    {
        var data = Read(file);
        var relative = file.Path.Replace('\\', '/');
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        Directory.CreateDirectory(outputDirectory);
        var target = Path.Combine(outputDirectory, name);
        File.WriteAllBytes(target, data);
        return target;
    }

    private static void Inflate(byte[] compressed, byte[] destination, int destinationOffset, int expected, bool zlib)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using Stream decoder = zlib
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        var written = 0;
        while (written < expected)
        {
            var n = decoder.Read(destination, destinationOffset + written, expected - written);
            if (n <= 0) break;
            written += n;
        }

        if (written != expected)
            throw new PakException($"{(zlib ? "Zlib" : "Gzip")} 解出 {written} 字节，期望 {expected}");
    }

    /// <summary>
    /// 读原始字节；加密条目把 <c>align16(length)</c> 字节整段解密后切出前 <paramref name="length"/> 字节。
    /// <para>
    /// <b>块起点不需要 16 字节对齐</b>：UE 是对每个块<b>从块起点开始</b>做 AES-ECB，再把块尾补到 16 字节。
    /// 实测 KARDS 的加密块起点 mod 16 是 12，按「对齐起点 + 切片」的写法会解出垃圾。
    /// </para>
    /// </summary>
    private byte[] ReadRaw(long offset, int length, bool encrypted)
    {
        if (length < 0) throw new PakException($"读长度 {length} 为负");
        if (!encrypted) return ReadAt(offset, length);

        var alignedLength = checked((int)AesEcb.Align(length));
        var buffer = ReadAt(offset, alignedLength);
        var key = AesEcb.ParseKey(_aesKeyHex ?? throw new PakException("条目加密但没给 AES key"));
        var plain = AesEcb.Decrypt(buffer, key, 0, alignedLength);
        return length == alignedLength ? plain : plain.AsSpan(0, length).ToArray();
    }

    /// <summary>解密一段索引，失败信息里带上「哪一段」。</summary>
    private byte[] DecryptIndex(byte[] data, string what)
    {
        if (string.IsNullOrWhiteSpace(_aesKeyHex))
            throw new PakException($"这个 pak 的索引是加密的，但没有给 AES key（PakOpenOptions.AesKeyHex）：{what}无法解密");
        var key = AesEcb.ParseKey(_aesKeyHex);
        return AesEcb.Decrypt(data, key, 0, data.Length);
    }

    private string? _aesKeyHex;

    internal void SetAesKey(string? key) => _aesKeyHex = key;

    private byte[] ReadAt(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > _stream.Length)
            throw new PakException($"读越界：offset={offset} length={length} 文件长={_stream.Length}");
        var buffer = new byte[length];
        _stream.Seek(offset, SeekOrigin.Begin);
        PakInfo.ReadFully(_stream, buffer, 0, length);
        return buffer;
    }

    /// <summary>扁平目录索引（UE 5.5+ 的 'PFDQ' 格式）magic，本实现不解析。</summary>
    private const uint FlatDirectoryIndexMagic = 0x50464451;

    public void Dispose() => _stream.Dispose();
}
