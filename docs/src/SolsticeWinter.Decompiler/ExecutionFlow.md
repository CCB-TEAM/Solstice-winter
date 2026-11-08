# ExecutionFlow — 基本块 / 跳转 / 序列

**职责一句话**：把线性字节码切成基本块，解析块间跳转与 `PushExecutionFlow` 序列，并给入口点建跳转。

**对外入口**：`DecompileFlow(script, offsets)`、`Blocks`、`EntryPoints`、`GetEntryPointJump(point)`。

**内部结构**：

```text
DecompileFlow
 ├─ FindBlocks      遇 JumpIfNot/Jump/PopExecutionFlow/PopExecutionFlowIfNot/EndOfScript/ComputedJump 断块
 ├─ FindSequences   连续 PushExecutionFlow + 落到顺序下一句 = 一个 Sequence
 ├─ FindBasicJumps  JumpIfNot → 两条跳转（落空 + 目标）；Jump → 一条
 └─ ResolveEntryPointJumps  入口偏移 → 跳转（指向首块）
```

**依赖**：`StatementMap`（偏移）、`UAssetAPI.Kismet.Bytecode.Expressions`。被 `FunctionWalker` 与 `BlockProbe` 使用。

**取舍**：

- **块边界按「控制流终结指令」切**，不按「语句数」：跳转目标只可能落在块首，切法必须保证这一点。
- **`Start`/`End` 存字节偏移而不是下标**：与 `StatementMap` 一致；`GetIndex(offset)` 在块内按偏移找下标。
- **序列识别沿用参考项目的做法**：连续 N 个 push + 一次顺序落点 = N+1 个出脚的 Sequence。否决了「按 `EX_PopExecutionFlow` 反推」，那种做法在共享尾的情况下会重复计数。
- **延迟动作（latent）未特殊处理**：参考项目会把它当 `LatentAction` 块并接 `SkipOffsetConst` 的偏移；本移植里它们按普通块处理，会在 `FunctionWalker` 里落成不透明节点。

---

### `DecompileFlow(IEnumerable<KismetExpression>, IReadOnlyDictionary<KismetExpression, uint>): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public`。一次性构建 `Blocks` 与入口跳转表；副作用是填充自身。抛 `InvalidOperationException`：表达式没有偏移映射、跳转目标不落在任何块内、目标落在块内但不在指令边界上。

**输入输出**：`BP_Logic` ubergraph（1536 指令 / 45675 字节，50 个入口）→ 410 块 / 348 跳转 / 42 序列。普通线性函数（`AddCardStats`，16 指令 / 791 字节）→ 1 块 / 0 跳转 / 0 序列。

**关键分支（伪代码）**：

```text
FindBlocks: 每加一条指令后看它是不是终结指令；是 → 收块、起新块
FindSequences: for i in 块内指令:
    count = 连续 PushExecutionFlow 的个数
    if count > 0: 收集 N 个 push 目标 + 第 i+count 条的偏移，建 Sequence，i += count-1
FindBasicJumps: JumpIfNot → [落空块, 目标块]；Jump → [目标块]；BranchEndIfNot → [落空块]
ResolveEntryPointJumps: for p in EntryPoints: 表[p] = 指向首块的跳转
```

**边界与失败**：

- ⛔ **要求跳转目标落在指令边界**：落在指令中间会抛（这种数据说明字节码被改坏或 `GetSize` 算错，不能猜）。
- ⛔ **不处理 `EX_Skip` / `EX_SwitchValue` 的内部相对偏移**：它们的目标不是顶层块边界。
- ⛔ 块内若以 `EX_Jump` 结尾但目标越界，`FindBlockJump` 抛异常，整次解析失败（调用方 `FunctionWalker` 会捕获并记问题）。

**调用者与被调用者**：被 `FunctionWalker.Walk`、`BlockProbe.Run` 调用；调用 `StatementMap.Build` 的结果。

### `GetEntryPointJump(uint): BlockJump`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public`。按入口偏移取跳转。未登记的入口抛 `InvalidOperationException`。

**输入输出**：`BP_Logic` 的 50 个入口偏移之一 → 指向首块的 `BlockJump`。

**关键分支（伪代码）**：查表命中返回；未命中抛。

**边界与失败**：⛔ 只有 `EntryPoints` 里登记过的偏移能查；ubergraph 的入口来自 `KismetWorkspace.UbergraphCallSites`。

**调用者与被调用者**：被未来的走查器用于从事件入口起走；调用字典查表。
