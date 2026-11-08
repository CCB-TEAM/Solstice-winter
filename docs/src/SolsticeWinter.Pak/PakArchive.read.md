# PakArchive.read — 查找、读取、提取

本文件是 [PakArchive](PakArchive.md) 的续篇：模块职责、对外入口、内部结构、依赖与**取舍**见 [PakArchive.md](PakArchive.md)，
这里只放「查条目 / 读字节 / 写盘」这一组函数。

**对外入口**：`Find(string)`、`Read(string)`、`Read(PakFile)`、`Extract(PakFile, string)`、`TotalUncompressedSize`。

**内部结构**：

```text
Find ────────────────→ PakFile（精确路径或唯一后缀）
Read(PakFile)
 ├─ IsCompressed == false → ReadRaw(Offset + StructSize, CompressedSize)
 └─ IsCompressed == true  → 逐块 ReadRaw → Oodle / Zlib / Gzip（Inflate）
Extract ─ Read(PakFile) → 写到 outputDirectory/<文件名>
```

**依赖**：`PakFile`、`Oodle`、`AesEcb`、`System.IO.Compression`。

**取舍**：见 [PakArchive.md](PakArchive.md) 的「取舍」——加密块不对齐起点、`Find` 要求后缀唯一、`Extract` 不还原目录树。

---
### `PakArchive.Find(string pathOrSuffix): PakFile`

**状态**：✅ 已实现

**签名与契约**：先按完整路径精确查（大小写不敏感），再按「路径后缀」匹配。后缀不唯一时抛并列候选。

**输入输出**：`pathOrSuffix` 允许用 `\` 或 `/`、允许带前导 `/`（都会被规范化）。命中 0 个 → `PakException("pak 里没有路径以 X 结尾的条目（共 N 条）")`；命中 >1 个（最多列 8 个）→ 抛并列候选。返回的是 `Files` 里的同一个对象（不是副本）。

**关键分支（伪代码）**：

```text
if Files.TryGetValue(pathOrSuffix): return it
normalized = 反斜杠转正斜杠 + 去前导 '/'
matches = Files.Keys.Where(k => k 以 normalized 结尾, 忽略大小写).Take(8)
if matches.Count == 1: return Files[matches[0]]
if matches.Count == 0: throw "没有这个后缀"
throw "命中 N 个，请给更长的路径" + 候选列表
```

**边界与失败**：⛔ 后缀匹配是「字符串后缀」，`BP_Logic.uexp` 也会命中 `I_BP_Logic_Receiver.uexp`？——不会，因为 `EndsWith("BP_Logic.uexp")` 对 `I_BP_Logic_Receiver.uexp` 为假；但 `Logic.uexp` 这种短后缀可能命中多条，此时按设计抛候选列表。不写盘。

**调用者与被调用者**：被 `Read(string)` 与 `SolsticeWinter.Cli.Program`（`find`/`extract`/`compare`）调用；只读 `Files`。

---

### `PakArchive.Read(string pathOrSuffix): byte[]`

**状态**：✅ 已实现

**签名与契约**：`Find` + `Read(PakFile)` 的便捷重载。抛错行为与两者一致。

**输入输出**：返回解压后的完整字节数组（长度 == `UncompressedSize`）。

**关键分支（伪代码）**：

```text
return Read(Find(pathOrSuffix))
```

**边界与失败**：同 `Find` + `Read(PakFile)`。

**调用者与被调用者**：目前无内部调用者（对外 API）；调用 `Find`、`Read(PakFile)`。

---

### `PakArchive.Read(PakFile file): byte[]`

**状态**：✅ 已实现

**签名与契约**：把条目读成完整字节。未压缩条目原样读 `CompressedSize` 字节；压缩条目逐块读、逐块解压。副作用：Oodle 解压长度不符时记 `Warnings`；可能触发 `Oodle.EnsureLoaded`。

**输入输出**：返回长度恒等于 `file.UncompressedSize`（否则抛）。压缩方法支持 `None` / `Oodle` / `Zlib` / `Gzip`；`LZ4` / `Zstd` 及其它 → 抛并写明「本实现只支持 …」。块大小：单块条目取 `UncompressedSize`，多块取 `BlockSize`。

**关键分支（伪代码）**：

```text
if file.Deleted: throw
if UncompressedSize > int.MaxValue: throw "超过 2GB"
if not IsCompressed: return ReadRaw(Offset+StructSize, CompressedSize, Encrypted)
chunk = Blocks.Length == 1 ? UncompressedSize : BlockSize
if chunk <= 0: throw
for 每块:
    raw = ReadRaw(块起点, 块长, Encrypted)
    outLen = min(chunk, UncompressedSize - 已写)
    Oodle  → Oodle.Decompress(raw, outLen)；返回长度不符只告警
    Zlib   → Inflate(zlib: true)
    Gzip   → Inflate(zlib: false)
    其它   → throw "用 X 压缩，本实现只支持 None/Oodle/Zlib/Gzip"
    已写 += outLen
if 已写 != UncompressedSize: throw
```

**边界与失败**：⛔ 单条 >2GB 不支持（`byte[]` 上限）；⛔ 不支持 LZ4/Zstd 压缩。解压失败不会留下半成品（返回前就抛）。可重复调用。

**调用者与被调用者**：被 `Read(string)`、`Extract`、`SolsticeWinter.Cli.Program`（`verify`/`compare`）调用；调用 `ReadRaw`、`Info.MethodName`、`Oodle.Decompress`、`Inflate`。

---

### `PakArchive.Extract(PakFile file, string outputDirectory): string`

**状态**：✅ 已实现

**签名与契约**：读出条目字节并写到 `outputDirectory/<文件名>`。副作用：`Directory.CreateDirectory` + `File.WriteAllBytes`（同名文件直接覆盖）。

**输入输出**：返回写出的**绝对**路径（`Path.Combine` 结果）。文件名取虚拟路径最后一段（`Path` 里的 `\` 先归一成 `/`）。目录不存在则创建。

**关键分支（伪代码）**：

```text
data = Read(file)
name = 虚拟路径最后一个 '/' 之后的部分
Directory.CreateDirectory(outputDirectory)
target = Path.Combine(outputDirectory, name)
File.WriteAllBytes(target, data)
return target
```

**边界与失败**：⛔ 同名不同目录的条目会互相覆盖（只取文件名）；⛔ 不还原目录树（见取舍）。写盘失败（权限/磁盘满）会抛 `IOException`，由 CLI 统一捕获成退出码 1。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program` 的 `extract` 调用；调用 `Read(PakFile)`。

---

### `PakArchive.Inflate(byte[] compressed, byte[] destination, int destinationOffset, int expected, bool zlib): void`

**状态**：✅ 已实现

**签名与契约**：私有静态。把一个 zlib/gzip 块解到目标缓冲的指定偏移，**期望正好 `expected` 字节**。

**输入输出**：`destination` 必须有 `destinationOffset + expected` 的空间。`zlib=true` 用 `ZLibStream`，`false` 用 `GZipStream`。解出字节数不等于 `expected` → 抛。

**关键分支（伪代码）**：

```text
decoder = zlib ? ZLibStream(compressed) : GZipStream(compressed)
while written < expected:
    n = decoder.Read(destination, offset+written, expected-written)
    if n <= 0: break
    written += n
if written != expected: throw "解出 written 字节，期望 expected"
```

**边界与失败**：压缩数据损坏 → 解出字节数不足 → 抛。⛔ 只支持单块整解，不支持「块内再分片」。实测本 pak 全是 Oodle，Zlib/Gzip 分支没有真实样本（见 INDEX 的未实现清单）。

**调用者与被调用者**：被 `Read(PakFile)` 调用；只调 `System.IO.Compression` 的解码器。

---

### `PakArchive.ReadRaw(long offset, int length, bool encrypted): byte[]`

**状态**：✅ 已实现

**签名与契约**：私有。读一段原始字节；加密时按「从 `offset` 起读 `align16(length)` 字节 → 整段 AES-ECB 解密 → 取前 `length` 字节」处理。

**输入输出**：`offset >= 0`、`length >= 0`、`offset + align16(length) <= 文件长`（由 `ReadAt` 保证，越界抛）。`encrypted == false` 时就是 `ReadAt(offset, length)`。

**关键分支（伪代码）**：

```text
if length < 0: throw
if not encrypted: return ReadAt(offset, length)
alignedLength = Align(length, 16)
plain = AesEcb.Decrypt(ReadAt(offset, alignedLength), key)
return length == alignedLength ? plain : plain[0..length]
```

**边界与失败**：⛔ 块起点不做 16 字节对齐（刻意，见取舍）；如果某个 pak 真的按「绝对对齐」写加密块，这里会解出垃圾——判据是 `compare` 不通过。没给 key 而条目加密 → 抛「条目加密但没给 AES key」。

**调用者与被调用者**：被 `Read(PakFile)` 调用；调用 `ReadAt`、`AesEcb.Align`、`AesEcb.ParseKey`、`AesEcb.Decrypt`。

---

### `PakArchive.DecryptIndex(byte[] data, string what): byte[]`

**状态**：✅ 已实现

**签名与契约**：私有。解密一段索引（主索引或目录索引），把「没给 key」的失败提前到带上下文的错误消息里。

**输入输出**：`_aesKeyHex` 为空 → 抛「这个 pak 的索引是加密的，但没有给 AES key（PakOpenOptions.AesKeyHex）：{what}无法解密」。否则返回解密后的字节数组（长度不变）。

**关键分支（伪代码）**：

```text
if _aesKeyHex 为空: throw "没有给 AES key：{what}无法解密"
return AesEcb.Decrypt(data, ParseKey(_aesKeyHex), 0, data.Length)
```

**边界与失败**：`data.Length % 16 != 0` → `AesEcb.Decrypt` 抛。key 格式非法 → `AesEcb.ParseKey` 抛。

**调用者与被调用者**：被 `Mount`、`MountPathHashIndex` 调用；调用 `AesEcb.ParseKey`、`AesEcb.Decrypt`。

---

### `PakArchive.SetAesKey(string? key): void`

**状态**：✅ 已实现

**签名与契约**：`internal`。存下 key 十六进制串（不校验格式，校验发生在真正使用时）。副作用：写 `_aesKeyHex`。

**输入输出**：`key` 为 `null`/空串表示「没有 key」。

**关键分支（伪代码）**：

```text
_aesKeyHex = key
```

**边界与失败**：不失败。key 只存在内存里，不落盘、不写日志。

**调用者与被调用者**：被 `PakArchive.Open` 调用；不调别的函数。

---

### `PakArchive.ReadAt(long offset, int length): byte[]`

**状态**：✅ 已实现

**签名与契约**：私有。从文件绝对偏移读 `length` 字节，越界立即抛。副作用：移动 `_stream.Position`（非线程安全）。

**输入输出**：`offset >= 0`、`length >= 0`、`offset + length <= 文件长`，否则抛「读越界：offset=… length=… 文件长=…」。返回新数组。

**关键分支（伪代码）**：

```text
if offset < 0 or length < 0 or offset+length > _stream.Length: throw
buffer = new byte[length]
_stream.Seek(offset); PakInfo.ReadFully(_stream, buffer, 0, length)
return buffer
```

**边界与失败**：⛔ 非线程安全（共享一个 `FileStream` 游标）；并发读同一归档要各自 `Open`。

**调用者与被调用者**：被 `Mount`、`MountPathHashIndex`、`ReadRaw` 调用；调用 `Stream.Seek`、`PakInfo.ReadFully`。

---

### `PakArchive.Dispose(): void`

**状态**：✅ 已实现

**签名与契约**：释放底层 `FileStream`。之后任何 `Read` 都会抛 `ObjectDisposedException`。

**输入输出**：无返回值。重复 `Dispose` 安全（`FileStream.Dispose` 幂等）。

**关键分支（伪代码）**：

```text
_stream.Dispose()
```

**边界与失败**：不失败。

**调用者与被调用者**：被 `using` 语句（CLI 的每个 `pak` 子命令）调用；不调别的函数。

---

### `PakArchive.TotalUncompressedSize: long`

**状态**：✅ 已实现

**签名与契约**：只读派生属性，`Files.Values.Sum(f => f.UncompressedSize)`。**不解压**，纯元数据求和。

**输入输出**：返回全部条目声明长度之和。实测本 pak：`2,256,615,510` 字节。

**关键分支（伪代码）**：

```text
return sum(每个条目的 UncompressedSize)
```

**边界与失败**：不失败（`long` 求和，33808 条 × 最大 2GB 不会溢出）。

**调用者与被调用者**：目前无 CLI 调用者（对外 API，用于报告/统计）；只读 `Files`。
