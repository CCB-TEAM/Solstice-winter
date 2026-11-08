# Oodle — `OodleLZ_Decompress` 的 P/Invoke

**职责一句话**：在运行时按候选路径找 Oodle 动态库、拿到 `OodleLZ_Decompress` 导出，并把一个压缩块解到目标缓冲。

**对外入口**：`Oodle.EnsureLoaded(string?)`、`Oodle.Decompress(byte[], int, byte[], int, int)`、`Oodle.Describe()`、`Oodle.LoadedFrom`。

**内部结构**：

```text
EnsureLoaded                      ← 加锁、只做一次
 ├─ 候选：显式路径 → 环境变量 SOLSTICE_WINTER_OODLE → 程序目录 oodle-data-shared.dll
 │        → 程序目录 oo2core_*win64.dll → D:\devenv\fmodel\Output\.data\oodle-data-shared.dll
 └─ NativeLibrary.Load + GetExport + GetDelegateForFunctionPointer
Decompress                        ← 两个 GCHandle 钉住输入/输出，调一次原生函数
```

**依赖**：`System.Runtime.InteropServices.NativeLibrary` / `GCHandle` / `Marshal`。**不附带任何 Oodle 二进制**。

**取舍**：

- **运行时 `NativeLibrary.Load` 而不是静态 `DllImport`**：Oodle 是闭源运行时，文件名随游戏/工具链不同（`oodle-data-shared.dll` / `oo2core_*_win64.dll`）。静态导入会让「没装 Oodle」变成进程启动即崩；动态加载能把失败变成一条可读的 `PakException`。
- **用 `GCHandle.Alloc(Pinned)` 而不是 `fixed`**：调用点在普通方法里，`fixed` 需要 `unsafe` 上下文；`GCHandle` 让整个项目保持 `AllowUnsafeBlocks=false`。代价：每次解压两次 pin/unpin，块级开销可以忽略（实测 33808 条全量对拍无压力）。
- **参数按 UE 的 `OodleLZ_Decompress` 全签名声明**（14 个参数），未用的传 0。否决了「只声明前 4 个参数」——x64 下能跑，但 `decBufBase/decBufSize` 传错会让某些块解出 0，且没有任何报错线索。
- **`Decode_ThreadPhaseAll = 3`、`FuzzSafe_Yes`、`CheckCRC_No`**：与 repak / CUE4Parse 一致；`CheckCRC_No` 是因为 pak 里存的 CRC 语义与 Oodle 自带 CRC 不同，打开反而会误判失败。
- **加载结果缓存**（`_decompress` 非空即返回）：避免每条条目都走一遍文件系统探测。**失败不缓存**——下次调用会重试并重新给出完整候选列表，方便补完 DLL 后不重启进程。

---

### `Oodle.EnsureLoaded(string? explicitPath = null): void`

**状态**：✅ 已实现

**签名与契约**：静态。按候选顺序尝试加载并取 `OodleLZ_Decompress` 导出；成功则记住函数指针、库句柄、来源路径。线程安全（`lock`）。副作用：`NativeLibrary.Load` 一个 DLL（不可卸载）、写 `_decompress` / `_library` / `_loadedFrom`。

**输入输出**：`explicitPath` 可空（非空且文件存在则优先试）。已加载过则立即返回。全部候选失败 → 抛 `PakException`，消息里写明「怎么补」+ 所有候选路径。

**关键分支（伪代码）**：

```text
lock:
  if _decompress != null: return                       # 已加载
  candidates = [explicitPath] + [env SOLSTICE_WINTER_OODLE] + [程序目录 oodle-data-shared.dll]
             + [程序目录 oo2core_*win64.dll...] + [D:\devenv\fmodel\Output\.data\oodle-data-shared.dll]
  for path in candidates 中存在的:
      try: lib = NativeLibrary.Load(path)
           export = NativeLibrary.GetExport(lib, "OodleLZ_Decompress")
           _decompress = Marshal.GetDelegateForFunctionPointer(export); _loadedFrom = path; return
      catch: 换下一个
  throw "找不到可用的 Oodle 运行时…候选：…"
```

**边界与失败**：⛔ 只认导出名 `OodleLZ_Decompress`（`oo2core_*` 里也有，已覆盖）；⛔ 不校验 DLL 版本/位数（x86 DLL 会在调用时崩，不在加载时被拦）；⛔ 加载后不卸载。实测本机命中 `D:\devenv\fmodel\Output\.data\oodle-data-shared.dll`。

**调用者与被调用者**：被 `Oodle.Decompress`、`Oodle.Describe` 调用；调用 `NativeLibrary.Load` / `GetExport` / `Marshal.GetDelegateForFunctionPointer`。

---

### `Oodle.Decompress(byte[] compressed, int compressedLength, byte[] destination, int destinationOffset, int destinationLength): int`

**状态**：✅ 已实现

**签名与契约**：把一个压缩块解到 `destination[destinationOffset .. +destinationLength)`。返回实际解出字节数（正常等于 `destinationLength`）。会先 `EnsureLoaded()`。

**输入输出**：`compressedLength` 是块的真实长度（不含 AES 补齐的尾部）；`destination` 必须有 `destinationOffset + destinationLength` 的空间。原生函数返回 `<= 0` → 抛「Oodle 解压失败（输入 N 字节，期望 M 字节）」。返回值 `!= destinationLength` 不抛（由调用方记 `Warnings`）。

**关键分支（伪代码）**：

```text
EnsureLoaded(); fn = _decompress
pin(compressed); pin(destination)
written = fn(comp, compressedLength, dst+destinationOffset, destinationLength,
             1 /*FuzzSafe*/, 0 /*CheckCRC*/, 0 /*Verbosity*/, 0,0, 0,0, 0,0, 3 /*ThreadPhaseAll*/)
unpin
if written <= 0: throw
return written
```

**边界与失败**：⛔ 一次只解一个块（多块由 `PakArchive.Read` 循环）；⛔ 不支持「块内偏移解压」（不需要）。失败后缓冲内容未定义，调用方丢弃即可；可重试（无状态）。

**调用者与被调用者**：被 `PakArchive.Read(PakFile)` 调用；调用 `EnsureLoaded` 与原生 `OodleLZ_Decompress`。

---

### `Oodle.Describe(): string`

**状态**：✅ 已实现

**签名与契约**：诊断用。成功返回 `"Oodle 已加载：<路径>"`；失败返回异常消息本体（**不抛**）。

**输入输出**：无参数。`pak info` 直接打印这行字符串。实测输出：`Oodle 已加载：D:\devenv\fmodel\Output\.data\oodle-data-shared.dll`。

**关键分支（伪代码）**：

```text
try: EnsureLoaded(); return "Oodle 已加载：" + _loadedFrom
catch PakException e: return e.Message
```

**边界与失败**：不抛是刻意的——「info」要在没有 Oodle 的机器上也能跑完并说明缺什么。

**调用者与被调用者**：被 `SolsticeWinter.Cli.Program` 的 `info` 调用；调用 `EnsureLoaded`。

---

### `Oodle.LoadedFrom: string?`

**状态**：✅ 已实现

**签名与契约**：只读属性，返回实际加载的 DLL 路径；未加载时为 `null`。

**输入输出**：`null` 或绝对路径字符串。

**关键分支（伪代码）**：无。

**边界与失败**：无。

**调用者与被调用者**：被 `Describe` 间接使用；不调别的函数。
