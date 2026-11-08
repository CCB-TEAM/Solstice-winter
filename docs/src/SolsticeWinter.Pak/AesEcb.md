# AesEcb — AES-256-ECB

**职责一句话**：pak 索引与条目加密的 AES-256-ECB 解密，以及 16 字节对齐换算。

**对外入口**：`AesEcb.ParseKey(string)`、`AesEcb.Decrypt(byte[], byte[], int, int)`、`AesEcb.Align(long)`、常量 `BlockSize`。

**内部结构**：无状态静态类；每次 `Decrypt` 新建 `Aes` 实例（`CipherMode.ECB` + `PaddingMode.None`）。

**依赖**：`System.Security.Cryptography.Aes`。

**取舍**：

- **ECB 而不是 CBC**：UE 的 pak 加密就是 ECB（每个 16 字节块独立），所以任意 16 字节对齐的**数据段**都能单独解密，不需要知道流的起点。代价：ECB 不安全，但这里只做只读解密，安全性由游戏侧决定。
- **每次新建 `Aes` 对象**：`Aes.Create()` 有点重，但一次提取只解密几十次；换成复用/池化会让「并发读」与生命周期问题变复杂。否决了「缓存 `ICryptoTransform`」——它不是线程安全的。
- **`ParseKey` 接受 `0x` 前缀与大小写混写**：FModel 配置里两种写法都出现过。长度必须正好 64 个十六进制字符（32 字节）。
- **不做加密**：本库只读，写路径没有需求；留空比留个半吊子的 `Encrypt` 好。

---

### `AesEcb.ParseKey(string hex): byte[]`

**状态**：✅ 已实现

**签名与契约**：静态纯函数。把十六进制串解成 32 字节密钥。

**输入输出**：接受可选 `0x` 前缀、前后空白、大小写混写。长度不是 64 个字符 → 抛「AES key 必须是 32 字节（64 个十六进制字符），给的是 N 个字符」；含非十六进制字符 → 抛「AES key 不是合法十六进制：…」。返回 32 字节数组。**不打印、不记录 key 本体**。

**关键分支（伪代码）**：

```text
s = hex.Trim(); if s 以 "0x" 开头（忽略大小写）: s = s[2..]
if s.Length != 64: throw
try: return Convert.FromHexString(s) catch FormatException: throw "不是合法十六进制"
```

**边界与失败**：⛔ 只支持 32 字节密钥（AES-256）；AES-128 的 pak 不支持。`null` 传入会 `NullReferenceException`——调用方（`PakArchive.DecryptIndex`/`ReadRaw`）保证只在非空时调用。

**调用者与被调用者**：被 `PakArchive.DecryptIndex`、`PakArchive.ReadRaw` 调用；调用 `Convert.FromHexString`。

---

### `AesEcb.Decrypt(byte[] data, byte[] key, int offset, int count): byte[]`

**状态**：✅ 已实现

**签名与契约**：静态。解密 `data[offset .. offset+count)`，返回**新数组**（不修改入参）。

**输入输出**：`count % 16 != 0` → 抛「AES 解密长度 N 不是 16 的倍数（偏移 M）」。`key` 必须 32 字节。返回长度 == `count`。解密产出字节数与 `count` 不符也抛（防御 `TransformBlock` 的边界行为）。

**关键分支（伪代码）**：

```text
if count % 16 != 0: throw
aes = Aes.Create(); aes.Key = key; Mode = ECB; Padding = None
written = aes.CreateDecryptor().TransformBlock(data, offset, count, output, 0)
if written != count: throw
return output
```

**边界与失败**：⛔ 不校验「解出来是不是合法明文」——那由调用方（`ReadMountPoint` 的长度/字符集检查、SHA-1 比对）负责。可以重复调用（无状态）。

**调用者与被调用者**：被 `PakArchive.DecryptIndex`、`PakArchive.ReadRaw` 调用；调用 `Aes.Create` / `ICryptoTransform.TransformBlock`。

---

### `AesEcb.Align(long value): long`

**状态**：✅ 已实现

**签名与契约**：静态纯函数。把字节数向上取整到 16 的倍数。

**输入输出**：`value >= 0` → 返回 `(value + 15) / 16 * 16`；`value == 0` → 0。

**关键分支（伪代码）**：

```text
return (value + BlockSize - 1) / BlockSize * BlockSize
```

**边界与失败**：不抛。负值会算出负数（调用方只传非负长度）。

**调用者与被调用者**：被 `PakArchive.ReadRaw` 调用（算「加密条目要读多少字节」）；不调别的函数。

---

### `AesEcb.BlockSize: const int`

**状态**：✅ 已实现

**签名与契约**：常量 16。给「加密块对齐」和「索引填充上界」两处共用。

**输入输出**：`AesEcb.BlockSize == 16`。

**关键分支（伪代码）**：无。

**边界与失败**：无。

**调用者与被调用者**：被 `PakFile.ReadEncoded`（加密块起点对齐）、`PakArchive.ReadRaw`、`PakArchive.MaxIndexPadding`、`AesEcb.Align` 使用。
