# ByteCursor — 小端游标与 UE 字符串

**职责一句话**：在内存字节数组上按小端读定长整数、布尔、UE `FString`，越界立即抛。

**对外入口**：`ByteCursor(byte[] data, string what)`、`Position` / `Length` / `Remaining` / `Data`、`Require` / `ReadByte` / `ReadBool` / `ReadUInt32Bool` / `ReadInt32` / `ReadUInt32` / `PeekInt32` / `PeekUInt32` / `ReadInt64` / `ReadBytes` / `Skip` / `ReadFString`；同文件的 `PakException`。

**内部结构**：一个 `byte[]` + 一个 `int Position`，所有读取都先 `Require(n)` 再 `BitConverter`。`what` 只是错误消息里的段名（`"主索引"` / `"编码条目池"` / `"FPakInfo v11"`）。

**依赖**：`System.BitConverter`（小端，与 pak 一致）、`System.Text.Encoding`。

**取舍**：

- **用 `BitConverter` 而不是 `BinaryPrimitives`**：`BitConverter` 直接按主机字节序读，本机与目标平台都是小端；换成 `BinaryPrimitives` 更严谨，但会让「读一个 int32」变成两行。代价：理论上不能跑在大端主机上（记在边界里）。
- **越界即抛，不返回默认值**：`Require` 失败的消息里带段名、需要的字节数、剩余字节数和当前偏移。这是发现布局错误最有效的一条：v11 索引把 4 字节 bool 当 1 字节读，就是靠这个在偏移 4 处炸出来的。
- **`FString` 用 UTF-8 解 ASCII 分支**：UE 的 `FString` 在 pak 里存 ANSI/UTF-8 字节；对纯 ASCII 路径两种解一致，对非 ASCII 路径 UTF-8 更可能正确。否决了「按 `Encoding.ASCII` 解」——那会把非 ASCII 字节变成 `?`，且不可逆。
- **不缓存、不复用缓冲**：每次 `ReadBytes` 都新分配。索引解析总共几 MB，分配压力可以忽略，换来的是没有别名问题。

---

### `PakException(string message): Exception`

**状态**：✅ 已实现

**签名与契约**：pak 层唯一的异常类型（`sealed`），继承 `Exception`。CLI 顶层捕获它 → 打 `[pak] <message>` 到 stderr、退出码 1。

**输入输出**：`message` 必须自带定位信息（哪一段、读到什么、期望什么）。没有任何隐藏字段。

**关键分支（伪代码）**：

```text
class PakException(message) : Exception(message)   # 无额外逻辑
```

**边界与失败**：不带 `InnerException`——pak 解析失败基本都是「文件不符合假设」，不需要栈；CLI 只打印 `Message`。

**调用者与被调用者**：被本模块所有函数 `throw`，被 `SolsticeWinter.Cli.Program.Main` 捕获。

---

### `ByteCursor.Require(int n): void`

**状态**：✅ 已实现

**签名与契约**：检查还能不能再读 `n` 字节，不够就抛。所有读方法的统一闸门。

**输入输出**：`n < 0` 或 `Remaining < n` → 抛 `PakException("{what}: 需要 n 字节，只剩 R（偏移 P/L）")`。`n == 0` 通过。

**关键分支（伪代码）**：

```text
if n < 0 or Remaining < n: throw "{what}: 需要 {n} 字节，只剩 {Remaining}（偏移 {Position}/{Length}）"
```

**边界与失败**：不移动游标。抛了之后游标状态不变（调用方一般直接放弃整个解析）。

**调用者与被调用者**：被本文件所有 `Read*`/`Skip`/`Peek*` 调用；不调别的函数。

---

### `ByteCursor.ReadByte(): byte` / `ReadBool(): bool`

**状态**：✅ 已实现

**签名与契约**：读 1 字节；`ReadBool` 把它当 **1 字节** 布尔（`!= 0`）。推进 1 字节。

**输入输出**：`ReadBool` 只在「UE 用 1 字节写 bool」的字段上正确（如 `FPakInfo.bEncryptedIndex`、v9 的 `bIndexIsFrozen`）。要读 4 字节 bool 必须用 `ReadUInt32Bool`。

**关键分支（伪代码）**：

```text
Require(1); v = Data[Position++]; return v          # ReadBool: return v != 0
```

**边界与失败**：空缓冲 → `Require` 抛。

**调用者与被调用者**：`ReadBool` 被 `PakInfo.Parse` 调用；`ReadByte` 被 `ReadBool`、`PakFile.ReadLegacy` 调用。

---

### `ByteCursor.ReadUInt32Bool(): bool`

**状态**：✅ 已实现

**签名与契约**：把 4 字节 int32 当布尔读（`!= 0`），推进 4 字节。

**输入输出**：用于 UE `FArchive` 默认的 `bool` 序列化。实测 v11 主索引的 `bHasPathHashIndex` / `bHasFullDirectoryIndex` 都是 4 字节：按 1 字节读会读到 `01`，把紧随其后的 3 个 `00` 当成下一个字段，整体错位。

**关键分支（伪代码）**：

```text
return ReadUInt32() != 0
```

**边界与失败**：剩余不足 4 字节 → `Require` 抛。

**调用者与被调用者**：被 `PakArchive.MountPathHashIndex` 调用；调用 `ReadUInt32`。

---

### `ByteCursor.ReadInt32(): int` / `ReadUInt32(): uint`

**状态**：✅ 已实现

**签名与契约**：小端读 4 字节有/无符号整数，推进 4 字节。

**输入输出**：全 `int` / `uint` 范围。`ReadUInt32` 用于位域、长度、magic；`ReadInt32` 用于计数、目录定位（可能为负）、FString 长度。

**关键分支（伪代码）**：

```text
Require(4); v = BitConverter.ToInt32/ToUInt32(Data, Position); Position += 4; return v
```

**边界与失败**：剩余不足 4 字节 → `Require` 抛。

**调用者与被调用者**：被 `PakInfo.Parse`、`PakFile.ReadEncoded`、`PakFile.ReadLegacy`、`PakArchive.MountPathHashIndex`、`ReadFString` 调用。

---

### `ByteCursor.PeekInt32(): int` / `PeekUInt32(): uint`

**状态**：✅ 已实现

**签名与契约**：看一眼接下来 4 字节，**不推进游标**。

**输入输出**：`PeekUInt32` 用于识别扁平目录索引 magic `0x50464451`（`'PFDQ'`）；`PeekInt32` 用于在真正读挂载点前先判断长度是否合理（错 key 的早期拦截）。剩余不足 4 字节 → `Require` 抛。

**关键分支（伪代码）**：

```text
Require(4); return BitConverter.ToXxx(Data, Position)
```

**边界与失败**：不改变状态，失败也不改变状态。

**调用者与被调用者**：`PeekInt32` 被 `PakArchive.ReadMountPoint` 调用；`PeekUInt32` 被 `PakArchive.MountPathHashIndex` 调用。

---

### `ByteCursor.ReadInt64(): long`

**状态**：✅ 已实现

**签名与契约**：小端读 8 字节有符号整数，推进 8 字节。

**输入输出**：用于 `Offset`、`IndexOffset`、`IndexSize`、目录偏移、传统条目块区间。全 `long` 范围。

**关键分支（伪代码）**：

```text
Require(8); v = BitConverter.ToInt64(Data, Position); Position += 8; return v
```

**边界与失败**：剩余不足 8 字节 → `Require` 抛。

**调用者与被调用者**：被 `PakInfo.Parse`、`PakFile.ReadEncoded`/`ReadLegacy`、`PakArchive.MountPathHashIndex` 调用。

---

### `ByteCursor.ReadBytes(int n): byte[]`

**状态**：✅ 已实现

**签名与契约**：拷贝 `n` 字节到新数组并推进。用于 hash、GUID、压缩方法名缓冲、编码条目池。

**输入输出**：`n >= 0`；返回长度恰为 `n` 的新数组（不与源共享内存）。

**关键分支（伪代码）**：

```text
Require(n); b = new byte[n]; Buffer.BlockCopy(Data, Position, b, 0, n); Position += n; return b
```

**边界与失败**：`n < 0` 或剩余不足 → `Require` 抛。`n == 0` 返回空数组。

**调用者与被调用者**：被 `PakInfo.Parse`、`PakArchive.MountPathHashIndex` 调用。

---

### `ByteCursor.Skip(int n): void`

**状态**：✅ 已实现

**签名与契约**：跳过 `n` 字节（只推进游标，不读）。用于「知道长度但用不到的字段」：`PathHashSeed`、path hash 索引的 36 字节、`DirectoryIndexHash` 的 20 字节、SHA-1。

**输入输出**：`n >= 0` 且剩余足够，否则抛。

**关键分支（伪代码）**：

```text
Require(n); Position += n
```

**边界与失败**：跳过末尾之外的字节 → 抛（这条保证了「跳过」不会掩盖布局错误）。

**调用者与被调用者**：被 `PakInfo.Parse`、`PakFile.ReadLegacy`、`PakArchive.MountPathHashIndex` 调用。

---

### `ByteCursor.ReadFString(): string`

**状态**：✅ 已实现

**签名与契约**：读 UE 的 `FString`：`int32 长度 + 载荷`。长度 `> 0` → 该字节数的 ANSI/UTF-8（含结尾 `\0`）；`< 0` → `-长度` 个 UTF-16 码元（含结尾 `\0`）；`0` → 空串。返回时**去掉结尾 `\0`**。

**输入输出**：返回字符串；调用后 `Position` 指向载荷之后。长度字段本身为负且绝对值过大、或剩余不足 → 抛。

**关键分支（伪代码）**：

```text
len = ReadInt32()
if len == 0: return ""
if len > 0:  Require(len); s = UTF8(Data, Pos, len); Pos += len; return s 去掉尾部 '\0'
chars = -len; Require(chars*2); s = Unicode(Data, Pos, chars*2); Pos += chars*2; return s 去掉尾部 '\0'
```

**边界与失败**：⛔ 长度为正时按 UTF-8 解（UE 里可能是 ANSI 代码页，非 ASCII 路径在别的 pak 上可能解错）；⛔ 不处理「长度为正但没写结尾 `\0`」的畸形数据（只是不去掉任何字符，不会抛）。

**调用者与被调用者**：被 `PakArchive.ReadMountPoint`、`MountPathHashIndex`、`MountLegacyIndex` 调用；调用 `ReadInt32`、`Require`。
