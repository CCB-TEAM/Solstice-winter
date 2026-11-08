using System.Security.Cryptography;

namespace SolsticeWinter.Pak;

/// <summary>
/// pak 用的 AES 解密。UE 的 pak 加密是 <b>AES-256-ECB</b>（每个 16 字节块独立），
/// 所以任意 16 字节对齐的位置都能单独解，不需要知道流的起点。
/// </summary>
public static class AesEcb
{
    public const int BlockSize = 16;

    /// <summary>把 32 字节密钥从十六进制串解出来。接受 <c>0x</c> 前缀与大小写混写。</summary>
    public static byte[] ParseKey(string hex)
    {
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != 64)
            throw new PakException($"AES key 必须是 32 字节（64 个十六进制字符），给的是 {s.Length} 个字符");
        try
        {
            return Convert.FromHexString(s);
        }
        catch (FormatException e)
        {
            throw new PakException($"AES key 不是合法十六进制：{e.Message}");
        }
    }

    /// <summary>原地解密（长度必须是 16 的倍数）。</summary>
    public static byte[] Decrypt(byte[] data, byte[] key, int offset, int count)
    {
        if (count % BlockSize != 0)
            throw new PakException($"AES 解密长度 {count} 不是 16 的倍数（偏移 {offset}）");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        var output = new byte[count];
        var written = decryptor.TransformBlock(data, offset, count, output, 0);
        if (written != count)
            throw new PakException($"AES 解密产出 {written} 字节，期望 {count}");
        return output;
    }

    public static long Align(long value) => (value + BlockSize - 1) / BlockSize * BlockSize;
}
