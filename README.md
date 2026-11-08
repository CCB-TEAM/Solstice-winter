# Solstice-winter

UE cooked 蓝图的**读写工作台**（目标形态：仿 UE 编辑器的节点视图）。
当前状态：只读渲染可用；编辑未接。

## 跑起来

```powershell
.\run.ps1
```

它做三件事：`dotnet build -m:1` → 索引不存在就建 → 启动 UI。

> **必须带 `-m:1`**：本机 MSBuild 的并行节点会失败（表现为「生成失败 / 0 个错误」，日志里连 error 都没有）。
> `run.ps1` 已经替你带上了；如果你直接敲 `dotnet build` / `dotnet run`，请自己加 `-m:1`。

## 各模块

| 模块 | 能干什么 | 验证过什么 |
|---|---|---|
| `SolsticeWinter.Pak` | 读真实 pak（v11 / AES 加密索引 / Oodle） | 33808 条目；4274 个文件与 FModel 导出逐字节一致 |
| `SolsticeWinter.Index` | 扫资产建跨资产索引（类/函数签名/变量/引用） | 2137 资产 / 9303 函数 / 6.6 s 建、296 ms 载 |
| `SolsticeWinter.App` | WPF + Nodify 节点视图（只读） | 打开 `BP_Logic / DrawTopCardFromDeck`：52 节点 / 46 连线 |
| `SolsticeWinter.Cli` | 命令行：`pak` / `index` / `profile` | 见 `docs/INDEX.md` |

## 文档

从 [docs/INDEX.md](docs/INDEX.md) 进：模块清单、函数状态看板、未实现清单。
设计与决策在 [docs/可行性评估.md](docs/可行性评估.md)（D1–D16 + 每条代价）。

## 不做的事

- 不还原原作者节点布局（cooked 资产里没有，布局是算出来的）
- 不把「序列化成功」当「运行时正确」
- 不在仓库里放游戏资产、密钥、解包产物
