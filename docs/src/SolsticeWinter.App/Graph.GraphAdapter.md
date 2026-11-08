# Graph.GraphAdapter — 新反编译器的图 → 画布 VM

**职责一句话**：把移植来的 `BPNode` 图（UE 式节点 + 引脚 + 连线）转成 Nodify 用的 `GraphNodeVm` / `ConnectionVm`，并做一次简单分层布局。

**对外入口**：`GraphAdapter.ToView(IReadOnlyList<BPNode>)` → `(List<GraphNodeVm>, List<ConnectionVm>)`。

**内部结构**：

```text
ToView
 ├─ 每个 BPNode → GraphNodeVm（Title / Kind / Tooltip / IsOpaque）
 │    每个 GraphPin → ConnectorVm（Title / IsExec / TypeName / Brush）
 ├─ 连线：输出脚.LinkedTo → 输入脚
 └─ Layout：exec 边算列号（BFS），同列纵向堆叠
```

**依赖**：`SolsticeWinter.Decompiler`（BPNode/GraphPin）、`SolsticeWinter.App.ViewModels`（GraphNodeVm 等）、`System.Windows`（Point/Media）。被 `MainViewModel.TryRenderWithNewDecompiler` 调用。

**取舍**：

- **唯一知道两边模型的地方**：左边是 UE 式节点，右边是 Nodify 的 VM。换画布库或换布局引擎只动这里。
- **布局先用简单分层**：Msagl（参考项目用的）还没接，现在是「按 exec 深度分列 + 同列纵向堆叠」。代价：大图交叉严重，但链式结构可读。
- **类型显示用 `GraphPinType.Display()`**：容器类型渲染成 `Array<T>` / `Set<T>` / `Map<T>`，对象/结构体取类名末段。

---

### `GraphAdapter.ToView(IReadOnlyList<BPNode>): (List<GraphNodeVm>, List<ConnectionVm>)`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。纯函数：只读输入、返回新集合。无副作用。

**输入输出**：`FunctionWalker.Walk` 产出的 125 个节点 → 125 个 `GraphNodeVm` + 85 条 `ConnectionVm`（实测 `BP_Logic / startGameReplay`）。

**关键分支（伪代码）**：

```text
for node in nodes:
    建 GraphNodeVm（Kind 由节点类型决定）
    for pin in node.Input:  ConnectorVm(IsInput=true, IsExec, TypeName, Brush)
    for pin in node.Output: ConnectorVm(IsInput=false, ...)
for node in nodes:
    for out in node.Output:
        for in in out.LinkedTo: ConnectionVm(IsExec = out 是 exec)
Layout(nodes)
```

**边界与失败**：

- ⛔ **同一条边可能重复**：参考模型允许一个输出脚连多个输入脚（分支汇合），这里如实展开，不去重。
- ⛔ **不合并同名引脚**：`arg0..argN` 这种占位名会原样显示。
- ⛔ 布局不看数据边，只看 exec 边：纯节点会跟它的消费者同列（或落在第 0 列），可能叠在别的节点上。

**调用者与被调用者**：被 `MainViewModel.TryRenderWithNewDecompiler` 调用；调用 `KindOf`、`BrushOf`、`Layout`。

### `GraphAdapter.Layout(IReadOnlyList<BPNode>, IReadOnlyDictionary<BPNode, GraphNodeVm>): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private static`。给每个 VM 写 `Location`；副作用是改 VM。无返回值、不抛（找不到深度就按 0 列）。

**输入输出**：无 exec 入脚的节点当根（深度 0）；沿 exec 边 BFS 递推；同列按出现顺序堆叠，行高用 `node.NodeHeight + 40`。

**关键分支（伪代码）**：

```text
roots = 没有已连接 exec 入脚的节点
if roots 为空 and 有节点: 取第一个当根
BFS: for out in node.Output: for linked: depth[target] = min(depth, current+1)
按节点顺序: y = 该列已用高度; vm.Location = (列 × 320, y)
```

**边界与失败**：

- ⛔ **有环时靠 `depth` 只降不升**：循环回边不会让 BFS 无限循环（目标深度已经更小就跳过），但也意味着环上的节点可能挤在同一列。
- ⛔ 列宽固定 320，不看节点实际宽度：宽节点会重叠。

**调用者与被调用者**：被 `ToView` 调用；不调用其它函数。
