# PromotableOperators — 运算符折叠表

**职责一句话**：把 `Add_IntInt` / `Not_PreBool` 这类数学函数名映射成 `+` / `NOT`，供画布显示成运算符节点。

**对外入口**：`PromotableOperators.Resolve(string functionName) → string?`。

**内部结构**：两张表，先按完整名查，再按 `_` 前缀查。

**依赖**：无。被 `FunctionWalker.MakeCall` / `MakeCallByName` / `NestedCall` 调用。

**取舍**：

- **表直接照抄 UEBGV**（`EngineBPData.PromotableOperators` + `KismetMathLibrarySpecialNodes`，53 条）：
  这是「显示成什么」的约定，自己发明一套只会让两边对不上。
- **前缀优先于完整名**：`Add_IntInt`、`Add_DoubleDouble` 都该显示成 `+`，所以先按 `_` 前缀匹配；
  但 `Not_PreBool` 这种「前缀会误判」的（`Not` 不在前缀表里）靠完整名兜住。
- **三个入口都要查**：算术大多作为**值**出现（`NestedCall`），只查顶层调用会漏掉绝大多数（实测 30 个运算符节点全部来自 `NestedCall`）。

---

### `PromotableOperators.Resolve(string): string?`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。纯函数，不抛。命中返回符号，未命中返回 `null`。

**输入输出**：`"Greater_IntInt"` → `">"`；`"Not_PreBool"` → `"NOT"`；`"Add_DoubleDouble"` → `"+"`；`"ReceiveBeginPlay"` → `null`；`"GetPI"` → `"PI"`。

**关键分支（伪代码）**：

```text
if ByName 有 functionName: return 符号
prefix = functionName 第一个 '_' 之前的部分（没有 '_' 就是整名）
if ByPrefix 有 prefix: return 符号
return null
```

**边界与失败**：

- ⛔ **只覆盖表里的 53 条**：`Vector + Vector` 这类走 `Add_VectorVector`（前缀 `Add` 命中，OK），但
  `ComposeTransforms` 这类特例必须在表里，否则显示成函数名。
- ⛔ **不看参数类型**：`Add_IntInt` 与 `Add_StrStr`（字符串拼接）都显示 `+`，UE 里前者是 `+`、后者是 `Append`。

**调用者与被调用者**：被 `FunctionWalker` 的三个调用构造点调用；不调用其它函数。
