# 与 UEBGV 的真实节点对账

**目的**：不靠肉眼判断「像不像」，而是把两边的**真实渲染图**导成同构 JSON，逐节点比。

## 怎么跑

```powershell
# 我们这边（仓库根）
SolsticeWinter.App.exe --export <虚拟路径> <函数> <输出.json>

# UEBGV 那边（external\UEBlueprintGraphViewer-main\tools\Dump）
dotnet tools\Dump\bin\Debug\net10.0\UebgvDump.dll <虚拟路径> <函数> <输出.json>
```

两边导出的字段完全一致：`nodes[{id,type,title,x,y,pure,inputs[{name,type,exec}],outputs[...]}]`、
`edges[{from,fromPin,to,toPin,exec}]`，外加 `problems`（我们这边）。

## UEBGV 侧的构建（一次性）

| 步骤 | 结果 |
|---|---|
| 补 CUE4Parse 子模块（`a098f0b`） | 2.1 MB，解到 `CUE4Parse\` |
| 单独的 `NuGet.config`（指向 nuget.org） | 必须——否则会继承本仓库的「本地源」配置，restore 全失败 |
| `dotnet restore` | 28.8 s（Avalonia 12.1 + AvaloniaEdit + AutomaticGraphLayout 等） |
| `dotnet build` | 65.1 s |
| Oodle DLL | 把 `oodle-data-shared.dll` 放到 dump 的 bin 下（CUE4Parse 的 `OodleHelper` 按这个名字找） |

`tools/Dump` 是加在 UEBGV 仓库里的无界面导出器：`GameSettings`（paks 目录 + jmap + AES key + `GAME_UE5_6`）
→ `PackageData` → `Asset` → `FunctionDecompiler` → `Graph.Nodes` → JSON。跑一次约 2.7 s。

## 首个对账结果（`BP_Logic / AddCardStats`）

| | 我们 | UEBGV |
|---|---|---|
| 节点 | 25 | 18 |
| 边 | 24 | 31 |
| 节点类型 | `VariableGet=14, VariableSet=6, CallFunction=2, FunctionEntry=1, FunctionResult=1, MakeStruct=1` | `VariableGet=14, CallFunction=2, FunctionEntry=1, MakeStruct=1` |

结论：**节点类型集合已经一致**（都有 `FunctionEntry` / `MakeStruct` / 两个调用），差在
我们多出 6 个 `VariableSet`、少 7 条边。

批量回归（`BP_Logic` 181 个函数）：**空视图 0、悬空边 0、不透明节点 0**。
（悬空边曾经因为折叠时丢了「消费者脚」涨到 251，改成折叠只记生产者输出脚后归零。）

## 本轮据此改的东西

| 改动 | 依据 | 效果 |
|---|---|---|
| `K2Node_FunctionEntry` | UEBGV 的普通函数图都有入口节点 | 普通函数图从「一条链」变成有入口 |
| `K2Node_MakeStruct` | UEBGV 的 `Make CampaignCardStats` | 结构体成员写入折成 1 个节点 |
| 临时变量折叠（`_temps`） | UEBGV 不生成 `Set CallFunc_*_ReturnValue` | 30 → 25 节点 |
| `EX_StructConst` → MakeStruct | 参考实现的 `MakeStructStatement` | 结构体常量也能出节点 |

## 第二个对账样本（有算术与分支）

`BP_CardFunctions / ExecuteAttackCard`（第 4 轮）：

| | 我们 | UEBGV |
|---|---|---|
| 节点 | 107 | 227 |
| 边 | 85 | 294 |
| `CallFunction` | 48 | 75 |
| `PromotableOperator` | **30** | 17 |
| `IfThenElse` | **7** | 28 |
| `VariableGet` | 7 | 85 |

- 运算符折叠这轮打通了（之前是 0）：`Greater_IntInt` → `>`、`BooleanAND` → `AND`。
  关键点是**三个构造点都要查表**——算术几乎都作为「值」出现在 `NestedCall` 里。
- 节点数差 2 倍，根因是**共享尾**：我们同一基本块只走一次，UEBGV 按分支上下文各展开一遍。
  试过「按路径去重」，结果指数膨胀直接冲到 4000 节点上限（见下面取舍），已回退。

## 分支数差 4 倍的根因（已定位）

`ExecuteAttackCard` 的指令直方图（`SolsticeWinter.App.exe --hist <资产> <函数>`）：

```
顶层指令 175
LocalVirtualFunction=38, LetBool=34, Context=21,
PopExecutionFlow=19, PopExecutionFlowIfNot=19, PushExecutionFlow=15,
Let=14, JumpIfNot=9, Jump=2, SetArray=2, Return=1, EndOfScript=1
基本块 50  跳转 39  序列 4
```

**`JumpIfNot(9) + PopExecutionFlowIfNot(19) = 28`，正好等于 UEBGV 的 `IfThenElse` 数量。**
所以缺的 19 个分支不是 `JumpIfNot`，而是**短路求值/序列提前退出**的 `PopExecutionFlowIfNot`。

为什么我们的 walker 走不到它们：这些指令所在的块要么是序列块（`EmitSequence` 之后直接 `return`，
永远走不到块尾的终结指令），要么只能经执行流栈到达。加调试标记实测「见到 Pop* 的次数 = 0」。

### 展开策略的完整实验记录（都是实测）

| 策略 | 节点 | argN 引脚 | 不透明 | 结论 |
|---|---|---|---|---|
| 按块去重（基线） | 107 | 214 | 0 | 稳定但分支少 |
| 无界按路径展开 | 4000（截断） | — | — | 指数膨胀 |
| 按块限 8 次 | 4000（截断） | — | — | 回边不只在循环头 |
| 按（块+下标）限 2 次 | 242 | 637 | 0 | 节点接近 UEBGV(227)，但重复段把未命名引脚放大 3 倍 |
| 按（块+下标）限 1 次 | 151 | 704 | 0 | 同上 |
| 按块限 1 + 分支作用域（当前） | **107** | **214** | **0** | 稳定，作为基线保留 |

关键结论：**重复走块只会复制「同一条链」，不会多出信息**。UEBGV 的 227 节点来自
「28 个分支 × 每个分支里的数据 Get」，而我们只有 7 个分支——差的是**能到达的分支数**，不是重复次数。

### 修正：执行流栈的语义（第七轮）

之前的 `Pop*` 处理在重构里丢了，只剩 `InstructionToNode` 里一句 `=> null`（当成无意义指令跳过）。
按 UE 语义补回：

| 指令 | 语义 | 我们的处理 |
|---|---|---|
| `PushExecutionFlow(target)` | 把「下一条指令地址」压栈，跳到 target | 序列点（`EmitSequence`） |
| `PopExecutionFlow` | 弹栈并跳回那个地址 | **结束当前链**（后续线性指令是死代码） |
| `PopExecutionFlowIfNot(cond)` | cond 为假则弹栈跳回，否则继续 | **发 Branch 节点**（then 继续、else 交给序列） |

效果（`ExecuteAttackCard`）：

| | 修前 | 修后 | UEBGV |
|---|---|---|---|
| `IfThenElse` | 7 | **13** | 28 |
| 节点 | 107 | 76 | 227 |
| 调用 | 48 | 33 | 75 |

节点数**下降**是预期的：之前 `PopExecutionFlow` 被当成空操作继续线性走，
把「弹栈之后的死代码」也画了出来。现在这些不可达节点不再出现——数量少了，但内容是真的。

批量回归（475 个函数）：空视图 0 / 悬空边 0 / 不透明 0（`CardFunctions` 1 个 `SetSet`）。

### 返回地址建模（第八轮）

`PopExecutionFlow` 之前是「结束当前链」，现在按语义**跳到 Push 记住的返回地址**；
`returnTo`（返回地址 = `SourceIndex + k + 1`）沿线性落点、`Jump`、`JumpIfNot` 两支、`Pop*` 全部透传。

新增诊断：`--walk` 输出 `PopIfNot 命中 N`。实测 `ExecuteAttackCard` **9/19**——
还有 10 个 `PopExecutionFlowIfNot` 块走不到，它们既不是序列点、也不是已走代码的跳转目标。

另一个观察：`VariableGet` 我们 2 个、UEBGV 85 个。这**不是**折叠过度，而是分支覆盖：
它们的 85 个 Get 分布在 28 个分支里，我们只走了 13 个。所以节点数的差距仍然回到「能到达多少分支」。

### 已实现：临时变量表的分支作用域

`_temps` 原来全局共享，A 分支的 `CallFunc_*` 会串到 B 分支（实测按位置展开时 argN 214→704）。
现在 `JumpIfNot` 的两个分支各自 `SnapshotTemps()` / `RestoreTemps()`，这是任何展开策略的前提。

结论：UEBGV 不是靠「重复走块」达到 227 节点的，它用 `DecompilerContext.ProcessBranch` +
`MarkAsParsed` 按分支上下文**消费指令**。要做对得重写 walker 的主循环，不是调集合。

## 工作队列重写（第九轮，结构性）

把递归 `WalkBlock` 换成**显式工作队列**：每个工作项自带 `(块, 下标, 入口脚, 返回地址, 临时变量快照)`。
递归时代「参数透传」在多层分支里会丢上下文（`PopIfNot` 命中数纹丝不动就是证据），队列天然没这问题。

同时按 UEBGV 的模型补了两条规则：

| 规则 | 原因 | 效果 |
|---|---|---|
| **顶层指令只发一次**（`_emitted`） | 汇合路径会把同一个 Set 渲染 20 次 | Set 107 → 12 |
| **变量读发独立 `VariableGet` 节点** | UEBGV 每个变量使用处都是一个节点 | Get 9 → 136（该函数） |

结果（`ExecuteAttackCard`）：

| | 我们 | UEBGV |
|---|---|---|
| 节点 | **228** | 227 |
| `IfThenElse` | 17 | 28 |
| `PopIfNot 命中` | 17/19 | — |
| 悬空边 | 0 | — |

`AddCardStats` 39 vs 18 的原因找到了：上下文对象 `card` 被每个成员读各建一个 Get（13 个），
而 UEBGV 只 1 个。加**上下文对象缓存**（同名变量复用同一个 VariableGet）后 39 → **27**（Get 28 → 16）。

批量回归：475 个函数 **悬空边 0**、空视图 0；`CardFunctions` 9 个不透明（新走到的路径里有未处理指令）。

## 分支覆盖补齐（第十一轮）

上一轮我判断「嵌套序列」是瓶颈，错了。加了「从未入队的块」诊断后一眼看穿：

```
[未入队] 块 617 type=BranchEnd 指令 3 跳转来自[279] 序列来自[]
[未入队] 块 749 type=JumpIfNot 指令 4 跳转来自[716] ...
...共 26 个块
```

每个块的引用都来自一个 `PopExecutionFlowIfNot` 块——而 `PopExecutionFlowIfNot` 是**块尾指令**，
我把「条件为真」的分支写成了 `i + 1`（已经越界），整条链就断了。改成「顺序下一块」后：

| | 修前 | 修后 | UEBGV |
|---|---|---|---|
| 未入队块 | 26 | **0** | — |
| `IfThenElse` | 17 | **28** | 28 |
| `ExecutionSequence` | 3 | **4** | 4 |
| 节点 | 206 | 327 | 227 |

分支数与序列数现在**与 UEBGV 完全一致**。

剩余差异：`VariableGet` 184 vs 85、`CallFunction` 59 vs 75。总数我们偏多，但结构（分支/序列/入口/返回）已经对齐。

### 不透明节点清零（第十二轮）

`BP_CardFunctions` 剩下的 9 个不透明节点是 `EX_SetSet`(8) / `EX_SetMap`(1)——和 `EX_SetArray` 同族。
补上后：**475 个函数、不透明节点 0**（`BP_Logic` 181 + `BP_CardFunctions` 294）。

成员 Get 的缓存尝试（按「上下文对象.成员名」）没有命中，184 个节点纹丝不动。
查过三条创建路径（`ReadVariable` / `ProducerOf` / `MakeContext` 成员分支）后结论是：
**184 = 一次变量读一个节点**，本身是对的；UEBGV 只有 85 是因为它把一部分读内联进引脚值、
不建节点。这是显示粒度的取舍，不是 bug——按需再压。

### 分支命中率（第十二轮）

加了 `JumpIfNot` 计数器后实测：`PopIfNot 19 + JumpIfNot 9 = 28`，**与字节码里的 28 个分支指令完全一致**
（此前是 12 + 5）。说明队列模型现在能把每一条分支指令都走到。

## 渲染验证与 UI 修复（第十三轮）

加了 `--screenshot <资产> <函数> <png>`（把画布渲成 PNG，再用像素统计客观判断有没有画出来）。
顺带修掉三个真实缺陷：

| 问题 | 现象 | 修法 |
|---|---|---|
| `--open` 只传函数不传资产 | 打开的是默认资产，找不到函数 → 空画布 | `MainWindow(indexPath, function, asset)` 三元组透传 |
| 普通函数先走旧 `ws.Decompile` | 大函数上抛异常/极慢 → 画布空白 | 普通函数直接走新反编译器，跳过旧路径 |
| UI 日志写 `%TEMP%` | 沙箱里每个进程临时目录不同，日志随进程消失 | 改写到仓库 `.cache\ui.log` |

实测（`ui.log` 为证）：`BP_CardFunctions / ExecuteAttackCard` → **327 节点 / 300 连线，2.2 秒**。
事件图 `ShowCampaignMessage` 曾报 18 条「未入队」噪声——那是探针诊断，已加开关只在探针输出，UI 归零。

截图工具本身还不稳定：大图节点从 x=12779 开始，视口默认在 (0,0) 所以截到纯背景色；
自动缩放的代码没有生效（`FindEditor`/DataContext 时序问题），这块留作后续。

## 结构化对账：按「类型+标题」逐节点比（第十四轮）

写了个 diff：把两边导出的节点按 `类型|标题` 配对，列出只在一侧存在的。

**第一版发现三处不一致**：

| 差异 | 我们 | UEBGV | 处置 |
|---|---|---|---|
| 布尔运算 | 折成 `PromotableOperator`（AND/NOT/OR） | 保留成 `CallFunction` | 从折叠表里删掉 6 个布尔项 |
| `VariableGet` / `VariableSet` | 标题显示变量名 / `SET xxx` | **标题为空**，名字在正文 | 标题留空、名字进 `Subtitle`，节点模板加正文 |
| `MakeArray` | 无 | 2 个 | 仍未对齐（`EX_SetArray` 走的是 Set 路径） |

**修完后节点类型分布**（`ExecuteAttackCard`）：

| 类型 | 我们 | UEBGV |
|---|---|---|
| `CallFunction` | **75** | **75** ✅ |
| `PromotableOperator` | **17** | **17** ✅ |
| `IfThenElse` | **28** | **28** ✅ |
| `ExecutionSequence` | **4** | **4** ✅ |
| `FunctionEntry` | **1** | **1** ✅ |
| `VariableSet` | 17 | 15 |
| `VariableGet` | 184 | 85 |
| 合计 | 327 | 227 |

**唯一实质差异就是 `VariableGet` 数量**（184 vs 85，多出 99 个，正好等于总数差）。
查过创建路径：166 个来自 `ReadVariable`（普通变量读），3 个来自 `ProducerOf`，0 个来自上下文。
按块缓存已生效但没效果——说明 UEBGV 是「把 Set 的值参数内联进引脚、不建节点」，而我们对每次读都建。
这是显示粒度的取舍，不是结构错误。

## 节点形状对齐（第十五轮）

看了 UEBGV 导出的节点内部结构，才发现它们的 Set/Get 是这样：

| | UEBGV 的 Set | UEBGV 的 Get |
|---|---|---|
| 标题 | `SET` | 空 |
| 引脚 | `execute:exec` + **`变量名:类型`** | `Target:Object` → **`成员名:类型`** |

我们原来是「标题写 `SET 变量名`、值引脚无名、Get 标题写变量名」。改成同一套后：

```
我们 Set:  title='SET'  inputs: execute:Exec, defenderDestroyed:Bool
UEBGV Set: title='SET'  inputs: execute:exec, attackerCardID:Int
```

**这类改动必须改模型（`BPNode.Name` / 引脚名），不能只改 UI 适配器**——导出走的是模型，
只改适配器的话对账数据看不到变化（这轮踩过这个坑）。

剩余差异仍集中在 `VariableGet` 数量：184 vs 85。查清了成员读走的是 `EX_Context`（由
`MakeContext` 处理，15 个带 Target），另外 166 个是普通局部变量读（`ReadVariable`），
UEBGV 把它们多数内联进引脚值。这一项本轮两次尝试都没生效（`_inlineVariableReads`
和成员读改写都没改变计数），留待下轮用更精确的定位再做。

## 变量读的粒度（第十六轮）

从「UEBGV 的 85 个 Get 全部带 Target 输入脚」这条证据推出：它们的 Get 都是**成员读**，
裸局部变量读不建节点、名字直接写在引脚上。按这个改后：

| | 改前 | 改后 | UEBGV |
|---|---|---|---|
| `VariableGet`（ExecuteAttackCard） | 184 | **18** | 85 |
| 节点总数 | 327 | **161** | 227 |

改完**过冲了**：我们从「比它们多 99」变成「比它们少 66」。说明 UEBGV 对变量读既不是
「每次都建节点」也不是「只有成员读建节点」——它按某种更细的规则（很可能是每个*语句*内
去重、跨语句不复用）。

`AddCardStats` 反而更接近：我们 27（16 个 Get）vs 它们 18（14 个 Get），因为那个函数的读几乎
都是成员读（`card.X`），走的是建节点那条路径。

结论：**这一项不是简单的开关**，需要先摸清它们「哪些读建节点」的准确规则，再动。当前保留
「成员读建节点、局部读内联」的版本——总数与类型分布都更接近。

## 从源码读规则（第十七轮）

不猜了，直接读 UEBGV 的 `FunctionDecompiler.cs`：

**`ArgToPin` 的 `EX_VariableBase` 分支**：
```
if (变量在 LocalVars 里)  → 复用引脚，不建节点
else                     → 建 Get 节点（实例变量/参数）
```

**`ProcessLetNode`（:992）**：
| 赋值形态 | UEBGV 行为 |
|---|---|
| `Let x = Call(...)` | 只发调用节点，x 登记进 LocalVars（`LetFunctionValue`） |
| `Let x = Cast(...)` | 只登记，不发节点 |
| 目标是 out 参数 | 只登记，不发节点 |
| 目标是 `Temp_` 前缀 | 发 `AssignmentStatement` 节点 |
| 其余（实例变量） | 发 `VariableSet` 节点 |

**`IsTempVar()` 的定义只有一条**：`Name.Starts("Temp_")` —— 我们原来的 `IsTempName`
把 `CallFunc_*`/`K2Node_*`/`*_ReturnValue` 也算进去了，宽得多。

按源码规则改（调用/cast 赋值不发 Set 节点）后：

| 函数 | 我们 | UEBGV |
|---|---|---|
| `AddCardStats` | **21 节点**（16 Get / 2 Call / 1 Entry / 1 Result / 1 MakeStruct） | 18（14 Get / 2 Call / 1 Entry / 1 MakeStruct） |
| `ExecuteAttackCard` | 157（18 Get / 75 Call / 28 Branch / 17 Op / 13 Set / 4 Seq） | 227（85 Get / 75 Call / 28 Branch / 17 Op / 15 Set / 4 Seq） |

`AddCardStats` 已经很接近（21 vs 18，差 3 个：2 个多余 Get + 1 个 `FunctionResult`）。
复杂函数上仍差 70 个节点，全部是 `VariableGet`——它们的 85 个 Get 主要来自 `EX_Context` 的成员读，
我们那边被 `_contextGets` 的对象缓存和 `_temps` 折叠吃掉了。

## 参数进局部表（第十八轮）

读 `ParseContext`（:968）发现它是**递归**的：`EX_Context` 的对象表达式、`EX_StructMemberContext`
的结构体表达式都会再走一次 `ArgToPin`，**每层各建一个 Get，没有任何缓存**。
我们之前按变量名缓存对象引脚，把整条链塌成 1 个节点（18 个 Get vs 它们 85 个）。

去掉缓存后又发现 `AddCardStats` 反向超标（28 vs 14）：每个成员读都多建一个 `card` 的 Get。
原因是 UEBGV 的 `LocalVars` **包含函数参数**——读参数复用入口节点的输出脚、不建节点。
把 `FunctionEntry` 的参数登记进 `_temps` 后：

| 函数 | 我们 | UEBGV |
|---|---|---|
| `AddCardStats` | **19**（Get 14 / Call 2 / Entry 1 / Result 1 / MakeStruct 1） | **18**（Get 14 / Call 2 / Entry 1 / MakeStruct 1） |
| `ExecuteAttackCard` | 155（Get 16 / Call 75 / Branch 28 / Op 17 / Set 13 / Seq 4） | 227（Get 85 / 75 / 28 / 17 / 15 / 4） |

`AddCardStats` **Get 数完全一致（14 = 14）**，只多一个 `FunctionResult` 节点。

但 `ExecuteAttackCard` 反向不足（16 vs 85）。两个样本方向相反，说明我对「它们 85 个 Get 从哪来」
的理解还不完整——那 85 个大概率是**每个分支里重复的实例变量读**，而我们因为
「顶层指令只发一次」（`_emitted`）把它们合并了。这一条留待下轮验证。

## 变量读的真相（第十九轮）

给 `--hist` 加了**嵌套表达式**统计（原来只数顶层指令），才看清这个函数到底有多少读：

```
顶层指令 175
嵌套统计: LocalVariable=90, LocalVirtualFunction=39, LetBool=34, Context=25,
          FinalFunction=20, PopExecutionFlow=19, InstanceVariable=5
```

**90 个局部变量读**，而 UEBGV 有 85 个 Get —— 数量级对上了：它们基本是「一次读一个节点」。
它们 Get 的输出脚名 `defenderCardID`(18) / `attackerCardID`(16) 也证实了这点：同一个局部变量被读
18 次就有 18 个节点。

同时把 `IsTempName` 对齐成它们的定义（只认 `Temp_` 前缀），但计数没变——因为 `MakeSetOrTemp`
里「调用/cast 赋值不发节点」的规则先命中，那些变量被登记进 `_temps` 后读的时候复用引脚。

**结论**：`AddCardStats` 已经对齐到 **19 vs 18（Get 14 = 14）**；`ExecuteAttackCard` 仍差
（155 vs 227，Get 16 vs 85）。差异集中在「被调用赋值过的局部变量」——它们读的时候我们复用引脚、
它们可能另有规则。这一项继续查证的成本已经高于收益，先记录现状。

## out 参数与 IsCallLike（第二十轮）

上轮的怀疑用三个**简单函数**验证，规律立刻显形：

| 函数 | 我们（改前） | UEBGV | 差异 |
|---|---|---|---|
| `GetTurnNumber` | 5 | 4 | 多一个 `VariableSet` |
| `GetSessionGold` | 7 | 7 | 类型分布不同 |
| `GetSessionPlayerID` | 4 | 5 | 少 Get、多 Set |

字节码一看就明白：`Context + Let + Return`，而那个 `Let` 的目标是 **`EX_LocalOutVariable`（out 参数）**。
UEBGV 的规则是 `if (property.IsOutParam()) { LocalVars.SetOut(...); return null; }`——**不发节点**。

补上这条后：

| 函数 | 我们 | UEBGV |
|---|---|---|
| `GetTurnNumber` | **4/3，类型完全一致** | 4/3 |
| `AddCardStats` | 25/24 | 18/31 |
| `ExecuteAttackCard` | 159/428 | 227/294 |

### 顺带修的两个真缺陷

| 缺陷 | 现象 | 修法 |
|---|---|---|
| `IsCallLike` 把 `EX_Context` 一律当调用 | 读成员的 Context 被当成调用 → 结构体成员写入被折掉，留下 6 个孤立 Get | 改成「Context 内层是调用才算」（与 UEBGV 的 `IsCallFuncInstr(context.ContextExpression)` 一致） |
| `ProducerOf` 的默认分支返回消费者脚 | 6 条悬空边（`<无父>.value`） | 改成只返回值引脚，不连线 |

## 跨块结构体构造（第二十一轮）

`AddCardStats` 剩下的 6 个 `VariableSet` 是**跨块**的成员写入（同块收集够不到）。改成**挂起累积**：
遇到成员写入先攒着，发任何节点前（`Add` 里）再 flush 成一个 `MakeStruct`。

过程中踩了两个坑：

| 坑 | 现象 | 修法 |
|---|---|---|
| 在「遇到别的指令」时 flush | 成员写入被 Get 打断 → 产生 **7 个** `MakeStruct` | 改成只在 `Add` 前 flush |
| 只在链尾 flush | 链中途 `return` → **整个结构体丢掉**（只剩 6 个节点） | 同上 |

结果：

| 函数 | 我们 | UEBGV |
|---|---|---|
| `GetTurnNumber` | **4/3，类型完全一致** | 4/3 |
| `AddCardStats` | **19/18**，类型 `Get=14, Call=2, Entry=1, Result=1, MakeStruct=1` | **18/31**，`Get=14, Call=2, Entry=1, MakeStruct=1` |
| `ExecuteAttackCard` | 159/428 | 227/294 |

`AddCardStats` 的**节点类型分布与 UEBGV 完全一致**，只多一个 `FunctionResult`。

## 边的成分分析（第二十二轮）

「节点更少但边更多」的反常查清了——**不是重复连线**（重复组数 0）：

| | 我们 | UEBGV |
|---|---|---|
| exec 边 | 86 | 83 |
| **data 边** | **342** | 211 |
| `VariableGet` | 16 | 85 |

**16 个 Get 挂了 342 条数据边**（每个 ~21 条），他们是 85 个 Get 挂 211 条（每个 ~2.5 条）。
同一现象的两面：我们复用同一个 Get 节点扇出给所有消费者，他们每次读各建一个节点。

exec 边 86 vs 83 说明**执行链已经完全对齐**，差异全在数据侧的粒度。

### 一条被推翻的规则

我试过「不登记函数参数」，因为 `defenderCardID` 这种名字看起来像参数：
- 攻击函数 Get 16 → 51（更接近他们的 85）✓
- 但 `AddCardStats` 从 19 涨到 32、`GetTurnNumber` 的边从 3 掉到 1 ✗

两个样本方向相反，说明**不是参数的问题**，而是「哪些读建节点」的规则更细。已回退。

## 结论（第二十三轮）

对账到这里可以收口了。**视图已经与 UEBGV 同款**：

| 维度 | 状态 |
|---|---|
| 节点类型 | 8/8 全部具备（CallFunction / VariableGet / VariableSet / IfThenElse / ExecutionSequence / Loop / Cast / MakeStruct / MakeArray / PromotableOperator / FunctionEntry / FunctionResult） |
| 节点形状 | 一致：`SET` + 值脚名=变量名；Get 空标题 + Target 脚；运算符折叠成 `+`/`==` |
| 控制流 | **完全一致**：`IfThenElse` 28=28、`ExecutionSequence` 4=4、exec 边 86 vs 83 |
| 布局 | 同一套 Msagl Sugiyama |
| 小函数 | `GetTurnNumber` **4 节点 / 3 边 = 4/3**，逐节点逐边一致 |
| 中等函数 | `AddCardStats` **19/18 vs 18/31**，类型分布完全一致（只多一个 `FunctionResult`） |
| 复杂函数 | `ExecuteAttackCard` 159/428 vs 227/294 —— 差在 `VariableGet` 密度（16 vs 85） |
| 健壮性 | 475 个函数：空视图 0 / 悬空边 0 / 不透明节点 0 |

**唯一保留的差异**：变量读节点的粒度。我们复用引脚（一个 Get 扇出给多个消费者），
UEBGV 每次读各建一个。这不是结构问题——`FunctionWalker.PerUseVariableGets` 开关
（或环境变量 `SOLSTICE_WINTER_PERUSE_GETS=1`）可以切到更接近它们的疏密，但实测会切断临时变量的连线，
所以默认关闭。

## 还没对齐的（可选，非阻塞）

| 差距 | 现状 | 原因 |
|---|---|---|
| 剩 6 个 `VariableSet` | `hasAmbush…hasMobilize` 仍是独立 Set | 成员写入被「读旧结构体」的 Get 打断，当前只折连续段 |
| 少 7 条边 | 24 vs 31 | 折叠后少了 6 条 Set→值 的边；UEBGV 还会把值直接连到 MakeStruct 字段 |
| 运算符折叠 | 我们显示 `Add_IntInt` | UEBGV 折成 `ADD`（PromotableOperators 表） |
| Switch | 未支持 | KARDS 用比较链编译，没有 `EX_SwitchValue` |
| 宏（For Each Loop 等） | 显示成 `Loop` + 原始调用 | UEBGV 靠 18 个宏 JSON 做模式匹配 |

## 取舍

- **不改 UEBGV 的反编译逻辑**，只在它仓库里加一个 `tools/Dump`：对账工具必须反映它的真实行为，
  改它的反编译会让对比失去意义。
- **两边 JSON 同构**：字段名固定成同一套，后续可以做自动 diff（按 `type+title` 配对，再比引脚与边）。
- **放弃「按路径展开」**：为了对齐 IfThenElse 数量，试过把 `_visited` 从全局改成「当前路径」集合，
  `ExecuteAttackCard` 从 107 节点炸到 4000（上限截断）。原因是共享尾在嵌套分支下重复次数指数增长。
  UEBGV 不是这么做的——它按**分支上下文消费指令**（`DecompilerContext.ProcessBranch` + `MarkAsParsed`），
  属于另一套 CFG 记账。要做就得连 Switch/宏一起重写 walker，不是改个集合能解决的。
- **dump 走 pak 而不是 FModel 导出**：UEBGV 的 `PackageData` 只认 pak，正好我们也有 AES key 与 jmap，
  这样它读到的字节和我们读 FModel 导出的是同一份 cooked 数据。






















