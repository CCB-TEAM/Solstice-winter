# PakFile — 条目（`FPakEntry`）

**职责一句话**：把一个 pak 条目（编码条目或传统条目）解析成统一结构：数据在哪、多大、怎么压缩、是否加密、分几块。

**对外入口**：`PakFile` 的属性（`Path` / `Offset` / `CompressedSize` / `UncompressedSize` / `CompressionMethodIndex` / `Encrypted` / `Deleted` / `BlockSize` / `Blocks` / `StructSize` / `IsCompressed` / `Extension` / `ToString()`）、常量 `FixedStructSize`。

**内部结构**：

```text
ReadEncoded(池, 定位)  → 位域 → [块大小] → Offset → UncompressedSize → [CompressedSize] → [每块长度]
ReadLegacy(游标)       → Offset, Compressed, Uncompressed, [时间戳], 方法下标, Hash, [块], Flags, BlockSize
                       ↓
                同一个 PakFile（Blocks 已换算成文件绝对区间）
```

**依赖**：`ByteCursor`、`PakInfo`（版本决定字段宽度）、`AesEcb.BlockSize`（加密块对齐）、`PakException`。

**取舍**：

- **`StructSize` 按「磁盘上真实前置的条目头」算，不是按索引里省掉的字段算**。索引为了省空间会省略单块条目的块长度，但数据前面那份条目头永远是完整序列化（含块长度）。两者混用会让 `Offset + StructSize` 差 16 字节，直接读错数据。实测 `BP_Logic.uasset`：`StructSize=89 = 53 + 4 + 2*16`，`Offset+89` 处的第一块能解出 65536 字节。
- **`IsCompressed` 判「有方法且有块」，不看 `BlockSize > 0`**。否决了原来的 `CompressionMethodIndex != 0 && BlockSize > 0`：实测单块条目的块大小字段可以是 0（位域 `0xE0800040` → `bit0..5 == 0`），那样会把压缩条目当原始数据返回，长度对不上却不报错。
- **`BlockSize` 在只有一块时直接取 `UncompressedSize`**（UE `DecodePakEntry` / CUE4Parse 同款规则）。位域里的块大小字段在单块场景不可靠，用它算最后一块长度会短读。
- **加密块的长度按 16 字节向上取整后累加**（`Align(length, 16)`），未加密块紧挨着排。实测 `BP_Logic.uasset` 两块 `25214 + 6442` 存储值，实际占 `25216 + 6448 = 31664`，正好等于条目里的 `CompressedSize`，且下一块的起点对上了下一个条目的 `Offset`。
- **不做「防御性裁剪」**：块数量、方法下标越界直接抛，不猜测修复。

---

### `PakFile.ReadEncoded(ByteCursor entries, int position, PakInfo info, string path): PakFile`

**状态**：✅ 已实现

**签名与契约**：`internal` 静态。`entries` 是主索引里的编码条目池，`position` 是该条目在池里的字节偏移；**会改动 `entries.Position`**（随机定位后顺序读）。返回填好 `Blocks` 的 `PakFile`；`path` 只用于报错和回填。

**输入输出**：`position ∈ [0, entries.Length)`；`path` 是拼好的虚拟路径。位域字段顺序（实测）：

| 字段 | 宽度 | 说明 |
|---|---|---|
| `bit0..5` | 6 bit | 块大小 / 2048，`0x3f` 表示后面另给 uint32 |
| `bit6..21` | 16 bit | 压缩块数量 |
| `bit22` | 1 bit | 加密 |
| `bit23..28` | 6 bit | 压缩方法下标（0 = 不压缩） |
| `bit29` | 1 bit | `CompressedSize` 是 32 位 |
| `bit30` | 1 bit | `UncompressedSize` 是 32 位 |
| `bit31` | 1 bit | `Offset` 是 32 位 |

`Deleted` 恒为 `false`（删除由目录索引的 `int.MinValue` 定位表示，在 `PakArchive` 里过滤）。

**关键分支（伪代码）**：

```text
if position < 0 or position >= entries.Length: throw
bits = readU32()
块数 = (bits>>6)&0xFFFF; 方法 = (bits>>23)&0x3F; 加密 = bits>>22 & 1
块大小 = (bits&0x3F)==0x3F ? readU32() : (bits&0x3F)<<11
Offset          = bits>>31 & 1 ? readU32() : readI64()
Uncompressed    = bits>>30 & 1 ? readU32() : readI64()
Compressed      = 方法!=0 ? (bits>>29 & 1 ? readU32() : readI64()) : Uncompressed
StructSize      = 53 + (方法!=0 ? 4 + 块数*16 : 0)
if 块数==1 and not 加密: Blocks = [(Offset+StructSize, +Compressed)]     # 索引里没存块长度
elif 块数>0:            逐块 readU32() 长度，起点按 加密?16:1 对齐累加
if 块数==1: BlockSize = Uncompressed
elif 块数>1: BlockSize = 位域/扩展字段里的块大小
else: BlockSize = 0
```

**边界与失败**：`position` 越界抛；池里字节不够（`ByteCursor.Require`）抛并带池名与偏移。块数上限 `0xFFFF`（位域宽度决定），`int` 溢出不可能。不写盘。

**调用者与被调用者**：被 `PakArchive.MountPathHashIndex` 调用（目录索引 `location >= 0` 时）；调用 `ByteCursor` 的读取方法与 `PakFile.Align`、`AesEcb.BlockSize`。

---

### `PakFile.ReadLegacy(ByteCursor cursor, PakInfo info, string path): PakFile`

**状态**：✅ 已实现

**签名与契约**：`internal` 静态。解析传统条目：v10+ 主索引里的 `NonEncodedEntries`，或 v9 及以前的整段索引。**顺序读**，推进 `cursor.Position`。

**输入输出**：字段顺序 `Offset(8) Compressed(8) Uncompressed(8) [v1: 时间戳 8] 方法下标 Hash(20) [块数组] Flags(1) BlockSize(4)`；方法下标在 **v8 是 1 字节**、其余版本是 int32；块/`Flags`/`BlockSize` 只在 v3+ 存在；v5+ 的块偏移是**相对条目起点**的，要加 `Offset`。`Deleted = (Flags & 2) != 0`、`Encrypted = (Flags & 1) != 0`。

**关键分支（伪代码）**：

```text
Offset/Compressed/Uncompressed = readI64() * 3
方法 = version==8 ? readByte() : readI32()
if version < 2: skip(8)            # 时间戳
skip(20)                           # SHA-1
if version >= 3:
    if 方法 != 0: 块数 = readI32(); if 块数 <0 or >1<<20: throw; 逐块 readI64(),readI64()
    Flags = readByte(); BlockSize = readU32()
if version >= 5: 每块 start/end += Offset
if 块数 == 1: BlockSize = Uncompressed
StructSize = 53 + (方法!=0 ? 4 + 块数*16 : 0)
```

**边界与失败**：块数被夹在 `[0, 1<<20]`，负值抛（否则 `new (long,long)[count]` 会抛 `OverflowException` 这种没信息量的异常）。v1/v2 不读 `Flags`，因此 v1/v2 条目恒为「未加密未删除」。读越界由 `ByteCursor.Require` 抛。

**调用者与被调用者**：被 `PakArchive.MountPathHashIndex`（非编码条目）与 `PakArchive.MountLegacyIndex`（v1..v9 索引）调用；调用 `ByteCursor` 的读取方法与 `Skip`。

---

### `PakFile.Align(long value, long alignment): long`

**状态**：✅ 已实现

**签名与契约**：`internal` 静态纯函数。把 `value` 向上取整到 `alignment` 的倍数。

**输入输出**：`alignment <= 1` → 原样返回；否则返回 `(value + alignment - 1) / alignment * alignment`。`value < 0` 不特殊处理（调用方传的都是非负长度）。

**关键分支（伪代码）**：

```text
if alignment <= 1: return value
return (value + alignment - 1) / alignment * alignment
```

**边界与失败**：不抛。`value + alignment - 1` 在 `long` 范围内不会溢出（pak 文件最大 2^63 字节的假设成立）。

**调用者与被调用者**：被 `PakFile.ReadEncoded`（块起点累加）调用；不调别的函数。

---

### `PakFile.ToString(): string`

**状态**：✅ 已实现

**签名与契约**：覆盖 `object.ToString()`，无副作用。给 `pak find` 打一行摘要用。

**输入输出**：`"<Path>  raw=<UncompressedSize>  stored=<CompressedSize>  m=<方法下标>"`，`Encrypted` 追加 `"  [enc]"`，`Deleted` 追加 `"  [deleted]"`。不抛。

**关键分支（伪代码）**：

```text
s = $"{Path}  raw={UncompressedSize}  stored={CompressedSize}  m={CompressionMethodIndex}"
if Encrypted: s += "  [enc]"
if Deleted:   s += "  [deleted]"
return s
```

**边界与失败**：不失败。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program` 的 `find` 命令（`Console.WriteLine(file)`）调用；不调别的函数。

---

### PakFile.IsCompressed 与 PakFile.Extension

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：只读派生属性，无副作用。`IsCompressed => CompressionMethodIndex != 0 && Blocks.Length > 0`；`Extension => Path.GetExtension(Path).ToLowerInvariant()`。

**输入输出**：`IsCompressed` 为 `false` 时 `PakArchive.Read` 走「原样读 `CompressedSize` 字节」；`Extension` 返回带点的后缀（`.uasset` / `.uexp` / 空串）。

**关键分支（伪代码）**：

```text
IsCompressed = 方法下标 != 0 and Blocks.Length > 0
Extension    = 小写化的 Path.GetExtension(Path)
```

**边界与失败**：⛔ 「有方法下标但块数为 0」的条目被判为不压缩——UE 对这种条目也是按原始数据读，`BlockSize` 为 0 无法分块。`Extension` 对无后缀路径返回空串，不抛。

**调用者与被调用者**：`IsCompressed` 被 `PakArchive.Read` 调用；`Extension` 目前只有调试/后续过滤会用。

