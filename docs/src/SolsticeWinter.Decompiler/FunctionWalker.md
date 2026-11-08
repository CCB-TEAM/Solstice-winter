# FunctionWalker — 字节码 → 节点图（核心子集）

**职责一句话**：按基本块走字节码，产出 UE 式节点（Branch / Sequence / Set / Call / Return / 不透明），并把 exec 与数据引脚接起来。

**对外入口**：`FunctionWalker.Walk(asset, function, entryPoints, startOffset?, maxNodes)` → `WalkResult(Nodes, Problems)`。

**内部结构**：

```text
Walk
 ├─ StatementMap.Build          表达式 → 字节偏移
 ├─ ExecutionFlow.DecompileFlow 切块 / 跳转 / 序列
 ├─ WalkBlock(首块, 起始下标, 虚拟 Entry 脚)
 │    ├─ 连续 PushExecutionFlow → Sequence 节点，逐个出脚递归
 │    ├─ JumpIfNot → Branch 节点，then/else 各递归一次
 │    ├─ Jump → 跟到目标块
 │    ├─ EndOfScript / ComputedJump → 停 / 不透明节点
 │    └─ 其它指令 → InstructionToNode → 接到当前 exec 链
 └─ CalculateSize 逐节点算尺寸
```

**依赖**：`StatementMap`、`ExecutionFlow`、`Model`（BPNode/GraphPin）、`Nodes`（6 个 K2 节点）、`UAssetAPI`。被 `MainViewModel.TryRenderWithNewDecompiler` 与 `BlockProbe.Walk` 调用。

**取舍**：

- **先做核心子集**（D22）：线性指令 + Branch + Sequence + Return + 调用/赋值。Switch/Cast/Spawn/宏/Timeline 落 `UnknownNode`，**不静默丢弃**。
- **循环按回边还原**：DFS 找回边（跳回栈上的块），把目标块记成循环头，发一个 `Loop` 节点（`LoopBody` / `Completed` 两个出脚）。UEBGV 走的是「宏实例」路线（18 个宏 JSON 模式匹配），这里先做字节码形态的还原：视觉一致，标题不带 ForEach。
- **执行流栈收尾不产生节点**：`EX_PopExecutionFlow` / `EX_PopExecutionFlowIfNot` 是 Sequence 与延迟动作的「回到调用点」，画布上不该出现（UEBGV 也不生成），直接跳过。实测这类曾占不透明节点的 38%。
- **入口偏移可指定**：ubergraph 的每个事件是不同入口，所以 `Walk` 接受 `startOffset`，从包含它的块开始走——这解决了「段复用」下的事件隔离。
- **`EX_Context` 只处理内层是 `EX_FinalFunction` 的情况**：其余落不透明节点。参考项目对 context 有一整套 `ParseContext`/`GetVarWithTarget`，这里没移植。
- **参数名先用 `arg{i}`**：jmap 签名补名是下一步（`GraphBuilder` 那边已经做过，可以搬过来）。

---

### `FunctionWalker.Walk(UAsset, FunctionExport, IReadOnlyList<uint>, uint?, int): WalkResult`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。纯函数：不改资产、不写盘。抛错不向外冒——内部捕获后放进 `Problems` 并返回已产出的节点。

**输入输出**：`BP_Logic` 的 `ExecuteUbergraph_BP_Logic`，入口 20764 → **125 节点**，问题 10 条（全部是「块被再次进入」，即循环回边）。`AddCardStats`（无分支）→ 一条直线链。函数没有字节码 → 空结果 + 1 条问题。

**关键分支（伪代码）**：

```text
if 没有字节码: return ([], ["函数没有字节码"])
offsets = StatementMap.Build(bytecode, asset)
flow.DecompileFlow(bytecode, offsets)
startBlock = startOffset? 包含它的块 : 首块
WalkBlock(startBlock, 起始下标, Entry 脚)
每个节点 CalculateSize()
return (nodes, problems)
```

**边界与失败**：

- ✅ **循环已还原**：回边 → `Loop` 节点（body 走顺序下一块、Completed 走 `JumpIfNot` 目标）。实测 `startGameReplay` 有 2 个 Loop 节点。
- ⛔ **非回边的重复进入仍记问题**：共享尾（多个事件汇入同一块）会记「块被再次进入（非回边）」，那是段复用的正常现象，不是错误。
- ✅ **`EX_Context` 三种内层都处理**：FinalFunction / 虚调用 → 调用节点；变量/结构体成员访问 → `VariableGet` + target 脚。
- ⛔ **`ComputedJump` 直接终止当前链**：ubergraph 的调度头就靠它，所以从函数起点走只能看到调度头；必须给 `startOffset` 才有意义。
- ⛔ **节点上限 4000**：超过就截断并记问题，防止病态函数把 UI 拖死。

**调用者与被调用者**：被 `MainViewModel.TryRenderWithNewDecompiler`（失败则回退旧投影）与 `BlockProbe.Walk` 调用；调用 `StatementMap`、`ExecutionFlow`、`InstructionToNode`、`ArgToPin`。

### `FunctionWalker.InstructionToNode(KismetExpression): BPNode?`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。把一条顶层指令变成节点；返回 null 表示这条指令不产生节点。无副作用（嵌套调用会**就地**往 `_nodes` 加纯节点）。

**输入输出**：`EX_Let` → `SET x`；`EX_LetBool/LetObj/…` → `SET x`；`EX_Return` → `Return Node`；`EX_CallMath` / `EX_LocalFinalFunction` / `EX_FinalFunction` → 调用节点（`CallMath` 视为纯）；`EX_LocalVirtualFunction` / `EX_VirtualFunction` → 按名字的调用；`EX_Context` → 带 self 脚的调用；其它 → `UnknownNode`。

**关键分支（伪代码）**：

```text
switch 指令:
  EX_Let            → MakeSet(Variable, Expression)
  EX_LetBase        → MakeSetFromBase(VariableExpression, AssignmentExpression)
  EX_Return         → MakeReturn(ReturnExpression)
  EX_CallMath       → MakeCall(isPure: true)      # 必须排在 FinalFunction 之前
  EX_LocalFinalFunction → MakeCall(isPure: false)  # 必须排在 FinalFunction 之前
  EX_FinalFunction  → MakeCall(isPure: false)
  EX_LocalVirtualFunction → MakeCallByName        # 必须排在 VirtualFunction 之前
  EX_VirtualFunction → MakeCallByName
  EX_Context        → MakeContext
  其它              → UnknownNode
```

**边界与失败**：

- ⛔ **继承顺序敏感**：UAssetAPI 里 `EX_LocalFinalFunction : EX_FinalFunction`、`EX_CallMath : EX_FinalFunction`、`EX_LocalVirtualFunction : EX_VirtualFunction`，派生类必须写在基类之前，否则编译期报「模式不可访问」。
- ⛔ 不认识的一律 `UnknownNode`，不返回 null 丢掉（`EX_Nothing` 这类无意义指令除外）。

**调用者与被调用者**：被 `WalkBlock` 调用；调用 `MakeSet` / `MakeSetFromBase` / `MakeReturn` / `MakeCall` / `MakeCallByName` / `MakeContext`。

### `FunctionWalker.ArgToPin(KismetExpression?, string): GraphPin`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。把表达式变成引脚；嵌套调用会生成纯节点并接好线。无副作用之外的行为：会往 `_nodes` 加节点。

**输入输出**：字面量 → 带 `Value` 文本的引脚（`Int` / `Bool` / `String` / `Name` / `Object`）；变量 → `Value` 为变量短名的引脚；`EX_FinalFunction`/`EX_CallMath` → 生成纯调用节点，输出脚接到返回的输入脚上；其它 → `Unknown` 引脚。

**关键分支（伪代码）**：

```text
switch 表达式:
  字面量            → Pin(type, 字面量文本)
  变量              → Pin(Unknown, PointerName(expr))
  FinalFunction/CallMath → NestedCall：建纯节点 → 输出脚 → 新建输入脚 → 返回输入脚
  Context           → NestedContext：同上
  其它              → Pin(Unknown, "")
```

**边界与失败**：

- ⛔ 类型只从表达式**形态**推，不查 jmap/usmap：`Array_Get` 这种输出类型要靠签名才知道，现在一律 `Unknown`。
- ⛔ 嵌套调用的消费者脚是新造的（`name` 或函数名），不是被调函数真正的参数脚名。

**调用者与被调用者**：被 `InstructionToNode` 的各分支调用；调用 `NestedCall` / `NestedContext` / `ResolveFunction` / `ResolveObject`。

