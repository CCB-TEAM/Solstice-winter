# Graph.GraphBuilder — `KNode` 树 → 平铺 exec 图 + 数据流图 + 自动布局

**职责一句话**：把 `Solstice.Kismet` 反编译出的**嵌套 AST**（`IfNode{Then, Else}`）投影成 UE 编辑器那种**平铺图**（Branch 有两个 exec 出脚），并在没有位置信息的前提下算出一套坐标。

**对外入口**：唯一静态入口 `GraphBuilder.Build(roots)`；其余成员都是实例方法/私有辅助（`BuildChain` / `BuildArg` / `CreateNode` / `CreateCallNode` / `Describe` / `ArgTypeName` / `BrushOf` / `Connect` / `ExecPin` / `DataPin` / `AddExecPins`）。

> 本模块的 md 按职责拆成三份：本文件（总入口 + exec 链布局）、[Graph.GraphBuilder.nodes](Graph.GraphBuilder.nodes.md)（造节点 / 造引脚 / 连线）、[Graph.GraphBuilder.mapping](Graph.GraphBuilder.mapping.md)（三张映射表）。

**内部结构**：

```text
Build(roots)
 └─ BuildChain(roots, x=0, y=0, dangling=[])          一条 exec 链，从左到右
     for node in nodes:
         vm = CreateNode(node, cursorX, y)           节点 + 引脚（Branch/While/DoWhile 都有 exec 入脚）
         把上游悬空 exec 脚接到本节点 exec 入脚        （本节点没有 exec 入脚时，上游悬空脚被丢弃）
         switch node:
             IfNode          → BuildArg(cond) + 两条子链（y∓RowHeight/2）→ pending = 两个末端
             LoopNode        → BuildArg(cond) + 子链（body）→ 回边接回「循环节点自己的 exec 入脚」→ pending = [done]
             BottomLoopNode  → 同上，子链 = Body + Tail
             Assign*         → BuildArg(value)
             ReturnNode      → BuildArg(value)
         没有悬空脚时，pending = 本节点的 exec 出脚
         cursorX += ColumnWidth

BuildArg(arg, pin, x, y)
 └─ arg 是 CallArg → CreateCallNode(..., y + DataOffsetY) + 递归 BuildArg(它的每个参数)
    否则 → 什么都不做（字面量/变量只靠引脚类型名表达）
```

常量（布局的三个旋钮，全部写死在 `GraphBuilder` 顶部）：

| 常量 | 值 | 含义 |
|---|---|---|
| `ColumnWidth` | `300` | exec 链每步的横向间距，也是分支子链的缩进 |
| `RowHeight` | `260` | 分支 then/else 两条子链相对父节点的纵向偏移（`±RowHeight/2` = `±130`） |
| `DataOffsetY` | `-170` | 数据节点相对使用者的纵向偏移（负 = 在上方） |

**依赖**：`Solstice.Kismet` 的节点/参数模型（`KNode` 家族、`KArg` 家族、`KismetDecompiler.RawNode`）、`SolsticeWinter.App.ViewModels`（`GraphNodeVm` / `ConnectorVm` / `ConnectionVm`）、`System.Windows.Point` 与 `System.Windows.Media.Brush`（WPF 类型）。被 [MainViewModel](MainViewModel.md) 的 `LoadGraphAsync` 与 [GraphProbe](Graph.GraphProbe.md) 的 `Run` 调用。

**取舍**：

- **只做「读」的投影**：设计文档 §2.2 的反向路径（平铺图 → 线性化 → 字节码）完全没实现。否决了「双向投影」——先把读的这半边跑通，编辑时再决定要不要换真相。代价：所有节点都是只读展示，`NodeCanvas` 里的拖拽只改位置。
- **位置算出来而不是存下来**：cooked 资产里没有布局（设计文档 §0 事实 1），所以位置只能是「链式布局」的产物。否决了「先做位置侧车再渲染」——没有初始布局就没有可存的基线。代价：每次打开同一函数，坐标都一样但与原作者那张图无关；手动位置还没有落盘通道。
- **数据节点一律放在使用者上方固定偏移处**：不做避让、不做层次排布。代价：**同一节点的多个嵌套调用参数会完全重叠**（见 `BuildArg` 边界）。
- **`Describe` / `ArgTypeName` / `BrushOf` 用 `switch` 表达式而不是反射或字典**：节点类型集合是封闭的（`KNode` 家族由 `solstice-kismet` 固定），编译期能查漏。代价：`solstice-kismet` 新增节点类型时，这里会**静默**落到 `default` 分支（标题 = 类型名去掉 `Node`、颜色 = 灰、参数类型 = `unknown`），不会有编译错误提醒。
- **颜色写死在 `BrushOf` 里**：与 `Themes/Dark.xaml` 的 `Pin.*` 资源重复（同一组 RGB 抄了两遍）。否决了「VM 里查资源字典」——VM 不该依赖 `Application.Current.Resources`，而且 `GraphProbe` 在无窗口环境跑，取不到资源字典会炸。代价：改配色要改两处，现在只有 `BrushOf` 那一处真正生效。

---

### `GraphBuilder.Build(IReadOnlyList<KNode> roots): (IReadOnlyList<GraphNodeVm> Nodes, IReadOnlyList<ConnectionVm> Connections)`

**状态**：✅ 已实现

**签名与契约**：`public static`。新建一个 `GraphBuilder` 实例，跑一次 `BuildChain(roots, 0, 0, [])`，把内部累积的两张表原样返回。纯函数式用法（每次调用一个新实例），**不缓存、不抛**（除非 `roots` 里出现让 `Describe`/`CreateNode` 抛的畸形节点——实际不会，`default` 分支兜住了）。

**输入输出**：

- `roots`：顶层节点序列。普通函数是 `ws.Decompile(name)` 的返回值；ubergraph 是**某一段**的 `UberSectionNode.Nodes`（由调用方挑段）。
- `roots` 为空 → 返回两个空列表（画布空白，不是错误）。
- 返回的 `Nodes` 顺序 = **创建顺序**（深度优先：先父节点，再 then 子链，再 else 子链），不是坐标顺序。`GraphProbe` 打印的就是这个顺序。
- 返回的 `Connections` 顺序 = `Connect` 被调用的顺序（先 exec 主干，后分支/数据）。

**关键分支（伪代码）**：

```text
builder = new GraphBuilder()               # _nodes = [], _connections = [], _maxX = 0
builder.BuildChain(roots, 0, 0, [])        # 起点固定为原点，无上游悬空脚
return (builder._nodes, builder._connections)
```

**边界与失败**：

- ⛔ 没有任何去重：同一段 AST 被调两次就会得到两份节点（当前每个函数只调一次）。
- ⛔ 实例是有状态的（`_nodes` / `_connections` / `_maxX` 累积），所以 `Build` 每次新建实例；把同一个实例复用两次会把两张图混在一起。
- ⛔ `_maxX` 被赋值但**从未读取**（历史残留），不影响输出。
- 实测规模参考：`BP_Logic / DrawTopCardFromDeck` → 52 节点 / 46 连线；`BP_Logic / AddCardStats` → 16 节点 / 15 连线（见 INDEX 的「UI 首次运行实测」）。

**调用者与被调用者**：被 `MainViewModel.LoadGraphAsync`（在 `Task.Run` 内）与 `GraphProbe.Run` 调用；调用 `BuildChain`。

---

### `GraphBuilder.BuildChain(IReadOnlyList<KNode> nodes, double x, double y, List<ConnectorVm> dangling): List<ConnectorVm>`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`，递归。把一串节点沿 `y` 这一行从 `x` 开始逐个摆开（每步 `+ColumnWidth`），并把上游悬空的 exec 出脚接到每个节点的 exec 入脚。返回**这条链末端仍然悬空**的 exec 出脚（调用方用它接下一段或作为分支汇合点）。

**输入输出**：

- `nodes`：要摆的节点序列，可为空（空时原样返回 `dangling`）。
- `x` / `y`：这条链第一个节点的坐标；后续节点 `x += 300`。
- `dangling`：上游悬空 exec 出脚。多个 = 分支汇合点（then/else 都接到下一个节点）。
- 返回：非空 = 链尾还有没接出去的 exec 脚（分支/循环产生）；空 = 链在这里结束（`ReturnNode` 之后）。

**关键分支（伪代码）**：

```text
cursorX = x; pending = dangling
for node in nodes:
    vm = CreateNode(node, cursorX, y); _nodes.Add(vm)
    execIn = vm.Inputs.FirstOrDefault(p => p.IsExec)
    if execIn != null:                                   # 触发条件：本节点有 exec 入脚（只有不透明节点没有）
        for upstream in pending: Connect(upstream, execIn, isExec: true)
        pending = []                                     # 悬空脚已消费
    switch node:
        IfNode:                                          # Branch：两条子链，y 各偏 ±130
            BuildArg(Condition, cond 脚, cursorX, y)
            thenEnds = BuildChain(Then, cursorX+300, y-130, [true 脚])
            elseEnds = Else 非空 ? BuildChain(Else, cursorX+300, y+130, [false 脚]) : [false 脚]
            pending = thenEnds + elseEnds; cursorX += 300
        LoopNode:                                        # While：body 子链 + 回边
            BuildArg(Condition, cond 脚, cursorX, y)
            bodyEnds = BuildChain(Body, cursorX+300, y-130, [body 脚])
            for end in bodyEnds: Connect(end, loop 的 exec 入脚, isExec: true)   # 回边接回循环节点本身
            pending = [done 脚]; cursorX += 300
        BottomLoopNode:                                  # DoWhile：子链 = Body + Tail，其余同 Loop
            同上，bodyEnds = BuildChain(Body+Tail, cursorX+300, y-130, [body 脚])
        AssignNode / AssignBoolNode / PersistentAssignNode:
            BuildArg(Value, "value" 脚, cursorX, y)       # 不改 pending
        ReturnNode:
            if Value != null: BuildArg(Value, "value" 脚, cursorX, y)
    pending = pending.Count > 0 ? pending : [.. vm.Outputs.Where(p => p.IsExec)]   # 无悬空 → 本节点出脚
    cursorX += 300
return pending
```

**边界与失败**：

- ✅ **Branch / While / DoWhile 都有 exec 入脚**（`AddExecPins(vm, withOutput: false)` + `cond` 数据脚 + 两个 exec 出脚），上游 exec 线能接进来。
- ✅ **循环回边接回循环节点自己的 exec 入脚**（不是接在 `body` 出脚上）。空循环体时得到「body 出脚 → 循环入脚」的一条回边，不再是 `body → body` 自环。
- ⛔ **不透明节点被跳过**：`RawNode` / `BlockGraphNode` / `BasicBlockNode` / `UberSectionNode` 的 `CreateNode` 提前返回（零引脚），既没有 exec 入脚也没有出脚——于是 `pending` 既不被消费也不被替换，下一个节点的 exec 入脚会接到**不透明节点之前**那个节点的出脚上。不透明节点在图上是一条孤立的方块，exec 链视觉上绕过它。
- ⛔ 递归深度 = AST 嵌套深度：`If` 里套 `If` 会一直递归（本机样本没有深到栈溢出的例子，但没有深度上限）。
- ⛔ `cursorX += ColumnWidth` 在 `case IfNode`/`LoopNode` 里**又加了一次**：分支节点占两列宽度（本身一列 + 子链一列），这是有意的；但它同时也把后续节点推到了子链右边，所以「链」不是等距的。
- 布局是算出来的：坐标只取决于 AST 结构与三个常量，**与原始蓝图无关**。值可能随迭代调整。

**调用者与被调用者**：被 `Build`（顶层）与自身（递归）调用；调用 `CreateNode`、`BuildArg`、`Connect`；读 `GraphNodeVm.Inputs` / `Outputs` 与 `ConnectorVm.IsExec`。

---

### `GraphBuilder.BuildArg(KArg? arg, ConnectorVm pin, double x, double y): void`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。把「一个参数值」渲染到「一个输入引脚」上：如果参数本身是一次调用，就递归造一个数据节点并连到该引脚；否则什么都不做（字面量/变量不造节点）。副作用：可能新增节点与连线；末尾对引脚写一次 `IsConnected = false`。

**输入输出**：

- `arg == null` → 直接返回（`ReturnNode.Value` 可为 null）。
- `arg is CallArg` → 在 `(x-40, y+DataOffsetY)` 造一个 `Kind="Data"` 的节点，把它的第一个非 exec 出脚（`"return"`）连到 `pin`；再对 `call.Args` 逐项递归，**按位置**接到数据节点的 `arg0..argN` 输入脚（`i < call.Args.Count && i < vm.Inputs.Count`）。
- 其余 `KArg`（字面量、变量、结构体…）→ 只执行 `pin.IsConnected = false`（本来就是 `false`，等于空操作）。字面量的值**不会**显示在引脚上。

**关键分支（伪代码）**：

```text
if arg == null: return                                  # 触发条件：Return 没有返回值
if arg is CallArg call:                                 # 触发条件：参数是一次嵌套调用
    vm = CreateCallNode(call, x-40, y+DataOffsetY, isExec: false); _nodes.Add(vm)
    output = vm.Outputs.FirstOrDefault(p => !p.IsExec)   # 数据节点只有一个出脚 "return"
    if output != null: Connect(output, pin, isExec: false)
    for i in 0..min(call.Args.Count, vm.Inputs.Count)-1: # 按位置对齐，不按名字
        BuildArg(call.Args[i], vm.Inputs[i], x-40, y+DataOffsetY)   # 子节点与父节点同坐标
    return
pin.IsConnected = false                                 # 字面量/变量：什么都不画
```

**边界与失败**：

- ⛔ **同级嵌套调用完全重叠**：所有递归调用都传同一个 `(x-40, y+DataOffsetY)`，所以一个调用的多个参数如果是调用，它们会精确叠在同一坐标上，视觉上只能看到一个（后加的盖住先加的）。
- ⛔ 参数与引脚**按位置对齐**，不按名字：`call.Args[i]` → `arg{i}`。参数顺序变了（或 `CreateCallNode` 的引脚构造顺序变了）就会错位，且不会报错。
- ⛔ 字面量不可见：`Set X = 7` 的 `7` 在图上没有任何痕迹（设计文档 §3 的「常量内联」没做）。`pin.IsConnected = false` 是空操作，读代码容易误以为它在做「标记未连接」。
- ⛔ 只处理 `CallArg` 一种复合参数：`StructConstArg` / `ArrayConstArg` / `ContextMemberArg` 等都不会展开成节点（落进「什么都不画」分支）。

**调用者与被调用者**：被 `BuildChain`（`cond` / `value` 脚）与自身（嵌套参数）调用；调用 `CreateCallNode`、`Connect`。


---

## 附：段内连（按入口切片）的现状

cooked 的 ubergraph 是**段复用**的：段是连续块，事件可以从段中间进入，多个事件共享同一段尾。
实测 `BP_Logic`：段起始 `[10,83,648,1267,…]`，入口 `[107,721,726,731,938,1295,…]`，两者不相等。

处理方式见 [SectionSlicer](Graph.SectionSlicer.md)：按入口偏移切字节码再反编译（`baseOffset` 传入口偏移）。
实测一条：`startGameReplay` 入口 @20764、段起始 @20736（晚 28 字节）→ 切片反编译出 1 个顶层 `LoopDoWhile`，
展开后 39 节点；整段渲染 41 节点。数字对得上（顶层 vs 展开的差别）。

⛔ **已知边界**：切片起点落在结构化块中间时，结构还原的上下文与整段不同，可能漂移
（例如本该是 `If` 的分支被还原成循环的一部分）。彻底解决需要**每个节点带字节区间**（可行性评估 D13 的图 IR），
现在只能在切片失败（返回 null / 0 节点）时回退整段。
