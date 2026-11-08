# Graph.GraphBuilder.nodes — 节点与引脚构造

> 本文件是 [Graph.GraphBuilder](Graph.GraphBuilder.md) 的拆分（原 md 超过 400 行）：模块职责、布局常量、取舍见主文件；这里只有「造节点 / 造引脚 / 连线」这几节的五段。

调用顺序：`BuildChain` 对每个节点调 `CreateNode`，遇到嵌套调用调 `CreateCallNode`；两者都通过 `AddExecPins` / `ExecPin` / `DataPin` 造引脚，最后由 `Connect` 落成 `ConnectionVm`。

---

### `GraphBuilder.CreateCallNode(CallArg call, double x, double y, bool isExec): GraphNodeVm`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。为一个**被当作值使用**的调用造节点。引脚：`isExec` 为 `true` 时先加 exec 入脚、末尾加 exec 出脚；参数脚 `arg0..argN`（类型由 `ArgTypeName` 决定）；最后一个出脚恒为 `"return"`。不抛、不连任何线（连线由调用方做）。

**输入输出**：

- `call.Function` → `Title`；`call.Owner` → `Subtitle`（`null` 时为空串）；`call.Shape` + 函数名 → `Tooltip`（形如 `"Context  Foo"`）。
- `Kind`：`isExec ? "Call" : "Data"`。**当前唯一调用方 `BuildArg` 恒传 `false`**，所以 `isExec == true` 分支是死代码。
- 引脚：`Inputs` = [`exec`（仅 isExec）] + `arg0..arg{N-1}`；`Outputs` = [`exec`（仅 isExec）] + `return:value`。

**关键分支（伪代码）**：

```text
vm = GraphNodeVm{ Title=call.Function, Subtitle=call.Owner ?? "",
                  Kind = isExec ? "Call" : "Data", Location=(x,y),
                  Tooltip = $"{call.Shape}  {call.Function}" }
if isExec: vm.Inputs.Add(ExecPin("exec", true))          # 触发条件：当作 exec 语句用（当前没有调用方）
for i in 0..call.Args.Count-1: vm.Inputs.Add(DataPin($"arg{i}", ArgTypeName(call.Args[i]), true))
if isExec: vm.Outputs.Add(ExecPin("exec", false))
vm.Outputs.Add(DataPin("return", "value", false))
return vm
```

**边界与失败**：

- ⛔ `isExec` 参数永远收到 `false`（死参数）：将来若要「把一个调用画成 exec 节点」直接传 `true` 即可，但没有调用方验证过这条路径。
- ⛔ 出脚类型恒为 `"value"`（灰色），即使 jmap 里有真实返回类型也不会被采用——`CallArg` 模型里没有返回类型字段，`GraphBuilder` 也没接 `FunctionCatalog`。
- ⛔ `Subtitle` / `Tooltip` 在 Nodify 视图里看不到（模板只绑 `Header` 与 `ToolTip`，`Subtitle` 完全没绑）。

**调用者与被调用者**：只被 `BuildArg` 调用；调用 `ExecPin`、`DataPin`、`ArgTypeName`。

---

### `GraphBuilder.CreateNode(KNode node, double x, double y): GraphNodeVm`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`private`。为一个**语句级**节点造 VM 并按节点类型加引脚。不连线、不抛。不透明节点（`KismetDecompiler.RawNode` / `BlockGraphNode` / `BasicBlockNode` / `UberSectionNode`）**提前返回、零引脚**。

**输入输出**：

- `Title` / `Kind` / `Tooltip` 来自 `Describe(node)`；`Location = (x, y)`；`IsOpaque` 按上面四类判定。
- 引脚按类型（见下表）；不在表里的类型走 `default`：只加 `exec` 入脚 + `exec` 出脚。

| 节点 | 输入脚 | 输出脚 |
|---|---|---|
| `CallNode` / `VirtualCallNode` / `LocalFinalCallNode` / `MathCallNode` / `MulticastCallNode` | `exec`, `arg0..` | `exec` |
| `ContextCallNode` | `exec`, `object`（标题 = `ObjectVariable`）, `arg0..` | `exec` |
| `IfNode` | `cond:bool` | `true`(exec), `false`(exec) |
| `LoopNode` / `BottomLoopNode` | `cond:bool` | `body`(exec), `done`(exec) |
| `AssignNode` / `PersistentAssignNode` | `exec`, `value` | `exec` |
| `AssignBoolNode` | `exec`, `value:bool` | `exec` |
| `SetArrayNode` | `exec`, `elem0..` | `exec` |
| `ReturnNode` | `exec`, `value` | （无出脚） |
| `ComputedJumpNode` | `exec` | （无出脚） |
| `PushFlowNode` / `PopFlowNode` / `PopFlowIfNotNode` | `exec` | `exec` |
| 其它（含 `default`） | `exec` | `exec` |
| 不透明四类 | （无） | （无） |

**关键分支（伪代码）**：

```text
(title, kind, tooltip) = Describe(node)
vm = GraphNodeVm{ Title=title, Kind=kind, Location=(x,y), Tooltip=tooltip,
                  IsOpaque = node is RawNode or BlockGraphNode or BasicBlockNode or UberSectionNode }
if vm.IsOpaque: return vm                       # 提前返回：零引脚（调用方会因此「跳过」它）
switch node:
    CallNode 家族  → AddExecPins + arg 脚
    ContextCallNode→ AddExecPins + object 脚 + arg 脚
    IfNode         → exec 入脚 + cond 脚 + true/false 两个 exec 出脚
    Loop/BottomLoop→ exec 入脚 + cond 脚 + body/done 两个 exec 出脚
    Assign*        → AddExecPins + value 脚
    SetArrayNode   → AddExecPins + elem{i} 脚
    ReturnNode     → 只有 exec 入脚 + value 脚
    ComputedJump   → 只有 exec 入脚
    Push/PopFlow*  → AddExecPins
    default        → AddExecPins
return vm
```

**边界与失败**：

- ✅ `IfNode` / `LoopNode` / `BottomLoopNode` 都有 exec 入脚（`AddExecPins(vm, withOutput: false)`），上游 exec 线能接进来；`Branch` 没有单一 exec 出脚（走 `true`/`false` 两个），`While/DoWhile` 走 `body`/`done` 两个。
- ⛔ `ComputedJumpNode` 的跳转目标（`Target`）不显示：只画成一个 `ComputedJump` 的孤立节点，看不到「跳到哪」。
- ⛔ `PushFlowNode` 的目标偏移（`Target`）同理不显示。
- ⛔ `ContextCallNode` 的 `object` 脚只显示变量名（`call.ObjectVariable`），不画它对应的对象来源节点。
- ⛔ 不透明节点是「信息黑洞」：既没有引脚也没有展开入口（设计文档 §3 的「双击展开原始字节」没做），图里只能看到它的标题。
- 引脚命名（`arg0` / `cond` / `value` / `elem0` / `true` / `false` / `body` / `done`）是 `BuildChain` 与 `BuildArg` 的**隐式契约**：两边靠字符串 `Title` 对齐，改名会静默断链（`FirstOrDefault` 返回 null → 那一步什么都不做）。值可能随迭代调整。

**调用者与被调用者**：只被 `BuildChain` 调用；调用 `Describe`、`AddExecPins`、`DataPin`、`ExecPin`、`ArgTypeName`。

---

### `GraphBuilder.AddExecPins(GraphNodeVm vm, bool withOutput = true): void`

**状态**：✅ 已实现

**签名与契约**：`private static`。给节点补 exec 入脚（恒加）与 exec 出脚（`withOutput` 为 `true` 时加）。无返回值、不抛、无副作用之外的行为。

**输入输出**：`vm.Inputs` 追加 `ExecPin("exec", true)`；`withOutput == true` 时 `vm.Outputs` 追加 `ExecPin("exec", false)`。`withOutput == false` 用于 `ReturnNode` 与 `ComputedJumpNode`（它们没有后继）。

**关键分支（伪代码）**：

```text
vm.Inputs.Add(ExecPin("exec", true))
if withOutput: vm.Outputs.Add(ExecPin("exec", false))    # 触发条件：节点还有后继可能
```

**边界与失败**：⛔ 不检查重复：对同一个 `vm` 调两次会得到两个同名 exec 入脚，`BuildChain` 的 `FirstOrDefault(p => p.IsExec)` 只会接第一个，第二个悬空。

**调用者与被调用者**：被 `CreateNode` 的各个 `case` 调用；调用 `ExecPin`。

---

### `GraphBuilder.ExecPin(string title, bool isInput): ConnectorVm`

**状态**：✅ 已实现

**签名与契约**：`private static`。造一个 exec 引脚：`IsExec = true`、`TypeName = "exec"`、`Brush = BrushOf("exec")`（白）。不抛。

**输入输出**：`title` 是显示名（`"exec"` / `"true"` / `"false"` / `"body"` / `"done"`）；`isInput` 决定 `IsInput`（当前只用于数据绑定与调试，画布不按它过滤）。`IsConnected` 初始 `false`，由 `ConnectionVm` 置真。

**关键分支（伪代码）**：

```text
return new ConnectorVm{ Title=title, IsInput=isInput, IsExec=true,
                        TypeName="exec", Brush=BrushOf("exec") }
```

**边界与失败**：⛔ `Title` 与 `IsExec` 是两条独立信息，`"true"`/`"false"`/`"body"` 这些 exec 脚的 `Title` 会被 `BuildChain` 用字符串匹配来找（`vm.Outputs.First(p => p.Title == "true")`）——如果 `CreateNode` 里改了这些名字而 `BuildChain` 没跟着改，会抛 `InvalidOperationException`（`First` 无匹配）。

**调用者与被调用者**：被 `AddExecPins` 与 `CreateCallNode` 调用；调用 `BrushOf`。

---

### `GraphBuilder.DataPin(string title, string typeName, bool isInput): ConnectorVm`

**状态**：✅ 已实现

**签名与契约**：`private static`。造一个数据引脚：`IsExec = false`、`TypeName = typeName`、`Brush = BrushOf(typeName)`。不抛。

**输入输出**：`typeName` 决定颜色与 `IsUnknown`（`"unknown"` / `""` → `IsUnknown == true`）。`PointerArg` 的 `typeName` 是**变量名**（见 `ArgTypeName`），于是 `IsUnknown == false` 但颜色落到默认灰。

**关键分支（伪代码）**：

```text
return new ConnectorVm{ Title=title, IsInput=isInput, IsExec=false,
                        TypeName=typeName, Brush=BrushOf(typeName) }
```

**边界与失败**：⛔ 不校验 `typeName`：传任何字符串都行，颜色只会退到默认灰。⛔ `IsUnknown` 只看 `"unknown"` / `""`，所以「变量名当类型」的引脚不会显示成 unknown（视觉上仍是灰点，但少一圈描边）。

**调用者与被调用者**：被 `CreateNode` / `CreateCallNode` 调用；调用 `BrushOf`。

---

### `GraphBuilder.Connect(ConnectorVm output, ConnectorVm input, bool isExec): void`

**状态**：✅ 已实现

**签名与契约**：`private`。往 `_connections` 追加一条 `ConnectionVm`，并把两端引脚的 `IsConnected` 置 `true`。`output` 或 `input` 为 `null` 时直接返回（不抛）。

**输入输出**：`isExec` 只影响线的画法（粗实线/细虚线），不影响引脚。不检查「上游是出脚、下游是入脚」，也不检查类型兼容。

**关键分支（伪代码）**：

```text
if output == null or input == null: return          # 触发条件：Find/FirstOrDefault 没找到脚
_connections.Add(new ConnectionVm(output, input, isExec))
```

**边界与失败**：

- ⛔ 不做任何合法性校验：可以重复连同一条边；循环回边是「body 出脚 → 循环的 exec 入脚」，因此图上存在环（这是对的，`While` 本来就有回边）。
- ⛔ 空判断掩盖了契约错误：`BuildChain` 里 `First(p => p.Title == "true")` 用 `First`（无匹配会抛），而 `Connect` 用 `null` 兜底——两处策略不一致，排查时要看清是哪一层吞掉了错误。

**调用者与被调用者**：被 `BuildChain` 与 `BuildArg` 调用；调用 `ConnectionVm` 的 3 参构造函数。


