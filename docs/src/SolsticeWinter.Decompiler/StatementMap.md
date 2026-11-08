# StatementMap — 表达式 → 字节偏移

**职责一句话**：给每条顶层指令算它在函数字节码里的偏移，补上 UAssetAPI 缺的「语句位置」。

**对外入口**：`StatementMap.Build(bytecode, asset)`、`StatementMap.Length(bytecode, asset)`。

**内部结构**：一次线性扫描，按 `GetSize` 累加。

**依赖**：`UAssetAPI`（`KismetExpression.GetSize`）。被 `ExecutionFlow` 与 `FunctionWalker` 使用。

**取舍**：

- **为什么需要**：参考项目（MIT）直接读 CUE4Parse 的 `KismetExpression.StatementIndex`，而那个字段在 CUE4Parse 里**就是流的字节位置**（`expression.StatementIndex = index`，index 是 `FKismetArchive.Index`）。跳转操作数 `EX_Jump.CodeOffset` 也是字节偏移，两者才能直接比。UAssetAPI 没有这个字段，只能自己算。
- **只映射顶层**：跳转目标永远落在顶层指令边界上；嵌套表达式（参数、条件）的偏移用不到，算了也是浪费。
- **用引用相等字典**：`KismetExpression` 是类且没重写 `Equals`，用 `ReferenceEqualityComparer` 避免误判。

---

### `Build(IReadOnlyList<KismetExpression>, UAsset): Dictionary<KismetExpression, uint>`

**状态**：✅ 已实现 ⛔ 边界

**签名与契约**：`public static`。返回「表达式实例 → 起始偏移」；纯读、不改输入。复杂度 O(n)，n = 顶层指令数。

**输入输出**：`BP_Logic` 的 ubergraph：1536 条指令 → 45675 字节，最后一条的偏移 + 它的 `GetSize` = 45675。空数组 → 空字典。

**关键分支（伪代码）**：

```text
offset = 0
for expr in bytecode:
    map[expr] = offset            # 引用相等
    offset += expr.GetSize(asset)
return map
```

**边界与失败**：

- ⛔ 依赖 `GetSize` 正确：`GetSize` 算错，后面所有偏移全错（跳转解析会指向错误块）。实测 1536 条指令的累计长度与函数字节码总长一致。
- ⛔ 只对**同一个数组里的实例**有效；反编译后重新构造的表达式对象查不到（字典按引用）。

**调用者与被调用者**：被 `ExecutionFlow.DecompileFlow`、`FunctionWalker.Walk` 调用；调用 `KismetExpression.GetSize`。

### `Length(IReadOnlyList<KismetExpression>, UAsset): uint`

**状态**：✅ 已实现

**签名与契约**：`public static`。返回字节码总长度（偏移上界）。纯读。

**输入输出**：同上的 ubergraph → 45675。

**关键分支（伪代码）**：累加 `GetSize`，无分支。

**边界与失败**：无（空数组返回 0）。

**调用者与被调用者**：被 `BlockProbe` 用于打印；调用 `GetSize`。
