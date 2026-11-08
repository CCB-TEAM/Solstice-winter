# Graph.MsaglLayout — Sugiyama 分层布局

**职责一句话**：用 Microsoft.Msagl 给 `BPNode` 图算位置，输出 WPF 坐标（Y 向下）。

**对外入口**：`MsaglLayout.Apply(nodes, sizes, assign)` → `Result(Applied, FallbackReason, Nodes, Edges)`。

**内部结构**：

```text
Apply
 ├─ 每个 BPNode → Msagl Node（矩形 = 估算的 NodeWidth/NodeHeight）
 ├─ exec 边权重 2、数据边权重 1（数据边只把纯节点拉近，不强制成层）
 ├─ SugiyamaLayoutSettings{ LayerSeparation=120, NodeSeparation=60, Spline 路由 }
 ├─ LayoutHelpers.CalculateLayout
 └─ 坐标转换：Y 取反（Msagl 向上 / WPF 向下）+ 整体平移到 (60,60)
```

**依赖**：`Microsoft.Msagl` 1.1.3（MIT，nupkg 已 vendored 到 `.nuget-local/`）。被 `GraphAdapter.Layout` 调用。

**取舍**：

- **和参考项目 UEBGV 用同一套算法**：它也用 Msagl 的 `SugiyamaLayoutSettings`。手写分层在 100+ 节点时交叉严重（实测旧布局 125 节点几乎不可读）。
- **数据边也进图，但权重低**：完全不进图会让纯节点乱飘；权重相同又会让 exec 链被数据边拉歪。
- **失败不抛**：返回 `Applied=false` + 原因，调用方退回简单分列。布局是「显示效果」，不该让整个渲染挂掉。

---

### `MsaglLayout.Apply(IReadOnlyList<BPNode>, IReadOnlyDictionary<BPNode, Point>, Action<BPNode, Point>): Result`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。读节点与尺寸，通过 `assign` 回写位置。不抛（内部 catch 后返回 `Applied=false`）。副作用：调用 `assign`。

**输入输出**：`BP_Logic / startGameReplay` 的 125 节点 / 113 边 → 成功，耗时约 0.5 s（含走查器）。空节点 → `Applied=false, "没有节点"`。

**关键分支（伪代码）**：

```text
if nodes 为空: return (false, "没有节点")
建 GeometryGraph：每个节点一个矩形 Node
对每条 exec/data 边：Edge(from, to, weight = exec ? 2 : 1)，自环跳过
SugiyamaLayoutSettings{ LayerSeparation=120, NodeSeparation=60, Spline }
LayoutHelpers.CalculateLayout(...)
每个节点：x = Center.X - w/2; y = -(Center.Y + h/2)
整体平移到最小角 (60,60)，逐个 assign
```

**边界与失败**：

- ⛔ **尺寸是估算值**（`BPNode.CalculateSize`），不是实际渲染尺寸：估小了节点会重叠。
- ⛔ **有环时 Msagl 会断开回边**：循环回边（目前不生成）不会让布局失败，但也不会画出漂亮的环路。
- ⛔ 大图（>500 节点）耗时可能到秒级，没有增量布局。

**调用者与被调用者**：被 `GraphAdapter.Layout` 调用；调用 Msagl 的 `LayoutHelpers.CalculateLayout`。
