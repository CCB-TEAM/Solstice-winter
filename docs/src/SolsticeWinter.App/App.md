# App — 进程入口、崩溃日志与 `--graph` 自检分派

**职责一句话**：`Application` 子类，负责在 `OnStartup` 里决定「开窗口」还是「跑无窗口自检」，并把任何未处理异常写进 `%TEMP%\solstice-winter-ui\crash.log` 后再退出。

**对外入口**：进程命令行，两种形态：

```text
SolsticeWinter.App.exe [<index.json>]                       # 开窗口；不给参数时用 <exe 目录>\.cache\index.json
SolsticeWinter.App.exe --graph <虚拟路径> <函数名>            # 无窗口自检，结果写 %TEMP%\solstice-winter-ui\graph.txt
```

`App.xaml` 里**没有** `StartupUri`，主窗口由 `OnStartup` 自己 `new` 出来（因为要先看命令行）。

**内部结构**：

```text
OnStartup(e)
 ├─ DispatcherUnhandledException += → Log + MessageBox + Handled=true + Shutdown(1)
 ├─ AppDomain.UnhandledException  += → Log（只能记，救不了进程）
 ├─ if e.Args.Length >= 3 && e.Args[0] == "--graph"
 │     Environment.ExitCode = GraphProbe.Run(args[1], args[2])
 │     Shutdown(ExitCode); return                      # 自检模式不建窗口
 ├─ Log("Startup", null, "args=[...]")
 ├─ new MainWindow(e.Args.FirstOrDefault()).Show()
 └─ Log("Window shown", null)
        catch → Log("Startup", ex) + MessageBox + Shutdown(1)
```

**依赖**：`SolsticeWinter.App.Graph.GraphProbe`（自检）、`SolsticeWinter.App.MainWindow`（窗口）、`System.Windows.Threading.DispatcherUnhandledException`。被 WPF 运行时调用（`App.g.cs` 的 `Main` 里 `new App().InitializeComponent(); app.Run()`）。

**取舍**：

- **自检挂在 `App` 而不是单独的可执行文件**：GUI 程序没有控制台，`stdout` 看不到，所以自检只能靠「写文件 + 退出码」。复用同一个 `App` 意味着自检与真实启动走同一套依赖注入与异常处理，不会出现「自检过了、界面炸了」。代价：`--graph` 分支必须先于 `MainWindow` 构造，参数判断写死在 `OnStartup` 里。
- **`Shutdown(1)` 而不是 `Environment.Exit(1)`**：`Environment.Exit` 会跳过 WPF 的清理，`Shutdown` 让 `Application` 正常走关闭流程。代价：`Shutdown` 之后同一帧里若还有别的代码要跑，会被吞掉（这里靠 `return` 保证不再往下走）。
- **日志全走 `%TEMP%`**：模型不支持读图，出问题时用户能贴出来的只有日志文件；写进程序目录会碰上只读安装位置。代价：日志分散在临时目录，换用户/清理临时目录后就没了。
- **`AppDomain.UnhandledException` 只记不拦**：那个事件拦不住崩溃（进程照样死），记下来是为了下一次能看到「死之前发生了什么」。

---

### `App.OnStartup(StartupEventArgs e): void`

**状态**：✅ 已实现

**签名与契约**：`protected override`。读 `e.Args` 分派两条路径：自检（`--graph`）或开窗口。副作用：注册两个全局异常处理器、写 `crash.log`、可能弹出 `MessageBox`、可能 `Shutdown`。不抛异常（整体包在 `try/catch` 里）。

**输入输出**：

- `e.Args.Length >= 3 && e.Args[0] == "--graph"` → `GraphProbe.Run(args[1], args[2])`，把返回值写进 `Environment.ExitCode`，再 `Shutdown(该值)`，**不建窗口**。返回码 0 = 成功，1 = 失败（失败原因在 `graph.txt` 里）。
- 其余情况 → 把 `e.Args.FirstOrDefault()` 当索引路径传给 `MainWindow`。无参数时该值为 `null`，`MainViewModel.InitializeAsync` 交给 `FindIndex` 走四级回退（`<exe 目录>\.cache` → `<当前目录>\.cache` → 从 exe 往上找到 `Solstice-winter.slnx` 的仓库根下的 `.cache`）。
- `--graph` 后只给 1～2 个参数（如 `--graph /Game/A`）→ 不满足 `Length >= 3`，会**当成索引路径去开窗口**，随后 `InitializeAsync` 报「找不到索引」。这是当前的静默退化。

**关键分支（伪代码）**：

```text
base.OnStartup(e)
register DispatcherUnhandledException → Log("Dispatcher", ex); MessageBox; Handled=true; Shutdown(1)
register AppDomain.UnhandledException  → Log("AppDomain", ex)
try:
    if e.Args.Length >= 3 and e.Args[0] == "--graph":     # 触发条件：恰好以 --graph 打头且够 3 个参数
        ExitCode = GraphProbe.Run(e.Args[1], e.Args[2])
        Shutdown(ExitCode); return
    Log("Startup", null, $"args=[{string.Join(",", e.Args)}]")   # 注意 args 为空时记的是 "args=[]"
    new MainWindow(e.Args.FirstOrDefault()).Show()              # 索引路径 = 第 1 个参数
    Log("Window shown", null)
catch ex:                                                       # MainWindow 构造/XAML 解析失败会走这里
    Log("Startup", ex); MessageBox.Show(ex.ToString(), "Solstice-winter 启动失败"); Shutdown(1)
```

**边界与失败**：

- ⛔ XAML 解析异常（例如曾经写错 `CircuitConnection.ArrowSize="8"` 引发的 `XamlParseException`）发生在 `new MainWindow(...)` 里，被 `catch` 捕获后弹 `MessageBox` 并 `Shutdown(1)`——进程不会留下窗口，所以「双击没反应」类问题的唯一证据是 `crash.log`。
- ⛔ 自检模式**不写 `crash.log` 的成功记录**：`GraphProbe.Run` 内部自己 `catch` 所有异常并写 `graph.txt`，所以自检失败时 `crash.log` 里什么都没有。
- ⛔ `DispatcherUnhandledException` 里 `args.Handled = true` 之后立刻 `Shutdown(1)`：这是在「不让 WPF 默认崩」与「不让程序带伤继续跑」之间的选择，结果是任何 UI 线程未处理异常都会直接关掉窗口。
- 自检模式下 `Shutdown` 之前若 `GraphProbe` 已经卡住（打开大资产），没有超时机制，进程只能手动杀。

**调用者与被调用者**：被 WPF 运行时（`App.g.cs` 生成的 `Main`）调用；调用 `GraphProbe.Run`、`MainWindow(string?)`、`App.Log`。

---

### `App.Log(string where, Exception? ex, string? note = null): void`

**状态**：✅ 已实现

**签名与契约**：`private static`。追加一行（加异常全文）到 `%TEMP%\solstice-winter-ui\crash.log`，必要时先建目录。**吞掉所有异常**：写日志失败不会再抛一次。不返回值。

**输入输出**：

- `where`：调用点标签，实测出现过的值：`Dispatcher` / `AppDomain` / `Startup` / `Window shown`。
- `ex`：可为 `null`（成功路径只记一行时间戳 + `where` + `note`）。
- `note`：可为 `null`，为 `null` 时格式化成空串，于是成功行形如 `[12:00:00.123] Startup \n\n`（末尾带空行）。
- 落盘路径固定为 `Path.GetTempPath() + "solstice-winter-ui" + "crash.log"`，**追加**模式，从不清理。

**关键分支（伪代码）**：

```text
try:
    Directory.CreateDirectory(dirname(LogPath))        # 目录不存在时建；已存在不抛
    AppendAllText(LogPath, $"[{Now:HH:mm:ss.fff}] {where} {note}\n{ex}\n\n")
catch:                                                   # 磁盘满 / 权限 / 路径被占
    # 故意什么都不做：日志写不进去也不能因此再崩一次
```

**边界与失败**：

- ⛔ 不是线程安全的：`File.AppendAllText` 每次打开-追加-关闭，多线程同时写可能丢行或抛 `IOException`（被 `catch` 吞掉，表现为日志缺行）。当前只有 UI 线程与 `AppDomain` 回调会调它，风险低但存在。
- ⛔ 无轮转、无大小上限：反复崩溃会一直长。
- `note` 里带换行（如 `args` 本身含逗号/换行）不会转义，日志行会错位。

**调用者与被调用者**：被 `App.OnStartup` 与两个异常处理器调用；不调用其它函数。
