# PakInfo — pak 尾部（`FPakInfo`）

**职责一句话**：从 pak 文件末尾反推出尾部结构，给出索引位置/长度、是否加密、压缩方法名表。

**对外入口**：`PakInfo.Read(Stream)`、`PakInfo.MethodName(int)`、常量 `MagicValue` / `CompressionNameLength` / `MaxCompressionMethods`。

**内部结构**：

```text
Read
 ├─ Candidates()           枚举 (版本, 压缩方法槽数) 候选，从 v15 降到 v1
 ├─ FooterSizeFor()        算出该候选的尾部字节数
 ├─ 回读 len-size 处的 size 字节
 ├─ Parse()                按版本读字段，校验 magic + version + 索引范围 + 「刚好读完」
 └─ MethodName()           条目里的方法下标 → 名字
```

**依赖**：`ByteCursor`（小端读 + FString）、`PakException`。

**取舍**：

- **不用「扫末尾找 magic」**，改成「按版本枚举尾部长度并校验 magic+version」。理由：`FPakInfo` 的字段是**随版本增长**的（v4 加 `bEncryptedIndex`、v7 加 GUID、v9 加 frozen、v8 加压缩方法名），固定偏移只对某一个版本成立；扫 magic 还得额外猜「magic 前面有几个字段」。代价：最坏情况读 16 次小缓冲（本 pak 第 1 次就命中），可以忽略。
- **压缩方法名存两份**：`CompressionSlots` 按序列化下标（`[0]="None"`，`[i+1]` 是尾部第 i 槽），`CompressionMethods` 是跳过空槽后的紧凑表（给 CLI 打印）。理由：UE 允许槽位有空缺（`["Oodle","","","",""]` 之外还有别的排法），一旦有空洞，紧凑表的下标就和条目里的下标不是一回事。否决了「只留紧凑表」——那会在带空洞的 pak 上把方法名整体错位。
- **v8 两代都试**（4 槽 / 5 槽）：`repak` 用 `Version::V8A/V8B` 区分，尾部长 128 或 160；这里对 v8 两个候选都试一遍，避免为它单独引入子版本枚举。
- **尾部必须「刚好读完」**（`cursor.Remaining != 0` 直接抛）：这是发现布局假设错误最便宜的闸门。

---

### `PakInfo.Read(Stream stream): PakInfo`

**状态**：✅ 已实现

**签名与契约**：静态工厂。只读 `stream`（会 `Seek` 到末尾），不关闭、不缓存。成功返回填满的 `PakInfo`；失败抛 `PakException`（消息里带每个候选版本的失败原因）。副作用：把流的 `Position` 移到末尾附近。

**输入输出**：`stream` 必须是可 `Seek` 的只读流，长度 ≥ 44。返回值的 `FooterOffset + FooterSize == stream.Length` 恒成立，`IndexOffset + IndexSize <= stream.Length` 由本函数保证（否则抛）。文件短于 44 字节直接抛。

**关键分支（伪代码）**：

```text
if stream.Length < 44: throw
for (version, slots) in Candidates():            # v15→v1，v8 试 5 槽再试 4 槽
    size = FooterSizeFor(version, slots)
    if stream.Length < size: 记录失败, continue
    seek(len - size); 读 size 字节
    try: return Parse(buf, version, slots, len)  # 内部校验 magic/version/索引范围
    catch PakException: 记录失败, continue
throw PakException(所有候选的失败原因)
```

**边界与失败**：v1..v15 之外、magic 被破坏、尾部长度比文件还长、索引范围越界都会抛且不返回半成品。重试安全（纯读）。实测：`kards-Android_ASTC.pak` 第一次候选（v11/5 槽，221 字节）即命中；`wide-mount.pak`（v3）在第 14 次候选命中。

**调用者与被调用者**：被 `PakArchive.Open` 调用；调用 `Candidates`、`FooterSizeFor`、`Parse`、`ReadFully`，`Parse` 内调 `ByteCursor` 的读取方法。

---

### `PakInfo.MethodName(int index): string`

**状态**：✅ 已实现

**签名与契约**：纯函数，无副作用。把条目里的压缩方法下标翻成名字。

**输入输出**：`index` 在 `[0, CompressionSlots.Count)` 且该槽非空 → 返回名字（`"None"` / `"Oodle"` / `"Zlib"` …）；越界或空槽 → 返回 `"<method N>"`，不抛。实测本 pak：`MethodName(0)=="None"`，`MethodName(1)=="Oodle"`。

**关键分支（伪代码）**：

```text
if index < 0 or index >= slots.Count: return "<method N>"
if slots[index].Length == 0:          return "<method N>"   # 空槽
return slots[index]
```

**边界与失败**：不抛异常是刻意的——条目里的方法下标来自文件，坏数据不应该在「打印一行清单」时炸掉整个流程；真正的失败留给 `PakArchive.Read` 在解压时抛。

**调用者与被调用者**：被 `PakArchive.Read`、`SolsticeWinter.Cli.Program`（`list`/`info`）调用；不调别的函数。

---

### `PakInfo.FooterSizeFor(int version, int slots): int`

**状态**：✅ 已实现

**签名与契约**：静态纯函数，算某版本尾部结构应有的字节数。`internal`。

**输入输出**：`version` 1..15，`slots` 0/4/5。返回 `(v>=7?16:0) + (v>=4?1:0) + 4 + 4 + 8 + 8 + 20 + (v==9?1:0) + slots*32`。v11/5 槽 = 221；v3/0 槽 = 44；v8/4 槽 = 189；v9/5 槽 = 222。非法 `version` 不校验，按公式算（调用方只在候选里用合法值）。

**关键分支（伪代码）**：

```text
size = 0
if version >= 7: size += 16      # FGuid EncryptionKeyGuid
if version >= 4: size += 1       # bEncryptedIndex
size += 4 + 4 + 8 + 8 + 20       # Magic, Version, IndexOffset, IndexSize, IndexHash
if version == 9: size += 1       # bIndexIsFrozen
size += slots * 32               # 压缩方法名定长缓冲
```

**边界与失败**：无 I/O，不会失败。`slots=0` 时压缩方法名由 `Parse` 用硬编码表补齐。

**调用者与被调用者**：被 `Read` 调用；不调别的函数。

---

### `PakInfo.Parse(byte[] buffer, int version, int slots, long streamLength): PakInfo`

**状态**：✅ 已实现

**签名与契约**：静态私有。把「已按候选长度回读的尾部缓冲」解析成 `PakInfo`。抛 `PakException` 表示这个候选不成立（`Read` 会换下一个）。

**输入输出**：`buffer.Length == FooterSizeFor(version, slots)`；`streamLength` 用于回填 `FooterOffset`。返回值的 `FooterSize == buffer.Length`。任何字段读超界、magic/version 不符、尾部有残留、索引范围越界都抛。

**关键分支（伪代码）**：

```text
if version >= 7: guid = read(16) else guid = zeros
if version >= 4: encrypted = readByte() != 0
if readU32() != 0x5A6F12E1: throw
if readI32() != version: throw
indexOffset = readI64(); indexSize = readI64(); indexHash = read(20)
if version == 9: skip(1)
if slots > 0: 逐槽读 32 字节，TrimEnd('\0') → slotNames[i+1]
else: slotNames = ["None","Zlib","Gzip","Oodle"]          # v8 之前 UE 硬编码
if remaining != 0: throw                                   # 布局假设错
if indexOffset < 0 or indexSize <= 0 or indexOffset+indexSize > streamLength: throw
```

**边界与失败**：尾部多/少一个字节都会在这里抛（实测把 bool 当 1 字节读会让后续字段错位并最终触发残留检查）。不写盘，可重试。

**调用者与被调用者**：被 `PakInfo.Read` 调用；调用 `ByteCursor` 的 `ReadBytes` / `ReadBool` / `ReadUInt32` / `ReadInt32` / `ReadInt64` / `Skip` / `Remaining`。

---

### `PakInfo.Candidates(): IEnumerable<(int Version, int Slots)>`

**状态**：✅ 已实现

**签名与契约**：静态私有迭代器，按「从新到旧」给出候选布局。

**输入输出**：无参数。产出 `(15,5) (14,5) (13,5) (12,5) (11,5) (10,5) (9,5) (8,5) (8,4) (7,5) … (1,0)`；`version < 8` 时槽数固定 0。

**关键分支（伪代码）**：

```text
for version in 15..1:
    if version < 8: yield (version, 0); continue
    yield (version, 5)
    if version == 8: yield (version, 4)   # v8 有两代：4 槽 / 5 槽
```

**边界与失败**：纯枚举，不会失败。顺序即优先级，新版本优先是因为新版尾部更长、误命中概率更低。

**调用者与被调用者**：只被 `PakInfo.Read` 使用。

---

### `PakInfo.ReadFully(Stream stream, byte[] buffer, int offset, int count): void`

**状态**：✅ 已实现

**签名与契约**：静态 `internal`。把 `count` 字节**读满**，读不满就抛。副作用：推进 `stream.Position`。

**输入输出**：`buffer` 必须装得下 `offset + count`；`count == 0` 立即返回。流提前结束（返回 0）→ 抛 `PakException($"读流时提前结束（已读 {read}/{count}）")`。

**关键分支（伪代码）**：

```text
read = 0
while read < count:
    n = stream.Read(buffer, offset+read, count-read)
    if n <= 0: throw "读流时提前结束"
    read += n
```

**边界与失败**：`FileStream` 与 `MemoryStream` 都可能短读，所以必须循环；不做重试（读文件不该短读，短读=文件被截断）。

**调用者与被调用者**：被 `PakInfo.Read`、`PakArchive.ReadAt` 调用；只调 `Stream.Read`。
