namespace SolsticeWinter.Pak;

/// <summary>
/// pak 里的一个文件条目（UE 的 <c>FPakEntry</c>）。
///
/// <para>
/// 两种序列化形态：
/// <b>编码条目</b>（v10+ 的 <c>EncodedPakEntries</c>，一个 uint32 位域 + 变长字段）和
/// <b>传统条目</b>（<c>NonEncodedEntries</c> / v9 及以前的索引，固定字段）。两者都解析到同一个结构，
/// 上层只认这里。
/// </para>
/// </summary>
public sealed class PakFile
{
    /// <summary>条目头在数据区前置的字节数（读数据时要跳过）。压缩条目还要加 <c>4 + 块数*16</c>。</summary>
    public const int FixedStructSize = 3 * 8 + 4 + 20 + 1 + 4; // 53

    /// <summary>挂载点拼好的完整虚拟路径，例如 <c>../../../Kards/Content/Blueprints/Logic/BP_Logic.uasset</c>。</summary>
    public required string Path { get; set; }

    /// <summary>条目起点。真实数据在 <c>Offset + StructSize</c>（条目自身会再前置一份）。</summary>
    public required long Offset { get; init; }

    public required long CompressedSize { get; init; }
    public required long UncompressedSize { get; init; }

    /// <summary>压缩方法下标，0 = 不压缩。下标含义见 <see cref="PakInfo.CompressionSlots"/>。</summary>
    public required int CompressionMethodIndex { get; init; }

    public required bool Encrypted { get; init; }
    public required bool Deleted { get; init; }

    /// <summary>每个压缩块解压后的目标大小（最后一块可能更小）。只有一个块时按 UE 规则取 <see cref="UncompressedSize"/>。</summary>
    public required uint BlockSize { get; init; }

    /// <summary>压缩块在文件里的绝对区间，按顺序。</summary>
    public required (long Start, long End)[] Blocks { get; init; }

    /// <summary>条目头在数据区前置的字节数（读数据时要跳过）。</summary>
    public required int StructSize { get; init; }

    /// <summary>
    /// 是否走解压路径。判据是「有压缩方法 <b>且</b> 有压缩块」——
    /// 不能只看 <see cref="BlockSize"/>：单块条目的块大小字段可能是 0（实测 <c>0xE0800040</c> 这类位域）。
    /// </summary>
    public bool IsCompressed => CompressionMethodIndex != 0 && Blocks.Length > 0;

    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    public override string ToString() =>
        $"{Path}  raw={UncompressedSize}  stored={CompressedSize}  m={CompressionMethodIndex}" +
        (Encrypted ? "  [enc]" : string.Empty) + (Deleted ? "  [deleted]" : string.Empty);

    /// <summary>
    /// 解析 v10+ 的编码条目。位域含义（UE <c>FPakFile::DecodePakEntry</c>）：
    /// <code>
    /// bit 0..5   BlockSize / 2048（0x3f 表示后面另给 uint32）
    /// bit 6..21  压缩块数量
    /// bit 22     加密标志
    /// bit 23..28 压缩方法下标
    /// bit 29     CompressedSize 是否 32 位
    /// bit 30     UncompressedSize 是否 32 位
    /// bit 31     Offset 是否 32 位
    /// </code>
    /// 字段顺序：位域 → [块大小 uint32] → Offset → UncompressedSize → [CompressedSize] → 每块长度。
    /// <b>只有一块且未加密时，块长度不写进索引</b>（等于 CompressedSize）；这一点实测成立：
    /// 池里第 0 条正好占 16 字节，下一条从 +16 开始。
    /// </summary>
    internal static PakFile ReadEncoded(ByteCursor entries, int position, PakInfo info, string path)
    {
        if (position < 0 || position >= entries.Length)
            throw new PakException($"{path}: 编码条目定位 {position} 超出池范围（池长 {entries.Length}）");

        entries.Position = position;
        var bitfield = entries.ReadUInt32();

        var blockCount = (int)((bitfield >> 6) & 0xFFFF);
        var methodIndex = (int)((bitfield >> 23) & 0x3F);
        var encrypted = (bitfield & (1u << 22)) != 0;

        var rawBlockSize = (bitfield & 0x3F) == 0x3F ? entries.ReadUInt32() : (bitfield & 0x3F) << 11;

        var offset = (bitfield & (1u << 31)) != 0 ? entries.ReadUInt32() : (ulong)entries.ReadInt64();
        var uncompressed = (bitfield & (1u << 30)) != 0 ? entries.ReadUInt32() : (ulong)entries.ReadInt64();
        var compressed = methodIndex != 0
            ? ((bitfield & (1u << 29)) != 0 ? entries.ReadUInt32() : (ulong)entries.ReadInt64())
            : uncompressed;

        // 前置在数据前面的条目头长度：索引里省掉的块长度在磁盘上仍然存在
        var structSize = FixedStructSize + (methodIndex != 0 ? 4 + blockCount * 16 : 0);

        var blocks = Array.Empty<(long, long)>();
        if (blockCount == 1 && !encrypted)
        {
            // 单块未加密：长度省略，直接是整段 CompressedSize
            var start = (long)offset + structSize;
            blocks = [(start, start + (long)compressed)];
        }
        else if (blockCount > 0)
        {
            blocks = new (long, long)[blockCount];
            var start = (long)offset + structSize;
            var alignment = encrypted ? AesEcb.BlockSize : 1;
            for (var i = 0; i < blockCount; i++)
            {
                var length = entries.ReadUInt32();
                blocks[i] = (start, start + length);
                start += Align(length, alignment);
            }
        }

        // UE：单块时块大小直接取解压后大小，位域里的值不可靠
        var blockSize = blockCount switch
        {
            1 => (uint)Math.Min(uncompressed, uint.MaxValue),
            > 1 => rawBlockSize,
            _ => 0u,
        };

        return new PakFile
        {
            Path = path,
            Offset = (long)offset,
            CompressedSize = (long)compressed,
            UncompressedSize = (long)uncompressed,
            CompressionMethodIndex = methodIndex,
            Encrypted = encrypted,
            Deleted = false,
            BlockSize = blockSize,
            Blocks = blocks,
            StructSize = structSize,
        };
    }

    /// <summary>
    /// 解析传统条目（<c>NonEncodedEntries</c> 与 v9 及以前的索引，无文件名）。
    /// 顺序：Offset, Compressed, Uncompressed, [v1 时间戳], 方法下标, Hash(20), [压缩块], Flags, BlockSize。
    /// v3 起才有块/Flags/BlockSize；v5+ 的块偏移<b>相对条目起点</b>，要加上 <c>Offset</c>。
    /// </summary>
    internal static PakFile ReadLegacy(ByteCursor cursor, PakInfo info, string path)
    {
        var offset = cursor.ReadInt64();
        var compressed = cursor.ReadInt64();
        var uncompressed = cursor.ReadInt64();

        // v8 的方法下标是 1 字节（repak entry.rs: Version::V8A => U8），其余是 int32
        var methodIndex = info.Version == 8 ? cursor.ReadByte() : cursor.ReadInt32();
        if (info.Version < 2) cursor.Skip(8); // 时间戳，只在 v1 存在
        cursor.Skip(20);                      // SHA-1

        var blocks = Array.Empty<(long, long)>();
        var flags = 0;
        var blockSize = 0u;
        if (info.Version >= 3)
        {
            if (methodIndex != 0)
            {
                var count = cursor.ReadInt32();
                if (count < 0 || count > 1 << 20)
                    throw new PakException($"{path}: 传统条目的压缩块数量 {count} 不合理");
                blocks = new (long, long)[count];
                for (var i = 0; i < count; i++)
                {
                    var start = cursor.ReadInt64();
                    var end = cursor.ReadInt64();
                    blocks[i] = (start, end);
                }
            }

            flags = cursor.ReadByte();
            blockSize = cursor.ReadUInt32();
        }

        if (info.Version >= 5)
        {
            for (var i = 0; i < blocks.Length; i++)
                blocks[i] = (blocks[i].Item1 + offset, blocks[i].Item2 + offset);
        }

        if (blocks.Length == 1) blockSize = (uint)Math.Min(uncompressed, uint.MaxValue);

        var structSize = FixedStructSize + (methodIndex != 0 ? 4 + blocks.Length * 16 : 0);

        return new PakFile
        {
            Path = path,
            Offset = offset,
            CompressedSize = compressed,
            UncompressedSize = uncompressed,
            CompressionMethodIndex = methodIndex,
            Encrypted = (flags & 1) != 0,
            Deleted = (flags & 2) != 0,
            BlockSize = blockSize,
            Blocks = blocks,
            StructSize = structSize,
        };
    }

    internal static long Align(long value, long alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
