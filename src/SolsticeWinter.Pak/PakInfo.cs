using System.Text;

namespace SolsticeWinter.Pak;

/// <summary>
/// pak 尾部信息（UE 的 <c>FPakInfo</c>）。
///
/// <para>
/// <b>布局是从尾部反推的，不是从头读的</b>：UE 把新增字段序列化在 <c>Magic</c> <b>之前</b>，
/// 所以文件末尾的排列是：
/// </para>
/// <code>
/// [FGuid 16, v7+][bEncryptedIndex 1, v4+] [Magic 4][Version 4][IndexOffset 8][IndexSize 8][IndexHash 20]
/// [v9: bIndexIsFrozen 1] [v8+: 压缩方法名 4 或 5 × 32 字节定长]
/// </code>
/// <para>
/// 实测 KARDS 的 <c>kards-Android_ASTC.pak</c>：版本 11，尾部共 221 字节
/// （= 16 + 1 + 4 + 4 + 8 + 8 + 20 + 5×32），起点 <c>len-221</c>，magic 落在 <c>len-204</c>。
/// 压缩方法名字段是 <b>定长 32 字节缓冲</b>（<c>"Oodle\0" + 0 填充</c>），不是 FString——
/// 按 FString 读会直接跑飞。
/// </para>
/// </summary>
public sealed class PakInfo
{
    public const uint MagicValue = 0x5A6F12E1;

    /// <summary>压缩方法名缓冲的定长（UE 的 <c>COMPRESSION_METHOD_NAME_LEN</c>）。</summary>
    public const int CompressionNameLength = 32;

    /// <summary>压缩方法名槽位数量（UE 的 <c>MaxNumCompressionMethods</c>）。</summary>
    public const int MaxCompressionMethods = 5;

    public required uint Magic { get; init; }
    public required int Version { get; init; }
    public required long IndexOffset { get; init; }
    public required long IndexSize { get; init; }
    public required byte[] IndexHash { get; init; }
    public required bool EncryptedIndex { get; init; }
    public required byte[] EncryptionKeyGuid { get; init; }

    /// <summary>压缩方法名，下标 0 恒为 <c>None</c>，其余按尾部字段顺序、跳过空槽。实测本 pak 为 <c>["None","Oodle"]</c>。</summary>
    public required IReadOnlyList<string> CompressionMethods { get; init; }

    /// <summary>
    /// 按<b>序列化下标</b>排的压缩方法名表：<c>[0]="None"</c>，<c>[i+1]</c> 是尾部第 i 个槽位（空槽为 <c>""</c>）。
    /// 条目里存的压缩方法下标直接索引这张表；槽位有空缺时它和 <see cref="CompressionMethods"/> 的下标不一致。
    /// </summary>
    public required IReadOnlyList<string> CompressionSlots { get; init; }

    /// <summary>尾部结构的实际字节数（含 magic 之前的字段），用于自检。</summary>
    public required int FooterSize { get; init; }

    /// <summary>尾部结构在文件里的起始偏移。</summary>
    public required long FooterOffset { get; init; }

    /// <summary>把条目里的压缩方法下标翻成名字；越界或空槽返回 <c>&lt;method N&gt;</c>。</summary>
    public string MethodName(int index) =>
        index >= 0 && index < CompressionSlots.Count && CompressionSlots[index].Length > 0
            ? CompressionSlots[index]
            : $"<method {index}>";

    /// <summary>
    /// 从流末尾解析 <c>FPakInfo</c>。
    /// 策略：按版本从高到低枚举「尾部应有长度」，逐个回读并校验 magic + version 字段；
    /// 全部不匹配就抛，不返回「可能对」的结果。
    /// </summary>
    public static PakInfo Read(Stream stream)
    {
        if (stream.Length < 44)
            throw new PakException($"文件只有 {stream.Length} 字节，不可能是 pak");

        var failures = new List<string>();
        foreach (var (version, slots) in Candidates())
        {
            var size = FooterSizeFor(version, slots);
            if (stream.Length < size)
            {
                failures.Add($"v{version}/{slots} 槽：文件只有 {stream.Length} 字节，装不下 {size} 字节尾部");
                continue;
            }

            stream.Seek(stream.Length - size, SeekOrigin.Begin);
            var buffer = new byte[size];
            ReadFully(stream, buffer, 0, size);

            try
            {
                return Parse(buffer, version, slots, stream.Length);
            }
            catch (PakException e)
            {
                failures.Add($"v{version}/{slots} 槽 @ {stream.Length - size}：{e.Message}");
            }
        }

        throw new PakException(
            "尾部解析失败：没有任何已知版本的布局匹配这个文件。逐版本尝试结果：\n  " +
            string.Join("\n  ", failures));
    }

    private static PakInfo Parse(byte[] buffer, int version, int slots, long streamLength)
    {
        var cursor = new ByteCursor(buffer, $"FPakInfo v{version}");

        var guid = version >= 7 ? cursor.ReadBytes(16) : new byte[16];
        var encrypted = version >= 4 && cursor.ReadBool();

        var magic = cursor.ReadUInt32();
        if (magic != MagicValue) throw new PakException($"magic 不是 0x{MagicValue:X8}（读到 0x{magic:X8}）");

        var versionField = cursor.ReadInt32();
        if (versionField != version) throw new PakException($"版本字段是 {versionField}，与假设的 {version} 不符");

        var indexOffset = cursor.ReadInt64();
        var indexSize = cursor.ReadInt64();
        var indexHash = cursor.ReadBytes(20);

        if (version == 9) cursor.ReadByte(); // bIndexIsFrozen，只在 v9 出现

        var slotNames = new string[slots + 1];
        slotNames[0] = "None";
        if (slots > 0)
        {
            for (var i = 0; i < slots; i++)
            {
                var raw = cursor.ReadBytes(CompressionNameLength);
                slotNames[i + 1] = Encoding.ASCII.GetString(raw).TrimEnd('\0');
            }
        }
        else
        {
            // v8 之前没有名字字段，UE 硬编码这张表（repak footer.rs 的 push 顺序）
            slotNames = ["None", "Zlib", "Gzip", "Oodle"];
        }

        if (cursor.Remaining != 0)
            throw new PakException($"尾部解析后残留 {cursor.Remaining} 字节，布局假设不成立");

        if (indexOffset < 0 || indexSize <= 0 || indexOffset + indexSize > streamLength)
            throw new PakException($"索引范围非法：offset={indexOffset} size={indexSize} 文件长={streamLength}");

        var compact = slotNames.Where((n, i) => i == 0 || n.Length > 0).ToList();

        return new PakInfo
        {
            Magic = magic,
            Version = version,
            IndexOffset = indexOffset,
            IndexSize = indexSize,
            IndexHash = indexHash,
            EncryptedIndex = encrypted,
            EncryptionKeyGuid = guid,
            CompressionMethods = compact,
            CompressionSlots = slotNames,
            FooterOffset = streamLength - buffer.Length,
            FooterSize = buffer.Length,
        };
    }

    /// <summary>按版本从高到低列出候选布局。v8 有两代（4 槽 / 5 槽），其余固定。</summary>
    private static IEnumerable<(int Version, int Slots)> Candidates()
    {
        for (var version = 15; version >= 1; version--)
        {
            if (version < 8)
            {
                yield return (version, 0);
                continue;
            }

            yield return (version, MaxCompressionMethods);
            if (version == 8) yield return (version, 4);
        }
    }

    /// <summary>某个版本的尾部长度：magic 之前的字段 + 定长压缩方法名。</summary>
    internal static int FooterSizeFor(int version, int slots) =>
        (version >= 7 ? 16 : 0)  // FGuid EncryptionKeyGuid
        + (version >= 4 ? 1 : 0) // bEncryptedIndex
        + 4                      // Magic
        + 4                      // Version
        + 8                      // IndexOffset
        + 8                      // IndexSize
        + 20                     // IndexHash (SHA-1)
        + (version == 9 ? 1 : 0) // bIndexIsFrozen
        + slots * CompressionNameLength;

    internal static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0) throw new PakException($"读流时提前结束（已读 {read}/{count}）");
            read += n;
        }
    }
}
