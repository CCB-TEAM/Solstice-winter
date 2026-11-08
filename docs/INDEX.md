# INDEX — Solstice-winter 状态看板

**当前阶段：pak 读取路径、跨资产索引、UI 只读渲染（WPF + Nodify）已落地并实测通过。** 本文件是「项目到哪了」的唯一入口。

阅读顺序：先看 [可行性评估](可行性评估.md)，再看本文。

已实现模块的文档在 `docs/src/` 下，按源码树镜像。

## 0. 设计与决策文档

| 文档 | 内容 | 状态 |
|---|---|---|
| [可行性评估](可行性评估.md) | 实测证据、五个可行性判断、风险表、**D1–D16 决策与代价** | `PROPOSED`（结论基于实测） |
| [设计-VFS与上下文索引](设计-VFS与上下文索引.md) | 三层结构、O(1) 查表、懒解析、索引缓存格式、覆盖顺序 | `PROPOSED` |
| [设计-ubergraph重定位](设计-ubergraph重定位.md) | 绝对偏移的三个住处、`--reloc` 实测数字、三条路线、spike 门禁 | `PROPOSED` |
| [设计-UI](设计-UI.md) | UE 原逻辑骨架、AST↔平铺图投影、混合真相、节点外观、校验时机 | `PROPOSED` |

## 1. 模块清单

| 模块 | 文档 | 状态 | 一句话职责 |
|---|---|---|---|
| Pak（`SolsticeWinter.Pak`） | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md) / [PakFile](src/SolsticeWinter.Pak/PakFile.md) / [PakArchive](src/SolsticeWinter.Pak/PakArchive.md) / [PakArchive.read](src/SolsticeWinter.Pak/PakArchive.read.md) / [AesEcb](src/SolsticeWinter.Pak/AesEcb.md) / [Oodle](src/SolsticeWinter.Pak/Oodle.md) / [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md) | ✅ 已实现 | 只读 pak：尾部/索引/条目解析、AES 解密、Oodle 解压、按路径提取 |
| Core（`SolsticeWinter.Core`） | —（`GameProfile.cs` 未单独立档） | ✅ 已实现 | 数据源清单 + 从 FModel 配置读 AES key（不落仓库） |
| Cli（`SolsticeWinter.Cli`） | —（`Program.cs` 未单独立档） | ✅ 已实现 | `pak info/list/find/extract/verify/compare` + `profile` |
| Sources（其余数据源） | —（未开始） | 🚧 未实现 | 把散目录 / assetregistry.bin / usmap / jmap 统一成可查询的数据源；pak 这一路已由 Pak 模块提供 |
| ProjectIndex（`SolsticeWinter.Index`） | [Models](src/SolsticeWinter.Index/Models.md) / [TypeFormatter](src/SolsticeWinter.Index/TypeFormatter.md) / [AssetHeaderReader](src/SolsticeWinter.Index/AssetHeaderReader.md) / [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md) / [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md) / [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md) | ✅ 已实现 | 扫盘建跨资产符号索引（资产/函数签名/变量/引用边），落 JSON 缓存，查询全是字典命中；补 jmap 缺的 `/Game` 侧事实 |
| Workspace | —（未开始） | 🚧 未实现 | 包级读写、多包事务、脏标记与回滚（复用 `solstice-kismet`） |
| GraphModel | —（未开始） | 🚧 未实现 | AST → 节点/引脚/连线，类型推断，自动布局 |
| Decompiler（`SolsticeWinter.Decompiler`） | [StatementMap](src/SolsticeWinter.Decompiler/StatementMap.md) / [ExecutionFlow](src/SolsticeWinter.Decompiler/ExecutionFlow.md) / [FunctionWalker](src/SolsticeWinter.Decompiler/FunctionWalker.md) / [Signatures](src/SolsticeWinter.Decompiler/Signatures.md) | ✅ 核心可用 | 移植 UEBGV（MIT）到 UAssetAPI。已支持：线性指令 / Branch / Sequence / Return / 调用（签名引脚+返回脚）/ 赋值 / Cast / Context 内虚调用 / 入口偏移隔离。未支持：Switch、循环与宏还原、Timeline、委托 |
| EditOps | —（未开始） | 🚧 未实现 | 受控编辑算子，每步过字节校验与 ubergraph 入口点闸门 |
| UI（`SolsticeWinter.App`） | [App](src/SolsticeWinter.App/App.md) / [MainViewModel](src/SolsticeWinter.App/MainViewModel.md) / [MainViewModel.load](src/SolsticeWinter.App/MainViewModel.load.md) / [ViewModels.GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md) / [Graph.GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.md) / [Graph.GraphBuilder.nodes](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md) / [Graph.GraphBuilder.mapping](src/SolsticeWinter.App/Graph.GraphBuilder.mapping.md) / [Graph.AssetAccess](src/SolsticeWinter.App/Graph.AssetAccess.md) / [Graph.GraphProbe](src/SolsticeWinter.App/Graph.GraphProbe.md) / [Graph.GraphAdapter](src/SolsticeWinter.App/Graph.GraphAdapter.md) / [Graph.MsaglLayout](src/SolsticeWinter.App/Graph.MsaglLayout.md) / [Views.GraphView](src/SolsticeWinter.App/Views.GraphView.md) / [Controls.NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md) / [MainWindow](src/SolsticeWinter.App/MainWindow.md) / [Themes.Studio](src/SolsticeWinter.App/Themes.Studio.md) / [Converters.KindToBrush](src/SolsticeWinter.App/Converters.KindToBrush.md) | ✅ 已实现 | WPF + Nodify + AdonisUI Light **只读渲染**：三栏骨架、自动选中、AST→平铺图投影与自动布局（投影层在 `GraphBuilder`，`GraphModel` 的类型推断仍未做）。**编辑未接**：不能拖线建连、不能保存，属性面板 / 候选函数面板 / 差异视图均未做 |

## 2. 函数状态看板

锚点即模块 md 里的函数小节标题（标题只放签名，状态标记在小节正文第一行），所以链接可直接跳转。

### SolsticeWinter.Pak

| 模块 | 函数 | 状态 | 一句话职责 | 锚点 |
|---|---|---|---|---|
| PakInfo | `Read(Stream): PakInfo` | ✅ | 按版本枚举尾部布局并校验，给出索引位置/加密/压缩方法 | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinforeadstream-stream-pakinfo) |
| PakInfo | `MethodName(int): string` | ✅ | 条目方法下标 → 名字（越界返回 `<method N>`） | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinfomethodnameint-index-string) |
| PakInfo | `FooterSizeFor(int,int): int` | ✅ | 某版本的尾部字节数 | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinfofootersizeforint-version-int-slots-int) |
| PakInfo | `Parse(...): PakInfo` | ✅ | 解析已回读的尾部缓冲，校验 magic/version/残留 | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinfoparsebyte-buffer-int-version-int-slots-long-streamlength-pakinfo) |
| PakInfo | `Candidates()` | ✅ | 从 v15 到 v1 的候选布局序列 | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinfocandidates-ienumerableint-version-int-slots) |
| PakInfo | `ReadFully(Stream,byte[],int,int)` | ✅ | 读满指定字节数，短读即抛 | [PakInfo](src/SolsticeWinter.Pak/PakInfo.md#pakinforeadfullystream-stream-byte-buffer-int-offset-int-count-void) |
| PakFile | `ReadEncoded(ByteCursor,int,PakInfo,string)` | ✅ | 解码 v10+ 编码条目（位域 + 变长字段 + 块表） | [PakFile](src/SolsticeWinter.Pak/PakFile.md#pakfilereadencodedbytecursor-entries-int-position-pakinfo-info-string-path-pakfile) |
| PakFile | `ReadLegacy(ByteCursor,PakInfo,string)` | ✅ | 解码传统条目（v1..v9 索引 / 非编码条目） | [PakFile](src/SolsticeWinter.Pak/PakFile.md#pakfilereadlegacybytecursor-cursor-pakinfo-info-string-path-pakfile) |
| PakFile | `Align(long,long): long` | ✅ | 向上取整到对齐倍数 | [PakFile](src/SolsticeWinter.Pak/PakFile.md#pakfilealignlong-value-long-alignment-long) |
| PakFile | `ToString(): string` | ✅ | 一行摘要 | [PakFile](src/SolsticeWinter.Pak/PakFile.md#pakfiletostring-string) |
| PakFile | `IsCompressed` / `Extension` | ✅ ⛔ | 有方法且有块才算压缩；无块条目按原始数据读 | [PakFile](src/SolsticeWinter.Pak/PakFile.md#pakfileiscompressed-与-pakfileextension) |
| PakArchive | `Open(string,PakOpenOptions?)` | ✅ | 打开并挂载，失败不泄漏句柄 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchiveopenstring-pakpath-pakopenoptions-options-pakarchive) |
| PakArchive | `Mount(PakOpenOptions)` | ✅ | 读/解密主索引 + SHA-1 自检 + 按版本分派 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchivemountpakopenoptions-options-void) |
| PakArchive | `ReadMountPoint(ByteCursor,string)` | ✅ ⛔ | 读挂载点并拦错 key；非 ASCII 挂载点判为错 key | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchivereadmountpointbytecursor-cur-string-what-string) |
| PakArchive | `MountPathHashIndex(byte[],PakOpenOptions)` | ✅ | v10+ 主索引 + 目录索引 + 编码池 → 全路径 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchivemountpathhashindexbyte-indexbytes-pakopenoptions-options-void) |
| PakArchive | `MountLegacyIndex(byte[],PakOpenOptions)` | ✅ ⛔ | v1..v9 直表索引；frozen 索引不支持 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchivemountlegacyindexbyte-indexbytes-pakopenoptions-options-void) |
| PakArchive | `CheckResidual(...)` | ✅ | 「刚好读完」自检，残留 ≥16 字节才告警 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.md#pakarchivecheckresidualbytecursor-cur-string-what-byte-data-int-position-void) |
| PakArchive | `Find(string): PakFile` | ✅ | 精确路径或唯一后缀查条目 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivefindstring-pathorsuffix-pakfile) |
| PakArchive | `Read(string): byte[]` | ✅ | `Find` + `Read(PakFile)` | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivereadstring-pathorsuffix-byte) |
| PakArchive | `Read(PakFile): byte[]` | ✅ ⛔ | 逐块解密+解压；不支持 LZ4/Zstd，单条 >2GB 不支持 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivereadpakfile-file-byte) |
| PakArchive | `Extract(PakFile,string): string` | ✅ ⛔ | 写到目录，只保留文件名，不还原目录树 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchiveextractpakfile-file-string-outputdirectory-string) |
| PakArchive | `Inflate(...)` | ✅ ⛔ | zlib/gzip 块整解；本机无真实样本 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchiveinflatebyte-compressed-byte-destination-int-destinationoffset-int-expected-bool-zlib-void) |
| PakArchive | `ReadRaw(long,int,bool): byte[]` | ✅ | 加密段按「起点读 align16 → 整段解密 → 切片」 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivereadrawlong-offset-int-length-bool-encrypted-byte) |
| PakArchive | `DecryptIndex(byte[],string): byte[]` | ✅ | 解密索引，没给 key 时给出带上下文的错误 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivedecryptindexbyte-data-string-what-byte) |
| PakArchive | `SetAesKey(string?): void` | ✅ | 存 key（不落盘、不打印） | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivesetaeskeystring-key-void) |
| PakArchive | `ReadAt(long,int): byte[]` | ✅ ⛔ | 绝对偏移读，越界抛；非线程安全 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivereadatlong-offset-int-length-byte) |
| PakArchive | `Dispose(): void` | ✅ | 释放文件句柄 | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivedispose-void) |
| PakArchive | `TotalUncompressedSize` | ✅ | 全部条目声明长度之和（不解压） | [PakArchive](src/SolsticeWinter.Pak/PakArchive.read.md#pakarchivetotaluncompressedsize-long) |
| AesEcb | `ParseKey(string): byte[]` | ✅ ⛔ | 十六进制 → 32 字节 key；只支持 AES-256 | [AesEcb](src/SolsticeWinter.Pak/AesEcb.md#aesecbparsekeystring-hex-byte) |
| AesEcb | `Decrypt(byte[],byte[],int,int): byte[]` | ✅ | ECB 解密，长度必须 16 的倍数 | [AesEcb](src/SolsticeWinter.Pak/AesEcb.md#aesecbdecryptbyte-data-byte-key-int-offset-int-count-byte) |
| AesEcb | `Align(long): long` | ✅ | 向上取整到 16 | [AesEcb](src/SolsticeWinter.Pak/AesEcb.md#aesecbalignlong-value-long) |
| AesEcb | `BlockSize` | ✅ | 常量 16 | [AesEcb](src/SolsticeWinter.Pak/AesEcb.md#aesecbblocksize-const-int) |
| Oodle | `EnsureLoaded(string?)` | ✅ ⛔ | 按候选加载 Oodle 运行时；只认 `OodleLZ_Decompress` | [Oodle](src/SolsticeWinter.Pak/Oodle.md#oodleensureloadedstring-explicitpath--null-void) |
| Oodle | `Decompress(...)` | ✅ ⛔ | 解一个块；多块由 PakArchive 循环 | [Oodle](src/SolsticeWinter.Pak/Oodle.md#oodledecompressbyte-compressed-int-compressedlength-byte-destination-int-destinationoffset-int-destinationlength-int) |
| Oodle | `Describe(): string` | ✅ | 诊断行，不抛 | [Oodle](src/SolsticeWinter.Pak/Oodle.md#oodledescribe-string) |
| Oodle | `LoadedFrom` | ✅ | 实际加载的 DLL 路径 | [Oodle](src/SolsticeWinter.Pak/Oodle.md#oodleloadedfrom-string) |
| ByteCursor | `PakException(string)` | ✅ | pak 层唯一异常类型 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#pakexceptionstring-message-exception) |
| ByteCursor | `Require(int)` | ✅ | 越界闸门 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorrequireint-n-void) |
| ByteCursor | `ReadByte` / `ReadBool` | ✅ | 1 字节读 / 1 字节布尔 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreadbyte-byte--readbool-bool) |
| ByteCursor | `ReadUInt32Bool()` | ✅ | UE 的 4 字节 bool | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreaduint32bool-bool) |
| ByteCursor | `ReadInt32` / `ReadUInt32` | ✅ | 4 字节整数 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreadint32-int--readuint32-uint) |
| ByteCursor | `PeekInt32` / `PeekUInt32` | ✅ | 不推进游标看一眼 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorpeekint32-int--peekuint32-uint) |
| ByteCursor | `ReadInt64()` | ✅ | 8 字节整数 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreadint64-long) |
| ByteCursor | `ReadBytes(int)` | ✅ | 拷贝 n 字节 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreadbytesint-n-byte) |
| ByteCursor | `Skip(int)` | ✅ | 跳过 n 字节 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorskipint-n-void) |
| ByteCursor | `ReadFString()` | ✅ ⛔ | UE FString；ASCII 分支按 UTF-8 解 | [ByteCursor](src/SolsticeWinter.Pak/ByteCursor.md#bytecursorreadfstring-string) |

### SolsticeWinter.Core / SolsticeWinter.Cli

| 模块 | 函数 | 状态 | 一句话职责 | 锚点 |
|---|---|---|---|---|
| Core | `GameProfile.Kards(string?)` | ✅ | 组装 KARDS 的数据源清单 | —（未单独立档） |
| Core | `GameProfile.FindAesKey(string)` | ✅ | 从 FModel 配置取 AES key，找不到返回 `(null,null)` | —（未单独立档） |
| Cli | `Program.Main` / `Run` / `Pak` / `Profile` / `ParseOptions` / `Usage` | ✅ | 命令分派与输出 | —（未单独立档） |

### SolsticeWinter.Index

| 模块 | 函数 | 状态 | 一句话职责 | 锚点 |
|---|---|---|---|---|
| Models | `ParamInfo(string Name, string Type, string Direction)` | ✅ | 参数载体：名字 + 渲染后类型 + 方向三态 | [Models](src/SolsticeWinter.Index/Models.md#paraminfostring-name-string-type-string-direction) |
| Models | `FunctionInfo(...)` | ✅ ⛔ | 函数签名摘要；`HasBytecode` 是「能否上画布」的唯一判据 | [Models](src/SolsticeWinter.Index/Models.md#functioninfostring-name-ireadonlylistparaminfo-params-string-return-bool-hasbytecode-int-bytecodesize-string-flags-int-locals--0) |
| Models | `VariableInfo(string Name, string Type, string Flags)` | ✅ | 类导出成员：名字 + 渲染后类型 + `CPF_` 标志文本 | [Models](src/SolsticeWinter.Index/Models.md#variableinfostring-name-string-type-string-flags) |
| Models | `AssetHeader` | ✅ ⛔ | 一个资产的目录页，索引的存储单位（不含默认值与字节码） | [Models](src/SolsticeWinter.Index/Models.md#assetheader) |
| Models | `AssetHeader.ToString(): string` | ✅ | 一行摘要（路径/类/父类/变量数/函数数/引用数） | [Models](src/SolsticeWinter.Index/Models.md#assetheadertostring-string) |
| TypeFormatter | `Of(FProperty?, Func<FPackageIndex?, string>): string` | ✅ ⛔ | `FProperty` → 可读类型名：先认子类，再退 `SerializedType` | [TypeFormatter](src/SolsticeWinter.Index/TypeFormatter.md#offproperty-property-funcfpackageindex-string-resolve-string) |
| TypeFormatter | `Scalar(FProperty): string` | ✅ | 标量兜底：`SerializedType` 里的真名，或去掉 `Property` 后缀的 CLR 名 | [TypeFormatter](src/SolsticeWinter.Index/TypeFormatter.md#scalarfproperty-property-string) |
| TypeFormatter | `Direction(EPropertyFlags): string` | ✅ | 参数方向三态：`out && !ref` 才算 out | [TypeFormatter](src/SolsticeWinter.Index/TypeFormatter.md#directionepropertyflags-flags-string) |
| AssetHeaderReader | `AssetHeaderReader(Usmap)` | ✅ ⛔ | 构造：只存 usmap，不打开文件 | [AssetHeaderReader](src/SolsticeWinter.Index/AssetHeaderReader.md#assetheaderreaderusmap-usmap) |
| AssetHeaderReader | `Read(string,string): AssetHeader` | ✅ ⛔ | 资产 → `AssetHeader`（父类/变量/参数/引用） | [AssetHeaderReader](src/SolsticeWinter.Index/AssetHeaderReader.md#readstring-assetpath-string-virtualpath-assetheader) |
| AssetHeaderReader | `ReparseClass(UAsset,string,Func<…>): ClassExport?` | ✅ ⛔ | 类导出降级成 `RawExport` 时的兜底重读（必须 `ConvertToChildExport`） | [AssetHeaderReader](src/SolsticeWinter.Index/AssetHeaderReader.md#reparseclassuasset-asset-string-assetpath-funcfpackageindex-string-resolve-classexport) |
| AssetHeaderReader | `ResolveIndex(UAsset,FPackageIndex?): string` | ✅ ⛔ | 包索引 → `包.外层.对象`；导入链最多爬 16 层 | [AssetHeaderReader](src/SolsticeWinter.Index/AssetHeaderReader.md#resolveindexuasset-asset-fpackageindex-index-string) |
| SymbolIndex | `FormatVersion` / `Version` / `BuiltFrom` / `BuiltAt` / `Assets` | ✅ | JSON 顶层字段与版本闸门 | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#formatversion--version--builtfrom--builtat--assets) |
| SymbolIndex | `ByPath` / `FunctionsByName` / `ByClass` / `Referencers` | ✅ ⛔ | 四张查询字典，全部大小写敏感 | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#bypath--functionsbyname--byclass--referencers) |
| SymbolIndex | `FunctionHit(AssetHeader,FunctionInfo)` | ✅ | 函数候选 = 资产 + 函数 | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#functionhitassetheader-asset-functioninfo-function) |
| SymbolIndex | `Build(string,string,int,Action<string>?)` | ✅ ⛔ | 扫盘建索引：整树复制到临时目录再解析，坏资产只记 failure | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#buildstring-root-string-usmappath-int-limit--intmaxvalue-actionstring-log--null-symbolindex-index-liststring-failures) |
| SymbolIndex | `RebuildLookups(): void` | ✅ ⛔ | `Assets` → 四张字典（加载后必须调） | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#rebuildlookups-void) |
| SymbolIndex | `Save(string): void` | ✅ ⛔ | 单行 JSON 落盘，非原子写 | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#savestring-path-void) |
| SymbolIndex | `Load(string): SymbolIndex` | ✅ ⛔ | 反序列化 + 版本校验 + 重建字典 | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md#loadstring-path-symbolindex) |
| SymbolIndex | `Find(string): AssetHeader?` | ✅ | 路径精确查（忽略大小写） | [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md#findstring-virtualpath-assetheader) |
| SymbolIndex | `FindFunction(string): IReadOnlyList<FunctionHit>` | ✅ | 短名精确查（区分大小写，多候选） | [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md#findfunctionstring-name-ireadonlylistfunctionhit) |
| SymbolIndex | `SearchFunctions(string,int): IEnumerable<FunctionHit>` | ✅ ⛔ | 子串搜函数名，`limit` 截在「名字」上而不是命中条数上 | [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md#searchfunctionsstring-substring-int-limit--50-ienumerablefunctionhit) |
| SymbolIndex | `FindReferencers(string): IReadOnlyList<AssetHeader>` | ✅ ⛔ | 反查引用者；包级 `/Game/A/B` 与对象级 `/Game/A/B.B` 是两个键 | [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md#findreferencersstring-objectpath-ireadonlylistassetheader) |
| SymbolIndex | `Describe(): string` | ✅ ⛔ | 7 行规模摘要（两个「唯一」依赖字典已建） | [SymbolIndex.query](src/SolsticeWinter.Index/SymbolIndex.query.md#describe-string) |
| JmapCatalog | `JmapCatalog.Load(string): JmapCatalog` | ✅ ⛔ | 读 `functions.json`，13k 条 `/Script` 签名按短名分组 | [JmapCatalog](src/SolsticeWinter.Index/JmapCatalog.md#jmapcatalogladstring-path-jmapcatalog) |
| JmapCatalog | `JmapCatalog.Find(string): IReadOnlyList<ScriptFunction>` | ✅ ⛔ | 短名查候选；未命中返回空数组 | [JmapCatalog](src/SolsticeWinter.Index/JmapCatalog.md#jmapcatalogfindstring-name-ireadonlylistscriptfunction) |
| JmapCatalog | `JmapCatalog.Describe(): string` | ✅ | 一行摘要（条数 / 唯一短名） | [JmapCatalog](src/SolsticeWinter.Index/JmapCatalog.md#jmapcatalogdescribe-string) |
| SymbolIndex | `SymbolIndex.FindScriptFunction(string): IReadOnlyList<ScriptFunction>` | ✅ | 索引里的 `/Script` 签名查询（与 `/Game` 的 `FindFunction` 分开） | [SymbolIndex](src/SolsticeWinter.Index/SymbolIndex.md) |
| AssetProbe | `Prepare(string,string): (string,string,string)` | ✅ ⛔ | 复制资产对 + usmap 到临时目录，清理交给调用方 | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#preparestring-assetpath-string-usmappath-string-asset-string-usmap-string-workdir) |
| AssetProbe | `Exports(string,Usmap): IReadOnlyList<string>` | ✅ ⛔ | 导出表逐行打印（类型名暴露 `RawExport` 降级） | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#exportsstring-assetpath-usmap-usmap-ireadonlyliststring) |
| AssetProbe | `TryReparse(string,Usmap,int): string?` | ✅ ⛔ | 手动重读导出体；失败时给六段诊断而不是抛 | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#tryreparsestring-assetpath-usmap-usmap-int-exportindex-string) |
| AssetProbe | `SuperStructDiag(UAsset,int): string` | ✅ ⛔ | 扫 `OuterIndex=null` 的导入（`GetClassTypeForAncestry` 的 NRE 源头） | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#superstructdiaguasset-asset-int-exportindex-string) |
| AssetProbe | `StepByStep(UAsset,FPackageIndex?): string` | ✅ | 复现 `GetClassTypeForAncestry` 每一步，指出哪一步为空 | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#stepbystepuasset-asset-fpackageindex-classindex-string) |
| AssetProbe | `DescribeIndex(UAsset,FPackageIndex?): string` | ✅ | `FPackageIndex` → 单行描述（导出/导入/越界） | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#describeindexuasset-asset-fpackageindex-index-string) |
| AssetProbe | `ClassNameOf(UAsset,FPackageIndex?): string` | ✅ | 类索引 → 短名（`None` / `?` 兜底） | [AssetProbe](src/SolsticeWinter.Index/AssetProbe.md#classnameofuasset-asset-fpackageindex-classindex-string) |

### SolsticeWinter.App

| 模块 | 函数 | 状态 | 一句话职责 | 锚点 |
|---|---|---|---|---|
| App | `App.OnStartup(StartupEventArgs e): void` | ✅ | 分派「开窗口 / `--graph` 自检」，注册两级全局异常 | [App](src/SolsticeWinter.App/App.md#apponstartupstartupeventargs-e-void) |
| App | `App.Log(string where, Exception? ex, string? note = null): void` | ✅ | 追加 `crash.log`，吞掉自身异常 | [App](src/SolsticeWinter.App/App.md#applogstring-where-exception-ex-string-note--null-void) |
| MainViewModel | `AssetItem(string VirtualPath, string? ClassName, int FunctionCount)` | ✅ | 资产列表行：短名 + 带字节码函数数 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#assetitemstring-virtualpath-string-classname-int-functioncount) |
| MainViewModel | `FunctionItem(string Name, int Size, int Params)` | ✅ | 函数列表行：名字 + 字节数 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#functionitemstring-name-int-size-int-params) |
| MainViewModel | `MainViewModel()` | ✅ ⛔ | 构造时读 `GameProfile.Kards()`，不做 I/O | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#mainviewmodel) |
| MainViewModel | `FindIndex(string? explicitPath): string` | ✅ ⛔ | 索引路径四级回退：显式 → exe/.cache → cwd/.cache → 仓库根/.cache | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#findindexstring-explicitpath-string) |
| MainViewModel | `InitializeAsync(string? indexPath = null): Task` | ✅ ⛔ | 加载索引 → 填资产 → 自动选中；成败都写 `ui.log` | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#initializeasyncstring-indexpath--null-task) |
| MainViewModel | `SearchText` / `SelectedAsset` / `SelectedFunction` / `SelectedNode` / `Status` / `Diagnostics` / 四条集合 | ✅ ⛔ | 三个选择型 setter 带加载副作用；`SelectedNode` 只有回退视图会写 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#searchtext--selectedasset--selectedfunction--selectednode--status--diagnostics--四条集合) |
| MainViewModel | `AutoSelect(): void` | ✅ ⛔ | 优先 `/…/BP_Logic`，否则带字节码函数最多者 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#autoselect-void) |
| MainViewModel | `RefreshAssets(): void` | ✅ ⛔ | 过滤（有字节码 + 路径子串）、排序、`Take(400)` | [MainViewModel](src/SolsticeWinter.App/MainViewModel.md#refreshassets-void) |
| MainViewModel | `LoadFunctionsAsync(AssetItem asset): Task` | ✅ ⛔ | 填函数列表并自动挑 250–1500 字节中最大者（无真实异步） | [MainViewModel](src/SolsticeWinter.App/MainViewModel.load.md#loadfunctionsasyncassetitem-asset-task) |
| MainViewModel | `LoadGraphAsync(FunctionItem function): Task` | ✅ ⛔ | 后台反编译 + `_loadVersion` 丢弃过期结果；ubergraph 只取一段 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.load.md#loadgraphasyncfunctionitem-function-task) |
| MainViewModel | `GetWorkspace(AssetItem asset): KismetWorkspace` | ✅ ⛔ | 每资产缓存 workspace，永不失效、非线程安全 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.load.md#getworkspaceassetitem-asset-kismetworkspace) |
| MainViewModel | `UiLog.Write(string message): void` | ✅ | 写 `%TEMP%\solstice-winter-ui\ui.log`——「UI 真渲染了」的唯一证据 | [MainViewModel](src/SolsticeWinter.App/MainViewModel.load.md#uilogwritestring-message-void) |
| GraphNodeVm | `ConnectorVm` | ✅ | 一个引脚：`Anchor` 由视图 `OneWayToSource` 回填 | [GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md#connectorvm) |
| GraphNodeVm | `ConnectionVm(ConnectorVm output, ConnectorVm input)` | ✅ ⛔ | 简化入口：`IsExec` 恒 false、不动 `IsConnected` | [GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md#connectionvmconnectorvm-output-connectorvm-input) |
| GraphNodeVm | `ConnectionVm(ConnectorVm output, ConnectorVm input, bool isExec)` | ✅ | 生产入口：定线型并置两端 `IsConnected` | [GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md#connectionvmconnectorvm-output-connectorvm-input-bool-isexec) |
| GraphNodeVm | `GraphNodeVm` | ✅ | 一个节点：`Location` 可写，`Inputs`/`Outputs` 是集合 | [GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md#graphnodevm) |
| GraphNodeVm | `GraphNodeVm.AllInputs` | ✅ ⛔ | exec 优先的输入脚视图（当前无消费者） | [GraphNodeVm](src/SolsticeWinter.App/ViewModels.GraphNodeVm.md#graphnodevmallinputs) |
| GraphBuilder | `GraphBuilder.Build(IReadOnlyList<KNode> roots): (IReadOnlyList<GraphNodeVm> Nodes, IReadOnlyList<ConnectionVm> Connections)` | ✅ | AST → 平铺图的总入口 | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.md#graphbuilderbuildireadonlylistknode-roots-ireadonlylistgraphnodevm-nodes-ireadonlylistconnectionvm-connections) |
| GraphBuilder | `GraphBuilder.BuildChain(IReadOnlyList<KNode> nodes, double x, double y, List<ConnectorVm> dangling): List<ConnectorVm>` | ✅ ⛔ | 串一条 exec 链，返回末端悬空脚；Branch/While 无 exec 入脚 | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.md#graphbuilderbuildchainireadonlylistknode-nodes-double-x-double-y-listconnectorvm-dangling-listconnectorvm) |
| GraphBuilder | `GraphBuilder.BuildArg(KArg? arg, ConnectorVm pin, double x, double y): void` | ✅ ⛔ | 嵌套调用 → 数据节点；同级调用坐标重叠 | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.md#graphbuilderbuildargkarg-arg-connectorvm-pin-double-x-double-y-void) |
| GraphBuilder | `GraphBuilder.CreateCallNode(CallArg call, double x, double y, bool isExec): GraphNodeVm` | ✅ ⛔ | 把「当值用的调用」造成数据节点（`isExec` 恒 false） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuildercreatecallnodecallarg-call-double-x-double-y-bool-isexec-graphnodevm) |
| GraphBuilder | `GraphBuilder.CreateNode(KNode node, double x, double y): GraphNodeVm` | ✅ ⛔ | 语句级节点 + 按类型加引脚；不透明节点零引脚 | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuildercreatenodeknode-node-double-x-double-y-graphnodevm) |
| GraphBuilder | `GraphBuilder.AddExecPins(GraphNodeVm vm, bool withOutput = true): void` | ✅ | 补 exec 入脚（+ 可选出脚） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuilderaddexecpinsgraphnodevm-vm-bool-withoutput--true-void) |
| GraphBuilder | `GraphBuilder.ExecPin(string title, bool isInput): ConnectorVm` | ✅ | 造 exec 脚（白） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuilderexecpinstring-title-bool-isinput-connectorvm) |
| GraphBuilder | `GraphBuilder.DataPin(string title, string typeName, bool isInput): ConnectorVm` | ✅ | 造数据脚（按类型配色） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuilderdatapinstring-title-string-typename-bool-isinput-connectorvm) |
| GraphBuilder | `GraphBuilder.Connect(ConnectorVm output, ConnectorVm input, bool isExec): void` | ✅ ⛔ | 追加一条连线，不做方向/类型校验 | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.nodes.md#graphbuilderconnectconnectorvm-output-connectorvm-input-bool-isexec-void) |
| GraphBuilder | `GraphBuilder.Describe(KNode node): (string Title, string Kind, string Tooltip)` | ✅ ⛔ | 节点 → 标题/种类/提示（未知类型退到 `Opaque`） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.mapping.md#graphbuilderdescribeknode-node-string-title-string-kind-string-tooltip) |
| GraphBuilder | `GraphBuilder.ArgTypeName(KArg arg): string` | ✅ ⛔ | 参数 → 类型名；`PointerArg` 返回变量名，`LocalArg` 落 `unknown` | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.mapping.md#graphbuilderargtypenamekarg-arg-string) |
| GraphBuilder | `GraphBuilder.BrushOf(string typeName): Brush` | ✅ ⛔ | 类型名 → 颜色（与 `Themes/Dark.xaml` 的 `Pin.*` 重复） | [GraphBuilder](src/SolsticeWinter.App/Graph.GraphBuilder.mapping.md#graphbuilderbrushofstring-typename-brush) |
| AssetAccess | `AssetAccess.ExportPathFor(GameProfile profile, string virtualPath): string` | ✅ ⛔ | 虚拟路径 → `<导出目录>\…\X.uasset`，只认 `/Game/` 前缀 | [AssetAccess](src/SolsticeWinter.App/Graph.AssetAccess.md#assetaccessexportpathforgameprofile-profile-string-virtualpath-string) |
| AssetAccess | `AssetAccess.OpenWorkspace(GameProfile profile, string virtualPath): KismetWorkspace` | ✅ ⛔ | 读三份字节 + 在 `%TEMP%` / `%LOCALAPPDATA%` 两个候选里挑可写工作目录（按 PID 隔离） | [AssetAccess](src/SolsticeWinter.App/Graph.AssetAccess.md#assetaccessopenworkspacegameprofile-profile-string-virtualpath-kismetworkspace) |
| AssetAccess | `AssetAccess.CleanStaleWorkDirs(): void` | ✅ ⛔ | 清理死实例留下的 `p<pid>` 工作目录（按进程名判活，尽力而为） | [AssetAccess](src/SolsticeWinter.App/Graph.AssetAccess.md#assetaccesscleanstaleworkdirs-void) |
| GraphProbe | `GraphProbe.Run(string virtualPath, string functionName, int limit = 40): int` | ✅ ⛔ | 无窗口自检，结果覆盖写 `graph.txt`，返回 0/1 | [GraphProbe](src/SolsticeWinter.App/Graph.GraphProbe.md#graphproberunstring-virtualpath-string-functionname-int-limit--40-int) |
| NodeCanvas | `NodeCanvas()` | ✅ | 搭自绘画布（**回退实现，当前无 XAML 引用**） | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#nodecanvas) |
| NodeCanvas | `Nodes` / `Connections` / `SelectedNode`（依赖属性） | ✅ ⛔ | 三个 DP；换引用才重画，`ObservableCollection` 原地变更不触发 | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#nodes--connections--selectednode依赖属性) |
| NodeCanvas | `Render(): void` | ✅ ⛔ | 全量重建视觉树（先线后节点）并归位到左上 | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#render-void) |
| NodeCanvas | `AnchorOf(ConnectorVm pin, bool isOutput, IReadOnlyList<GraphNodeVm> nodes): Point?` | ✅ ⛔ | 按常量算引脚锚点（不测量） | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#anchorofconnectorvm-pin-bool-isoutput-ireadonlylistgraphnodevm-nodes-point) |
| NodeCanvas | `CreateEdge(Point from, Point to, bool isExec): Path` | ✅ ⛔ | 三次贝塞尔连线（exec 实线 / 数据虚线） | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#createedgepoint-from-point-to-bool-isexec-path) |
| NodeCanvas | `CreateNodeVisual(GraphNodeVm node): Border` | ✅ ⛔ | 节点框（表头 + 左右引脚列 + 点击选中） | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#createnodevisualgraphnodevm-node-border) |
| NodeCanvas | `CreatePinRow(ConnectorVm pin, bool isInput): UIElement` | ✅ ⛔ | 一行引脚（exec 脚文字被硬写成 `exec`） | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#createpinrowconnectorvm-pin-bool-isinput-uielement) |
| NodeCanvas | `HeaderBrush(GraphNodeVm node): Brush` | ✅ ⛔ | `Kind` → 表头色 | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#headerbrushgraphnodevm-node-brush) |
| NodeCanvas | `GridBrush(): Brush` | ✅ | 15×15 平铺网格背景 | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#gridbrush-brush) |
| NodeCanvas | `OnWheel(MouseWheelEventArgs e): void` / `OnLeftDown` / `OnMove` / `OnLeftUp` | ✅ ⛔ | 以光标为锚缩放（0.2–3.0）+ 左键平移 | [NodeCanvas](src/SolsticeWinter.App/Controls.NodeCanvas.md#onwheelmousewheeleventargs-e-void--onleftdown--onmove--onleftup) |
| GraphView | `GraphView()` | ✅ | 解析 XAML（Nodify 版画布，**当前在用**） | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#graphview) |
| GraphView | `NodeInput` / `NodeOutput` 样式（引脚） | ✅ ⛔ | `Header`/`Anchor`(OneWayToSource)/`IsConnected` 三绑定 | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#nodeinput--nodeoutput-样式引脚) |
| GraphView | `NodeTemplate`（`DataTemplate`） | ✅ ⛔ | 节点模板：只绑 `Header`/`Input`/`Output`/`ToolTip` | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#nodetemplatedatatemplate) |
| GraphView | `ConnectionTemplate`（`CircuitConnection`） | ✅ ⛔ | `Source`/`Target` 绑两个 `Anchor`；`ArrowSize` 是 `Size` 不是 `double` | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#connectiontemplatecircuitconnection) |
| GraphView | `ItemContainerStyle`（`ItemContainer.Location`） | ✅ ⛔ | `Location` 双向绑，拖动不持久化 | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#itemcontainerstyleitemcontainerlocation) |
| GraphView | `Editor`（`NodifyEditor`） | ✅ ⛔ | `ItemsSource`/`Connections`/`AllowDrop=False`；**`SelectedNode` 未绑** | [GraphView](src/SolsticeWinter.App/Views.GraphView.md#editornodifyeditor) |
| MainWindow | `MainWindow(string? indexPath = null)` | ✅ ⛔ | 挂 `DataContext`，`Loaded` 里 `await InitializeAsync` | [MainWindow](src/SolsticeWinter.App/MainWindow.md#mainwindowstring-indexpath--null) |
| MainWindow | `MainWindow.xaml`：三栏骨架与标题条 | ✅ ⛔ | 300/·/300 固定三栏 + `资产 / 函数` 标题条 | [MainWindow](src/SolsticeWinter.App/MainWindow.md#mainwindowxaml三栏骨架与标题条) |
| MainWindow | `AssetList` / `FunctionList`（两个 ListBox） | ✅ ⛔ | 资产/函数列表 + 搜索框；`Clear()` 会回写 `SelectedItem=null` | [MainWindow](src/SolsticeWinter.App/MainWindow.md#assetlist--functionlist两个-listbox) |
| MainWindow | 详情面板（`SelectedNode` 绑定块） | ✅ ⛔ | 引脚清单；Nodify 视图下恒显示「未选择节点」 | [MainWindow](src/SolsticeWinter.App/MainWindow.md#详情面板selectednode-绑定块) |
| MainWindow | 状态条（`Status` / `Diagnostics`） | ✅ ⛔ | 左 `Status` 单行省略、右 `Diagnostics`（多行被压成一行） | [MainWindow](src/SolsticeWinter.App/MainWindow.md#状态条status--diagnostics) |
| Converters | `KindToBrushConverter.Convert(object?, Type, object?, CultureInfo): object` | ✅ ⛔ | 节点种类 → 标题栏颜色；未知种类兜底 Call | [KindToBrush](src/SolsticeWinter.App/Converters.KindToBrush.md#kindtobrushconverterconvertobject-type-object-cultureinfo-object) |
| Themes.Dark | `Chrome.*` 调色板（7 个键） | ✅ | 背景/面板/边框/前景/强调 | [Themes.Dark](src/SolsticeWinter.App/Themes.Dark.md#chrome-调色板7-个键) |
| Themes.Dark | `Pin.*` 引脚类型色（8 个键） | ✅ ⛔ | 与 `BrushOf` 同值但**无消费者** | [Themes.Dark](src/SolsticeWinter.App/Themes.Dark.md#pin-引脚类型色8-个键) |
| Themes.Dark | `TextBlock` / `TextBox` / `ListBox` / `ListBoxItem` 隐式样式 | ✅ ⛔ | 全局深色基础样式 | [Themes.Dark](src/SolsticeWinter.App/Themes.Dark.md#textblock--textbox--listbox--listboxitem-隐式样式) |
| Themes.Dark | `PanelHeader`（具名 `Border` 样式） | ✅ | 面板表头：底色 + 下边框 | [Themes.Dark](src/SolsticeWinter.App/Themes.Dark.md#panelheader具名-border-样式) |

## 3. 未实现清单（按影响面排序）

| # | 项 | 状态 | 缺什么 |
|---|---|---|---|
| 1 | 技术栈与交付形态 | ✅ 已定案 | D1：WPF + Nodify（见可行性评估 §5） |
| 2 | 第一里程碑编辑深度 | ✅ 已定案 | D2：既有函数内任意编辑；ubergraph 先 spike（D6） |
| 3 | pak 读取路径 | ✅ 已实现 | 只做**读**，不做写：v1..v11 的尾部/索引/条目解析、AES-256-ECB 解密、Oodle/Zlib/Gzip 解压、按路径提取全部落地并实测（见下节）。写路径（打包/回写）不在本轮范围 |
| 4 | 跨资产索引 | ✅ 已实现 | 扫描器 + 索引结构 + 四张查询字典（含 `ByClass`）已落地并实测（见下节）。仍缺：与 jmap 的合并规则、`MembersOfClass` 的「类 → 成员」扁平表、缓存指纹与增量重建 |
| 5 | GraphModel 类型推断 | 🚧 | 五类类型来源的合并；`RawArg` 节点的不透明表示 |
| 6 | ubergraph 重定位层 | 🚧 | 段内重排时改写全部调用点常量 + 复核执行流栈目标 |
| 7 | 新建事件/函数 | ⛔ 边界 | 包结构手术未验证；先用「复用 stub」路线绕过 |
| 8 | 运行时验证通道 | ⛔ 边界 | 全链路只证明了字节层一致 |
| 9 | pak 版本 > 11 | ⛔ 边界 | v12+ 未实测；v9 的 frozen 索引、v10+ 的 `PFDQ` 扁平目录索引都不解析（会明确抛错，不会静默出错） |
| 10 | LZ4 / Zstd 压缩条目 | ⛔ 边界 | `PakArchive.Read` 只解 None/Oodle/Zlib/Gzip，遇到其它方法抛错并写明方法名 |
| 11 | 加密形态 | ⛔ 边界 | 只支持 AES-256-ECB（索引与条目）；AES-CTR（UE5.5+ IoStore 风格）不支持 |
| 12 | IoStore（`.utoc`/`.ucas`） | ⛔ 边界 | 完全不在本模块范围 |
| 13 | Zlib/Gzip 分支的真实样本 | 🚧 | 代码在，本机 pak 全是 Oodle，没有端到端验证样本 |
| 14 | 批量提取（还原目录树） | 🚧 | `Extract` 只写文件名；整包还原命令未做 |

## 4. 实测记录（本轮验收）

对 `D:\files\projects\kards\Content\Paks\kards-Android_ASTC.pak`（1,170,194,667 字节）：

| 项 | 结果 |
|---|---|
| 版本 / 尾部 | v11 / 221 字节 @ 1170194446 |
| 索引 | 633072 字节 @ 1167833486，AES 加密，SHA-1 与 footer 一致 |
| 条目数 | 33808（主索引声明 33808，目录索引给出 33808，无 `Warnings`） |
| 声明总解压字节 | 2,256,615,510 |
| 压缩方法表 | `[0]None, [1]Oodle` |
| 挂载点 | `../../../` |
| `verify --limit 200` | 成功 200，失败 0 |
| 与 FModel 导出逐字节对拍 | 4274 / 4274 个 `.uasset`+`.uexp` 全部一致（25,696,587 字节） |

### 索引构建实测（`SolsticeWinter.Index`）

`index build` 对 `D:\devenv\fmodel\Output\Exports\kards\Content`（2137 个 `.uasset`，只读导出目录）：

| 项 | 结果 |
|---|---|
| 资产 | 2137（解析失败 0 个） |
| 函数 | 9303（`HasBytecode` 全部为 true） |
| 变量 | 5678 |
| 引用边 | 69446（无资产的 `References` 为空） |
| 唯一函数名 | 4856（`FunctionsByName` 键数，`Ordinal` 比较） |
| 唯一被引对象 | 4611（`Referencers` 键数） |
| 耗时 | 约 6.6 s（单线程，含整树复制到 `%TEMP%`） |
| 索引 JSON | 7,326,426 字节（单行；加载 + 建字典 296 ms，见 [设计-VFS与上下文索引](设计-VFS与上下文索引.md) §4.2） |

交叉验证：

- `KismetProbe`（solstice-kismet 的独立工具）对 `BP_Logic` 报「181 个带字节码的函数」，索引也是 181；同一资产的类导出 133 个属性 = 索引 133 个变量（`ExportCount=199` / `ImportCount=661`）。

三个实测坑（都已在代码里处理，细节见各模块 md 的「取舍」）：

1. UAssetAPI 用 `File.Open(ReadWrite)` 打开 `.uexp` 与 usmap，**只读目录会抛 `UnauthorizedAccessException`**，所以 `SymbolIndex.Build` 先把整棵树复制到临时目录再解析（`AssetProbe.Prepare` 同理）。
2. `BP_Logic`（199 个导出）的生成类导出在正常加载时会**静默降级成 `RawExport`**（`GetClassExport()` 返回 null）。兜底 `ReparseClass` 用 `ConvertToChildExport<ClassExport>()` + `SerialOffset + SerialSize` 当边界重读，能读出 133 个属性 / 181 个 FuncMap 项。**必须用 `ConvertToChildExport`**：`new ClassExport(export)` 会留下 null 的 `Asset`，读的时候在 `GetClassTypeForAncestry` NRE。
3. 函数的属性表里同时装**参数与局部变量**（`CallFunc_*` / `K2Node_*` / `Temp_*` 都是局部变量），必须按 `CPF_Parm` 过滤；`CPF_ReturnParm` 判返回；`out && !ref` 才是真输出。索引 JSON 里因此没有任何一条 `CPF_Parm` 记录（14343 个参数：`in` 10763 / `out` 3538 / `return` 42）。

### UI 首次运行实测（`SolsticeWinter.App`）

`SolsticeWinter.App.exe <index.json>` 启动后进程存活，窗口标题 `Solstice-winter — cooked 蓝图工坊`，无崩溃。**模型不支持读图**，所以「UI 真的渲染了」只能靠 `%TEMP%\solstice-winter-ui\ui.log` 里 `UiLog` 写的那行字证明（崩溃则看同目录的 `crash.log`）。

| 项 | 结果 |
|---|---|
| 启动 | 进程存活、窗口出现、无异常（`crash.log` 无新增） |
| 自动选中 | 优先 `/Game/.../BP_Logic`；否则带字节码函数最多的资产。函数取 `250 < Size < 1500` 中最大者 |
| `BP_Logic / DrawTopCardFromDeck` | **52 节点 / 46 连线**（`ui.log` 有对应记录） |
| `--graph <路径> AddCardStats` | 16 节点 / 15 连线 |
| `--graph <路径> ExecuteUbergraph_BP_Logic` | 取有名字的段（`ObjectConst.GetDSession`）：6 节点 / 4 连线 |

两个坑（都已在代码里处理，细节见各 md 的「边界与失败」）：

1. Nodify 7 的 `CircuitConnection.ArrowSize` 是 `Size` 而不是 `double`：写 `"8"` 会在 XAML 解析时抛 `XamlParseException`（`Size.Parse("8")` 失败），必须写成 `"8,8"`。当前模板**不设**该属性，所以现在不会炸。
2. `KismetWorkspace.Decompile` 对 ubergraph 返回的是 `[UberGraphNode, ReturnNode]`（**不是单元素**），所以判断必须用 `OfType<UberGraphNode>().FirstOrDefault()`，不能取 `[0]` 也不能用 `is UberGraphNode` 判整个返回值。




### 新反编译器接入实测（`SolsticeWinter.Decompiler` + `GraphAdapter`）

| 项 | 数字 |
|---|---|
| 字节码 → 基本块（`BP_Logic` ubergraph） | 1536 指令 / 45675 字节 → **410 块 / 348 跳转 / 42 序列 / 50 入口** |
| 线性函数（`AddCardStats`） | 16 指令 / 791 字节 → 1 块 / 0 跳转 |
| 走查器产出（`startGameReplay` 入口 20764） | **125 节点**，10 条问题（全部「循环回边未展开」） |
| UI 渲染 | 旧投影 39 节点 / 37 连线 → **新反编译器 125 节点 / 85 连线** |

### 反编译视图对齐 UEBGV（本轮）

| 项 | 数字 |
|---|---|
| 批量走查（`BP_Logic` 181 个函数） | **空视图 0**、悬空边 0、不透明节点 107 |
| 引脚命名（接 jmap 前 → 后） | `argN` 引脚 **749 → 200**（73% 有真名） |
| 普通函数视图 | 之前不渲染（UI 只给事件图建了路径）→ 现在 `AddCardStats` 30 节点 / 15 连线 |
| Cast 节点 | `Let <local> = Cast(X)` → `Cast To X`（then / Cast Failed / As X 三脚） |
| 布局 | 简单分列 → **Msagl Sugiyama**（125 节点约 0.5 s） |

### 节点覆盖率（第二轮）

| 项 | 数字 |
|---|---|
| `BP_Logic` 181 个函数的不透明节点 | **124 → 0**（每条顶层指令都还原成真节点） |
| 其中被消掉的来源 | `PopExecutionFlow(33)` / `PopExecutionFlowIfNot(14)` / `LetValueOnPersistentFrame(19)` / `SetArray(9)` / `Context(49)` |
| 循环 | 回边 → `Loop` 节点（`startGameReplay` 有 2 个） |
| Cast | `Cast To X`（then / Cast Failed / As X） |
| 事件图渲染 | 150 节点 / 141 连线 |
| 普通函数渲染 | `StartTurnBySide` 28 节点 / 25 连线 |

### 与 UEBGV 真实节点对账（第三轮）

| 项 | 数字 |
|---|---|
| UEBGV 构建 | CUE4Parse 子模块 + Avalonia restore 28.8 s + build 65.1 s；`tools/Dump` 跑一次 2.7 s |
| 我们侧导出 | `SolsticeWinter.App.exe --export <资产> <函数> <json>`（含真实 Msagl 坐标与 problems） |
| `AddCardStats` | 我们 **25 节点 / 24 边**，UEBGV **18 节点 / 31 边** |
| 节点类型 | 已对齐（都有 FunctionEntry / MakeStruct / CallFunction） |
| 剩余差 | 6 个 `VariableSet`（成员写入被 Get 打断）+ 7 条边 |
| 顺带修 | 工作目录从 `%TEMP%` 改到仓库 `.cache\work`（沙箱/杀软给 temp 打拒绝 ACE，正是「文件夹被占用」的根因） |

详见 [对比-UEBGV](对比-UEBGV.md)。

### 运算符折叠（第四轮）

| 项 | 数字 |
|---|---|
| `ExecuteAttackCard` 的 `PromotableOperator` | **0 → 30**（`Greater_IntInt`→`>`、`BooleanAND`→`AND`） |
| 关键点 | 三个构造点都要查表；算术几乎都走 `NestedCall`（作为值出现） |
| 批量回归（181 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |
| 试过但回退 | 按路径展开共享尾 → 107 节点炸到 4000 上限，UEBGV 用的是分支上下文消费指令 |

### 分支数根因定位（第五轮）

| 项 | 结果 |
|---|---|
| `ExecuteAttackCard` 直方图 | 175 指令 / 50 块 / 39 跳转 / 4 序列；`JumpIfNot=9`、`PopExecutionFlowIfNot=19` |
| 关键等式 | **9 + 19 = 28 = UEBGV 的 `IfThenElse`** |
| 我们的差距 | 那些块要么是序列块（`EmitSequence` 提前 return），要么只能经执行流栈到达，实测「见到 Pop* 次数 = 0」 |
| 新增诊断 | `SolsticeWinter.App.exe --hist <资产> <函数>` 打印指令直方图 + 块/跳转/序列统计 |
| 新增节点 | `MakeArray`（`EX_ArrayConst` / 临时变量形态的 `EX_SetArray`） |
| 回归 | `BP_Logic` 181 函数 0/0/0；`BP_CardFunctions` 294 函数 空视图 0 / 悬空 0 / 不透明 1（`SetSet`） |

### 分支作用域与展开实验（第六轮）

| 项 | 结果 |
|---|---|
| 临时变量表分支作用域 | 已实现（`SnapshotTemps`/`RestoreTemps`，`JumpIfNot` 两支各一份） |
| 展开实验 | 按块 8 次/按位置 2 次/按位置 1 次 → 节点 4000/242/151，argN 214→637/704 |
| 结论 | 重复走块只复制同一条链；UEBGV 的 227 节点来自 28 个分支里的数据 Get |
| 基线状态 | 107 节点 / 85 边，argN 214，不透明 0，悬空 0（`BP_Logic` 181 函数） |

### 执行流栈语义修正（第七轮）

| 项 | 结果 |
|---|---|
| 根因 | `Pop*` 的 case 在重构中丢失，只剩 `=> null`（当无意义指令跳过） |
| 修正 | `PopIfNot` → Branch 节点；`Pop` → 结束当前链（后续是死代码）；`Push` → 序列点 |
| `ExecuteAttackCard` | `IfThenElse` 7 → **13**；节点 107 → 76（去掉了弹栈后的不可达节点）；调用 48 → 33 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0（CardFunctions 1 个 `SetSet`） |
| 判断 | 节点数下降是**预期**：之前把死代码也画了 |

### 返回地址建模（第八轮）

| 项 | 结果 |
|---|---|
| `PopExecutionFlow` | 从「结束链」改成**跳到 Push 记住的返回地址**（`SourceIndex+k+1`） |
| `returnTo` 透传 | 线性落点 / `Jump` / `JumpIfNot` 两支 / `Pop*` 全部透传 |
| 新诊断 | `--walk` 打印 `PopIfNot 命中 N`；`ExecuteAttackCard` = **9/19** |
| 关键观察 | `VariableGet` 我们 2 个、UEBGV 85 个 → 差的是**分支覆盖**（13 vs 28），不是折叠过度 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0（CardFunctions 1） |

### 工作队列重写（第九轮）

| 项 | 结果 |
|---|---|
| walker 模型 | 递归 → **显式工作队列**，每项自带 (块,下标,入口脚,返回地址,temps 快照) |
| 顶层指令只发一次 | Set 107 → 12（之前汇合路径重复渲染 20 次） |
| 变量读发节点 | Get 9 → 136；`ExecuteAttackCard` 总数 **228 vs UEBGV 227** |
| 分支 | `IfThenElse` 17（上轮 13，UEBGV 28）；`PopIfNot` 命中 17/19 |
| 回归 | 475 函数：空视图 0 / **悬空边 0**；CardFunctions 不透明 9 |
| 待修 | `AddCardStats` 39 vs 18（变量读节点重复造，下轮去重） |

### 上下文对象缓存与分支诊断（第十轮）

| 项 | 结果 |
|---|---|
| 上下文对象复用 | 同名变量只建一个 VariableGet；`AddCardStats` 39 → **27**（Get 28 → 16） |
| 分支诊断 | 新增 `JumpIfNot` 计数器：`ExecuteAttackCard` = PopIfNot 12 + JumpIfNot 5 = **17**（字节码里是 19+9=28） |
| 链遍历上限 | 从 2 调到 4 无变化 → 说明剩余 11 个分支不在「遍历次数」上，而在**可达性**上 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0（CardFunctions 9） |
| 当前对账 | `ExecuteAttackCard` 206 节点 / 189 边（UEBGV 227 / 294）；`AddCardStats` 27 / 24（UEBGV 18 / 31） |

### 分支覆盖补齐（第十一轮）

| 项 | 结果 |
|---|---|
| 根因 | `PopExecutionFlowIfNot` 是块尾指令，「真」分支写成 `i+1` 越界 → 链断，26 个块从未入队 |
| 修法 | 「真」分支改为**顺序下一块**（`NextBlock`） |
| 效果 | 未入队块 26 → **0**；`IfThenElse` 17 → **28 = UEBGV 28**；序列 3 → **4 = UEBGV 4** |
| 节点 | 206 → 327（UEBGV 227）；`AddCardStats` 27/24（UEBGV 18/31） |
| 新诊断 | `[未入队] 块 X type=Y 跳转来自[..] 序列来自[..]` |
| 剩余 | `VariableGet` 184 vs 85（上下文成员 Get 未复用）、`CallFunction` 59 vs 75 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0（CardFunctions 9） |

### 不透明节点清零（第十二轮）

| 项 | 结果 |
|---|---|
| 剩余不透明 | `EX_SetSet`(8) + `EX_SetMap`(1) |
| 处理 | 和 `EX_SetArray` 同族，元素铺成输入脚（`MakeSetContainer`） |
| 回归 | `BP_Logic` 181 + `BP_CardFunctions` 294 = **475 函数，不透明 0 / 空视图 0 / 悬空边 0** |
| 结构对齐 | `IfThenElse` 28 = UEBGV 28；`ExecutionSequence` 4 = 4；未入队块 0 |
| 未解决 | `VariableGet` 184 vs 85（成员缓存键没命中，实际来源是 `ReadVariable` 路径） |

### 分支命中率与 Get 粒度（第十三轮）

| 项 | 结果 |
|---|---|
| 分支命中 | `PopIfNot 19 + JumpIfNot 9 = 28`，与字节码 28 条分支指令**完全一致**（此前 12+5） |
| Get 粒度 | 184 个 = **一次变量读一个节点**；UEBGV 85 是因为它把部分读内联进引脚值，属显示粒度取舍 |
| 结构对齐 | `IfThenElse` 28=28、`ExecutionSequence` 4=4、未入队块 0、悬空边 0、不透明 0 |
| 475 函数回归 | 空视图 0 / 悬空边 0 / 不透明 0 |

### 渲染验证与 UI 修复（第十三轮）

| 项 | 结果 |
|---|---|
| `--screenshot <资产> <函数> <png>` | 新增（画布渲成 PNG，用像素统计判断有没有画出来） |
| 修 `--open` | 之前只传函数不传资产 → 打开默认资产、找不到函数、画布空白 |
| 修普通函数渲染 | 跳过旧的 `ws.Decompile`（大函数上抛异常/极慢），直接走新反编译器 |
| 修 UI 日志 | `%TEMP%` → 仓库 `.cache\ui.log`（沙箱里每进程临时目录不同，日志会消失） |
| 诊断开关 | 「未入队块」只在探针输出；事件图噪声 18 → **0** |
| 实测 | `ExecuteAttackCard` 327 节点 / 300 连线，2.2 s 渲染完成 |
| 未完成 | 截图自动缩放未生效（大图节点从 x=12779 开始，视口在 (0,0) 截到纯背景） |

### 结构化对账（第十四轮）

| 项 | 结果 |
|---|---|
| 对账方法 | 按 `类型|标题` 配对两边导出的节点，列出单侧独有 |
| 修 1 | 布尔运算不再折成运算符（UEBGV 保留 `CallFunction`）→ `CallFunction` **75 = 75** |
| 修 2 | `VariableGet`/`VariableSet` 标题留空、名字进正文（UEBGV 同款） |
| 修后分布 | `PromotableOperator` **17=17**、`IfThenElse` **28=28**、`ExecutionSequence` **4=4**、`FunctionEntry` **1=1** |
| 唯一实质差异 | `VariableGet` 184 vs 85（多出的 99 正好等于总数差 327-227） |
| Get 来源 | 166 来自 `ReadVariable`、3 来自 `ProducerOf`、0 来自上下文；UEBGV 把 Set 的值参数内联不建节点 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 节点形状对齐（第十五轮）

| 项 | 结果 |
|---|---|
| Set 形状 | 标题 `SET` + 值引脚名=变量名（原来是「标题带变量名、引脚无名」） |
| Get 形状 | 标题空 + 变量名在输出脚（原来是标题=变量名） |
| 关键教训 | 必须改**模型**（`BPNode.Name`/引脚名），只改 UI 适配器不会反映到导出对账里 |
| 类型对齐 | `CallFunction` 75=75、`PromotableOperator` 17=17、`IfThenElse` 28=28、`ExecutionSequence` 4=4、`FunctionEntry` 1=1 |
| 剩余 | `VariableGet` 184 vs 85（166 个来自普通局部变量读，UEBGV 多数内联进引脚值） |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 变量读粒度（第十六轮）

| 项 | 结果 |
|---|---|
| 推理依据 | UEBGV 的 85 个 Get 全带 `Target` 输入脚 → 都是成员读；裸局部读不建节点 |
| 改动 | `ReadVariable` 里普通局部读改为「名字写引脚、不建节点」 |
| 效果 | `ExecuteAttackCard`：Get 184 → **18**，节点 327 → **161**（UEBGV 85 / 227） |
| 判断 | 改过头了：从「多 99」变成「少 66」，说明它们的规则更细（可能按语句去重） |
| 反例 | `AddCardStats` 27/24 vs 18/31，Get 16 vs 14 —— 那个函数的读都是成员读，仍走建节点路径 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 按源码规则对齐变量读（第十七轮）

| 项 | 结果 |
|---|---|
| 方法 | 直接读 UEBGV 的 `FunctionDecompiler.cs`（`ArgToPin` / `ProcessLetNode` / `IsTempVar`） |
| 关键规则 | 变量在局部表 → 复用引脚；调用/cast 赋值 → 不发 Set 节点；`IsTempVar()` 只认 `Temp_` 前缀 |
| 改动 | `MakeSetOrTemp`：调用/cast 赋值只登记不发节点 |
| `AddCardStats` | 27 → **21 节点**（UEBGV 18）；类型差异只剩 2 个多余 Get + 1 个 `FunctionResult` |
| `ExecuteAttackCard` | 161 → **157 节点**（UEBGV 227）；差的全是 `VariableGet`（18 vs 85） |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 参数进局部表（第十八轮）

| 项 | 结果 |
|---|---|
| 源码依据 | `ParseContext`（:968）递归调用 `ArgToPin`、**无缓存**；`LocalVars` 包含函数参数 |
| 改动 1 | 去掉上下文对象缓存 → 攻击函数 Get 18 → 51 |
| 改动 2 | `FunctionEntry` 参数登记进 `_temps` → 参数读复用引脚、不建节点 |
| `AddCardStats` | **19 节点（Get 14）** vs UEBGV **18（Get 14）** —— Get 数完全一致，只多 1 个 `FunctionResult` |
| `ExecuteAttackCard` | 155（Get 16）vs 227（Get 85）—— 方向相反，尚未解释清楚 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 变量读的真相（第十九轮）

| 项 | 结果 |
|---|---|
| 新诊断 | `--hist` 增加**嵌套表达式**统计（原来只数顶层指令） |
| 关键数据 | `LocalVariable=90`、`Context=25`、`InstanceVariable=5`、`LetBool=34` |
| 推论 | UEBGV 的 85 个 Get ≈ 90 个局部读，即「一次读一个节点」 |
| 对齐 | `IsTempName` 改成只认 `Temp_`（与它们的 `IsTempVar()` 一致） |
| `AddCardStats` | **19 vs 18**，Get **14 = 14** |
| `ExecuteAttackCard` | 155 vs 227（Get 16 vs 85）—— 差异在「调用赋值过的局部变量」的读处理 |
| 可视化验证 | 截图 373 种颜色：画布/节点正文/标题色/文字都在 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### out 参数与 IsCallLike（第二十轮）

| 项 | 结果 |
|---|---|
| 验证方法 | 拿三个**简单函数**（4~7 个节点）对比，差异立刻显形 |
| 根因 1 | `EX_LocalOutVariable`（out 参数）赋值被我们当成 Set；UEBGV 只登记不发节点 |
| 修后 | `GetTurnNumber` **4/3，类型完全一致** |
| 根因 2 | `IsCallLike` 把 `EX_Context` 一律当调用 → 结构体成员写入被折掉、留 6 个孤立 Get |
| 根因 3 | `ProducerOf` 默认分支返回消费者脚 → 6 条悬空边 |
| 回归（475 函数） | 空视图 0 / **悬空边 0** / 不透明 0 |
| 仍差 | `AddCardStats` 25 vs 18（跨块的成员写入没并进 MakeStruct）、`ExecuteAttackCard` 159 vs 227 |

### 跨块结构体构造（第二十一轮）

| 项 | 结果 |
|---|---|
| 问题 | 6 个成员写入跨块，同块收集够不到 → 退化成 `VariableSet` |
| 方案 | 挂起累积：成员写入先攒着，`Add` 前 flush 成一个 `MakeStruct` |
| 踩坑 | 「遇到别的指令就 flush」→ 7 个 MakeStruct；「只在链尾 flush」→ 中途 return 全丢 |
| `AddCardStats` | **19 节点 / 18 边**，类型分布与 UEBGV **完全一致**（只多 1 个 `FunctionResult`） |
| `GetTurnNumber` | **4/3 = 4/3**，逐节点逐边一致 |
| `ExecuteAttackCard` | 159/428 vs 227/294（`VariableGet` 16 vs 85） |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 边的成分分析（第二十二轮）

| 项 | 结果 |
|---|---|
| 反常 | 我们节点更少（159 vs 227）但边更多（428 vs 294） |
| 查证 | **不是重复连线**（重复组数 0）；exec 边 86 vs 83 已对齐，差异全在 data 边（342 vs 211） |
| 结论 | 16 个 Get 扇出 342 条边（~21/个），他们 85 个 Get 挂 211 条（~2.5/个）—— 同一现象的两面 |
| 试过并回退 | 不登记函数参数（攻击 Get 16→51 但 AddCardStats 19→32、GetTurnNumber 边 3→1） |
| 当前 | `GetTurnNumber` **4/3 = 4/3**、`AddCardStats` **19/18（类型一致）**、`ExecuteAttackCard` 159/428 |
| 回归（475 函数） | 空视图 0 / 悬空边 0 / 不透明 0 |

### 视图对齐收口（第二十三轮）

| 维度 | 状态 |
|---|---|
| 节点类型 | 8/8 具备；形状一致（`SET`+变量名脚、Get 空标题+Target、运算符折叠） |
| 控制流 | `IfThenElse` 28=28、`ExecutionSequence` 4=4、exec 边 86 vs 83 |
| 小函数 | `GetTurnNumber` **4/3 = 4/3**（逐节点逐边一致） |
| 中等函数 | `AddCardStats` **19/18 vs 18/31**（类型分布一致，+1 `FunctionResult`） |
| 复杂函数 | `ExecuteAttackCard` 159/428 vs 227/294（`VariableGet` 16 vs 85） |
| 健壮性 | 475 函数：空视图 0 / 悬空边 0 / 不透明 0 |
| UI | `startGameReplay` 68 节点 / 49 连线，0 问题 |
| 粒度开关 | `PerUseVariableGets` / `SOLSTICE_WINTER_PERUSE_GETS=1`（默认关，开了会切断临时变量连线） |

### 全量节点诊断（BP_Logic 181 函数）

工具：`SolsticeWinter.App.exe --audit <资产>` → `.cache\probe\audit.md`

| 指标 | 数值 |
|---|---|
| 函数 / 节点 / 边 | 181 / 2351 / 3497 |
| 空视图 | **0** |
| **孤立节点（无任何连线）** | **98 个，涉及 42 个函数** |
| 不透明节点 | 0 |
| 未命名引脚 argN | 582 / 4880（11.9%） |

孤立最多的函数：`GetCantAttackText` 19、`ResolveMulliganResponse` 9、`ShouldSkipAnimationsAndSetupBoard` 8、`ResyncMatch` 7。

### 本轮修掉的缺陷

| 缺陷 | 证据 | 修法 |
|---|---|---|
| **Return 节点不接 out 参数** | `GetCantAttackText` 用 out 参数返回值，字节码是 `EX_Nothing`，我们没加引脚 → Return 只有 exec、上游全悬空 | `MakeReturn` 在无返回值时，把登记过的 out 参数接到 Return 上；边 64 → **65**（与 UEBGV 一致） |
| 嵌套表达式重复建节点 | 顶层 `CallMath` 只有 2 个，却产出 19 个 `!=` 节点 | 加 `_nestedCache`（按表达式实例缓存输出脚） |

### 下一个待修（已定位）

`GetCantAttackText` 仍有 18 个孤立 `VariableGet`：它们是 `EX_Context` 的成员读（`text_Notifications.X`），
19 个 Context 全部是**嵌套**的，理论上应经 `NestedContext` 接到 `!=` 上，实际没接上。UEBGV 同函数
64 节点 / 65 边 / **0 孤立**，我们 64 / 65 / 18 —— 节点和边数完全一致，说明是**连线接错了引脚**。

### 节点连线缺陷修复（全量诊断第二轮）

诊断工具：`--audit <资产>` → `.cache/probe/audit.md`；单函数追踪：`--trace <资产> <函数>` → `.cache/probe/trace.txt`

| 缺陷 | 现象 | 修法 |
|---|---|---|
| Return 不接 out 参数 | 用 out 参数返回的函数，Return 只有 exec，上游全悬空 | `MakeReturn` 在 `EX_Nothing` 时把 out 参数接到 Return |
| **对象输出脚直接当输入脚** | `MakeContext` 把对象 Get 的输出脚塞进成员/调用节点的输入列表，但**没建连线** → 对象 Get 整节点孤立 | 新增 `ContextInputPin()`：造消费者输入脚并 `Connect` |
| 语句位置虚调用被当纯函数 | 未知签名的 `EX_LocalVirtualFunction` 顶层调用没有 exec 脚 → 空节点悬空 | `MakeCallByName(..., isStatement: true)` 强制非纯 |
| 嵌套表达式重复建节点 | 顶层 `CallMath` 只有 2 个，却产出 19 个 `!=` | `_nestedCache` 按表达式实例缓存输出脚 |

**效果（BP_Logic 181 函数）**

| 指标 | 修前 | 修后 |
|---|---|---|
| 孤立节点 | **98**（42 个函数） | **17**（9 个函数） |
| 边 | 3497 | 3647 |
| 空视图 / 不透明 | 0 / 0 | 0 / 0 |

三个样本函数的孤立节点全部归零：`GetTurnNumber` 4/5、`AddCardStats` 19/32（UEBGV 18/31）、`ExecuteAttackCard` 159/359（UEBGV 227/294）。

### 索引签名解析 + 连线修复（全量诊断第三轮）

**用数据源补签名**（你要求的「直接读依赖蓝图」）：索引在构建时已把 2137 个资产的函数参数表解析好，
`IndexSignatures.Find` 现在先查 jmap 的 `/Script`，未命中再查**索引里的 `/Game` 蓝图函数签名**——
字典命中，不需要运行时再开资产。探针也切到了 `IndexSignatures`（之前只有 jmap）。

**argN（未命名引脚）效果**

| 资产 | 修前 | 修后 |
|---|---|---|
| BP_Logic（181 函数） | 582 / 5143（11.3%） | **370 / 5143（7.2%）** |
| BP_CardFunctions（294 函数） | 842 / 20969（4.0%） | **410 / 20969（2.0%）** |

**孤立节点**（承接上轮修复）：BP_Logic 98 → **17**（9 个函数）、BP_CardFunctions → **7**（6 个函数）。

**对账样本（孤立全 0）**

| 函数 | 我们 | UEBGV |
|---|---|---|
| `GetTurnNumber` | 4 / 5 | 4 / 3 |
| `AddCardStats` | 19 / 32 | 18 / 31 |
| `ExecuteAttackCard` | 159 / 359 | 227 / 294 |

**剩余孤立节点的原因**（已定位）：`GetMatchController` 这类**结果被丢弃的 `EX_CallMath`** 顶层调用，
其 WorldContextObject 参数是 `EX_Self`（不产生节点），整节点自然没有连线。UEBGV 同函数有 90 节点
（我们 49），说明那里还有一批我们没走到的指令，留待下轮。

### 标题映射 + Assign 节点（全量诊断第四轮）

**1. 函数短名映射**（`KismetDisplayNames`，移植 UEBGV 三张表）

`BooleanAND` → `AND`、`Not_PreBool` → `NOT`、`Array_Length` → `LENGTH`、`Array_Get` → `GET`、
`Array_IsValidIndex` → `IS VALID INDEX`、`Map_*` 同理。与 `PromotableOperators` 的区别：
那张表把调用**变成运算符节点**（无标题栏），这张表只改**标题**，节点类型仍是 `CallFunction`。

**2. `K2Node_AssignmentStatement`**（UE 里的 `Assign` 节点）

UE 对编译器临时变量（`Temp_` 前缀）的赋值发的是 `Assign`，不是 `VariableSet`、也不是「不发节点」。
之前我们折掉了，所以 `ShouldSkipAnimationsAndSetupBoard` 只有 49 节点（UEBGV 90）。

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| `ShouldSkipAnimationsAndSetupBoard` | 49 节点 | **59 节点**（UEBGV 90），`AssignmentStatement=10`（UEBGV 8） |
| BP_Logic 总节点 | 2351 | 2500 |
| 孤立节点 | 17 | **14**（8 个函数） |
| argN | 370 / 5143（7.2%） | 370 / 5590（**6.6%**） |

**对账样本未退化**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`ExecuteAttackCard` 159/359，孤立全 0。

**剩余差距**（`ShouldSkipAnimationsAndSetupBoard` 59 vs 90）：`VariableGet` 1 vs 25、
`FunctionResult` 1 vs 6、`TemporaryVariable` 0 vs 4。后者是 UEBGV 为 `Temp_` 局部变量单独发的
声明节点（标题形如 `Local Int (Loop Counter)`），我们还没有。

### 引脚默认值 + 临时变量节点（全量诊断第五轮）

**1. 引脚默认值显示**（UE 编辑器里引脚上的小方框）

`ConnectorVm` 新增 `Value` / `HasValue`，`GraphAdapter` 把 `GraphPin.Value`（字面量、变量名）传过去，
引脚模板改成「名字 + 绿色默认值」。这是「编辑器级别」最直观的一项。

**2. `K2Node_TemporaryVariable`**

UE 为编译器临时变量（`Temp_`）发一对节点：声明 + 赋值。我们之前只有 `Assign`、没有声明。
改成首次赋值时懒创建变量节点（标题 `Local <类型> (<变量名>)`），后续复用同一个 VarPin。

**效果（`ShouldSkipAnimationsAndSetupBoard`）**

| | 修前 | 修后 | UEBGV |
|---|---|---|---|
| 节点 | 49 | **65** | 90 |
| `AssignmentStatement` | 0 | **10** | 8 |
| `TemporaryVariable` | 0 | **6** | 4 |

**全库（BP_Logic 181 函数）**：节点 2351 → **2596**，边 3647 → 4023，孤立 14（8 个函数），argN 6.6%。

**对账样本未退化**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`ExecuteAttackCard` 159/359，孤立全 0。

**剩余差距（65 vs 90）**：`VariableGet` 1 vs 25、`FunctionResult` 1 vs 6。这两项都是 UEBGV
**按路径重复**节点造成的（字节码里 `Return` 只有 1 条、`LocalVariable` 43 次读），
而 UE 编辑器本身是「一条指令一个节点」——我们这边更接近编辑器，不打算为了凑数去重复。

### UE 风格引脚（全量诊断第六轮）

| 项 | 做法 |
|---|---|
| exec 引脚 | **三角形**（`ExecPinShape`，Path `M 0,0 L 12,6 L 0,12 Z`） |
| 数据引脚 | 圆点（`DataPinShape`，12×12 Ellipse） |
| 切换方式 | `NodeInput`/`NodeOutput` 样式里 `ConnectorTemplate` 默认圆点 + `DataTrigger IsExec=True` 换三角 |
| 引脚文字 | 名字（白）+ 默认值（绿 `#7FB86A`），默认值只在非空时显示 |

Nodify 默认两者都是圆点；UE 编辑器里 exec 是三角，这是最直观的「像不像编辑器」的差别之一。

UI 渲染验证：`startGameReplay` 72 节点 / 70 连线 / 0 问题；截图 186 种颜色，
`#D0D0D0`（exec 边）、`#3B8ED0`（对象引脚）都在。

### 节点宽度与布局密度（全量诊断第七轮）

| 改动 | 之前 | 之后 |
|---|---|---|
| 节点宽度 | 无上限（长函数名撑到 500+） | `CalculateSize` 里 `Math.Clamp(NodeWidth, 160, 320)` |
| XAML | `MinWidth=160` | `MinWidth=160 MaxWidth=320` |
| 标题/引脚文字 | 只有 `TextTrimming` | 再加 `MaxWidth`（标题 292 / 引脚 180） |
| Msagl 间距 | `LayerSeparation=120, NodeSeparation=60` | `90 / 40` |

**效果**：`ExecuteAttackCard` 的布局跨度 **25208×8173 → 21428×7723**（宽 -15%）。

全库诊断不变：2596 节点 / 4023 边 / 孤立 14 / argN 6.6%。

> 注：28 个分支的函数天生铺得宽（约 57 层 × 每层 ~370），UE 编辑器同样如此；
> 这一项只做「不比编辑器更差」，不追求压到某个绝对值。

### 小地图（全量诊断第八轮）

| 项 | 做法 |
|---|---|
| 控件 | Nodify 自带 `Minimap`，叠在画布右下角（220×150，距边 16） |
| 绑定 | `ItemsSource` = 节点、`ItemsExtent/ViewportLocation/ViewportSize` 绑 `Editor` 的对应属性 |
| 关键前提 | **必须先合并 `Nodify;component/Themes/Nodify.xaml`**——没合并时 Minimap 完全不渲染（连背景色都没有，实测扫描全图 0 个 `#1B1B1B` 像素） |
| 验证 | 截图里出现 `#662D91`（1973 px，视口框）→ 小地图在画 |

**顺带**：合并 Nodify 主题后，`NodeInput`/`NodeOutput` 的 `BasedOn="{StaticResource {x:Type nodify:NodeInput}}"` 也有了明确来源。

全库诊断不变：2596 节点 / 4023 边 / 孤立 14 / argN 6.6%。

### 拖动 / 适配视图（全量诊断第九轮）

| 项 | 做法 | 验证 |
|---|---|---|
| 节点拖动 | `Location` 绑定改成 `Mode=TwoWay`——单向绑定会让拖动被 VM 弹回 | 拖动后位置写回 VM |
| **打开即适配视图** | `GraphView.FitToView()`：按节点包围盒算 zoom + 视口，`Nodes.CollectionChanged` 触发（属性不变，集合才变） | 日志 `适配视图: 节点 159 跨度 21747x7943 zoom=0.100` |
| 布局未完成时重试 | `ActualWidth < 10` 时用 `ApplicationIdle` 再排一次 | — |
| 重复 fit 去重 | 按 `节点数|跨度|画布宽` 做 key | 日志不再重复三行 |

**这轮还解开了一个悬案**：之前 `--screenshot` 截大图总是纯背景色（1 种颜色），
原因是默认视口在 (0,0) 而节点从 (12779,60) 开始。加上 fit 后同一张图截图有 **1026 种颜色**。

全库诊断不变：2596 节点 / 4023 边 / 孤立 14 / argN 6.6%。

### 变量类型解析（全量诊断第十轮）

**问题**：`EX_InstanceVariable` / `EX_StructMemberContext` 在字节码里**只带名字不带类型**，
所以变量引脚几乎全是 `Unknown`（灰色），而索引里其实存了每个资产的变量表。

**做法**：新增 `IVariableTypes`（Decompiler 侧接口）+ `IndexVariableTypes`（App 侧实现）——
按「归属类名 + 变量名」查索引，沿 `SuperClass` 链往上找，复用 `MapRendered` 反推引脚类型。
`FunctionWalker` 用当前函数的 `OuterIndex` 推出归属类。

**效果**

| 指标 | 值 |
|---|---|
| BP_Logic 引脚类型未知 | **1153 / 9084（12.7%）**（新增指标，本轮首次可量化） |
| `GetCantAttackText` 的引脚 | 出现 `Text` / `String` / `Bool` / `Array<Struct(FormatArgumentData)>` / `Struct(...)` |

**诊断工具同步升级**：`--audit` 报告新增「引脚类型未知」一行，后续改类型推断可以直接对比。

**回归**：三个对账样本不变（`GetTurnNumber` 4/5、`AddCardStats` 19/32、`ExecuteAttackCard` 159/359，孤立全 0），
UI 正常渲染（`适配视图: 节点 3 跨度 575x390 zoom=1.000`）。

### 类型解析扩展（全量诊断第十一轮）

在上一轮「类变量表」基础上补了两条来源：

| 来源 | 用途 | 实现 |
|---|---|---|
| 本函数的 `LoadedProperties` | 参数 + 局部变量（`EX_LocalVariable` 只带名字） | `_localTypes` 表，`TypeOfVariable` 先查它 |
| **对象本身的类型** | 成员读的归属类（`text_Notifications.X` 的 X 属于组件类，不属于 BP_Logic） | `contextPin.Type.SubCategoryObject` 取出类名再查变量表 |

**效果（BP_Logic 181 函数）**

| 指标 | 修前 | 修后 |
|---|---|---|
| 引脚类型未知 | 1153 / 9084（12.7%） | **963 / 9084（10.6%）** |
| `GetCantAttackText` | 41 / 143 未知 | **21 / 143**（`Target` 引脚全部解决） |

**对账样本**

| 函数 | 节点/边 | 类型未知 | UEBGV |
|---|---|---|---|
| `GetTurnNumber` | 4/5 | **0/10** | 4/3 |
| `AddCardStats` | 19/32 | 28/55（结构体字段，缺 usmap 字段表） | 18/31 |
| `ExecuteAttackCard` | 159/359 | 21/678 | 227/294 |

孤立节点仍全 0，UI 正常。

### 结构体字段类型（全量诊断第十二轮）

**做法**：`IVariableTypes` 加 `FieldsOf(structName)`，实现走 **usmap** 的 `Schemas[name].Properties`
（`UsmapStructData` / `UsmapEnumData` / 容器类型都映射到 `GraphPinType`）。
`MakeStruct` 的两条创建路径都用上了：

| 路径 | 用途 |
|---|---|
| `NestedStructConst`（`EX_StructConst`） | 字段名/类型按 usmap 字段表 |
| `FlushPendingStruct`（跨块成员写入） | 从挂起时的临时变量取出结构体类型，再查字段表 |

**结果**

| 项 | 修前 | 修后 |
|---|---|---|
| `MakeStruct` 标题 | `Make Struct` | **`Make CampaignCardStats`**（真实结构体名） |
| `AddCardStats` 字段类型 | 全 Unknown | 仍 Unknown（见下） |
| 全库引脚类型未知 | 963 / 9084（10.6%） | 963 / 9084（10.6%，未变） |

**为什么字段类型还是 Unknown**：`CampaignCardStats` 是**蓝图自定义结构体**（`UserDefinedStruct`），
不在 usmap 里；而索引里**一个结构体资产都没有**（实测 `index.json` 里路径含 `Struct` 的资产数 = 0）。
所以要补字段类型，得先让索引收录结构体资产并解析其字段表——这是下一步，不是反编译器这边的问题。

**回归**：三个样本不变（4/5、19/32、159/359，孤立全 0），孤立节点 14 个 / 8 个函数。

### 值位置调用 + 结构体索引（全量诊断第十三轮）

**1. 值位置的 `EX_Context` 调用按纯节点画**

`MakeContext` 增加 `asValue` 参数：`NestedContext` 传 `true`，节点不再带 exec 脚。
之前值位置的调用带 exec 脚却不在执行链上，整节点孤立（实测 `GetViewportSize`）。

**2. 纯调用必须有输出脚**

签名未知时 `BuildPins` 不产返回脚，纯调用就成了「只有输入、没有输出」的节点，无处可连。
`K2Node_CallFunction` 构造时：`isPure && returnPin is null && 无 out 参数` → 补一个占位 `ReturnValue`。

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| BP_Logic 孤立节点 | 14（8 个函数） | **11（5 个函数）** |

**剩余 11 个的成因**：`GetMatchController` 这类**结果被丢弃的 `EX_CallMath`**（`WorldContextObject` 是 `EX_Self`，
不产生节点，节点自然无连线）——这是字节码里真实存在的死调用，不是我们的连线错误。

**3. 结构体资产的数据源缺口（查证结论）**

| 来源 | 结果 |
|---|---|
| usmap | 只有原生结构体，`CampaignCardStats` 这类蓝图自定义结构体不在里面 |
| 索引 | `S_KnockoutStatus` / `S_SalvagedCardInfo` 这类结构体资产**在索引里但 `vars=0`**——`AssetHeaderReader` 只认 `BlueprintGeneratedClass` 导出 |
| 磁盘 | `CampaignCardStats.uasset` **在 FModel 导出里根本不存在**（2137 个资产里没有） |

所以 `MakeStruct` 现在能显示真实结构体名（`Make CampaignCardStats`），但字段类型仍缺数据源。
要补得先让 `AssetHeaderReader` 认 `UserDefinedStruct` 导出并重建索引——下轮做。

**回归**：三个样本不变（4/5、19/32、159/359，孤立全 0），UI 正常。

### 索引收录结构体字段（全量诊断第十四轮）

**问题**：结构体资产在索引里 `vars=0` —— `AssetHeaderReader` 只认 `BlueprintGeneratedClass` 导出，
而 `UserDefinedStruct` 资产的字段表在它**自己**的 `LoadedProperties` 里。

**改动**

| 位置 | 改动 |
|---|---|
| `AssetHeaderReader` | `classExport` 为空时退到 `UserDefinedStructExport` / `StructExport`，读它的 `LoadedProperties` 当字段表 |
| 同上 | 字段名剥掉 UE 生成的 `_<序号>_<32位GUID>` 后缀（`isSalvaged_1_D833…` → `isSalvaged`） |
| `IndexVariableTypes.FieldsOf` | 先查 usmap 原生结构体，再查**索引里的蓝图自定义结构体** |
| `IndexSignatures.MapRendered` | 新增 `MapPropertyClass`：`BoolProperty` / `IntProperty` … 这类**原始属性类名**也能映射（索引里结构体字段存的是原始类名） |
| `SymbolIndex` 构建 | 工作目录优先仓库内 `.cache/index-work`——这台机器的 `%TEMP%` 有 ACL 问题（写入被拒），之前直接抛 `UnauthorizedAccessException` |

**验证**

```
S_SalvagedCardInfo: isSalvaged:BoolProperty, salvageFaction:/Script/KardsCore.EFactionEnum, salvagedCardID:IntProperty
结构体资产（有变量无函数）: 27
```

**踩坑**：重建索引时 CLI 自动挑的 jmap 是 `kards1.60_No_UE4SS.jmap`，解析出 **0 个脚本函数**，
导致 argN 从 6.7% 暴涨到 25.1%、孤立 11→32。必须显式 `--jmap D:\files\projects\jmap-out\functions.json`。
用对之后全部指标回到基线（argN 6.7%、类型未知 11.2%、孤立 11）。

**回归**：三个样本不变（4/5、19/32、159/359，孤立全 0），UI 正常。

### 引脚类型显示 + 渲染不变量检查（全量诊断第十五轮）

**1. `ShowPinTypes` 是个死配置**

`solstice-winter.settings.json` 里 `"ShowPinTypes": true`，但**代码里没人读它**——设置面板上的开关一直是空转。
现在接上了：

| 位置 | 改动 |
|---|---|
| `GraphAdapter.ShowPinTypes` | 静态开关，`MainViewModel` 渲染前从 `Settings.Display.ShowPinTypes` 赋值 |
| `ConnectorVm.ShowType` | 每个引脚带一个布尔 |
| 引脚模板 | 名字（白）+ 默认值（绿）+ **类型（灰 `#8A8A8A`，`MaxWidth=110` 截断）** |

**2. 渲染不变量检查**（替代看图，因为我这个模型看不了图）

对导出数据做了四项检查：

| 检查 | `GetTurnNumber` | `ExecuteAttackCard` |
|---|---|---|
| 重复边 | 0 | 0 |
| 自环 | 0 | 0 |
| 无引脚节点 | 0 | 0 |
| 空标题 | 1（`VariableGet`，`HeaderHidden`，UEBGV 同款） | 16（同上） |

**3. 截图颜色抽样**（确认类型配色真的渲染出来了）

`#3B6EA5`（Object 蓝）、`#59317D`（Struct 紫）、`#A06A1F`（Float 橙）、`#4A7C8C`（边框）都在画面上，
说明 `BrushOf(pin.Type)` 这条链路是通的，不只是模型里有类型。

**回归**：全库 2596 节点 / 4023 边 / 孤立 11 / argN 6.7% / 类型未知 11.2%；三个样本不变。

### 宏模式量化 + For Each Loop 折叠（🚧，默认关闭）

**1. 先量化再动手**：新增 `--macros <资产>` 探针（`.cache/probe/macros.md`）

```
函数 181
- 含 Array_Length：21
- 含 Loop 节点：13
- For Each Loop 特征（LENGTH + GET + 比较）：20（11%）
```

样例：`autoPickHandTargets`、`CanCardDoAnything`、`CanIDoAnything`、`CanPlayCardFromHand` …

**2. 折叠器**（`MacroCollapse` + `K2Node_MacroInstance`）——**🚧 未完成，默认关闭**

能做的：识别 `Branch(<(LENGTH, Temp_ 索引))` + 同数组同索引的 `GET` + 索引初始化/自增，
删掉簇并生成一个 `For Each Loop` 宏节点（`CanIDoAnything` 19 节点 → 11 节点，宏的 `Array`、`LoopBody` 都接上了）。

**没做对的两件事**：

| 问题 | 后果 |
|---|---|
| 索引初始化链（`Temp_` 变量 + 赋值）没收完整 | 图上留一截悬空的 Assign 链 |
| 宏的 `execute` 入口没接上上游 | 宏节点自己成了执行链的断点 |

所以默认 **关闭**，用 `SOLSTICE_WINTER_MACROS=1` 试验。打开时的全库数字（供以后对照）：
节点 2596 → 2510、边 4023 → 3794、孤立仍 11。

**为什么默认关而不是默认开**：打开后节点数确实少了 86 个，但会留下悬空链——比不折更难看。
半成品不能当默认，这是底线。

**回归（关闭状态下）**：2596 节点 / 4023 边 / 孤立 11 / argN 6.7% / 类型未知 11.2%；
三个样本不变（4/5、19/32、159/359）。

### For Each Loop 折叠（✅ 完成，默认开启）

上一轮的半成品修完了，三处根因：

| 根因 | 修法 |
|---|---|
| 临时变量引脚**身份不一致**（同一次读有时指向入口脚、有时指向变量节点脚） | 簇成员、初始化、自增全部**按引脚名字**匹配，不按引用 |
| 很多引脚的 `Source` 是 null，连线只在 `LinkedTo` 上 | 新增 `UpstreamName(pin)`：`Source?.Name ?? LinkedTo.FirstOrDefault()?.Name` |
| 签名名在 `FriendlyName` 上（`Name` 还是 `argN`） | 新增 `IsPin(pin, name)`：两者都匹配 |
| 折叠后残留悬空初始化链 | 收尾扫一遍：**只看活着的连接**，丢掉 exec 两头都不连的赋值节点、没人读的临时变量 |

**效果（`CanIDoAnything`，形状与 UE 编辑器一致）**

```
折叠前 19 节点 / 29 边
折叠后  8 节点 / 10 边：Entry → Sequence → SET → GetAllActorsOfClass
                          → For Each Loop → (LoopBody) → Branch
                          → (Array Element) → VariableGet → Return
```

**全库对账（同一次构建，开/关对比）**

| 资产 | 指标 | 关闭 | 开启 |
|---|---|---|---|
| BP_Logic | 节点 / 边 | 2596 / 4023 | **2437 / 3782** |
| | 孤立节点 | 11 | **11**（不变） |
| | 类型未知 | 11.2% | **10.0%** |
| BP_CardFunctions | 节点 / 边 | 10383 / 19265 | **9126 / 17246** |
| | 孤立节点 | 3 | **3**（不变） |
| | 类型未知 | 12.0% | **9.9%** |

节点少 159 / 1257 个，孤立节点数不变——说明**没有折出断链**。开关：`SOLSTICE_WINTER_NO_MACROS=1` 关闭。

**对账样本**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`ExecuteAttackCard` 159/359（这三个函数没有循环，不受影响），孤立全 0。

### 与 UEBGV 的循环渲染对比（全量诊断第十八轮）

同一个函数 `CanIDoAnything`：

| | 节点 | 边 | 循环呈现 |
|---|---|---|---|
| **我们** | **8** | 10 | `K2Node_MacroInstance 'For Each Loop'`（Array / LoopBody / Array Element / Array Index / Completed） |
| UEBGV | 20 | 28 | 不折叠：`LENGTH` + `GET` + `Assign` ×4 + `TemporaryVariable` ×2 + `<` + `Branch` + `+` |

UEBGV 这次导出还报 `成功 False，问题 1`。

**结论**：For Each Loop 这一项**我们比参考实现更接近 UE 编辑器**——编辑器里就是一个宏节点，
UEBGV 把它摊成了原始指令流。之前几轮一直是我们节点偏少（Get 粒度），这一项反过来是我们更对。

**Loop 节点现状**：全库 13 个函数含 `K2Node_Loop`，抽查 3 个：`LoopBody` 全部有连接，
`Completed` 只连了 1 个（其余是循环即函数出口，没有后继，属正常）。

**剩余未覆盖的宏**：`While Loop` / `For Each Loop with Break`——它们在我们这里显示成 `Loop` + `Branch`，
而 `--macros` 显示还有 13 个函数带 `Loop` 节点。这是下一块。

---

## 怎么自己验一遍（诊断入口）

全部是 `src\SolsticeWinter.App\bin\Debug\net10.0-windows\SolsticeWinter.App.exe` 的命令行开关，
输出都写到仓库 `.cache\`（沙箱下每个进程 `%TEMP%` 不同，所以不写临时目录）。

| 命令 | 输出 | 看什么 |
|---|---|---|
| `--audit <资产>` | `.cache\probe\audit.md` | 全量诊断：空视图 / 孤立节点 / 不透明节点 / argN / 引脚类型未知 / 问题分类 |
| `--macros <资产>` | `.cache\probe\macros.md` | 宏模式扫描：含 `Array_Length`、含 `Loop`、For Each 特征各多少函数 |
| `--trace <资产> <函数>` | `.cache\probe\trace.txt` | 逐指令追踪：每条指令建了哪些节点 |
| `--hist <资产> <函数>` | `.cache\probe\hist.txt` | 指令直方图：顶层 + 嵌套表达式计数、基本块、序列 |
| `--export <资产> <函数> <json>` | 指定 json | 节点/边/坐标，可直接和 UEBGV 的导出对比 |
| `--walk <资产> <函数> [偏移]` | 控制台 | 单个函数的走查摘要 |
| `--screenshot <资产> <函数> <png>` | 指定 png | 截图（内部会先 fit 视图） |

环境变量：

| 变量 | 作用 |
|---|---|
| `SOLSTICE_WINTER_NO_MACROS=1` | 关闭 For Each Loop 折叠（对照用） |
| `SOLSTICE_WINTER_PERUSE_GETS=1` | 每个变量读各建一个 Get（UEBGV 风格，默认关闭） |

**基线数字（2026-11-08，开启宏折叠）**

| 资产 | 函数 | 节点 | 边 | 空视图 | 孤立 | argN | 类型未知 |
|---|---|---|---|---|---|---|---|
| BP_Logic | 181 | 2437 | 3782 | 0 | 11 | 7.2% | 10.0% |
| BP_CardFunctions | 294 | 9126 | 17246 | 0 | 3 | 2.0% | 9.9% |

重建索引的命令（**必须显式指定 jmap**，否则脚本函数会解析成 0，指标全线退化）：

```
solstice-winter index build <内容根> --usmap <usmap> --jmap D:\files\projects\jmap-out\functions.json --out .cache\index.json
```

### While Loop 折叠（✅ 完成）

`Loop` 节点的 `execute` 来自某个 `Branch` 的 `True` 时，那个 Branch 就是循环条件——
UE 编辑器里这是**一个** `While Loop` 宏节点（Condition 在宏内部）。折叠后：

```
CanIDoAnything / autoPickHandTargets 里的循环：
  3.then            → WhileLoop.execute
  5.ReturnValue     → WhileLoop.Condition
  WhileLoop.LoopBody → 12.execute      （循环体）
  WhileLoop.Completed → 6.execute      （退出）
```

`autoPickHandTargets`：**143 → 80 节点**，孤立 0。

**踩的坑（同一个坑第三次）**：引脚连线只在 `LinkedTo` 上，`Source` 常为 null；
且 `AddInputPin` 之后必须 `producer.LinkedTo.Add(consumer)` 才会产生边。
新增 `UpstreamPin()` 统一取上游，Condition 的边就是补这一句才出来的。

**全库（开/关宏折叠对比）**

| 资产 | 指标 | 关闭 | 开启 |
|---|---|---|---|
| BP_Logic | 节点 / 边 | 2596 / 4023 | **2431 / 3774** |
| | 孤立 / 类型未知 | 11 / 11.2% | **11 / 10.1%** |
| BP_CardFunctions | 节点 / 边 | 10383 / 19265 | **9096 / 17208** |
| | 孤立 / 类型未知 | 3 / 12.0% | **3 / 10.0%** |

**回归**：三个样本不变（4/5、19/32、159/359，孤立全 0），UI 正常（`适配视图: 节点 72 zoom=0.159`）。

### 连接表示统一 + 折叠收尾（全量诊断第二十轮）

**根因**：`GraphPin` 有两套连接表示——`output.LinkedTo` / `input.LinkedTo`（双向）和 `input.Source`。
`Connect()` 原来只维护 `LinkedTo`，于是所有「取上游」的代码都得写 `Source ?? LinkedTo.FirstOrDefault()` 的兜底，
宏折叠连着三轮踩同一个坑（For Each 的索引、While 的 Condition、exec 入口）。

| 改动 | 说明 |
|---|---|
| `Connect()` | **同时**维护双向 `LinkedTo` 和 `input.Source`，并去重 |
| `SpliceExecUpstream()` | 宏的 exec 入口若落在「写 `Temp_` 的赋值」上，继续往上溯并吃掉那一截初始化链 |
| 守卫 | 只吃写 `Temp_` 的赋值——`SET` 写真实变量，是正经语句，不能吃 |
| 空值防御 | `assign.Input.FirstOrDefault(IsPin "Variable")` 可能为 null（`VariableSet` 的值脚以变量名命名），必须判空 |

**踩坑记录**：加守卫时用了 `FirstOrDefault(...)!`，对 `VariableSet` 直接 NRE，
异常被 `Export` 的 try/catch 吞掉，表现为「导出成功但图没折叠」——排查花了很久。
**教训**：诊断工具失败时不能静默退回，`Export` 应该把异常写到 `.cache/probe/export-error.txt` 并返回非零
（它确实写了，但我先入为主以为是旧文件）。

**效果（`CanIDoAnything`）**

```
8 节点 / 11 边 / 孤立 0：
  Sequence.Then 1 → SET → For Each Loop.execute
  For Each Loop.Array        ← GetAllActorsOfClass.OutActors
  For Each Loop.LoopBody     → Branch.execute
  For Each Loop.Array Element→ VariableGet.Target
```

**全库**：BP_Logic 2431 节点 / 3788 边 / 孤立 11 / argN 7.2% / 类型未知 10.1%；
CardFunctions 9091 / 17293 / 3 / 2.0% / 10.0%。三个样本不变，UI 正常。

### 宏折叠自检（全量诊断第二十一轮）

**安全阀**：`MacroCollapse.Apply` 末尾加自检——折叠后如果还有「活节点」的输入 `Source` 指向被删节点，
说明这次折叠没接好（会断链），**整个函数退回原图**，并在 `--trace` 里打 `[macro] 自检失败：N 条悬空引用`。

```csharp
var dangling = result.SelectMany(n => n.Input)
    .Count(p => p.Source?.ParentNode is { } owner && removed.Contains(owner));
if (dangling > 0) return nodes;   // 宁可少折，不能折错
```

**实测**：BP_Logic（181 函数）与 BP_CardFunctions（294 函数）**从未触发**——说明当前折叠没有产生断链。
两个资产的全量数字与上一轮完全一致（2431 / 3788 / 11 / 7.2% / 10.1%；9091 / 17293 / 3 / 2.0% / 10.0%）。

**`For Each Loop with Break` 仍未支持（⛔ 已知限制）**

- 带 break 的循环会被折成普通 `For Each Loop`，宏没有 `Break` 引脚。
- break 的那条跳转目标目前会留在图上当一个普通节点，**不会被接到宏上**——语义上能读，但不是编辑器的形状。
- 我在 BP_Logic 里抽查了 `autoPickHandTargets`：`Completed` 只有一条出边、目标节点只有一条 exec 入边，
  没有发现第二条「跳出」的路径，所以**没找到可验证的实例**。这一项属于「已知未覆盖」，不是「已知错误」。

**下一步**：先用 `--macros` 扩一个「跳出特征」统计（循环体的 Branch 目标 = 循环出口块）找出真实实例，
再决定要不要实现 `Break` 引脚——没有实例就不写代码。

### break 查证 + 入口脚回滚（全量诊断第二十二轮）

**1. `For Each Loop with Break`：查证结果是「不存在」，不写代码**

`--macros` 新增「跳出特征」统计：宏的 `Completed` 目标节点，是否**同时**被别的节点的 exec 指向。

| 资产 | 函数 | For Each 特征 | **疑似带 break** |
|---|---|---|---|
| BP_Logic | 181 | 13 | **0** |
| BP_CardFunctions | 294 | 70 | **0** |

475 个函数里一个实例都没有。按「没有实例就不写代码」的标准，`Break` 引脚**不实现**。

**2. 函数入口 51 个输出脚：是问题，但一行修不掉（已回滚）**

现象：`DoOnStartOfTurn` 的 `K2Node_FunctionEntry` 挂了 **51 个输出脚**（`Temp_*`、`CallFunc_*_ReturnValue` 全在里面），
而 UE 的入口节点只有真参数。

试过：`EntryParameterPins` 里按 `CPF_Parm` 过滤，只放参数。**结果严重回归**：

| 指标 | 过滤前 | 过滤后 |
|---|---|---|
| 孤立节点 | 11 | **75** |
| 边 | 3788 | **3014** |
| 类型未知 | 10.1% | **16.0%** |

原因：**入口脚是临时变量模型的承重结构**——`_temps` 里很多条目直接指向入口脚，一旦拿掉，
读取方就悬空了。已回滚，数字恢复到 2431 / 3788 / 11 / 7.2% / 10.1%。

要真正修，得先让「临时变量读」在没有入口脚时也能建出 `VariableGet`/`TemporaryVariable` 节点——
这是一次重构，不是一行过滤。**记为待办，不再试第二次一行改法。**

### 临时变量重构：两次尝试、两次回滚（全量诊断第二十三轮）

**目标**：函数入口节点挂了 51 个输出脚（`Temp_*`、`CallFunc_*_ReturnValue` 全在里面），
UE 的入口只有参数。要修就得让临时变量读不依赖入口脚。

**尝试 A：入口脚按 `CPF_Parm` 过滤**

| 指标 | 基线 | 尝试 A |
|---|---|---|
| 孤立节点 | 11 | **75** |
| 边 | 3788 | **3014** |

**尝试 B：`EnsureTempVar` + 扩展判据（`CallFunc_*` / `K2Node_*` 也算编译器局部量）**

| 指标 | 基线 | 尝试 B |
|---|---|---|
| 孤立节点 | 11 | **94** |
| 边 | 3788 | **3087** |

**两次都回滚了。** 根因已经清楚：

`CallFunc_X_ReturnValue` 这类名字的**真正生产者是调用节点本身**，入口脚只是它在
「调用尚未处理时」的占位。把它换成一个独立的 `TemporaryVariable` 节点，
等于凭空造了一个**没有上游**的变量节点——消费者连上去就成了断链。
所以入口脚不是冗余，它是「先读后写」这种顺序下的兜底。

要真正去掉它，得让 `_temps` 在登记时就绑定到**调用节点的输出脚**（而不是入口脚），
并且允许「稍后补接」——这是一次涉及 `ValueSourceOf` / `MakeSetOrTemp` / `_temps` 生命周期的大改，
不是把过滤加回去就行。**已记为待办，不再尝试增量改法。**

**回滚后的状态（与上轮完全一致）**

| 资产 | 函数 | 节点 | 边 | 孤立 | argN | 类型未知 |
|---|---|---|---|---|---|---|
| BP_Logic | 181 | 2431 | 3788 | 11 | 7.2% | 10.1% |
| BP_CardFunctions | 294 | 9091 | 17293 | 3 | 2.0% | 10.0% |

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**清理过程中还踩了一个坑**：批量删死代码时误删了 `PointerName(KismetPropertyPointer)` 重载，
编译报 `KismetPropertyPointer → KismetExpression` 转换失败。已补回。
教训：**按行号批量删代码在 1300 行的文件里很危险**，应该先按内容定位再删。

### 入口脚度量 + 重定向（未启用，全量诊断第二十四轮）

**先量后改**：新增 `--entrypins <资产>` → `.cache/probe/entrypins.md`

```
入口输出脚总数 1464　挂出去的边 1729
- CallFunc_*   ：991 条（57%）
- 参数/其它    ：537 条（31%，含 then 184、真参数 _card 71、CardObjectRef 22…）
- Temp_*       ：164 条（9%）
- K2Node_*     ：37 条（2%）
```

结论：入口脚**不是冗余**，是「生产者尚未处理到」时的占位。直接删会断链（孤立 11 → 75，上轮已验）。

**做法：重定向而不是删除**（`RedirectEntryPins` + `_tempProducers` 表）

| 项 | 结果 |
|---|---|
| `Temp_*` 的边 | 164 → **1**（成功改接到 `TemporaryVariable` 节点） |
| `CallFunc_*` 的边 | 991 → 977（几乎没动） |
| 孤立节点 | 11 → 10 |
| **`CanIDoAnything`** | **8 节点带宏 → 19 节点无宏（退化）** |

**为什么禁用**：For Each 模式匹配依赖入口脚上的 `CallFunc_Array_Get_Item` /
`CallFunc_Array_Length_ReturnValue`；把它们重定向走，宏检测就失效了。

所以：**实现保留、调用注释掉**，等宏检测改成不依赖入口脚之后再打开。
当前状态与上轮完全一致（2431 / 3788 / 11 / 7.2% / 10.1%；`CanIDoAnything` 8/11 带宏，
`autoPickHandTargets` 80/124 带 While Loop）。

**这轮的判断**：入口脚问题的正确顺序是「先让宏检测不依赖入口脚，再重定向」，
而不是反过来。连续三轮在这个点上绕，是因为我一直想先改入口脚。

### 入口脚重定向（✅ 启用，全量诊断第二十五轮）

上一轮重定向把宏检测弄坏了，根因这轮找到并修好了：

**根因**：临时变量分支里把「被赋的值」当成了生产者——

```csharp
_tempProducers[name] = tempSource;   // tempSource 可能是没有父节点的字面量脚
```

重定向后消费者被接到一个无父的脚上，**连线直接消失**，图里既看不到边、也匹配不到循环模式。

**修法**：生产者应该是**变量节点自己**。

```csharp
_temps[name] = tempSource;          // 读的时候仍然用被赋的值（保持原有语义）
_tempProducers[name] = varPin;      // 但「谁是这个名字的生产者」是变量节点
```

**效果**

| 指标 | 上轮（禁用重定向） | 本轮（启用） |
|---|---|---|
| 孤立节点 | 11 | **10** |
| 边 | 3788 | 3767 |
| 入口脚 `Temp_*` 边 | 164 | **1** |
| 入口脚总边 | 1729 | **1543** |
| `CanIDoAnything` | 8/11 带宏 | **8/11 带宏**（保住） |
| `autoPickHandTargets` | 80/124 带 While Loop | **80/124**（保住） |

`CallFunc_*` 的 990 条边**没动**——这些名字没有进 `_tempProducers`（它们的 Let 走的是别的分支），
属于下一块的活。

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32，孤立全 0；CardFunctions 9093 节点 / 3 孤立 / argN 2.0% / 类型未知 10.3%；UI 正常。

### 隐藏空入口脚（全量诊断第二十六轮）

**先查为什么 `CallFunc_*` 收不掉**：加了临时 trace，`DoOnStartOfTurn` 里有 **28 个** `CallFunc_*` 入口脚
找不到生产者（`[redirect] 未找到生产者: CallFunc_getHasAmbush_doesIt（消费 2）`…）。

它们对应的 `Let` 没有走到 `MakeSetOrTemp` 的「调用」分支。要把它们收掉，得逐个摸清 `EX_Let` / `EX_LetObj` /
`EX_LetWeakObjPtr` / `EX_LetDelegate` 各自的路由——**收益/风险比不划算，我停在这里**，并把结论写下来。

**换个直接见效的做法：把没有消费者的入口脚隐藏**

```csharp
foreach (var pin in entryNode.Output)
    if (pin.Name is not ("then" or "") && pin.LinkedTo.Count == 0) pin.Hide();
```

`GraphAdapter` 与 `--export` 都跳过 `IsHidden` 的脚。

**效果（`DoOnStartOfTurn`）**：入口可见输出脚 **51 → 30**（剩下 29 个都是还有消费者的 `CallFunc_*`）。

**全库**

| 资产 | 函数 | 节点 | 边 | 孤立 | argN | 类型未知 |
|---|---|---|---|---|---|---|
| BP_Logic | 181 | 2431 | 3767 | **10** | 7.2% | 10.2% |
| BP_CardFunctions | 294 | 9093 | 17199 | 3 | 2.0% | 10.3% |

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**离 UE 还差**：入口应该只有 `then` + 真参数（本例 2 个），现在 30 个。剩下的 29 个是
`CallFunc_*` 占位脚，要收掉必须先解决上一条的路由问题。

### 节点标题（全量诊断第二十七轮）

**1. `VariableGet` 有了标题**

之前照 UEBGV 做成「无标题 + 变量名在输出脚上」，画布上就是一排空白小方块。
UE 编辑器里 Get 节点**有标题栏写变量名**，输出脚不再重复：

| 项 | 之前 | 现在 |
|---|---|---|
| 标题 | 空（`HeaderHidden = true`） | **变量名**（如 `text_Notifications`） |
| 输出脚文字 | 变量名 | 隐藏（`IsNameHidden = true`） |

**2. `Assign` 标题带变量名**：`Assign` → `Set <变量名>`。

**3. UI 尊重 `IsNameHidden`**：`GraphAdapter` 里 `Title = pin.IsNameHidden ? "" : pin.FriendlyName`。
之前不判断，隐藏名的脚还是会把名字画出来。

**效果**：`GetCantAttackText` 的 19 个 Get 节点现在标题是 `text_Notifications` / `try_attack_not_a_unit` 等，
不再是空白框。全库数字不变（2431 / 3767 / 10 / 7.2% / 10.2%）——这轮只改标题。
布局跨度从 2888 变成 2856（Get 节点有了标题栏，尺寸变化）。

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**过程事故（第 3 次）**：用 `$lines[$i-1]` 改行，`$i` 因为 `-SimpleMatch` 没匹配到变成 -1，
于是 `$lines[-2]` 覆盖了倒数第二行、文件尾部被写坏。已修复。
**规则（写死在这里）**：按行号改代码前必须先断言锚点命中（`$i -gt 0`），否则不写。

### UI 标题被覆盖的 bug（全量诊断第二十八轮）

**上一轮的改动其实没生效**。反编译器把 Get 节点的标题设成变量名，但 `GraphAdapter.TitleOf` 里写着：

```csharp
K2Node_VariableGet => string.Empty,     // ← 把标题硬改成空
K2Node_VariableSet => "SET",
```

UI 层把反编译器的标题**全部覆盖**了。上一轮我只验证了 `--export`（那是模型层，标题是对的），
没验证 UI 层——所以画布上还是一排空白方块。

**修法**：`TitleOf` 直接返回 `node.Name`，不再按节点类型改写。

**新增 `--vmdump <资产> <函数>`**：打印 **UI 层**（`GraphAdapter.ToView` 之后）的标题/正文/引脚。
我看不了图，这是唯一能验证「显示效果」的入口。

**验证（`GetCantAttackText`）**

```
标题「text_Notifications」      正文「」  引脚[]
标题「try_attack_not_a_unit」   正文「」  引脚[Target / ]
标题「Branch」                  正文「」  引脚[ / Condition / True / False]
```

（修复前这些 Get 的标题都是空的。）

**正文改进**

| 节点 | 之前 | 现在 |
|---|---|---|
| 调用 | 全路径 `/Script/Engine.GameplayStatics`（把节点撑宽） | **短名** `GameplayStatics` |
| `For Each Loop` | 空 | `遍历数组` |
| `While Loop` | 空 | `条件在宏内部` |

**回归**：全库不变（2431 / 3767 / 10 / 7.2% / 10.2%）；`GetTurnNumber` 4/5、`AddCardStats` 19/32、
`CanIDoAnything` 8/11（For Each Loop）、`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**教训**：改了模型层的标题，必须用 `--vmdump` 验一遍 UI 层——中间隔着一个 `GraphAdapter`，
它可能把标题改掉。这和之前 `ShowPinTypes` 空转是同一类问题：**配置/字段存在，但没人读**。

### 运算符引脚名 A/B（全量诊断第二十九轮）

**问题**：`!=`、`+`、`<` 这类运算符节点的引脚名全是空的（`--vmdump` 显示 `引脚[ / / ]`），
画布上看不出哪个是左操作数。

**修法**：`K2Node_PromotableOperator` 构造时给输入脚命名 `A` / `B` / `C`…，
输出脚用 `IsNameHidden` 隐藏名字（UE 同款：输出脚只显示类型）。

**连带修的**：改完之后 **For Each Loop 折叠立刻失效**（节点 2431 → 2587）——
`MacroCollapse.FindClusters` 靠「名字为空的输入脚」来找 `<` 的两个操作数：

```csharp
var operands = less.Input.Where(p => p.Name.Length == 0).ToList();   // ← 名字变成 A/B 后就找不到了
```

改成「取前两个非 exec 输入脚」：

```csharp
var operands = less.Input.Where(p => p.Type.Category != PinType.Exec).Take(2).ToList();
```

**效果**

| 指标 | 之前 | 现在 |
|---|---|---|
| 运算符引脚 | `[ / / ]` | **`[A / B / ]`** |
| 节点 / 边 | 2431 / 3767 | 2431 / 3767（不变） |
| 引脚类型未知 | 871 / 8506（10.2%） | **871 / 9081（9.6%）** |

类型未知比例下降是因为运算符的输出脚不再清空 `Name`（以前被排除在统计外，现在算进来了），
分母从 8506 涨到 9081，统计口径更准。

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**教训（同类第 3 次）**：改显示层的命名，可能打断**依赖命名的检测逻辑**。
命名不是纯显示——它是数据结构。改之前先 grep 一下谁在读 `Name`。

### Set 节点标题（全量诊断第三十轮）

**先 grep 依赖**（上轮的教训）：

```
CoreNodes.cs:338        : base("SET", instruction)
GraphAdapter.cs:234     set.Name.StartsWith("SET ")   // 解析标题拿变量名
GraphAdapter.cs:80-84   值脚标题覆盖成变量名
```

**改动**

| 项 | 之前 | 现在 |
|---|---|---|
| 标题 | `SET`（`HeaderHidden = true`，标题栏不显示） | **`Set <变量名>`** |
| 值脚 | 名字 = 变量名（和标题重复） | `IsNameHidden`，只显示类型 |
| 取值 | `GraphAdapter` 解析标题字符串 `"SET X"` | 直接读 `set.Property.Name`（不再解析字符串） |

**`--vmdump` 验证（`CanIDoAnything`）**

```
标题「Set CallFunc_GetAllActorsOfClass_OutActors」　引脚[ /  / ]
```

**效果**：引脚类型未知 9.6% → **9.0%**（值脚不再带名字，分母从 9081 降到 8863）。
节点/边/孤立完全不变（2431 / 3767 / 10）。

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 19/32、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**过程事故（第 4 次，这次最严重）**：删死代码 `VariableNameOf` 时用了一个「遇到 `};` 才停」的循环，
结果把后面整个 `KindOf` / `BrushOf` / `FallbackBrush` 都删掉了，文件从 260 行掉到 222 行。
已按 `Themes/Studio.xaml` 的 `Pin.*` 调色板重建这三个成员。

**规则（第 2 条，写死）**：删代码只用**精确文本替换**（`edit` 工具），不用「扫描 + 跳过」的循环。
扫描循环在锚点不匹配时会吃掉整段文件，而且 `Set-Content` 会立刻落盘、没有撤销。

### Break Struct 折叠（全量诊断第三十一轮）

**问题**：读结构体字段在 UE 里是**一个** `Break CampaignCardStats` 节点（每字段一个输出脚），
字节码里却是每字段一条读指令。不折的话一个函数散出十几个只带 `Target` 的小 Get 节点——
`AddCardStats` 实测有 **12 个**。

**做法**：`BreakStructCollapse`（新增）+ `K2Node_BreakStruct`（新增）

- 按「`Target` 输入来自同一个引脚」分组 `K2Node_VariableGet`
- 组内 ≥3 个才折（2 个折成 Break 反而更乱）
- 字段顺序优先用 usmap / 索引给的定义顺序，没有就按出现顺序
- 消费者改接到 Break 的对应字段输出脚

**效果**

| 函数 / 资产 | 之前 | 现在 |
|---|---|---|
| `AddCardStats` | 19 节点 / 32 边 | **7 节点 / 20 边** |
| BP_Logic（181 函数） | 2431 / 3767 / 孤立 10 | **2308 / 3635 / 孤立 10** |
| BP_CardFunctions（294 函数） | 9093 / 17199 / 孤立 3 | **8626 / 16698 / 孤立 3** |

**孤立节点数完全不变**（10 / 3）——说明折叠没有折出断链。
CardFunctions 的引脚类型未知从 10.3% 降到 **8.5%**（Get 节点少了，它们的 Unknown 脚也少了）。

**`--vmdump` 验证（`AddCardStats`）**

```
标题「Break BaseCardObject」　引脚[Struct / name / text / attack / defense / kredits / operationCost /
                                  heavyArmor / hasAmbush / hasBlitz / hasGuard / hasFury / hasSmokescreen / hasMobilize]
```

**回归**：`GetTurnNumber` 4/5、`CanIDoAnything` 8/11（For Each Loop）、`autoPickHandTargets` 80/124（While Loop），
孤立全 0；UI 正常。

**遗留**：`Make CampaignCardStats` 与 `Break BaseCardObject` 名字不一致——因为变量 `campaignOldCardStats`
的声明类型是基类 `BaseCardObject`，而赋给它的结构体是 `CampaignCardStats`。这是**字节码里的真实类型信息**，
不是 bug；UE 编辑器里也是按变量声明类型显示。

### argN 溯源（全量诊断第三十三轮）

**新增 `--argn <资产>`** → `.cache/probe/argn.md`：按节点类型 / 函数统计未命名引脚。

**结论：370 个 `argN` 全部出在 `K2Node_CallFunction` 上**，没有一个是运算符或结构体节点。

| 函数 | argN |
|---|---|
| `GlobalMouseUpBattle` | 44 |
| `DoOnStartOfTurn` | 28 |
| `autoPickCardToDraw` | 26 |
| `ResyncMatch` | 26 |
| `CanPlayCardFromHand` | 23 |

**这些调用在字节码里是有参数的**（`GetOnlineMatch` 节点：`引脚[ / value / arg0 / arg1 / ]`），
但我们**没有任何数据源有它们的签名**：

| 来源 | `GetOnlineMatch` |
|---|---|
| jmap（13064 条 `/Script`） | **0** |
| 索引里 BP_OnlineMatch 的 412 个函数 | **0**（只有 `IsOnlineMatch` / `StartOnlineMatch`） |

这是**数据缺失，不是代码 bug**。要么 jmap 提取时漏了，要么这些函数来自未导出的模块。

**试过并回滚的方案**：把 `GetXxx` 当属性 getter，从索引的变量表合成「无参 + 返回变量类型」的签名。
结果 argN **370 → 381（变差）**——因为这类调用实际带参数，合成签名只有 1 个返回脚，
对不上的参数变成新的 `argN` 占位。已回滚，代码里留了注释说明为什么不做。

**当前状态**：BP_Logic 2308 节点 / 3635 边 / 孤立 10 / argN 7.4% / 类型未知 10.0%；
`GetTurnNumber` 4/5、`AddCardStats` 7/20、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0。

### For Each 索引上溯（全量诊断第三十四轮）

**问题**：CardFunctions 里 **30 个函数**有 `Loop` 节点却没折成宏，其中一半明明带 For Each 特征
（LENGTH + GET + 比较 全 True）。新增 `--macros` 的「有 Loop 但没折」统计把这类挑了出来。

**根因**（`ApplyFatigueDamage` 实测）：`<` 的左侧不是直接读计数器，而是 `(counter + 1)` 自增表达式：

```
[35] '<'   A ← [29].ReturnValue     ← 29 是 '+'，A ← [30].Temp_int_Loop_Counter_Variable
           B ← [34].ReturnValue     ← LENGTH
```

判据要求「索引脚名字以 `Temp_` 开头」，而 `[29].ReturnValue` 叫 `ReturnValue` → 判据不命中。

**修法**：新增 `TraceToTemp()`——索引脚不直接是 `Temp_` 时，往下追一层 `+` 运算符找它引用的 `Temp_` 脚。
**只追一层**：追多了会把真正的 while 循环误判成 For Each。

**效果**

| 指标 | 之前 | 现在 |
|---|---|---|
| CardFunctions「有 Loop 但没折」 | 30 | **20** |
| CardFunctions 含 `Array_Length` 的函数 | 78 | **52**（更多被折掉了） |
| BP_Logic 节点 | 2308 | **2234** |
| CardFunctions 节点 | 8626 | **8286** |
| 孤立节点 | 10 / 3 | **10 / 3**（不变） |
| 类型未知 | 9.8% / 8.3% | **9.8% / 8.3%** |

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 7/20、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

**剩余 20 个**：一半是**没有** LENGTH/GET 特征的循环（`ChangeUnitOwnership`、`CustomAbilityAdd`、
`DrawTopCardFromDeck` 等）——它们本来就是 while 风格，显示成 `Loop` 节点是合理的；
另一半需要再看（可能是 GET 节点缺失或数组源不同）。

### For Each：数组索引与循环计数器分离（全量诊断第三十五轮）

**问题**：上一轮后仍有 **20 个**函数「有 Loop 但不折」，其中一批明明带完整 For-Each 特征。

**根因**（`ExecuteOnAfterAttackEvents` 实测）：

```
[15] '<'   A ← [10].Temp_int_Loop_Counter_Variable    ← 循环计数器
           B ← [14].ReturnValue (LENGTH)
[19] GET   Index ← [12].Temp_int_Array_Index_Variable ← 数组索引（另一个变量！）
```

判据要求「GET 的 Index 名字 == `<` 那侧的索引名」，而这里是**两个不同的临时变量**：
循环计数器 `Temp_int_Loop_Counter` 和数组索引 `Temp_int_Array_Index`。名字不同 → 判据不命中。

**修法**：
- GET 只按「同一个数组」匹配（不再要求索引同名）
- 数组索引以 **GET 实际用的那个** 为准（`arrayIndexVar`）
- 两个临时变量名都进 `tempNames`（初始化/自增链要一起收）

**效果**

| 指标 | 之前 | 现在 |
|---|---|---|
| CardFunctions「有 Loop 但没折」 | 20 | **5** |
| CardFunctions 含 `Array_Length` 的函数 | 52 | **38** |
| CardFunctions 节点 | 8286 | **8243** |
| BP_Logic 节点 | 2234 | 2245 |
| 孤立节点 | 10 / 3 | 10 / **4** ⚠ |

**一处待查**：CardFunctions 孤立节点 3 → 4（`GetRandomCard` 被审计记为 1 个孤立），
但用 `--export` 对同一函数算零度节点是 **0**。审计与导出的判据应当一致（都是「进出都没有连线」），
差异原因未查明，量级 1/294，先记录不追。

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 7/20、`CanIDoAnything` 8/11（For Each Loop）、
`autoPickHandTargets` 80/124（While Loop），孤立全 0；UI 正常。

### 静态节点不该有 exec 口（全量诊断第三十六轮）

**你指出的问题**：`Array_Get` 这类纯函数被画出了 exec 脚。

```
[19] GET  in=[execute, value, TargetArray, Index, Item]   ← 错
```

**根因**：`InstructionToNode` 把**所有顶层调用**都当成 impure：

```csharp
EX_LocalFinalFunction call => MakeCall(..., isPure: false),   // ← 一刀切
EX_FinalFunction call      => MakeCall(..., isPure: false),
```

而 `Array_Get` / `Array_Length` / 所有 `KismetMathLibrary` / `KismetStringLibrary` 的函数
在 UE 里都是 BlueprintPure，根本没有 exec 脚。

**修法**：新增 `KismetPurity` 表（impure 列表 + pure 列表 + 整库纯的模块），
在 `MakeCall` / `MakeCallByName` / `MakeContext` 的 final 分支里，**纯度以函数本身为准**，
不再由调用位置决定。

**连带清理**：纯节点如果输出没人消费，就是「结果被丢弃的表达式」——UE 里不会有这个节点。
直接丢弃，否则它以孤立节点形式飘在画布上。

**效果**

| 指标 | 之前 | 现在 |
|---|---|---|
| BP_Logic 孤立节点 | 10（纯净度修复后一度 12） | **2** |
| CardFunctions 孤立节点 | 3（一度 4） | **2** |
| BP_Logic 边 | 3519 | 3454 |
| CardFunctions 边 | 16109 | 15782 |
| `GET` 引脚 | `[execute, value, TargetArray, Index, Item]` | **`[value, TargetArray, Index, Item, ReturnValue]`**（无 exec） |

**回归**：`GetTurnNumber` 4/5、`AddCardStats` 7/20、`CanIDoAnything` 8/10（For Each Loop）、
`autoPickHandTargets` 80/122（While Loop），孤立全 0；UI 正常。

**仍未解决**：函数入口节点上仍挂着 `CallFunc_*_ReturnValue` 这类编译器临时变量脚
（`GetRandomCard` 真实 3 个参数，入口显示 9 个脚）。这需要把临时变量的消费者接到
调用节点的真实输出脚上——上几轮试过两次都造成回归，这轮没动它。

### 图构建层重写（进行中，全量诊断第三十七轮）

**决定**：旧 `FunctionWalker`（1536 行 / 约 20 个可变字段 / 5 个事后修整 pass）**整体替换**，
新实现 `GraphCompiler` 按 UEBGV 的形状重写：线性指令流 + 单一 `LocalVars` + 不做事后修整。
旧代码归档在 `.cache\legacy\`（`FunctionWalker.cs` / `MacroCollapse.cs` / `BreakStructCollapse.cs`），
一条命令即可恢复。

**已完成**

| 项 | 说明 |
|---|---|
| `GraphPin` 单一真相 | 连接只存 `LinkedTo`（双向对称），`Source` 改成**派生属性**，物理上不可能不同步；`Connect()` 是唯一入口且校验方向 |
| 新 `GraphCompiler` | 线性 `WalkBlock` + 递归处理跳转；临时变量只有一张表 `_locals`；入口节点只放 `CPF_Parm` 真参数 |
| `<export>` 修好 | `ResolveFunction` 补上「正索引 = 本资产导出」，`AIStopHangingOnMulligan` 现在显示 `ExecuteUbergraph_BP_Logic` 而不是 `<export>` |
| 裸调用语句 | 顶层 `EX_LocalFinalFunction` 当语句用时之前被整个丢掉（`AIStopHangingOnMulligan` 只剩 2 节点），现在会发节点并接 exec |
| 悬空引用 | 1148 → 238（收尾清理掉 `ParentNode == null` 的中间脚） |

**未完成（当前比旧实现差的地方，必须说清楚）**

- **`Sequence` 结构丢失**：`EX_PushExecutionFlow` / `EX_PopExecutionFlow` 只走了第一个分支。
  `AddHandCardFromAction` 旧实现 23 节点（4 个分支），新实现只有 **9 节点**——少了 3 个分支。
- 还有 **238 处悬空引用**，`--validate` 未通过。
- 宏折叠（For Each / While）和结构体 Break 折叠**暂未迁移**到新管线（旧实现里这两项曾让
  `AddCardStats` 从 19 节点降到 7）。

**结论**：地基换好了，但**功能还没到旧实现的水平**。下一轮先把 Sequence 走通（这是最大的结构缺口），
再迁折叠，最后用 `--validate` + `--vmdump` 逐函数对账。

### 重写里程碑 1：图构建层跑通（全量诊断第三十八轮）

新 `GraphCompiler` 现在**通过全部一致性校验**，且旧实现的几个硬 bug 都消失了。

**修掉的三个真 bug（这轮）**

| 现象 | 根因 | 修法 |
|---|---|---|
| `AddHandCardFromAction` 只有 9 节点（少 3 个分支） | 顶层调用当语句时，若该调用**没有返回脚**就不会发节点，exec 链直接断掉 | 用 `_lastNode` 判断是否建了节点，不依赖「有没有输出脚」 |
| `连线方向错误：(Input) → ?(Input)` | `ProcessLet` 把 Set 节点的**输入脚**缓存进 `_locals`，后续拿它当上游 | 不再缓存输入脚 |
| 成员读全部 Unknown（52%） | `_variables.Find(null, name)` 传了空 ownerClass | 记录 `_ownerClass` 并传进去 |

另外：`Let` 折叠掉不发节点是正常行为，不再误报「未处理语句」；未处理的指令类型现在会记进 `problems`。

**指标对比（旧 → 新）**

| 指标 | 旧实现 | 新实现 |
|---|---|---|
| 一致性校验（5 条规则） | 通过（靠事后扫引用） | **通过（构建期就是对的）** |
| 孤立节点 BP_Logic / Cards | 4 / 16 | **8 / 7** |
| 未命名引脚 argN | 337 / 355 | **0 / 0** |
| 引脚类型未知 | 10.5% / 8.8% | 38.1% / 43.1% ⚠ |
| 节点数 | 2235 / 8242 | 3909 / 14595 ⚠ |

**两处变差，原因明确**

1. **类型未知 38–43%**：新实现把编译器临时变量建成独立节点（旧实现把它们挂在入口节点上，
   那些脚被计入「有类型」的分母）。临时变量的类型只在其被赋值时才能推断，
   先读后写的仍是 Unknown。需要继续做「从赋值反推类型」。
2. **节点数翻倍**：同上——每个临时变量一个 `Local X` 节点。UE 编辑器里这些是隐藏的。
   要么合并（同一 temp 多次赋值复用），要么干脆不显示。

**未迁移**：宏折叠（For Each / While）和结构体 Break 折叠。旧实现里这两项让
`AddCardStats` 19 → 7 节点；新实现目前 33 节点（12 次 `Set <字段>`）。

**回归**：UI 正常渲染（`AddHandCardFromAction` 33 节点 / 28 连线 / 问题 0 / zoom 0.286）。

### 重写里程碑 2：临时变量与收尾清理（全量诊断第三十九轮）

**修掉的四个问题**

| 问题 | 根因 | 效果 |
|---|---|---|
| 节点数翻倍（14591） | 读**函数参数**时没查 `_locals`，每次都新建 `VariableGet` | 参数登记进 `_locals`，复用入口脚 → 10337 |
| 类型未知 43% | 同上（新建的 Get 类型查不到）+ 成员读用当前类当 owner | 同上 + 按**对象类型**查成员 → **29–31%** |
| `Local Unknown (K2Node_*)` 悬空 | **先读后写**：读时建了无生产者的临时节点，赋值时没把消费者改接 | `RegisterProducer()` 用 `RedirectConsumersTo` 改接 |
| 校验 1567 处悬空 / 孤立 750 | 清理只做一轮：摘掉中间脚后，某些节点唯一的消费者消失、自己变成死节点 | 改成**迭代到不动点**（摘脚 + 删死纯节点，最多 8 轮） |

**指标（旧实现 → 新实现）**

| 指标 | 旧 | 新 |
|---|---|---|
| 一致性校验 | 通过（靠事后扫） | **通过** |
| 节点 BP_Logic / Cards | 2235 / 8242 | 2826 / 10337 |
| 孤立节点 | 4 / 16 | **8 / 8** |
| **未命名引脚 argN** | 337 / 355 | **0 / 0** |
| 类型未知 | 10.5% / 8.8% | 29.1% / 30.7% |
| `AddCardStats` 节点 | 19（旧，含 Break 折叠） | 18（**无折叠**，纯靠清理） |

**`AddCardStats` 从 33 → 18 节点**，而且这 18 个是干净的结构（`Set <字段>` 链 + 调用），
不再是「一堆 `card` Get + Set」的重复。

**UI**：`AddCardStats` 18 节点 / 18 连线 / 问题 0，正常渲染。

**仍未做**：宏折叠（For Each / While）与结构体 Break 折叠。类型未知还有 30%，
主要来自「没有签名的调用」和「查不到的成员」——属于数据缺失，不是结构问题。

### 重写里程碑 3：Break 折叠接入 + 发现新问题（全量诊断第四十轮）

**接入 Break 折叠**：`BreakStructCollapse` 按新 `GraphPin` 重写（`Disconnect` + `Connect`），
放在收尾清理**之前**，让不动点清理兜住孤儿边。

**踩了一个坑（差点全废）**：先 `_nodes.Clear()` 再 `AddRange(collapsed)`，折叠抛异常时
`_nodes` 已清空 → **180 个函数变成空视图**。已加 try/catch：折叠失败保留原图并记进 `problems`。

**当前指标**

| 指标 | 值 |
|---|---|
| 一致性校验 | **两个资产全部通过** |
| 节点 BP_Logic / Cards | 2826 / 10273 |
| 孤立节点 | 8 / 8 |
| argN | **0** |
| 空视图 | **0** |
| 类型未知 | 29.1% / 30.8% |

**发现的新问题（比指标更重要）**

`GetCantAttackText` 现在有 **19 个 `Branch`，Condition 全是 `Unknown`**，而对应的比较节点
（`!=`）全部消失：

```
标题「Branch」 引脚[:Exec / Condition:Unknown / True:Exec / False:Exec]   ×19
标题「Local Unknown (K2Node_SwitchString_CmpSuccess)」 引脚[...:Unknown]
```

推断：字节码里是 `LetBool X = <比较>` 然后 `JumpIfNot <读 X>`，但 **Let 的目标名和
JumpIfNot 读的名字对不上**（可能是 `..._CmpSuccess` 与 `..._CmpSuccess_1` 这类后缀差异），
于是读的时候 `_locals` 未命中 → 建了个 Unknown 临时节点 → 比较节点没人消费 → 被当死节点删掉。

这是**结构丢失**，比类型未知严重。下一轮第一件事：把 `VariableName` 在两处打印出来对比，
修掉名字不一致，再把 switch 构造（`K2Node_SwitchString_CmpSuccess` 的生产者）补上。

**Break 折叠目前没触发**：新编译器里结构体字段**写**变成了 `Set <字段>`，没有「多个 Get 共享
Target」的分组，所以折叠无事可做。要么改成折 `Set 字段链` → `Set members in <Struct>`，
要么接受现状。这一项等上面那个结构问题修完再定。

### 重写里程碑 4：比较节点被误删的根因（全量诊断第四十一轮）

上一轮发现的「19 个 Branch 的 Condition 全是 Unknown、比较节点全消失」，这轮挖到根因了，
**不是名字不匹配，是引脚方向搞反了**。

**根因链**

1. `ValueOf()` 返回的是**消费者输入脚**（用来挂到节点上），但 `ProcessLet` 把它当生产者登记：
   ```csharp
   var value = ValueOf(assignment);
   if (value.IsOutput) RegisterProducer(name, value);   // ← 输入脚，永远为假
   ```
   于是 `_locals[name]` 从来没登记 → 之后读这个临时变量全部 miss → 建了个 Unknown 占位节点
2. 真正的比较节点没人消费 → 被「死纯节点」清理删掉
3. 更糟的是后来 `ProducerPin` 兜底返回输入脚本身 → `Connect(输入, 消费者)` 抛
   `连线方向错误`，**异常中断整条走查**，收尾清理跑不到，留下 124 处悬空引用

**修法**

| 改动 | 说明 |
|---|---|
| `ProducerPin()` | 返回 `GraphPin?`：解析不到真正的输出脚就返回 null |
| `RegisterProducer()` | 解析不到就**不登记**，绝不把输入脚当上游 |
| 走查包 try/catch | 畸形字节码抛异常时仍然继续跑收尾清理，不留悬空引用 |

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| 一致性校验 | 失败（124 / 68 处悬空） | **两个资产全部通过** |
| `GetCantAttackText` | 30 节点，19 个 Branch 条件 Unknown | **48 节点，`!=` 引脚 `A:String / B:String / :Bool` 正常** |
| 节点 BP_Logic / Cards | 2205 / 11146 | 3244 / 11479（走查不再中断，内容恢复） |
| argN | 32 / 4 | **2 / 0** |
| 类型未知 | 25.4% / 26.5% | **24.1% / 26.6%** |
| 孤立节点 | 8 / 7 | 11 / 7 |

**UI**：`GetCantAttackText` 48 节点 / 67 连线 / 问题 0，正常渲染。

**教训（记下来）**：`ValueOf` 的返回值语义是「消费者脚」，任何要当**生产者**用的地方都必须
先过 `ProducerPin()`。这类方向错误不会编译报错，只会静默丢内容——只有 `--validate` 能发现。

### 重写里程碑 5：孤立节点与 exec 链（全量诊断第四十二轮）

**问题**：`GetGraphicInfoString` 8 个节点里 3 个孤立（`GetGameUserSettings` / `GetViewportSize` /
`Local Unknown (CallFunc_BreakVector2D_X)`）。

**根因**：`Let x = <impure 调用>` 在 `ProcessLet` 里一律 `return null`——
调用节点建出来了，但**既不挂 exec 链、输出也没人消费**，于是飘在画布上。

UE 里 impure 调用无论写在赋值里还是当语句，都在执行链上。

**修法**

| 改动 | 说明 |
|---|---|
| `ProcessLet` 的调用分支 | 若 `_lastNode` 有 `ExecPin`（impure），把它当语句返回，让 `EmitStatement` 接 exec |
| `EmitStatement` | 节点已由 `ValueOf` 加进列表时不再重复 `Add`（`_nodes.Contains` 判断） |
| 收尾追加一道闸 | 两端都没连线的**纯节点 / 临时变量节点**直接删（迭代 4 轮） |

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| 孤立节点 BP_Logic | 11（5 个函数） | **4（1 个函数）** |
| 孤立节点 Cards | 7（5 个函数） | 7（5 个函数，均为 exec 链断点，非纯节点） |
| `GetGraphicInfoString` | 8 节点 / 3 孤立 | **6 节点 / 0 孤立** |
| 一致性校验 | 通过 | **通过** |
| 节点 BP_Logic / Cards | 3244 / 11479 | 3241 / 11479 |
| argN | 2 / 0 | 2 / 0 |

**UI**：`GetGraphicInfoString` 6 节点 / 6 连线 / 问题 0，zoom 0.917（这个函数的图终于能直接看了）。

**当前总账（旧实现 → 新实现）**

| 指标 | 旧 | 新 |
|---|---|---|
| 一致性校验 | 通过（靠事后扫引用） | **通过（构建期就正确）** |
| 孤立节点 | 4 / 16 | **4 / 7** |
| argN | 337 / 355 | **2 / 0** |
| 类型未知 | 10.5% / 8.8% | 24.1% / 26.6% |
| 空视图 | 0 | 0 |

### 重写里程碑 6：GUID 后缀与 Make Array（全量诊断第四十三轮）

**两个「名字难看」的真 bug**

1. **GUID 后缀泄漏**：结构体字段在 cooked 资产里叫 `isSalvaged_1_D83369C241D0F4E11CEDCB8250BC0D97`，
   之前直接当变量名用，画布上就是这种鬼名字。现在 `VariableName` 统一过 `StripGuidSuffix`，
   显示成 `Set isSalvaged`。
2. **`Set K2Node_MakeArray_Array`**：目标是**编译器临时变量**的 `EX_SetArray` 被画成了 Set 节点，
   把编译器的内部名露出来了。现在识别为 compiler-local 就发 `Make Array` 节点并把它的输出
   登记为生产者。

**连带发现的清理漏洞**：`K2Node_MakeArray` 没标 `Pure = true`，输出没人消费时清不掉——
`GetNewLocationNumbers` 一下多出 **28 个孤立节点**。标上纯节点后自动清理。

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| 孤立节点 BP_Logic | 6（2 个函数） | **4（1 个函数）** |
| 孤立节点 Cards | **45（17 个函数）** | **4（4 个函数）** |
| 节点 BP_Logic / Cards | 3219 / 11483 | 3212 / 11408 |
| 一致性校验 | 通过 | **通过** |
| argN | 2 / 0 | 2 / 0 |
| 类型未知 | 24.3% / 26.7% | 24.2% / 26.5% |

**UI**：`SpawnCardinHandbySide` 24 节点 / 38 连线 / 问题 0，正常渲染。

**当前总账（旧实现 → 新实现）**

| 指标 | 旧 | 新 |
|---|---|---|
| 一致性校验 | 通过（靠事后扫引用） | **通过（构建期就正确）** |
| 孤立节点 | 4 / 16 | **4 / 4** |
| argN | 337 / 355 | **2 / 0** |
| 空视图 | 0 | 0 |
| 类型未知 | 10.5% / 8.8% | 24.2% / 26.5% |

### 重写里程碑 7：把「未处理语句」清零（全量诊断第四十四轮）

上一轮的孤立节点修完后，`problems` 里还剩 **44 条 `[未处理语句]`**——这是**真正在丢内容**的地方。
先把分类键改细（原来只按第一个空格切，全归到 `[未处理语句]` 一类，看不出是什么指令），
再逐类补：

| 指令 | 条数 | 处理 |
|---|---|---|
| `EX_SetArray` | 32 | **误报**（已折叠，只是日志判定没排除）→ 修判定 |
| `EX_BindDelegate` | 7 | 新增 `K2Node_DelegateOp`，标题 `Bind Event to <函数名>` |
| `EX_AddMulticastDelegate` | 3 | `Add <委托>` |
| `EX_RemoveMulticastDelegate` | 1 | `Remove <委托>` |
| `EX_ClearMulticastDelegate` | 1 | `Clear <委托>` |
| `EX_SetSet` | 9 | 走容器字面量路径（compiler-local → `Make Array`，否则 Set） |
| `EX_SetMap` | 1 | 同上 |

**结果：两个资产的 `[未处理语句]` 全部归零。**

**新增诊断：未知类型按节点类型分布**（之前只有总数，不知道该修哪里）

| 节点类型 | 未知引脚 | 占比 |
|---|---|---|
| `K2Node_CallFunction` | 892 | **43.8%** |
| `K2Node_TemporaryVariable` | 420 | 20.6% |
| `K2Node_VariableGet` | 408 | 20.0% |
| `K2Node_PromotableOperator` | 121 | 5.9% |
| `K2Node_IfThenElse` | 94 | 4.6% |

结论：**近一半来自「没有签名的调用」**——这是数据缺失（jmap / 索引里没有该函数的参数表），
不是结构 bug。剩下 20% 是临时变量的类型推断。

**指标**

| 指标 | 值 |
|---|---|
| 一致性校验 | **两个资产全部通过** |
| 未处理语句 | **0** |
| 空视图 / 不透明节点 | 0 / 0 |
| 孤立节点 | 4 / 4 |
| argN | 2 / 0 |
| 类型未知 | 24.3% / 26.5% |
| 节点 | 3239 / 11415 |

**UI**：`AddHandCardFromAction` 27 节点 / 35 连线 / 问题 0。

### 重写里程碑 8：Branch 方向 bug + For Each 折叠（全量诊断第四十五轮）

**这轮发现一个影响每个函数的 bug**：`EX_JumpIfNot <addr>` 的语义是「条件为**假**时跳到 addr」，
新实现一开始把跳转目标接成了 `True`、顺序执行接成了 `False`——**每一条 Branch 的 True/False 都是反的**。

```csharp
// 错
if (targetIndex >= 0) WalkBlock(..., branch.ExecOutPin);      // 目标 → True
if (nextIndex >= 0)   WalkBlock(..., branch.ExecElsePin);     // 顺序 → False

// 对
if (targetIndex >= 0) WalkBlock(..., branch.ExecElsePin);     // 目标 → False
if (nextIndex >= 0)   WalkBlock(..., branch.ExecOutPin);      // 顺序 → True
```

这个错误不会让校验失败（连线两端都存在），只会让**图的分支走向和真机相反**——
属于必须靠读字节码语义才能发现的错误。

**宏折叠迁移完成（For Each Loop）**

- `MacroCollapse` 按新 `GraphPin` 重写：删掉了旧版到处出现的 `Source ?? 扫全图` 兜底
  （`Source` 现在是派生的），改连线统一走 `Redirect()`（先断旧边再连新边）。
- `K2Node_MacroInstance` 的构造里有一处**单向连线**（`ThenPin.LinkedTo.Add`），在新模型下会
  让消费者的 `Source` 为 null、渲染断线——改成 `GraphPin.Connect`。
- `Loop` 节点从「回边处」搬到「循环头」：以前建在循环体末尾，结构是错的，`While Loop` 永远折不出来。

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| `CanIDoAnything` | 8 节点 | **7 节点**（含 For Each Loop） |
| 节点 BP_Logic / Cards | 3239 / 11415 | **2973 / 9500** |
| 一致性校验 | 通过 | **通过** |
| 孤立节点 | 4 / 4 | 4 / 4 |
| argN | 2 / 0 | 2 / 0 |

**`While Loop` 还没折出来**：循环头检测只到「回边目标偏移」这一层，而 while 循环的
条件块起点通常比 `JumpIfNot` 本身更早（条件算式的第一条指令），所以 `EmitJumpIfNot`
拿到的偏移对不上。需要按**基本块**判断「这个块是循环头」，下一轮做。

**节点数降幅需要复核**：Cards 降了 1915（-17%）。抽查 `SpawnCardinHandbySide` 24→23、
`ConvertCard` 184→165、`autoPickHandTargets` 93→89（Return 节点仍在、11 个 Branch 仍在），
单个函数降幅都在 5% 左右，判断是「Loop 节点不再重复 + 死节点清理更彻底」，但总数下降的
去向还需要逐函数对账一次才算数。

### 重写里程碑 9：截断检测 + Return 兜底 + While 折叠叫停（全量诊断第四十六轮）

**先回答上一轮欠的对账**：Cards 少了 1915 个节点，是不是丢了内容？

新增校验规则 6：**字节码里有 `EX_Return`、图里却没有 Return 节点的，算截断**。
这是唯一能机械发现「循环/分支后半段丢了」的检查——节点变少本身不可疑，丢了出口才可疑。

结果：

| 资产 | 截断函数 |
|---|---|
| BP_Logic | 1（`ExecuteUbergraph_BP_Logic`） |
| Cards | 2（`DrawTopCardFromDeck`、`ExecuteUbergraph_*`） |

所以**不是大规模截断**。真正有问题的是 `DrawTopCardFromDeck`：39 个节点、没有 Return，
执行链在某条 `PopExecutionFlow` 处终止了。

**修法**：走查时记录每条链的末端 exec（`_tailExec`）；走查结束后若字节码里有 `Return`
但图里没有 Return 节点，就用链尾补一个。ubergraph 除外——UE 里它本来就没有单一出口。

**While Loop 折叠：试了三种判定，全部叫停**

| 判定 | 结果 |
|---|---|
| `_loopHeaders.Contains(offset)` | 回边目标是条件算式第一条指令、不是 `JumpIfNot`，永远对不上 |
| 回边目标落在 `[链起点, 当前]` 之间 | 链起点本身就在回边目标集合里 → 整条链被当成循环体，`autoPickHandTargets` **89 → 24 节点**，循环体全丢 |
| 经过回边目标打标记，紧随的 `JumpIfNot` 即条件 | 同样塌成 **24 节点** |

已全部停用，一律按普通 Branch 处理。正确做法要先做**基本块划分**（块边界 = 跳转目标 /
跳转指令的下一条），再判断「块首是回边目标」——那是下一步。**少折一个宏，好过丢一半节点。**

**当前指标**

| 指标 | 值 |
|---|---|
| 一致性校验（6 条规则） | **两个资产全部通过** |
| 节点 BP_Logic / Cards | 2974 / 10057 |
| 孤立节点 | 4 / 4 |
| argN | 2 / 0 |
| 空视图 / 不透明节点 | 0 / 0 |
| 类型未知 | 26.5% / 29.7% |

**UI**：`CanIDoAnything` 7 节点 / 7 连线 / 问题 0（含 For Each Loop，zoom 0.733）。

### 重写里程碑 10：基本块划分 → While Loop 拿下（全量诊断第四十七轮）

上一轮把 While 折叠叫停，这轮按「基本块划分」重做，成功。

**基本块划分**（`BuildBlocks`）：块首 = 函数第一条指令 ∪ 所有跳转目标 ∪ 跳转/返回/流程栈指令的下一条。
`_blockStartOf[i]` 给出每条指令所属块的起始偏移。

有了它，循环头判定才是精确的：

```csharp
var isLoopHeader = _loopHeaders.Contains(_currentBlockStart)   // 块首是回边目标
                   && jumpIfNot.CodeOffset > offset;           // 且是**向前跳**（出口在目标处）
```

**为什么必须加「向前跳」**：只认 while 形状 `<cond>; JumpIfNot exit; <body>; Jump header`。
do-while 是反的（`<body>; <cond>; JumpIfNot header`），body 在 `JumpIfNot` **之前**，
按 while 处理会把出口当循环体、把循环体整个吞掉。

**又踩到同一个方向坑**：`ValueOf` 返回消费者输入脚，直接喂给 `K2Node_MacroInstance` 的
condition 会抛「连线方向错误」，**中断整条走查**——`autoPickHandTargets` 因此塌成 24 节点。
改用 `ProducerPin(condition)`，解析不到就退回普通 Branch。
（这是第三次栽在「`ValueOf` 的返回值是输入脚」上了，已在 `ProducerPin` 的注释里写明。）

**效果**

| 函数 | 修前 | 修后 |
|---|---|---|
| `autoPickHandTargets` | 89 节点 / 0 宏 | **89 节点 / 2 个 While Loop / Return ✓** |
| `DrawTopCardFromDeck` | 39 节点 / 截断 | **56 节点 / 2 宏 / Return ✓** |
| `ConvertCard` | 165 节点 | **173 节点 / 2 宏 / Return ✓** |
| `CanIDoAnything` | 7 节点 | 16 节点 / 1 宏 / Return ✓（Branch 方向修正后两条路都走到了） |

**指标**

| 指标 | 值 |
|---|---|
| 一致性校验（6 条规则） | **两个资产全部通过** |
| 节点 BP_Logic / Cards | 3103 / 10659 |
| 孤立节点 | 4 / 4 |
| argN | 2 / 0 |
| 空视图 / 不透明节点 | 0 / 0 |
| 类型未知 | 25.5% / 28.3% |

**UI**：`autoPickHandTargets` 89 节点 / 107 连线 / 问题 0。

### 重写里程碑 11：out 参数变成输出脚（全量诊断第四十八轮）

**类型未知从 25.5% / 28.3% 降到 18.2% / 19.8%**，靠的是修一个结构错误而不是补数据。

**根因**：UE 蓝图函数大量用 `out` 参数返回值（`GetHandLocationBySide(side, out handLocation)`），
而我把签名里的**所有**参数都建成了输入脚，`out` 参数没有对应的输出端。于是：

```
GetHandLocationBySide  →  引脚[side:Unknown / handLocation:Unknown]   ← 错
```

`handLocation` 明明在索引里有类型（`/Script/kards.ECardLocationEnum`），却因为没建输出脚而显示 Unknown。

**修法**

| 改动 | 说明 |
|---|---|
| `CallNode` | 签名的 `out` 参数额外建**输出脚**（带名字和类型） |
| `BuildArgumentPins` | `out` 参数的字节码实参是「结果写到哪」，不是数据来源 → **不建输入脚**（避免同名打架） |
| `ProcessLet` 调用分支 | `Let X = call(...)` 时若 X 正好是某个 out 参数名，登记**那个**输出脚（多 out 参数的函数之前会登记错） |

**效果**

```
GetHandLocationBySide  →  引脚[side:Unknown / handLocation:Enum(ECardLocationEnum)]
GetCardFromID          →  引脚[cardID:Unknown / card:Object(BaseCardObject)]
```

| 指标 | 修前 | 修后 |
|---|---|---|
| 类型未知 BP_Logic | 25.5% | **18.2%** |
| 类型未知 Cards | 28.3% | **19.8%** |
| 节点 BP_Logic / Cards | 3103 / 10659 | 2857 / 9639（out 参数不再造多余输入脚） |
| 一致性校验（6 条） | 通过 | **通过** |
| 孤立节点 | 4 / 4 | 4 / **7** |
| argN | 2 / 0 | 2 / 0 |

**新发现的问题（下一轮修）**：`SpawnCardinHandbySide` 里

```
标题「TextIsEmpty (KismetTextLibrary)」　引脚[InText:Text / ReturnValue:Bool]
标题「Branch」　引脚[:Exec / Condition:Text / True:Exec / False:Exec]     ← Condition 是 Text，应为 Bool
```

Branch 的条件接到了 `TextIsEmpty` 的**输入脚**上，而不是它返回的 `ReturnValue`。这是又一处
「拿输入脚当上游」的方向错误（和 `ProducerPin` 是同一类），需要定位是哪条路径把它接上去的。

**UI**：`DrawTopCardFromDeck` 46 节点 / 51 连线 / 问题 0。

### 重写里程碑 12：const-ref 与 out 的区分（全量诊断第四十九轮）

上一轮标记的 `Branch.Condition:Text`（应为 `Bool`），根因找到了——**不是方向错误，是签名语义理解错了**。

**证据链**

1. `--trace` 打印条件解析：`[cond] pin='value' type=Text source='InText'`
2. 说明条件接到了 `TextIsEmpty` 的 `InText` 上，而它是**输出脚**
3. jmap 原始数据：`{"name":"InText","type":"TextProperty","dir":"out"}` —— **jmap 把 const-ref 参数也标成了 out**
4. 于是 `GetFirstOutputParam()` 返回 `InText`（Text），而不是 `ReturnValue`（Bool）

**修法**：`out` 参数有两种含义，按「函数有没有返回值」区分。

| 情况 | `out` 参数的真实含义 | 处理 |
|---|---|---|
| 函数**没有**返回值 | 真输出（`GetHandLocationBySide(side, out handLocation)`） | 建**输出脚** |
| 函数**有**返回值 | `const&` 引用输入（`TextIsEmpty(const FText& InText)`） | 建**输入脚** |

另外 `CallNode` 现在优先返回名字为 `ReturnValue` 的脚，而不是「第一个输出脚」——
多 out 参数的函数里第一个输出脚可能是别的参数，拿错了整条链路类型都错。

**效果**

```
TextIsEmpty (KismetTextLibrary)  引脚[InText:Text / ReturnValue:Bool]
Branch                           引脚[:Exec / Condition:Bool / True:Exec / False:Exec]   ← 修好
```

| 指标 | 修前 | 修后 |
|---|---|---|
| 类型未知 BP_Logic | 18.2% | 19.5% |
| 类型未知 Cards | 19.8% | 21.1% |
| 一致性校验（6 条） | 通过 | **通过** |
| 孤立节点 | 4 / 7 | **4 / 4** |
| 节点 BP_Logic / Cards | 2857 / 9639 | 2981 / 10285 |

类型未知指标**略微变差**（18.2% → 19.5%），原因是 const-ref 参数从「带签名的输出脚」变成了
「输入脚」，而它的值来自字节码里的局部变量读（类型未知）。这是**正确的代价**：之前那个
数字好看是因为把输入当成了输出，整条链路的类型都是错的。

**UI**：`SpawnCardinHandbySide` 22 节点 / 35 连线 / 问题 0。

**当前总账（相对旧实现起点）**

| 指标 | 旧实现 | 现在 |
|---|---|---|
| 一致性校验 | 通过（靠事后扫引用） | **通过（构建期就正确，6 条规则）** |
| 孤立节点 | 4 / 16 | **4 / 4** |
| argN | 337 / 355 | **2 / 0** |
| 类型未知 | 10.5% / 8.8% ⚠ | 19.5% / 21.1% |
| 空视图 / 未处理语句 | — | **0 / 0** |

### 重写里程碑 13：变量类型从属性表补全（全量诊断第五十轮）

**类型未知 19.5% / 21.1% → 14.4% / 15.8%**。

三处改动：

1. **函数属性表里的「所有」变量都登记类型**，不只参数。
   函数的局部变量（`side` / `cardID` 这类）也在 `LoadedProperties` 里，之前只取了 `CPF_Parm`，
   读局部变量时查不到 → Unknown。现在全部进 `_localTypes`，`ReadMember` 优先查它。
2. **参数脚显式采用签名里的类型**：调用点的参数类型是确定的，
   即使实参来自「类型未知的局部变量读」，引脚也应该显示成签名要求的类型。
3. （上一轮已有）有返回值的函数，`out` 参数按 const-ref 输入处理。

**效果**

```
FetchCardsByLocation  引脚[:Exec / location:Unknown / :Exec / QtyInLocation:Int /
                            isLocationFull:Bool / AllCardsInLocation:Array<Object(BaseCardObject)> /
                            FirstCard:Object(BaseCardObject) / fetchedCardsIDs:Array<Int>]
```

| 指标 | 修前 | 修后 |
|---|---|---|
| 类型未知 BP_Logic | 19.5% | **14.4%** |
| 类型未知 Cards | 21.1% | **15.8%** |
| 一致性校验（6 条） | 通过 | **通过** |
| 孤立节点 | 4 / 4 | 4 / 4 |
| argN | 2 / 0 | 2 / 0 |
| 节点 BP_Logic / Cards | 2981 / 10285 | 2981 / 10285 |

**剩余的未知分布**（Cards，4664 个）

| 节点类型 | 未知引脚 | 占比 |
|---|---|---|
| `K2Node_VariableGet` | 1961 | **42.0%** |
| `K2Node_CallFunction` | 1269 | 27.2% |
| `K2Node_TemporaryVariable` | 879 | 18.8% |
| `K2Node_IfThenElse` | 369 | 7.9% |

`VariableGet` 是大头：这些是**成员读**（`card.attack`），类型要从「对象所属类的属性表」查，
而对象本身的类型往往也是 Unknown（连锁）。要真正解决得做类型传播（从赋值链推对象类型），
那是另一个量级的活。

**UI**：`DrawTopCardFromDeck` 47 节点 / 52 连线 / 问题 0。

### 重写里程碑 14：观感对齐 UE 编辑器（全量诊断第五十一轮）

类型未知降到 15% 之后，这一轮转去做「像不像 UE」的观感。三处改动：

**1. 节点标题去掉归属类后缀**

```
之前：GetCardFromID (BP_CardFunctions_C)
现在：GetCardFromID          副标题：BP_CardFunctions_C
```

UE 里节点标题就是函数名，归属类靠**标题栏颜色**区分。之前括号后缀又长又重复——
归属类本来就已经在副标题里了。

**2. 引脚配色补齐到 UE 的调色板**

之前只有 8 种颜色，`Enum` / `Name` / `Text` / `Byte` / `Int64` / `Delegate` 全落到灰色兜底，
画布上一片灰、认不出类型。现在按 UE 的对应关系补齐：

| 类型 | 颜色 | 类型 | 颜色 |
|---|---|---|---|
| Exec | 白 `#F0F0F0` | String | 品红 `#E056A0` |
| Bool | 暗红 `#C0392B` | Name | 淡紫 `#C39BD3` |
| Byte | 深绿 `#1E8449` | Text | 肉粉 `#D98880` |
| Int | 青 `#1ABC9C` | Object / Class | 蓝 `#3B8ED0` |
| Int64 | 蓝 `#2E86C1` | Struct | 青绿 `#148F77` |
| Float / Double | 绿 `#2ECC71` | Enum | 绿松石 `#27AE60` |
| | | Delegate | 土黄 `#B7950B` |

**3. 布局收紧**

`LayerSeparation` 90 → 68，`NodeSeparation` 40 → 26。

| 函数 | 跨度 | zoom |
|---|---|---|
| `DrawTopCardFromDeck` | 1650×3927 → **1524×3409** | 0.185 → **0.205** |
| `CanIDoAnything` | 962×1397 | **0.520** |

**顺带的效果**（上一轮类型改动的可视化验证）

```
GetCardFromID          引脚[:Exec / cardID:Int / :Exec / card:Object(BaseCardObject)]
FetchCardsByLocation   引脚[:Exec / location:Enum(ECardLocationEnum) / QtyInLocation:Int / ...]
TextIsEmpty            引脚[InText:Text / ReturnValue:Bool]
```

之前这些都是 `Unknown`，现在有类型、有颜色。

**指标**：一致性校验两个资产仍**全部通过**（6 条规则），孤立节点 4 / 4，argN 2 / 0。
UI：`CanIDoAnything` 16 节点 / 19 连线 / 问题 0。

### 重写里程碑 15：UI 线程崩溃 + 按类上色（全量诊断第五十二轮）

**先记一个真崩溃**——它解释了「为什么有时候图看起来完全不对」。

```
新反编译器失败（回退旧投影）：InvalidOperationException:
Operations that change non-concurrent collections must have exclusive access.
```

`ApplyGraph` 直接改绑定到 UI 的 `ObservableCollection`，而它可能跑在后台线程上。
异常被外层 catch 吞掉后**静默回退到旧投影**——画布上显示的不是新反编译器的图。

实测症状：`DrawTopCardFromDeck` 从 47 节点 / 1524×3509 变成 **52 节点 / 10520×780**（单行铺开），
一眼就是坏的，但日志里只有一行「回退旧投影」，很容易被当成「图本来就长这样」。

**修法**：`ApplyGraph` 检查 `Dispatcher.CheckAccess()`，不在 UI 线程就 `Invoke` 过去；
异常日志补上 `StackTrace`，下次能直接定位。

修后：`DrawTopCardFromDeck` **47 节点 / 52 连线 / 问题 0，跨度 1524×3509，zoom 0.207**。

**按类上色**（新增 `NodeColors`）

- 无归属类的节点（Branch / Set / Get / Return）用固定调色板；
- 有归属类的 impure 调用节点，把**类名哈希成色相**（S=0.42、V=0.55，深色画布上不刺眼），
  同一个类在整张图里颜色一致。
- 用 FNV-1a 而不是 `string.GetHashCode()`——后者在不同进程间可能不同，
  同一个类在两次启动里颜色会变。
- `GraphNodeVm` 增加 `HeaderBrush`，XAML 从「Kind + 转换器」改成直接绑定它。

**指标**：一致性校验两个资产**全部通过**（6 条规则），
节点 2981 / 边 3192，孤立 4，argN 2，类型未知 **14.4%**。

### 重写里程碑 16：失败必须显式 / UI 一致性自检（全量诊断第五十三轮）

上一轮修了 UI 线程崩溃，这轮把**「失败被静默吞掉」**这个根子堵死。

**1. 移除静默回退**

之前的逻辑：新反编译器抛异常 → catch → 回退旧投影 → 画布上出现一张**看起来完全正常**的图。
于是「渲染失败」和「图本来就这样」在界面上无法区分。上一轮那个 52 节点 / 10520×780 的单行图
就是这么来的，而且日志里只有一行「回退旧投影」。

现在：

```csharp
if (Settings.Display.UseNewDecompiler)
{
    if (TryRenderWithNewDecompiler(item)) return;

    ClearGraph();                     // 宁可空着，也不显示一张错的图
    Status = $"反编译失败：{_lastDecompilerError}";
    Diagnostics = $"{Status}　画布已清空（未回退旧投影，避免显示一张看似正常的错图）";
    return;
}
```

旧投影只在设置里**显式关掉**新反编译器时才走。

**2. 新增 `tools/check-ui.ps1`**

逐个函数对照两个数字：

| 来源 | 路径 |
|---|---|
| 无头 `--vmdump` | 字节码 → 节点（不经过 UI） |
| 真实 UI `--open` | 字节码 → 节点 → GraphAdapter → 画布 |

两者不一致、或日志里出现「失败」，脚本退出码非 0。这是唯一能抓到「静默回退」的检查。

**实测（7 个函数，两个资产）**

```
OK   CanIDoAnything          无头=16  UI=16
OK   GetGraphicInfoString    无头=6   UI=6
OK   SpawnCardinHandbySide   无头=22  UI=22
OK   DrawTopCardFromDeck     无头=47  UI=47
OK   ConvertCard             无头=175 UI=175
```

**3. 异常日志补 StackTrace**，下次失败能直接定位到行。

**指标**：一致性校验（6 条规则）两个资产**全部通过**；
节点 2981 / 边 3192；孤立节点 4；argN 2；类型未知 14.4%。

### 重写里程碑 17：全函数渲染自检（全量诊断第五十四轮）

上一轮有了单函数对照脚本，这轮补上**全量**：`--check-all <资产>`。

对每个函数跑一遍 **构建 → GraphAdapter.ToView（含布局）**，报告四类问题：

| 检查 | 意义 |
|---|---|
| 渲染失败 | 某个函数在转 VM / 布局阶段抛异常 |
| 数量不一致 | 模型节点数 ≠ VM 节点数（说明有节点被丢了） |
| 空图 | 构建出 0 节点 |
| 布局回退 | Msagl 失败、退回简单分列（图会排得很难看） |

**结果（475 个函数）**

```
BP_Logic          函数 181　空节点 0　渲染失败 0　数量不一致 0　布局回退 0
BP_CardFunctions  函数 294　空节点 0　渲染失败 0　数量不一致 0　布局回退 0
```

**布局回退 = 0** 是个好消息：说明这 475 张图的 Sugiyama 布局**没有一次**退回简单分列，
画布上的排布都是真的算出来的，不是兜底出来的。

**与上一个里程碑配合使用**

| 命令 | 覆盖范围 |
|---|---|
| `--validate <资产>` | 图**语义**自洽（6 条规则：入口脚、纯节点 exec、悬空边、执行链、零引脚、截断） |
| `--check-all <资产>` | 图**渲染**可用（475 个函数全部能渲染 + 布局） |
| `tools/check-ui.ps1` | UI 与无头**一致**（防静默回退旧投影） |

三层合并之后，「我验证过的图」和「你看到的图」之间那层不确定性基本消掉了。

### 重写里程碑 18：布局方向改成从左往右（全量诊断第五十五轮）

UE 编辑器的图是**从左往右**流动的，而 Msagl 的 Sugiyama 默认分层是**从上到下**——
于是我们的图长成又高又窄的一条（`DrawTopCardFromDeck` 1524×3509），和 UE 的阅读方向正好垂直。

**一行改动**

```csharp
var settings = new SugiyamaLayoutSettings
{
    LayerSeparation = 68,
    NodeSeparation = 26,
    EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.Spline },
    Transformation = PlaneTransformation.Rotation(Math.PI / 2),   // ← 旋转 90°
};
```

注意 `PlaneTransformation` 在 `Microsoft.Msagl.Core.Geometry.**Curves**` 下，
不在 `Geometry` 下（找错命名空间会编译失败）。

**效果（跨度从竖高变横宽，zoom 全面提升）**

| 函数 | 改前 | 改后 | zoom |
|---|---|---|---|
| `DrawTopCardFromDeck` | 1524×3409 | **4652×836** | 0.205 → **0.258** |
| `AddCardStats` | 320×2342 | **3512×153** | 0.299 → **0.342** |
| `CanIDoAnything` | 962×1297 | **1916×529** | 0.540 → **0.626** |

`CanIDoAnything` 的 zoom 从 0.54 到 0.63，`AddCardStats` 从 0.30 到 0.34——
同样的屏幕面积能看清更多细节。

**验证（三层全过）**

| 检查 | 结果 |
|---|---|
| `--check-all` 两个资产 | 475 函数，渲染失败 **0**，数量不一致 **0**，布局回退 **0** |
| `--validate` 两个资产 | 六条规则**全部通过** |
| `tools/check-ui.ps1` | `CanIDoAnything` 16/16、`AddCardStats` 15/15 一致 |

**布局回退仍是 0**：旋转之后 475 张图依然全部由 Sugiyama 算出来，没有一张退回简单分列。

### 重写里程碑 19：孤立节点清零（全量诊断第五十六轮）

**BP_Logic 4 → 0，Cards 4 → 0。475 个函数，一个孤立节点都没有了。**

**根因**：`ValueOf()` 返回的是**消费者输入脚**，但有好几处写成 `if (value.IsOutput) Connect(...)`：
条件永远为假，值**从来没连上**，生产者就成了孤立节点。

```csharp
// 错（4 处）
if (value.IsOutput) GraphPin.Connect(value, valuePin);
else valuePin.Value = value.Value;

// 对
if (ProducerPin(value) is { } source) GraphPin.Connect(source, valuePin);
else valuePin.Value = value.Value;
```

修掉的 4 处：`EX_LocalOutVariable` 赋值、编译器临时变量赋值、类成员 Set、持久帧 Set。

**同一个坑的第四次**（前三次分别在 `RegisterProducer`、While 宏的 condition、Switch 条件）。
这次是**最后一次**：`ProducerPin()` 把所有「要当上游用」的地方统一收口，
以后再写 `if (pin.IsOutput)` 就是错的。

**效果**

| 指标 | 修前 | 修后 |
|---|---|---|
| 孤立节点 BP_Logic | 4（1 个函数） | **0** |
| 孤立节点 Cards | 4（4 个函数） | **0** |
| 节点 BP_Logic / Cards | 2980 / 10284 | 3263 / 11326（孤立的链重新接上，不再被当死节点删） |
| 边 | 3191 / 12519 | 3583 / 14125 |
| 走查器问题 | 1（ubergraph 被误补 Return） | **0** |
| 一致性校验（6 条） | 通过 | **通过** |
| 渲染自检（`--check-all`） | 0 失败 / 0 回退 | **0 失败 / 0 回退** |
| argN | 2 / 0 | 2 / 39（0.2%，重新接上的节点带进来的） |

**顺带修掉**：ubergraph 被误补 Return 的问题（`isUbergraph` 判断之前锚点没匹配上、没生效）。
UE 里 `ExecuteUbergraph_*` 没有单一出口，补一个 Return 是错的。

**验证三层全过**

```
--validate   两个资产：六条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、AddCardStats 16/16 一致
```

### 重写里程碑 20：边连对了没有（全量诊断第五十七轮）

孤立节点清零只证明「有边」，**不证明边连对了**。这轮加了两条语义校验：

**规则 7：数据输入脚不能接多个上游**

UE 里一个数据脚只能有一个来源；多接说明连线被重复登记。

（第一版没区分 exec / 数据，报了 19 处，全是 ubergraph 和 `Branch.True/False → Return` ——
这两类在 UE 里**本来就允许多条上游**，所以规则收窄为只约束**非 exec** 的数据脚。）

**规则 8：连线两端类型必须兼容**

| 允许的兼容 | 理由 |
|---|---|
| 完全相同 | — |
| Int / Int64 / Byte / Float / Double 之间 | UE 自动转换 |
| Object / Class / Interface / SoftObject / SoftClass 之间 | 蓝图类型系统 |
| Enum ↔ Byte | 枚举就是按 byte 传输的 |
| 任一端 Unknown | 类型缺失不是「连错」的证据 |

**结果：两个资产全部通过，类型不兼容 0 处。**

这个 0 比孤立清零更有说服力：它说明**每条数据边的上游类型和下游期望类型是对得上的**，
不是随便连了个东西上去。

**校验器现在是 8 条规则**

| # | 规则 | 抓什么 |
|---|---|---|
| 1 | 入口只应有真参数 | 入口挂编译器临时变量 |
| 2 | 纯节点不能有 exec 脚 | 静态节点画错形状 |
| 3 | 连线两端节点必须存在 | 悬空引用 |
| 4 | 执行链不能断 | exec 入脚没接 |
| 5 | 节点不能零引脚 | 空壳节点 |
| 6 | 有 Return 指令必须有 Return 节点 | 执行链截断 |
| 7 | 数据脚不能多上游 | 连线重复 |
| 8 | 两端类型兼容 | **连错线** |

**三层验证全过**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、AddCardStats 16/16 一致
```

### 重写里程碑 21：类型传播（全量诊断第五十八轮）

**类型未知 14.4% / 15.8% → 11.6% / 12.8%。**

做法：沿数据边把已知类型推给未知的一侧，迭代到不动点（最多 8 轮）。

两个方向都要做：

| 方向 | 场景 |
|---|---|
| 生产者已知 → 消费者未知 | 普通情况 |
| 消费者已知（签名给的）→ 生产者未知 | 成员读、临时变量「自己查不到但下游知道」 |

不做第二个方向的话，一个未知的成员读会让整条下游链路都保持未知。

**冲突不传播**：两端都已知但不兼容时直接跳过，留给校验器报——默默改掉一个类型会把真问题藏起来。

顺带：`Local Unknown (X)` 这类临时变量节点标题里嵌了类型，定型后要跟着改
（`RetypeNodeHeader`），否则引脚有类型、标题还写着 `Unknown`。

**暴露出的类型不兼容：8 处 → 2 处**

传播一上来就在 Cards 报了 8 处不兼容，全是同一个模式：

```
「Local Int (CallFunc_Map_Find_Value)」(Int) → ExecuteOnCardLocationMoved.oldLocation(Enum(ECardLocationEnum))
```

原因是 **jmap 把泛型容器的元素类型抹成了 `IntProperty`**——map 的值本来是 `Enum` / `Object`，
签名里却是 Int。放宽「枚举 ↔ Int」兼容后降到 2 处。

**剩下 2 处是同一种数据噪声**（已确认不是连错线）：

```
MoveCardToTopOfDeck: Map_Find(Int) → ResetCardInBattle.cardToChange(Object)
selectCardToDraw:    GetCardFromID(Object) → ADD.NewItem(Int)
```

一个是 map 的值本该是 Object、jmap 说 Int；一个是数组元素本该是 Object、jmap 说 Int。
要根治得让「传播来的类型」在遇到已知消费者时可以被覆盖（标成软类型），那是下一步。

**三层验证**

```
--validate   BP_Logic 八条规则全过；Cards 只剩上述 2 处已知噪声
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、AddCardStats 16/16 一致
```

### 重写里程碑 22：软类型与推断边界（全量诊断第五十九轮）

**类型未知 14.4% / 15.8% → 11.6% / 12.5%**，并且八条校验规则**两个资产全部通过**。

**软类型机制**

`GraphPin` 新增 `IsTypeInferred`：标记「这个类型是推断的，不是事实来源」。
两类引脚会被标成 soft：

| 来源 | 为什么 |
|---|---|
| 编译器临时变量节点（`Local X`） | 类型本来就是猜的（先读后写时甚至还是 Unknown） |
| 沿数据边传播得到的类型 | 推出来的，不是签名或属性表给的 |

传播规则：未知的、或者 just「推断值」的一侧，让位给权威的一侧；**两边都权威且不兼容就不动**。

**踩到的两个坑**

1. **传播必须放在折叠之后**。折叠会重建连线（宏把消费者的上游改接到自己的输出脚上），
   放在前面的话这些新边没参与传播——校验器于是把它们报成「连错线」（实测 2 处）。
2. **`ProcessLet` 给临时变量定型时把 soft 标记清掉了**（调了不带 `inferred` 的重载），
   于是「猜出来的 Int」又变回权威值，顶住了「签名要求 Object」。

**校验器只报权威冲突**

最后剩下的 4 处冲突全部来自 **jmap 泛型类型抹除**：`Map_Find(...)` 在 jmap 里一律返回 `Int`，
不管容器实际装的是 Object / String / Name。校验器现在跳过：

- `IsTypeInferred` 的引脚；
- 生产者是编译器临时变量节点的边。

理由：这两类的类型都不是事实来源，拿它们当「连错线」的证据是假阳性。
**没有靠放宽类型兼容来掩盖问题**——权威类型之间的冲突依然会报。

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、AddCardStats 16/16 一致
孤立节点     0 / 0
```

### 重写里程碑 23：连线按引脚类型上色（全量诊断第六十轮）

**这轮找到一个「一直存在但没人发现」的渲染 bug。**

`GraphView.xaml` 的连线样式：

```xml
<Style TargetType="nodify:CircuitConnection">
    <Setter Property="Stroke" Value="{StaticResource Edge.Data}" />      <!-- 数据线：灰 -->
    <Setter Property="StrokeThickness" Value="1.4" />
            <Setter Property="Stroke" Value="{StaticResource Edge.Exec}" />   <!-- ← 没包在 Trigger 里 -->
            <Setter Property="StrokeThickness" Value="2.2" />
</Style>
```

第二组 Setter **无条件覆盖了第一组**——于是**所有连线都是同一种白色、2.2px**，
exec 和数据线完全分不出来。这不只是「不够像 UE」，是「看起来全一个样」。

**修法**

```xml
<Setter Property="Stroke" Value="{Binding Output.Brush, FallbackValue=#FF7F8C8D}" />
<Setter Property="StrokeThickness" Value="1.4" />
<Style.Triggers>
    <DataTrigger Binding="{Binding IsExec}" Value="True">
        <Setter Property="Stroke" Value="{StaticResource Edge.Exec}" />
        <Setter Property="StrokeThickness" Value="2.2" />
    </DataTrigger>
</Style.Triggers>
```

数据连线现在用**上游引脚的颜色**（与引脚同色，UE 就是这样），exec 线保持白色加粗。

`FallbackValue` 是必需的保险：我**看不到画面**，绑定一旦失败连线会变成透明/不可见；
给一个灰色兜底，至少「看得见」。

**顺带确认**：`DrawTopCardFromDeck` 从 47 节点变成 **56 节点**（类型传播让更多节点
连上了线、不再被当死节点删掉），无头与 UI 都是 56，一致。

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     DrawTopCardFromDeck 56/56、SpawnCardinHandbySide 24/24 一致
```

### 重写里程碑 24：绑定错误不再静默（全量诊断第六十一轮）

上一轮那个「连线全白」的 bug 之所以存在很久，根本原因是：**WPF 的样式/绑定失效是静默的**。
写了不生效不会报错，只会画错，而我**看不到画面**。

**这轮把绑定错误接进日志**

```csharp
System.Diagnostics.PresentationTraceSources.Refresh();
var source = PresentationTraceSources.DataBindingSource;
source.Listeners.Add(new BindingLogListener());
source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;
```

挂载成功会写一行 `绑定追踪已启用（Error+Warning）`——
否则日志里「0 条绑定错误」无法区分「没问题」和「压根没挂上」（排查过程中真踩了这个）。

**实测结果**

```
[18:44:27.283] 绑定追踪已启用（Error+Warning）
[18:44:29.692] 已渲染 .../DrawTopCardFromDeck: 56 节点 / 61 连线
[18:44:29.710] 新反编译器 DrawTopCardFromDeck: 56 节点 / 61 连线，问题 0
```

**渲染过程中 0 条绑定错误**：说明 `GraphView.xaml` 里所有绑定路径都解析成功了，
包括上一轮新加的 `Output.Brush`、`HeaderBrush`、`IsExec` 触发器。

**顺带核对的 XAML**

| 项 | 结论 |
|---|---|
| `Output.Brush` / `FallbackValue` | 有效（0 条绑定错误） |
| `HeaderBrush`（`GraphNodeVm`） | 有效 |
| `ConnectorTemplate` 的 `IsExec` 触发器 | 有效 |
| `Anchor` 的 `OneWayToSource` | 有效（否则连线不会跟随引脚） |
| `KindToBrush` 转换器 | **已不再被使用**（标题栏改成直接绑 `HeaderBrush`），保留不删，避免动到未知引用 |

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17 一致
```

### 重写里程碑 25：逐函数对账 + Return 类型（全量诊断第六十二轮）

这轮兑现上一轮的承诺：把字节码指令和节点并排摆出来，人工核对 3 个小函数。

**`AIStopHangingOnMulligan`**

```
字节码: LocalFinalFunction=1, Return=1, EndOfScript=1
节点:   Entry → ExecuteUbergraph_BP_Logic(调用) → Return Node
```

✅ 1 个调用对 1 个 `LocalFinalFunction`，Return 对上，3 个节点正好。

**`AddActionPlayedGotcha__DelegateSignature`**

```
字节码: Return=1, EndOfScript=1
节点:   Entry(cardID:Int) → Return Node
```

✅ 委托签名，一个参数、没有函数体，2 个节点正确。

**`GetGraphicInfoString`**（29 节点）

```
字节码: Let=23, LetObj=2, CallMath=1, Return=1（嵌套 CallMath=19, FinalFunction=2, VirtualFunction=1）
节点:   Entry / Set str / Return / 7×Concat_StrStr / 4×Conv_IntToString / 2×FTrunc / Round /
        2×GetGameUserSettings / GetViewportSize(SizeX,SizeY) / GetOverallScalabilityLevel /
        GetFullscreenMode / GetEnumeratorUserFriendlyName / 2×Local Double(BreakVector2D X,Y)
```

✅ 结构正确：字符串拼接链 + 一个 `Set str`，`GetViewportSize` 的 out 参数变成了
`SizeX:Int / SizeY:Int` 输出脚。

**对账查出的 4 个小问题**

| 问题 | 处理 |
|---|---|
| `Return Node` 的返回值脚是 `Unknown`（函数声明返回 String） | **已修**：优先用 `_localTypes` 里的声明类型 |
| `str` 的 Get 脚是 Unknown | 类变量不在函数属性表里，属数据限制 |
| `BreakVector2D` 显示成两个 `Local Double` 节点 | Break 折叠只处理 `VariableGet` 分组，不处理调用结果 |
| `Round` 的参数叫 `arg0` | jmap 里该参数无名 |

**修完的效果**

```
Return Node  引脚[:Exec / value:String]     ← 之前是 value:Unknown
```

| 指标 | 修前 | 修后 |
|---|---|---|
| 类型未知 BP_Logic | 11.6% | **11.0%** |
| 类型未知 Cards | 12.5% | **12.2%** |

**顺带修掉 1 处不兼容**：`Object ↔ Struct` 现在算兼容——索引的类型字符串
（`/Script/SlateCore.SlateBrush`）**分不出 struct 和 class**，那是不兼容是数据局限，
不是连错线。这条写进了 `TypesCompatible` 的注释。

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、GetGraphicInfoString 29/29 一致
```

### 重写里程碑 26：值表达式与临时变量标题（全量诊断第六十三轮）

**改动 1：`Let X = <结构体成员读>` 不再生成临时变量节点 + 赋值节点**

原来 `ProcessLet` 只把「调用」当成值表达式，其他一律走「建临时变量节点 + 赋值节点」。
于是 `BreakVector2D` 这类结构体字段读会多出两坨东西（一个 `Local Double` 占位 +
一个赋值节点），而 UE 里它只是 `Break Vector2D` 的一个输出脚。

新增 `IsValueExpression`（调用 / 成员读 / 结构体字段读 / 结构体常量 / 转型），
命中就只登记生产者。

| 指标 | 修前 | 修后 |
|---|---|---|
| 节点 BP_Logic | 3263 | **3195**（少 68） |
| 节点 Cards | 11326 | **11226**（少 100） |
| 类型未知 BP_Logic | 11.0% | **10.6%** |
| 类型未知 Cards | 12.2% | **12.0%** |

**改动 2：编译器内部名不再出现在标题里**

```
之前：Local Double (CallFunc_BreakVector2D_X)
现在：Local Double
```

规则交给节点自己（`K2Node_TemporaryVariable.RefreshTitle`），
`CallFunc_*` / `K2Node_*` 这类名字只保留在引脚名和 `VariableName` 里。

**没修掉的部分（根因已定位）**

`GetGraphicInfoString` 里那两个 `Local Double` 依然是**先读后写的编译器临时变量**：

```
[miss] 'CallFunc_BreakVector2D_Y'
[miss] 'CallFunc_BreakVector2D_X'
```

`--trace` 显示这两个名字**从来没被赋值过**——生产者在**我们没走到的块**里。
所以它们只能是占位节点，变不成 `Break Vector2D`。要真正解决得先让走查覆盖那些块，
那是比「折叠」大一个量级的活。

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     GetGraphicInfoString 29/29 一致
```

### 重写里程碑 27：块覆盖诊断，推翻上一轮的结论（全量诊断第六十四轮）

上一轮我说「`CallFunc_BreakVector2D_X/Y` 的赋值在**没走到的块**里」。**这轮证明那个说法是错的。**

**新诊断：`--blocks <资产> <函数>`**

列出每条指令的偏移、操作码、是否被走查访问过：

```
# GetGraphicInfoString 块覆盖
指令 28　访问过 27　未访问 1

| 1083 | EndOfScript | ✗ 未访问 |
```

**只有 1 条未访问，还是 `EndOfScript`**（`Return` 已经结束执行链，它本来就到不了）。
所以「块没走到」这个假设**不成立**。

**给 `[miss]` 加上指令偏移后，真相是：**

```
[miss] 'CallFunc_BreakVector2D_Y' @202
[miss] 'CallFunc_BreakVector2D_X' @383
```

`@202` / `@383` 都是 **`Let` 指令**——这两个临时变量是被某条 Let 的**表达式内部**读到的，
而**整个函数里没有任何一条 Let 把它们当成赋值目标**。

也就是说：UE 编译器生成了「读 `CallFunc_BreakVector2D_X`」，却把这个名字的写入藏在了
我们不认识的语法形式里（大概率是「Let 到结构体成员」那种形态）。
这不是可达性问题，是**赋值形态没被识别**。

**这一轮的产出**

| 项 | 内容 |
|---|---|
| `--blocks` 命令 | 逐指令的偏移 / 操作码 / 是否访问过，块覆盖问题的唯一证据来源 |
| `[miss]` 带偏移 | 定位「这个临时变量被哪条指令读」只差这一条信息 |
| 结论修正 | 上一轮把「没识别赋值形态」误判成「块没走到」 |

**回归验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     GetGraphicInfoString 29/29 一致
```

**下一步**：拿 `@202` 那条 `Let` 的完整表达式树打出来，看它的 `Variable` 到底是什么形态
（`EX_StructMemberContext`？`EX_LocalOutVariable` 的变体？），再决定怎么处理。

### 重写里程碑 28：表达式树诊断，`BreakVector2D` 的写入者查无此人（全量诊断第六十五轮）

**新增 `--expr <资产> <函数> <偏移>`**：把那条指令的完整表达式树打出来，递归到指针路径。

第一次打的时候只输出 `Variable = KismetPropertyPointer`——等于没打。补上指针路径解析后才有信息量：

```
# GetGraphicInfoString @202

EX_Let
  Value → [CallFunc_FTrunc_ReturnValue]
  EX_LocalVariable
    Variable → [CallFunc_FTrunc_ReturnValue]
  EX_CallMath
    StackNode = -260
    Parameters[1]:
      EX_LocalVariable
        Variable → [CallFunc_BreakVector2D_Y]
```

翻译过来就是：

```csharp
Let CallFunc_FTrunc_ReturnValue = FTrunc(CallFunc_BreakVector2D_Y);
```

`@383` 是同一条形态的 X 分量。

**那么写入者是谁？查不到。**

| 证据 | 结论 |
|---|---|
| `--blocks` | 28 条指令里 27 条走到（唯一没走的 `EndOfScript` 本来就到不了） |
| `--trace` | **25 条 `Let`（正好等于 `Let=23 + LetObj=2`），没有任何一条写入 `CallFunc_BreakVector2D_*`** |
| `--expr` | 两处读取都在普通 `Let` 的参数里，形态没有特殊之处 |

所以：**按 UAssetAPI 解出来的字节码，这两个名字只有读、没有写**。
这不是走查漏了块（已证），也不是我们认不出某种赋值形态（`--expr` 显示读取处是标准 `EX_LocalVariable`）。

**诚实的结论**：值从哪里来，我查不到。要么是 UAssetAPI 对这个函数的字节码解析有缺，
要么 UE 编译器确实允许这种「跨函数宏临时量」（`BreakVector2D` 是宏，编译时内联）。
在没有更多证据之前，我不再编一个根因——上一轮就是因为编根因编错了。

**实际影响**：每个 `BreakVector2D` 多两个 `Local Double` 占位节点，属于观感问题，不影响其他节点正确性。

**回归**

```
--validate   两个资产：八条规则全部通过
```

**下一步（有证据支撑的那一条）**：把这个函数的字节码和 **UEBGV 对同一函数的输出**逐节点比。
UEBGV 用 CUE4Parse 解析，如果它那里有写入者，就说明是 UAssetAPI 的解析差异；如果也没有，
就说明字节码本来就是这样，我们只能在显示层处理（把这类占位节点折进 Break 节点）。

### 重写里程碑 29：查实 `BreakVector2D` 的写入者「不存在」（全量诊断第六十六轮）

**新增 `--locals <资产> <函数>`**：列出函数属性表的全部条目（名字 / 标志 / 类型）。

```
| `fps`                          | CPF_BlueprintVisible, CPF_Parm | FGenericProperty |
| `value`                        | CPF_Parm, CPF_OutParm          | FGenericProperty |
| `str`                          | CPF_Edit, CPF_BlueprintVisible | FGenericProperty |
| `CallFunc_GetViewportSize_ReturnValue` | CPF_None               | FStructProperty  |
| `CallFunc_BreakVector2D_X`     | CPF_None                       | FGenericProperty |
| `CallFunc_BreakVector2D_Y`     | CPF_None                       | FGenericProperty |
```

**关键**：`CallFunc_BreakVector2D_X/_Y` **确实被声明成本函数的局部变量**（`CPF_None`）。
所以不是「名字解析错了」，是写入侧的问题。

**再排除「嵌套 Let」这个可能**

同一命令还扫**整棵表达式树**（含嵌套）里的 `EX_Let`：

```
## 表达式树里的 Let
共 23 条（含嵌套）。
```

23 条 `EX_Let` + 2 条 `EX_LetObj` = **25 条赋值，和 `--trace` 记录的 25 条完全对上**，
其中没有一条写入 `CallFunc_BreakVector2D_*`。

**结论（证据充分，不再猜）**

| 检查 | 结果 |
|---|---|
| 块覆盖 | 28 条指令走了 27 条（剩 1 条 `EndOfScript`） |
| 属性表 | 两个名字**确实声明为局部变量** |
| 顶层 Let | 25 条，无一对准它们 |
| 表达式树嵌套 Let | 23 条，同样无一对准它们 |

**按 UAssetAPI 解析出来的字节码，这两个局部变量「只读不写」，而写入者在哪都不存在。**
这是解析层面的缺口，不是走查逻辑的 bug——继续在走查里找只会白费力气。

**影响**：每个 `BreakVector2D` 多两个 `Local Double` 占位节点，纯观感。

**回归**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
```

**权衡（记在这里，避免下轮重复调查）**

要绕过它只有两条路，都要付代价：

1. **换解析器**（CUE4Parse）——整个反编译器要重写，风险极高，收益只有几个占位节点。
2. **显示层合成**：名字形如 `<X>_X` / `<X>_Y` 且同族有 `<X>_ReturnValue` 时，画成一个
   `Break <Struct>` 节点。属于**猜**，违反了「宁可少折，不能折错」。

两条我都不做。这几个节点留着，比引入不确定性更好。

### 重写里程碑 30：发现 For Each 折叠归零（全量诊断第六十七轮）

**这轮的核心不是「修好了什么」，是「发现了一个一直没人发现的退化」。**

**新增宏普查**（`--audit` 里新增一节）

```
=== BP_Logic ===
## 宏普查
- `While Loop` × 35

=== BP_CardFunctions ===
## 宏普查
- `While Loop` × 187
```

**`For Each Loop` 的折叠数是 0**——475 个函数里一个都没折出来。

而第 47 轮时 `CanIDoAnything` 明明有 `For Each Loop`（8 节点），现在是 17 节点、
一堆 `LENGTH` / `<` / `Local Unknown` 的散件。

**为什么一直没被发现**：`--validate` 通过（图是自洽的）、`--check-all` 通过（能渲染）、
`--check-ui` 通过（数量和 UI 一致）。**三层验证都不检查「节点组成对不对」**，
所以「30 个散件 vs 1 个宏」这种退化完全看不出来。宏普查就是补这个洞的。

**根因（已定位）**：第 10 轮我让「循环头直接发 `While Loop`」抢在了 `MacroCollapse` 前面。
For Each Loop 编译出来也是 while 形状（`LENGTH(arr) < index`），
Branch 被换成 While Loop 之后，`MacroCollapse` 需要的那三样（Branch + `<` + LENGTH + GET）
就永远凑不齐了。

**已做的修复（一半）**

`EmitJumpIfNot` 里加 `LooksLikeForEach(condition)`：条件形如 `LENGTH(...) < ...` 时
**不发 While Loop**，退回普通 Branch，交给宏折叠。

效果：`While Loop` 从 35/187 降到 **15/75**（说明那 20/112 个确实是 For-Each 形状的），
但 **`For Each Loop` 仍然是 0**——说明 `FindClusters` 后面还有一道关卡没过
（大概率是 `TraceToTemp` 要求索引来源的引脚名以 `Temp_` 开头，重构后引脚换了个实例）。

**回归检查**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
节点          3197 / 11247（比上轮 3195 / 11226 略增，Branch 比 While 多一个节点）
```

**下一轮**：把 `FindClusters` 每一道判定加 trace，逐条看 For Each 卡在哪一步。
这比继续猜快得多——`--blocks` / `--locals` / `--expr` 那套已经证明「加诊断」比「猜根因」划算。

### 重写里程碑 31：For Each 卡在哪一步，查清了（全量诊断第六十八轮）

给 `FindClusters` 的每道判定都加了 trace（挂在 `GraphCompiler.Trace` 下，正常运行为 null、零开销）：

```
[for] 跳过：找不到同一个数组的 GET（LENGTH 数组 = 'Array'，现存 GET/Array_Get 的数组 = [null]）
[for] 跳过：条件不是 '<'（实际 lastCanDoAnythingCheck）
```

**第一条就是卡点**：`LENGTH` 的数组来源叫 `Array`，而 GET 那边是 **null**。

再看 `CanIDoAnything` 的节点列表，真相是——**根本没有 GET 节点**：

```
标题「Make Array」            引脚[Array:Array<Unknown>]
标题「GetAllActorsOfClass」   引脚[:Exec / self / ActorClass / OutActors:Unknown]
标题「LENGTH」                引脚[Target / TargetArray:Array<Unknown> / ReturnValue:Int]
标题「Local Unknown」         引脚[CallFunc_Array_Get_Item:Unknown]      ← 这是占位，不是调用节点
```

`Array_Get` 的**调用节点没被建出来**，只有一个 `Local Unknown` 占位节点（引脚名
`CallFunc_Array_Get_Item`）。所以 `MacroCollapse` 找不到 GET 不是它的判定写错了，
是**上游根本没给它 GET**。

**这意味着**：For Each 折叠要恢复，得先修「`Array_Get` 调用为什么没建成节点」——
而 `CanIDoAnything` 里的 `Local Unknown` 和之前 `BreakVector2D` 是**同一类现象**：
都出现了「只读不写的编译器临时变量占位」。

**这一轮没有修好 For Each**，但把「卡在哪」从猜测变成了两条可复现的 trace 与一组节点证据。

**回归**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
```

**下一轮**：查 `Array_Get` 那条 Let 为什么没走到 `CallNode`——用 `--trace` 的 `[let]` / `[miss]`
配合 `--expr` 看它的表达式形态。这和 `BreakVector2D` 可能是同一个根因，先证实或证伪这一点。

### 重写里程碑 32：For Each 的根因找到，但修复失败并已回退（全量诊断第六十九轮）

**根因（这轮用 `--expr` 证实的）**

`CanIDoAnything` @234 的完整结构：

```
EX_Context
  RValuePointer → []
  EX_FinalFunction(Array_Get)
    params: [OutActors, Index, CallFunc_Array_Get_Item]   ← 第三个是 out 参数
```

**UE 把 out 参数当实参传进调用，写入发生在调用内部，根本没有 `Let`。**
我们把第三个实参当成「读」，于是 miss → 占位节点 → `Array_Get` 调用节点因为「纯节点没人消费」
被当死节点删掉 → `MacroCollapse` 找不到 GET → `For Each Loop` 折叠数 0。

**尝试修复：三次，全部失败**

| 尝试 | 结果 |
|---|---|
| 按索引登记 out 实参 | `GET` 节点出现了，但引脚错位（`TargetArray` 没接上） |
| 按名字（`_Item` 后缀）认 out 实参 | `GET` 又被当死节点删掉 |
| 「有返回值时只认非容器 out」 | Cards 类型不兼容 **0 → 13**，节点 **11247 → 10361**（少 900） |

**已全部回退**。回退后与改动前完全一致：

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
节点          3197 / 11247（与上一轮完全相同）
```

**为什么回退而不是继续修**：`out` 参数在 jmap 里既可能是真输出、也可能是 const-ref 输入，
两者的**字节码形态几乎一样**（都可能是编译器临时变量）。判据不可靠时硬修，
代价是 Cards 的 13 处类型错乱 + 900 个节点消失——比「For Each 折不出来」严重得多。

**保留的产出**

| 工具 | 用途 |
|---|---|
| `[for] 跳过：…` trace | `FindClusters` 每道判定的失败原因，一眼看出卡在哪 |
| `--audit` 的宏普查 | `For Each Loop` / `While Loop` 各自折出多少，退化立刻可见 |
| `--blocks` / `--locals` / `--expr` | 上一轮加的，定位指令 / 属性表 / 表达式树 |

**当前状态（明确记录）**：`For Each Loop` 折叠数为 **0**，`While Loop` 15 / 75。
这是已知缺陷，不是回归——但它需要**可靠的 out 参数判据**才能修，
而那需要比 jmap 更权威的签名来源（比如读 `FUNC_HasOutParms` + 参数标志位），是下一阶段的事。

### 重写里程碑 33：第三种判据也失败，基线已还原（全量诊断第七十轮）

**这轮的实验**：用「实参临时变量此前有没有被生产过」来区分真 out 参数与 const-ref 输入。

| 场景 | 预期判定 |
|---|---|
| `Array_Get(arr, idx, CallFunc_Array_Get_Item)` | `_locals` 里没有 → **写入目标**，真 out |
| `Array_Length(CallFunc_GetAllActorsOfClass_OutActors)` | 更早的调用已登记 → 在读值，输入 |
| `TextIsEmpty(InText)` | 函数参数，早已登记 → 输入 |

**结果：校验通过，但目标没达成。**

```
--validate   两个资产：八条规则全部通过   ← 这次没有类型错乱（比上一种判据好）
--audit      节点 3197→3136 / 11247→10832（少 61 / 415）
For Each Loop：仍然是 0
```

`LENGTH.TargetArray` 确实从 `Unknown` 变成了 `Array<Unknown>`（数组接上了），
但 `GET` 的 `TargetArray` 依旧接不上，`For Each Loop` 折不出来。

**已回退**。回退后与基线**逐字节一致**：

```
节点 3197 / 11247　　边 3515 / 14064
--validate 8/8　--check-all 475 函数全清　check-ui 17/17、16/16
```

**为什么回退**：节点少了 475 个而我**无法验证这个减少是安全的**。改动没有达成它的目标
（For Each 仍是 0），却改变了 475 个函数的输出——这种「收益不明、影响面大」的改动不该留在主干上。
这是上一轮学到的：先验证再前进，验证不了就退。

**三种判据的完整记录（避免重复实验）**

| # | 判据 | 结果 |
|---|---|---|
| ① | 实参是不是编译器临时变量 | Cards 类型不兼容 0→13、节点少 900 |
| ② | 按 `_Item` 后缀名字匹配 | GET 被当死节点删掉 |
| ③ | 实参此前有没有被生产过 | 校验通过，但节点少 475、For Each 仍 0 |

**结论**：jmap 的 `dir` 字段不足以区分「真 out 参数」和「const-ref 输入」，
需要 `FUNC_HasOutParms` + 每个参数自己的标志位。**jmap 里没有这个信息**，
所以这条路在当前数据源下走不通——除非引入新的签名来源。

**当前状态**：`For Each Loop` 折叠数 0、`While Loop` 15/75，属已知缺陷，非回归。

### 重写里程碑 34：用数字关掉「只对蓝图函数启用」这条路（全量诊断第七十一轮）

**新诊断：`--outstat <资产>`** —— 带 out 参数的调用里，原生函数和蓝图函数各占多少。

```
BP_Logic          扫描 181 函数：native 342 / blueprint 132 / no-out 911
BP_CardFunctions  扫描 294 函数：native 1509 / blueprint 758 / no-out 1995
```

| 资产 | native | blueprint | 比例 |
|---|---|---|---|
| BP_Logic | 342 | 132 | 2.6 : 1 |
| Cards | 1509 | 758 | **2.0 : 1** |

**这条数字直接否掉了上一轮的计划**：我原本想「只对蓝图函数启用精确 out 判据」
（蓝图函数的签名方向来自真实的 `CPF_OutParm`，是准的）。但原生占 2/3，
而 `For Each Loop` 依赖的 `Array_Get` / `Array_Length` **全是原生**。
圈住蓝图函数等于绕开了问题本身。

**顺带试了第四条路：容器访问器白名单**

只对 `Array_Get` / `Map_Find` / `Map_FindChecked` / `ArrayGetByRef`
把最后一个非返回的 out 参数当写入目标。校验通过、节点只差 1 / 29 个，
但 `[miss] 'CallFunc_Array_Get_Item'` 依旧、`For Each Loop` 仍 0 —— 因为上一轮回退时
把「登记 out 引脚」的代码一并删了，只剩「跳过」。**已回退。**

**基线（逐字节还原）**

```
节点 3197 / 11247　　边 3515 / 14064
--validate 8/8　--check-all 全清　check-ui 17/17、16/16
```

**四条路的完整记录**

| # | 判据 | 结果 |
|---|---|---|
| ① | 实参是不是编译器临时变量 | Cards 类型不兼容 0→13、节点少 900 |
| ② | 实参此前有没有被生产过 | 校验通过，节点少 475，For Each 仍 0 |
| ③ | 只对蓝图函数启用 | **数字否掉**（原生占 2/3，`Array_Get` 就是原生） |
| ④ | 容器访问器白名单 | 校验通过，但登记代码缺失，For Each 仍 0 |

**结论**：`For Each Loop` 在当前数据源（jmap 的 `dir`）下修不了。
要修必须换签名来源（能拿到每参数的 `CPF_ConstParm` 标志），那是换数据层的事。
这条记录在此，避免第 N 次重试。

### 重写里程碑 35：argN 从 41 降到 1（全量诊断第七十二轮）

离开 For Each 之后，回到能验证收益的事：`argN` 引脚。

**诊断先指出凶手**（`--argn`）

```
BP_CardFunctions：42 个，全部来自「Clamp」
BP_Logic：2 个 → 「GetPlayerController」1 个、「Round」1 个
```

三个函数在 **jmap 里根本查不到**（`Clamp` 直接 grep 不到），所以参数只能叫 `arg0/arg1/arg2`。

**修法：补一张有界的小表**

`ResolveSignature` 在前三级（导出 / 本资产函数 / jmap+索引）都失败后，查
`KnownSignatures`——只收录**实测出现过、名字确定**的引擎 API：

| 函数 | 签名 |
|---|---|
| `UKismetMathLibrary::Clamp` | `Value, Min, Max → ReturnValue` |
| `UKismetMathLibrary::Round` | `A → ReturnValue` |
| `UGameplayStatics::GetPlayerController` | `WorldContextObject, PlayerIndex → ReturnValue` |

补的是**事实**，不是猜：这些都是引擎里名字固定的函数。

**效果**

```
标题「Clamp」　引脚[Value:Int / Min:Int / Max:Int / ReturnValue:Int]
```

| 指标 | 修前 | 修后 |
|---|---|---|
| argN BP_Logic | 2 | **1**（`GetPlayerController` 被更早的签名源覆盖了） |
| argN Cards | **42** | **0** |
| 节点 BP_Logic / Cards | 3197 / 11247 | 3197 / 11247（不变） |
| 类型未知 | 10.6% / 11.9% | 10.6% / 11.9%（不变） |

**三层验证**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17、AddCardStats 16/16
```

**代价说明**：这张表是**人工知识**，不是从数据里推出来的。所以只收录了三个函数，
以后出现新的 `argN` 时按同样方式补——`--argn` 会直接点名是谁。

### 重写里程碑 36：控制流对账，475 个函数零截断（全量诊断第七十三轮）

**新检查（`--audit` 里新增一节）**：字节码里的分支点数 vs 图上的分支/宏节点数。

```
BP_Logic          分支点 339　分支/宏节点 375　少掉 0
BP_CardFunctions  分支点 973　分支/宏节点 1418　少掉 0
```

**「少掉 0」这个结果本身就是证据**：每个 `JumpIfNot` 都至少产出了一个 `Branch` 或
`MacroInstance` 节点，说明**没有任何一个函数的分支在遍历中被丢掉**。

**为什么节点数会多于分支点数**：ubergraph 有多个事件入口，共享块会被多次走进来
（同一段字节码产生多个节点）。所以这一项**不能**用来判断截断——已在代码里显式排除
`ExecuteUbergraph_*`，并写进注释。

**这个检查补了一类此前完全没有覆盖的缺口**：
`--validate` 检查「图自洽」、`--check-all` 检查「能渲染」、`check-ui` 检查「UI 与无头一致」，
但**没有任何一项检查「图的内容是否完整」**。For Each 折叠归零就是从这个缝里漏过去的
（那次是靠人工发现宏普查为 0）。

现在的检查矩阵：

| 检查 | 覆盖 |
|---|---|
| `--validate`（8 条规则） | 图**自洽**：入口脚、纯节点 exec、悬空边、执行链、零引脚、截断、多上游、类型兼容 |
| `--check-all` | 图**可渲染**：475 函数空图/失败/数量不一致/布局回退全为 0 |
| `tools/check-ui.ps1` | UI 与无头**一致** |
| **对账（本轮）** | 控制流**完整**：分支点一个不少 |
| 宏普查（上轮） | 宏折叠**没退化** |

**三层回归**

```
--validate   两个资产：八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
check-ui     CanIDoAnything 17/17
```

### 重写里程碑 37：更名 Solstice-winter + 首发 GitHub（全量诊断第七十四轮）

**更名**（按要求更名 `Solstice-winter`）

| 层级 | 旧 | 新 |
|---|---|---|
| 命名空间 / 项目名 | `BpStudio.*` | `SolsticeWinter.*` |
| 解决方案 | `bp-studio.slnx` | `Solstice-winter.slnx` |
| 设置文件 | `bpstudio.settings.json` | `solstice-winter.settings.json` |
| 仓库目录 | `bp-studio` | `Solstice-winter` |

替换规则（85 个源文件、503 处）：

```
BpStudio   -> SolsticeWinter      （命名空间 / 类型前缀）
bp-studio  -> Solstice-winter     （路径 / 文件名）
bpstudio   -> solstice-winter     （配置 / 临时目录）
BPSTUDIO_  -> SOLSTICE_WINTER_    （环境变量）
```

**验证**：更名后重新构建并跑全套检查，**功能零变化**：

```
--validate   八条规则全部通过
--check-all  475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
UI           CanIDoAnything 17 节点 / 22 连线，正常渲染
```

**上传 GitHub**（`CCB-TEAM/Solstice-winter`，public）

| 决定 | 理由 |
|---|---|
| 排除 `external/` | 1.2 GB 第三方参考源码（UEBlueprintGraphViewer，MIT），不是本项目代码 |
| 排除 `.nuget-local/` | 2.1 MB 离线包；**连带把 `NuGet.config` 补上 nuget.org 兜底**，否则 fresh clone 无法还原 |
| 排除 `.cache/` | 384 MB 运行期缓存，可重建 |

提交结果：**111 个文件 / 1.01 MB / 22633 行**。

**踩到的一个环境坑**：`C:/Users/ASUS/.gitconfig` 里有 URL 级代理
`http.https://github.com/.proxy = http://127.0.0.1:7890`，而该端口没在监听、`github.com:443` 其实是直连可达的。
推送用一次性覆盖解决，**没有改动全局 git 配置**：

```powershell
git -c "http.https://github.com/.proxy=" -c "https.https://github.com/.proxy=" push -u origin main
```

仓库地址：https://github.com/CCB-TEAM/Solstice-winter

### 重写里程碑 38：设置窗口 + 两条打开路径（全量诊断第七十五轮）

按 FModel 的设置形态重做配置入口，并把「打开」拆成两条数据源不同的路。

**1. 设置窗口 `Views/SettingsWindow`**

FModel 的设置是**带标签页的模态对话框**（General / Models / Keybindings），
点主窗口左上角的 *Settings* 进入，配置存 JSON。这里照这个形态做：

| 标签页 | 内容 |
|---|---|
| 通用 | 数据源七项：导出目录 / usmap / jmap / registry / pak 目录 / 游戏目录 / AES 来源（每项带「浏览…」） |
| 显示 | 标题风格（单选）、显示引脚类型、字号、新反编译器开关 |
| 布局 | 旧投影的列宽 / 行高 / 数据偏移（**标注了「仅旧投影生效」**）+ 配置文件路径 |

底部 **应用 / 确定 / 取消**。编辑的是 `StudioSettings.Clone()` 出来的**副本**，
取消即丢弃。原来的「视图」菜单里字号增减、标题美化/内部名、重新加载设置**已删除**——
配置只有设置窗口一个入口。

**2. 文件菜单拆两个打开入口**

```
从 VFS 打开… (Ctrl+O)       → 索引/导出目录，带函数签名与变量类型
从本地打开… (Ctrl+Shift+O)  → 直接选 .uasset，不走索引
```

`--check-local` 端到端验证：`BP_Logic.uasset` → **181 个函数**、`BP_CardFunctions.uasset` → **294 个**，
与 VFS 路径完全一致。

**3. 顺带清掉一个死配置**

`Display.ShowGrid` 在代码里**零引用**（网格由 `GraphView.xaml` 的 `Background` 决定），
设置窗口里也没放它——本轮从 `StudioSettings` 删除。留一个无效开关比少一个开关更误导。

**取舍**

| 决定 | 理由 | 代价 |
|---|---|---|
| 编辑副本而不是直接改 | 子对象是引用类型，浅拷贝会让「取消」变成「撤销一部分」 | 一次 JSON 往返 |
| `DataContext` 直接指向副本 | 省掉一堆包装属性 | 窗口只能做字段编辑，联动要另写 ViewModel |
| 数值框 `UpdateSourceTrigger=PropertyChanged` | `LostFocus` 会吞掉「点应用前最后编辑的那个数字」 | 输入中间态也会写进模型 |
| 「从本地打开」不查索引 | 资产不必在导出目录里 | 签名查不到 → `argN` / Unknown 类型 |

`AssetAccess.OpenWorkspace` 重构：把「字节 → 工作目录 → workspace」抽成 `OpenWorked`，
两条打开路径共用一份，避免工作目录候选/探针那套逻辑抄两遍。

**新增验证命令**

| 命令 | 作用 |
|---|---|
| `--check-settings` | 真的构造一遍设置窗口：XAML 解析、StaticResource、转换器实例化都在这里炸，构建通过不代表能加载 |
| `--check-local <路径>` | 走「从本地打开」的真实路径，打印函数个数 |

**回归**

```
--check-settings                      设置窗口构造成功（退出码 0）
--check-local BP_Logic.uasset          functions=181
--check-local BP_CardFunctions.uasset  functions=294
--validate 两个资产                     八条规则全部通过
--check-all 两个资产                    475 函数：空图 0 / 渲染失败 0 / 数量不一致 0 / 布局回退 0
UI --open                             17 节点 / 22 连线，零绑定错误
```

### 重写里程碑 39：纠正「VFS」——真正的 pak 源（全量诊断第七十六轮）

**用户指出我做偏了，核实后确认他是对的。**

三条事实（改前）：

| 问题 | 证据 |
|---|---|
| 「从 VFS 打开」读的其实是 FModel 导出目录 | `AssetAccess.ExportPathFor` → `profile.ExportDirectory`，索引走 `Directory.EnumerateFiles(root, "*.uasset")` |
| App 从来没用过 `PakArchive` | `App.csproj` 引了 Pak 项目，但 App 代码里 **0 处**引用 |
| 覆盖率的真实差距 | 导出目录 **2137** 个 uasset，pak 里 `kards/Content` 有 **12738** 个 → 只看见 **17%** |

设计文档 `docs/设计-VFS与上下文索引.md` 定义的 VFS 是
`PakSource（mmap + 路径表） | DirSource（散文件） | Overlay`——我只实现了 DirSource，
然后给它贴了「VFS」的标签。

**本轮落地（按用户选择：完整三层 + 本地打开复用索引）**

| 新增 | 位置 | 作用 |
|---|---|---|
| `IAssetSource` | Core | VFS 的「路径 → 字节」接口，上层只认它 |
| `DirAssetSource` | Core | 目录源（原有行为的正确实现） |
| `OverlayAssetSource` | Core | 叠加源，**先给的赢**；枚举取并集 |
| `ContentPakSource` | Pak | pak 源，`/Game/...` ↔ `../../../kards/Content/...` 双向映射 |
| `SymbolIndex.BuildFromSource` | Index | 从任意源建索引，逐资产解压→解析→覆盖，磁盘峰值 = 单资产 |
| `pak source <pak> [虚拟路径]` | CLI | 验证虚拟路径映射与字节 |

**验证（全部实跑）**

```
pak 总条目 33808，ContentPakSource 可见 12738 个资产

--check pak source /Game/Blueprints/Logic/BP_Logic
  uasset 107,140 字节  SHA256 79B357D3...5D5AAE
  导出文件             SHA256 79B357D3...5D5AAE   ← 逐字节一致

index build pak <pak> --limit 200
  200 个资产 / 3.7 秒（≈18 ms/个）/ 引用边 1486 / 无失败
```

**关键取舍**

| 决定 | 理由 |
|---|---|
| 字典在构造时建好 | `PakArchive.Files` 3.3 万条，每次读线性扫会让建索引退化成 4 亿次比较 |
| 复用 `one.uasset` 文件名 | 磁盘峰值是单资产而不是 6.8 GB 解压树 |
| `uexp` 缺失不算失败 | 纯结构体资产没有导出体；「没有」和「读失败」必须分开 |
| `PakException` → 返回 false 而不是抛 | 让 Overlay 能退到下一个源（pak 坏但导出目录有，仍能打开） |

**遗留（下一轮）**

- UI 还没接线：设置里没有「数据源」选项，资产列表与打开仍走导出目录。
- `SymbolIndex.BuildFromSource` 只验证到 limit=200；全量 12738 个需要后台跑一次（预估 4 分钟）。
- 「从本地打开」还没复用索引签名（用户已确认要做）。
