using System.Text;

namespace SolsticeWinter.Pak;

/// <summary>pak 解析失败的统一异常。消息里必须写清「哪一步、读到什么」，不允许静默兜底。</summary>
public sealed class PakException(string message) : Exception(message);

/// <summary>小端字节游标：所有 index 结构都按它读，越界立即抛。</summary>
internal sealed class ByteCursor(byte[] data, string what)
{
    public byte[] Data { get; } = data;
    public int Position { get; set; }
    public int Length => Data.Length;
    public int Remaining => Data.Length - Position;

    public void Require(int n)
    {
        if (n < 0 || Remaining < n)
            throw new PakException($"{what}: 需要 {n} 字节，只剩 {Remaining}（偏移 {Position}/{Length}）");
    }

    public byte ReadByte()
    {
        Require(1);
        return Data[Position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    /// <summary>
    /// UE 的 <c>bool</c> 在 FArchive 里默认按 <b>int32</b> 序列化（不是 1 字节）。
    /// v10+ 主索引里的 <c>bHasPathHashIndex</c> / <c>bHasFullDirectoryIndex</c> 都是这种 4 字节布尔——
    /// 实测 KARDS v11 pak：按 1 字节读会让后面所有字段整体错位 3 字节。
    /// </summary>
    public bool ReadUInt32Bool() => ReadUInt32() != 0;

    public int ReadInt32()
    {
        Require(4);
        var v = BitConverter.ToInt32(Data, Position);
        Position += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        Require(4);
        var v = BitConverter.ToUInt32(Data, Position);
        Position += 4;
        return v;
    }

    /// <summary>看一眼下一个 uint32，不移动游标（用于识别扁平目录索引 magic）。</summary>
    public uint PeekUInt32()
    {
        Require(4);
        return BitConverter.ToUInt32(Data, Position);
    }

    /// <summary>看一眼下一个 int32，不移动游标。</summary>
    public int PeekInt32()
    {
        Require(4);
        return BitConverter.ToInt32(Data, Position);
    }

    public long ReadInt64()
    {
        Require(8);
        var v = BitConverter.ToInt64(Data, Position);
        Position += 8;
        return v;
    }

    public byte[] ReadBytes(int n)
    {
        Require(n);
        var b = new byte[n];
        Buffer.BlockCopy(Data, Position, b, 0, n);
        Position += n;
        return b;
    }

    public void Skip(int n)
    {
        Require(n);
        Position += n;
    }

    /// <summary>
    /// UE 的 FString：int32 长度 + 载荷。长度 &gt; 0 是 ASCII（含结尾 \0），&lt; 0 是 UTF-16（字符数取负，含结尾 \0）。
    /// 长度 0 是空串。
    /// </summary>
    public string ReadFString()
    {
        var len = ReadInt32();
        if (len == 0) return string.Empty;
        if (len > 0)
        {
            Require(len);
            var s = Encoding.UTF8.GetString(Data, Position, len);
            Position += len;
            return s.EndsWith('\0') ? s[..^1] : s;
        }

        var chars = -len;
        Require(chars * 2);
        var str = Encoding.Unicode.GetString(Data, Position, chars * 2);
        Position += chars * 2;
        return str.EndsWith('\0') ? str[..^1] : str;
    }
}
