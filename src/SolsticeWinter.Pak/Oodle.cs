using System.Runtime.InteropServices;

namespace SolsticeWinter.Pak;

/// <summary>
/// Oodle 解压（<c>OodleLZ_Decompress</c>）。
///
/// <para>
/// Oodle 是闭源运行时，本仓库<b>不附带</b>任何 Oodle 二进制。查找顺序：
/// </para>
/// <list type="number">
///   <item>调用方显式给的路径（<see cref="PakOpenOptions.OodleLibraryPath"/>）</item>
///   <item>环境变量 <c>SOLSTICE_WINTER_OODLE</c></item>
///   <item>程序目录下的 <c>oodle-data-shared.dll</c> / <c>oo2core_*.dll</c></item>
///   <item>已知的本地工具链位置（FModel 的 <c>Output/.data</c>）</item>
/// </list>
/// <para>找不到就抛异常并写清怎么补，不静默返回压缩数据。</para>
/// </summary>
public static class Oodle
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint DecompressFn(
        nint compBuf, nint compSize, nint rawBuf, nint rawLen,
        int fuzzSafe, int checkCrc, int verbosity,
        nint decBufBase, nint decBufSize,
        nint fpCallback, nint callbackUserData,
        nint decoderMemory, nint decoderMemorySize, int threadPhase);

    private static readonly object Gate = new();
    private static DecompressFn? _decompress;
    private static nint _library;
    private static string? _loadedFrom;

    public static string? LoadedFrom => _loadedFrom;

    /// <summary>按候选路径加载 Oodle。可重复调用；成功结果会缓存，失败不缓存（下次重试并重新列出候选）。</summary>
    public static void EnsureLoaded(string? explicitPath = null)
    {
        lock (Gate)
        {
            if (_decompress is not null) return;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
            var env = Environment.GetEnvironmentVariable("SOLSTICE_WINTER_OODLE");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

            var appDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(appDir, "oodle-data-shared.dll"));
            if (Directory.Exists(appDir))
                candidates.AddRange(Directory.GetFiles(appDir, "oo2core_*win64.dll"));
            candidates.Add(@"D:\devenv\fmodel\Output\.data\oodle-data-shared.dll");

            foreach (var path in candidates.Where(File.Exists))
            {
                try
                {
                    var lib = NativeLibrary.Load(path);
                    var export = NativeLibrary.GetExport(lib, "OodleLZ_Decompress");
                    _decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(export);
                    _library = lib;
                    _loadedFrom = path;
                    return;
                }
                catch (Exception)
                {
                    // 换下一个候选；全部失败时给出完整候选列表
                }
            }

            throw new PakException(
                "找不到可用的 Oodle 运行时（OodleLZ_Decompress）。" +
                "这个 pak 的条目用 Oodle 压缩，没有它无法解压。\n" +
                "补法：把 oodle-data-shared.dll（或 oo2core_*_win64.dll）放到程序目录，" +
                "或设环境变量 SOLSTICE_WINTER_OODLE 指向它。\n候选：\n  " + string.Join("\n  ", candidates));
        }
    }

    /// <summary>
    /// 把 <paramref name="compressed"/> 里 <paramref name="compressedLength"/> 字节解到
    /// <paramref name="destination"/> 的 <paramref name="destinationOffset"/> 处，期望
    /// <paramref name="destinationLength"/> 字节。返回实际解出的字节数。
    /// </summary>
    public static int Decompress(
        byte[] compressed, int compressedLength,
        byte[] destination, int destinationOffset, int destinationLength)
    {
        EnsureLoaded();
        var fn = _decompress ?? throw new PakException("Oodle 未加载");

        var compHandle = GCHandle.Alloc(compressed, GCHandleType.Pinned);
        var dstHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var compPtr = compHandle.AddrOfPinnedObject();
            var dstPtr = dstHandle.AddrOfPinnedObject() + destinationOffset;

            var written = fn(
                compPtr, compressedLength,
                dstPtr, destinationLength,
                1, // FuzzSafe_Yes
                0, // CheckCRC_No
                0, // Verbosity_None
                0, 0,
                0, 0,
                0, 0,
                3); // Decode_ThreadPhaseAll

            if (written <= 0)
                throw new PakException($"Oodle 解压失败（输入 {compressedLength} 字节，期望 {destinationLength} 字节）");
            return (int)written;
        }
        finally
        {
            compHandle.Free();
            dstHandle.Free();
        }
    }

    /// <summary>用于诊断：确认运行时是否真的可用。</summary>
    public static string Describe()
    {
        try
        {
            EnsureLoaded();
            return $"Oodle 已加载：{_loadedFrom}";
        }
        catch (PakException e)
        {
            return e.Message;
        }
    }
}
