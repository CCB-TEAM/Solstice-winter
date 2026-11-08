# PakArchive — 挂载与提取

**职责一句话**：把 pak 文件挂成「虚拟路径 → `PakFile`」的字典，并按条目读/解密/解压出字节。

**对外入口**：`PakArchive.Open(string, PakOpenOptions?)`、`Read(PakFile)`、`Read(string)`、`Find(string)`、`Extract(PakFile, string)`、`Files`、`Info`、`MountPoint`、`EntryCount`、`TotalUncompressedSize`、`Warnings`、`Dispose()`；配套 `PakOpenOptions`（`AesKeyHex` / `OodleLibraryPath`）。

**内部结构**：

```text
Open
 └─ PakInfo.Read                     ← 尾部
 └─ Mount
     ├─ ReadAt + DecryptIndex        ← 主索引（可能加密）
     ├─ SHA-1 自检                    ← footer.IndexHash 是明文索引的 SHA-1
     ├─ MountPathHashIndex (v10+)     ← 4 字节 bool、编码条目池、目录索引
     └─ MountLegacyIndex   (v1..v9)   ← 挂载点 + (名字, 条目) 直表
Read
 ├─ IsCompressed == false → ReadRaw(Offset + StructSize, CompressedSize)
 └─ IsCompressed == true  → 逐块 ReadRaw → Oodle / Zlib / Gzip
```

**依赖**：`PakInfo`、`PakFile`、`ByteCursor`、`AesEcb`、`Oodle`、`System.IO.Compression`、`SHA1`。

**取舍**：

- **加密块不做「起点 16 字节对齐」**。UE 是**从块起点开始**对块做 AES-ECB、再把块尾补到 16 字节；块起点本身不必对齐。实测 `BP_Logic.uasset` 的块起点 `989803980 mod 16 = 12`，按「对齐起点 + 切片」的写法（原实现）解出垃圾，改成「从块起点读 `align16(length)` 字节 → 整段解密 → 取前 `length` 字节」后逐字节正确。
- **索引里两个 `bool` 按 4 字节读**。UE 的 `FArchive` 默认把 `bool` 序列化成 int32，`repak` 也是按 u32 读的；按 1 字节读会让后续所有字段整体错位 3 字节（实测立刻崩）。
- **「刚好读完」的判据放宽到「残留 < 16 字节」**。主索引实测残留 14 字节（内容正好是挂载点字符串的副本），目录索引残留 1 字节（`0xE2`）——都不是结构字段，而是加密索引按 AES 块对齐写盘的尾部填充。要求残留为 0 会把健康 pak 判成坏 pak；改成「≥16 字节才告警」既能兜住填充，又能在布局真错时报警。否决了「把残留当挂载点副本硬吃掉」——那只是对这一个 pak 成立。
- **`Find` 支持路径后缀且要求唯一**。CLI 的用法是 `pak extract <pak> BP_Logic.uexp`，让人不必记住 `../../../kards/Content/...` 前缀；命中多个就抛并列候选，避免「随便挑一个」的静默错误。
- **不校验目录索引的 SHA-1**：footer 里存了 `DirectoryIndexHash`，但实测只能确认主索引的 hash 是明文 SHA-1；目录索引 hash 的算法没在本机验证，先不加（加了就会在别的 pak 上误报）。这一条记在下面的边界里。
- **`Extract` 只写文件名，不还原目录树**：CLI 的 `--out` 语义是「把这一条写到这个目录」，恢复整棵 `../../../` 树会把文件写到工作区外面。批量还原留给后续命令。

---

### `PakArchive.Open(string pakPath, PakOpenOptions? options): PakArchive`

**状态**：✅ 已实现

**签名与契约**：静态工厂。以 `FileShare.ReadWrite` 打开文件（游戏可能同时占着），读尾部 → 挂索引 → 返回可用的归档。失败时自己 `Dispose` 掉文件句柄再抛，不泄漏。`options == null` 等价于「不给 key、Oodle 走默认候选」。

**输入输出**：`pakPath` 必须存在且可读（不存在 → `PakException("pak 不存在：…")`）。`options.AesKeyHex` 是 64 位十六进制串或 `null`；`options.OodleLibraryPath` 是 DLL 路径或 `null`。返回值已挂载完毕（`Files` 可查）。抛 `PakException` 覆盖：文件不存在、尾部解析失败、索引加密但没给 key、索引解密后不像索引、目录索引是 `PFDQ` 扁平格式。

**关键分支（伪代码）**：

```text
options ??= new()
if not File.Exists(pakPath): throw
stream = FileStream(Open, Read, ReadWrite)
try:
    info = PakInfo.Read(stream)
    archive = new PakArchive(stream, info)
    archive.SetAesKey(options.AesKeyHex)
    archive.Mount(options)
    return archive
catch: stream.Dispose(); throw
```

**边界与失败**：失败后不残留句柄、不留半挂载状态（对象没返回出去）。同一个 pak 可以重复 `Open`（各自持有句柄）。超大 pak（>2GB）没问题：索引长度 `checked((int))` 会抛 `OverflowException`——实际索引不会到 2GB，见边界清单。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program` 的 `info/list/find/extract/verify/compare` 调用；调用 `PakInfo.Read`、`SetAesKey`、`Mount`。

---

### `PakArchive.Mount(PakOpenOptions options): void`

**状态**：✅ 已实现

**签名与契约**：私有。读主索引字节、按需解密、做 SHA-1 自检、按版本分派到 path-hash 或传统索引解析。副作用：填 `MountPoint`、`Files`、`Warnings`。

**输入输出**：`Info` 必须已就绪。索引密文从 `Info.IndexOffset` 读 `Info.IndexSize` 字节；加密时先 AES-ECB 解密。SHA-1 与 `Info.IndexHash` 不一致只记 `Warnings`，不抛（key 错会在下一步 `ReadMountPoint` 抛，消息更准）。

**关键分支（伪代码）**：

```text
raw = ReadAt(IndexOffset, IndexSize)
indexBytes = EncryptedIndex ? DecryptIndex(raw, "主索引") : raw
if SHA1(indexBytes) != IndexHash: Warnings.Add("主索引 SHA-1 不一致…")
if Version >= 10: MountPathHashIndex(indexBytes, options)
else:             MountLegacyIndex(indexBytes, options)
```

**边界与失败**：索引范围越界由 `ReadAt` 抛；解密长度不是 16 的倍数由 `AesEcb.Decrypt` 抛（实测本 pak 索引 633072 = 16×39567，整除）。

**调用者与被调用者**：被 `PakArchive.Open` 调用；调用 `ReadAt`、`DecryptIndex`、`MountPathHashIndex`、`MountLegacyIndex`。

---

### `PakArchive.ReadMountPoint(ByteCursor cur, string what): string`

**状态**：✅ 已实现

**签名与契约**：私有静态。读挂载点 FString，并先用长度/字符集做「像不像索引」的判断。`what` 只用于错误消息（`"主索引"` / `"传统索引"`）。

**输入输出**：长度字段必须在 `(0, 4096]` 且不超过剩余字节；读出的字符串必须全是可打印 ASCII（`0x20..0x7E`）。否则抛 `PakException` 并把嫌疑指向 AES key。

**关键分支（伪代码）**：

```text
if remaining < 4: throw "连长度都读不出来，key 很可能不对"
declared = peekI32()
if declared <= 0 or declared > 4096 or declared > remaining-4: throw "挂载点长度 N 不可能是合法索引"
mountPoint = readFString()
if 含不可打印字符: throw "挂载点含不可打印字符"
return mountPoint
```

**边界与失败**：⛔ 挂载点理论上可以是 UTF-16（负长度）或含非 ASCII 字符，这里会判为「key 不对」——本机所有 pak 的挂载点都是 ASCII 相对路径，先按这个收紧，换到别的游戏要放宽这条。实测：错 key 时消息是「解密后的挂载点长度是 -689425151，不可能是合法索引——AES key 不对」，而不是原来的「需要 1378850302 字节」。

**调用者与被调用者**：被 `MountPathHashIndex`、`MountLegacyIndex` 调用；调用 `ByteCursor.PeekInt32`、`ReadFString`。

---

### `PakArchive.MountPathHashIndex(byte[] indexBytes, PakOpenOptions options): void`

**状态**：✅ 已实现

**签名与契约**：私有。解析 v10+ 主索引 + 目录索引 + 编码条目池，拼出全部虚拟路径。副作用：写 `MountPoint`、`Files`、`Warnings`。

**输入输出**：主索引明文布局（实测 `kards-Android_ASTC.pak`，逐字段偏移）：

| 字段 | 宽度 | 实测值 |
|---|---|---|
| 挂载点 FString | 4 + 10 | `"../../../"` |
| 文件数 int32 | 4 | 33808 |
| PathHashSeed uint64 | 8 | 0x7BA53B01 |
| 有 path hash index（**4 字节** bool） | 4 | 1 |
| pathHashIndex 偏移/长度/hash | 8+8+20 | 只跳过，不用 |
| 有目录索引（**4 字节** bool） | 4 | 1 |
| 目录索引偏移/长度/hash | 8+8+20 | 偏移 1168963934、长度 1230512 |
| 编码条目池长度 int32 + 池 | 4 + N | 632944 |
| 非编码条目数 int32 + 条目数组 | 4 + M | 0 |

目录索引：`int32 目录数`（1762），然后 `(FString 目录, int32 文件数, (FString 文件名, int32 定位)[])`；定位 `>= 0` 指向池内偏移，`< 0` 是 `-定位-1` 指向非编码条目，`int.MinValue` 是删除记录。挂载点以 `/` 结尾且目录以 `/` 开头时去掉目录的首个 `/`。

**关键分支（伪代码）**：

```text
MountPoint = ReadMountPoint(cur, "主索引"); 声明数 = readI32(); skip(8)
if not readU32Bool(): throw "没有 path hash index"
skip(36)
if not readU32Bool(): throw "没有 full directory index"
dirOffset, dirSize = readI64(), readI64(); skip(20)
池长 = readI32(); 池 = readBytes(池长); 非编码数 = readI32(); 逐个 ReadLegacy()
CheckResidual(主索引)
dirBytes = 解密(ReadAt(dirOffset, dirSize))
if dirBytes 开头是 'PFDQ': throw "扁平目录索引不支持"
for 每个目录:
    trim = MountPoint 以 '/' 结尾 and 目录以 '/' 开头
    for 每个文件:
        loc = readI32()
        if loc == int.MinValue: continue                    # 删除记录
        path = MountPoint + (trim ? 目录[1..] : 目录) + 文件名
        file = loc >= 0 ? ReadEncoded(池, loc, path) : 非编码[-loc-1]
        Files[path] = file
CheckResidual(目录索引)
if 声明数 != Files.Count: Warnings.Add(...)
```

**边界与失败**：`encodedSize` 超出剩余、`nonEncodedCount` 超 `1<<22`、`dirCount` 超 `1<<22`、单目录文件数超 `1<<24` 都直接抛（防 `OutOfMemory`）。非编码定位越界只记 `Warning` 并跳过那一条（一条坏记录不该让整个 pak 打不开）。⛔ 不支持：没有 path hash index 或没有目录索引的 v10+ 布局、`PFDQ` 扁平目录索引。

**调用者与被调用者**：被 `Mount` 调用；调用 `ReadMountPoint`、`ByteCursor` 读取方法、`PakFile.ReadEncoded`、`PakFile.ReadLegacy`、`ReadAt`、`DecryptIndex`、`CheckResidual`。

---

### `PakArchive.MountLegacyIndex(byte[] indexBytes, PakOpenOptions options): void`

**状态**：✅ 已实现

**签名与契约**：私有。解析 v1..v9 的索引：`FString 挂载点 / int32 数量 / (FString 名字, 传统条目)[]`。副作用：写 `MountPoint`、`Files`、`Warnings`。

**输入输出**：`Deleted && UncompressedSize == 0` 的条目跳过（v6 的删除记录写法）。其余条目路径 = 挂载点 + 名字。实测本机两个 v3 小 pak（`wide-mount.pak` / `BP_AsyncLoader.pak`）各 2 条，挂载点 `../../../kards/Content/`。

**关键分支（伪代码）**：

```text
MountPoint = ReadMountPoint(cur, "传统索引")
count = readI32(); if count < 0 or count > 1<<24: throw
for i in 0..count:
    name = readFString(); file = PakFile.ReadLegacy(cur, Info, MountPoint + name)
    if file.Deleted and file.UncompressedSize == 0: continue
    Files[path] = file
CheckResidual(传统索引)
```

**边界与失败**：⛔ v9 的 frozen 索引（`bIndexIsFrozen=1`，内存镜像格式）不支持——`PakInfo` 会读出该标志位但不据此分派，遇到 frozen 索引会在 `ReadLegacy` 里读飞并被 `ByteCursor.Require` 拦下（消息里带「传统索引」和偏移）。

**调用者与被调用者**：被 `Mount` 调用；调用 `ReadMountPoint`、`ByteCursor.ReadFString`/`ReadInt32`、`PakFile.ReadLegacy`、`CheckResidual`。

---

### `PakArchive.CheckResidual(ByteCursor cur, string what, byte[] data, int position): void`

**状态**：✅ 已实现

**签名与契约**：私有。「刚好读完」自检。残留 `>= 16` 字节记 `Warnings`，`< 16` 视为 AES 对齐填充放过。

**输入输出**：`remaining = data.Length - position`。`remaining <= 0` 直接返回；`0 < remaining < 16` 返回（静默）；否则告警并附前 32 字节十六进制。实测主索引残留 14 字节、目录索引残留 1 字节，都不告警。

**关键分支（伪代码）**：

```text
remaining = data.Length - position
if remaining <= 0: return
if remaining < 16: return            # AES 块对齐填充的上界
Warnings.Add($"{what}解析后残留 {remaining} 字节…: hex")
```

**边界与失败**：不抛。判据是「上界」而不是「精确匹配」——代价是 16 字节以内的结构性错误会漏报，收益是健康 pak 不出假警告。

**调用者与被调用者**：被 `MountPathHashIndex`、`MountLegacyIndex` 调用；不调别的函数。
