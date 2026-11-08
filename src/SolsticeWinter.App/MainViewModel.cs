using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SolsticeWinter.App.Graph;
using SolsticeWinter.App.ViewModels;
using SolsticeWinter.Core;
using SolsticeWinter.Decompiler;
using SolsticeWinter.Index;
using Solstice.Kismet;

namespace SolsticeWinter.App;

/// <summary>资产选择器里的一行。</summary>
public sealed record AssetItem(string VirtualPath, string? ClassName, int FunctionCount)
{
    public string Display => $"{VirtualPath[(VirtualPath.LastIndexOf('/') + 1)..]}   ({FunctionCount})";
    public string Folder => VirtualPath[..Math.Max(0, VirtualPath.LastIndexOf('/'))];
}

/// <summary>函数列表里的一行。<see cref="IsEventGraph"/> 为 true 的是置顶的「事件图」入口。</summary>
public sealed record FunctionItem(string Name, int Size, int Params, bool IsEventGraph = false)
{
    public string Display => IsEventGraph ? "事件图" : $"{Name}   ({Size} B)";
}

/// <summary>
/// 事件图里的一个事件 = 一个入口（调用点）。
/// <see cref="EntryOffset"/> 是它进入 ubergraph 的字节偏移，**不等于**段起始——
/// 事件可以从段中间进入，多个事件共享同一段尾（cooked 的段复用）。
/// </summary>
public sealed record EventItem(int SectionIndex, string Label, int NodeCount, int EntryOffset, int EndOffset, int SharedBy)
{
    public string Display => SharedBy > 1
        ? $"{Label}   ({NodeCount} 节点, 共享段 {SharedBy} 入口)"
        : $"{Label}   ({NodeCount} 节点)";
}

/// <summary>
/// 主窗口 ViewModel：设置 → 索引 → 资产 → 函数/事件 → 图。
///
/// <para>
/// 第一版 UI 的形状（用户 2026-11-07 定）：左栏只有函数列表且「事件图」置顶；
/// 资产从菜单进；事件靠下拉切；双击调用节点跳到被调函数（带返回栈）。
/// </para>
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, KismetWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly Stack<(string Asset, string Function)> _back = new();
    private SymbolIndex? _index;
    private int _loadVersion;
    private IReadOnlyList<UberSectionNode> _sections = [];
    private string _ubergraphName = string.Empty;

    public MainViewModel(string? repoRoot = null)
    {
        RepoRoot = repoRoot ?? FindRepoRoot();
        SettingsPath = StudioSettings.DefaultPath(RepoRoot);
        Settings = StudioSettings.Load(SettingsPath);
        Profile = GameProfile.FromSettings(Settings);
        JumpCommand = new RelayCommand(p => JumpToCallee(p as GraphNodeVm));
        Status = "启动中…";
    }

    public string RepoRoot { get; }
    public string SettingsPath { get; }
    public StudioSettings Settings { get; private set; }
    public GameProfile Profile { get; private set; }

    public System.Windows.Input.ICommand JumpCommand { get; }

    public ObservableCollection<AssetItem> Assets { get; } = [];
    public ObservableCollection<FunctionItem> Functions { get; } = [];
    public ObservableCollection<EventItem> Events { get; } = [];
    public ObservableCollection<GraphNodeVm> Nodes { get; } = [];
    public ObservableCollection<ConnectionVm> Connections { get; } = [];

    #region 状态

    private string _currentAsset = string.Empty;
    public string CurrentAsset
    {
        get => _currentAsset;
        private set { _currentAsset = value; OnPropertyChanged(); }
    }

    private FunctionItem? _selectedFunction;
    public FunctionItem? SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (ReferenceEquals(_selectedFunction, value)) return;
            _selectedFunction = value;
            OnPropertyChanged();
            if (value is not null) _ = LoadFunctionAsync(value);
        }
    }

    private EventItem? _selectedEvent;
    public EventItem? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (ReferenceEquals(_selectedEvent, value)) return;
            _selectedEvent = value;
            OnPropertyChanged();
            if (value is not null) RenderEvent(value);
        }
    }

    private GraphNodeVm? _selectedNode;
    public GraphNodeVm? SelectedNode
    {
        get => _selectedNode;
        set { _selectedNode = value; OnPropertyChanged(); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _diagnostics = "未加载";
    public string Diagnostics
    {
        get => _diagnostics;
        set { _diagnostics = value; OnPropertyChanged(); }
    }

    public bool HasEvents => Events.Count > 0;
    public bool CanGoBack => _back.Count > 0;

    #endregion

    #region 启动与资产

    public async Task InitializeAsync(string? indexPath = null, string? startupFunction = null, string? startupAsset = null)
    {
        try
        {
            var path = FindIndex(indexPath);
            if (!File.Exists(path))
            {
                Status = $"找不到索引 {path}——先跑 `solstice-winter index build`";
                return;
            }

            Status = "加载索引…";
            var (index, elapsed) = await Task.Run(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var loaded = SymbolIndex.Load(path);
                watch.Stop();
                return (loaded, watch.ElapsedMilliseconds);
            });

            _index = index;
            UiLog.Write($"索引已加载 {index.Assets.Count} 资产，来自 {path}");

            Assets.Clear();
            foreach (var asset in index.Assets
                         .Where(a => a.Functions.Any(f => f.HasBytecode))
                         .OrderBy(a => a.VirtualPath, StringComparer.Ordinal))
            {
                Assets.Add(new AssetItem(asset.VirtualPath, asset.ClassName,
                    asset.Functions.Count(f => f.HasBytecode)));
            }

            Status = $"索引已加载：{Assets.Count} 资产（{elapsed} ms）";
            Diagnostics = index.Describe();

            var start = Settings.RecentAssets.FirstOrDefault(p => Assets.Any(a => a.VirtualPath == p))
                        ?? Assets.FirstOrDefault(a => a.VirtualPath.EndsWith("/BP_Logic", StringComparison.Ordinal))?.VirtualPath
                        ?? Assets.OrderByDescending(a => a.FunctionCount).FirstOrDefault()?.VirtualPath;

            // 指定资产优先（--open / --screenshot 会传进来）
            if (!string.IsNullOrEmpty(startupAsset) && Assets.Any(a => a.VirtualPath == startupAsset))
                OpenAsset(startupAsset);
            else if (start is not null)
                OpenAsset(start);

            // 启动时可直接定位到某个函数（调试/验证用）
            if (!string.IsNullOrEmpty(startupFunction))
            {
                var target = Functions.FirstOrDefault(f => f.Name == startupFunction);
                if (target is not null) SelectedFunction = target;
            }
        }
        catch (Exception e)
        {
            Status = $"加载索引失败：{e.GetType().Name}: {e.Message}";
            UiLog.Write($"加载索引失败：{e.GetType().Name}: {e.Message}");
        }
    }

    private static UAssetAPI.Unversioned.Usmap? _usmap;

    /// <summary>usmap（结构体字段表）；加载失败就返回 null，类型推断退化但不影响反编译。</summary>
    private static UAssetAPI.Unversioned.Usmap? UsmapOf()
    {
        if (_usmap is not null) return _usmap;
        try
        {
            var path = GameProfile.Kards().UsmapPath;
            return path is { Length: > 0 } && File.Exists(path)
                ? _usmap = new UAssetAPI.Unversioned.Usmap(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开一个资产（菜单 / 最近列表 / 选择器都走这里）。</summary>
    public void OpenAsset(string virtualPath, bool pushBack = false)
    {
        if (_index is null) return;
        if (pushBack && CurrentAsset.Length > 0) _back.Push((CurrentAsset, string.Empty));

        CurrentAsset = virtualPath;
        Settings.RememberAsset(virtualPath);
        SaveSettingsQuietly();

        Functions.Clear();
        Events.Clear();
        Nodes.Clear();
        Connections.Clear();

        var header = _index.Find(virtualPath);
        if (header is null)
        {
            Status = $"索引里没有 {virtualPath}";
            return;
        }

        Functions.Add(new FunctionItem("事件图", 0, 0, IsEventGraph: true));
        foreach (var fn in header.Functions.Where(f => f.HasBytecode).OrderBy(f => f.Name, StringComparer.Ordinal))
            Functions.Add(new FunctionItem(fn.Name, fn.BytecodeSize, fn.Params.Count));

        UiLog.Write($"OpenAsset {virtualPath}: 函数 {Functions.Count - 1} 个");
        Status = $"{virtualPath}：{Functions.Count - 1} 个函数";
        OnPropertyChanged(nameof(CanGoBack));

        SelectedFunction = Functions.FirstOrDefault();
    }

    /// <summary>
    /// 从**本地文件**打开资产（文件菜单「从本地打开」）。
    ///
    /// <para>
    /// 与 <see cref="OpenAsset"/> 的区别：不走索引。资产不在导出目录里也能开，
    /// 函数列表直接从 <c>UAsset</c> 的导出表枚举（索引那套在这里没有条目可用）。
    /// </para>
    ///
    /// <para>
    /// 代价：签名/变量类型仍按名字查索引，查不到就退回 `argN` 和 Unknown 类型。
    /// 这是数据缺失，不是打开方式的缺陷——把文件放进导出目录再从 VFS 开即可恢复。
    /// </para>
    /// </summary>
    public void OpenLocalAsset(string uassetPath)
    {
        if (CurrentAsset.Length > 0) _back.Push((CurrentAsset, string.Empty));

        // 虚拟路径只是显示用的占位：本地文件没有 /Game 路径
        var virtualPath = "/Game/" + Path.GetFileNameWithoutExtension(uassetPath);

        Functions.Clear();
        Events.Clear();
        Nodes.Clear();
        Connections.Clear();

        KismetWorkspace workspace;
        try
        {
            workspace = AssetAccess.OpenWorkspaceFromFile(Profile, uassetPath);
        }
        catch (Exception e)
        {
            Status = $"本地打开失败：{e.Message}";
            UiLog.Write($"OpenLocalAsset 失败 {uassetPath}: {e.GetType().Name}: {e.Message}");
            return;
        }

        _workspaces[virtualPath] = workspace;
        CurrentAsset = virtualPath;

        Functions.Add(new FunctionItem("事件图", 0, 0, IsEventGraph: true));

        if (AssetAccess.GetAsset(workspace) is { } asset)
        {
            foreach (var export in asset.Exports.OfType<UAssetAPI.ExportTypes.FunctionExport>()
                         .Where(f => f.ScriptBytecode is { Length: > 0 })
                         .OrderBy(f => f.ObjectName?.ToString(), StringComparer.Ordinal))
            {
                // `LoadedProperties` 是 IEnumerable<Property>：`Count` 只是 LINQ 扩展方法，
                // 写成 `.Count` 会被当成方法组（CS0173/CS8978）。用 `.Count()`。
                var parameterCount = export.LoadedProperties is { } loaded ? loaded.Count() : 0;

                Functions.Add(new FunctionItem(
                    export.ObjectName?.ToString() ?? "?",
                    export.ScriptBytecode?.Length ?? 0,
                    parameterCount));
            }
        }
        else
        {
            // 反射拿不到 UAsset 时退回工作区的函数名（少了大小信息，但不至于空列表）
            foreach (var name in workspace.Functions.OrderBy(n => n, StringComparer.Ordinal))
                Functions.Add(new FunctionItem(name, 0, 0));
        }

        OnPropertyChanged(nameof(CanGoBack));
        Status = $"{Path.GetFileName(uassetPath)}（本地）：{Functions.Count - 1} 个函数";
        UiLog.Write($"OpenLocalAsset {uassetPath} → {virtualPath}: 函数 {Functions.Count - 1} 个");

        SelectedFunction = Functions.FirstOrDefault();
    }

    public bool GoBack()
    {
        if (_back.Count == 0) return false;
        var (asset, function) = _back.Pop();
        OpenAsset(asset);
        if (!string.IsNullOrEmpty(function))
        {
            var target = Functions.FirstOrDefault(f => f.Name == function);
            if (target is not null) SelectedFunction = target;
        }

        OnPropertyChanged(nameof(CanGoBack));
        return true;
    }

    #endregion

    #region 函数与事件

    private async Task LoadFunctionAsync(FunctionItem function)
    {
        var version = ++_loadVersion;
        var asset = CurrentAsset;

        // 普通函数没有「段」的概念：直接走新反编译器，不碰旧的 ws.Decompile
        // （旧路径在大函数上会抛异常/极慢，导致画布空白——实测 ExecuteAttackCard 就卡在这）
        if (!function.IsEventGraph)
        {
            RenderFunction(function.Name);
            return;
        }

        try
        {
            Status = $"反编译 {function.Display}…";
            var (sections, entries, note, uberName) = await Task.Run(() =>
            {
                var ws = GetWorkspace(asset);
                var name = function.IsEventGraph ? UbergraphNameOf(ws) : function.Name;
                var top = ws.Decompile(name);
                if (top.OfType<UberGraphNode>().FirstOrDefault() is not { } uber)
                    return (new List<UberSectionNode>(), new List<(int Offset, string Label)>(), string.Empty, name);

                var sites = function.IsEventGraph ? ws.UbergraphCallSites(name) : [];
                var entries = sites
                    .Select(s => (s.Offset, s.CallerFunction))
                    .OrderBy(s => s.Offset)
                    .ToList();

                return (uber.Sections, entries, $"ubergraph {uber.Sections.Count} 段 / {entries.Count} 个入口", name);
            });

            if (version != _loadVersion) return;

            _sections = sections;
            _ubergraphName = function.IsEventGraph ? uberName : string.Empty;
            Events.Clear();

            if (entries.Count > 0)
            {
                // 只列有入口的段（D14）。入口偏移不要求等于段起始——
                // 事件可以从段中间进入，所以按「入口落在哪一段」归属。
                var shared = entries
                    .GroupBy(e => SectionIndexOf(sections, e.Offset))
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var (offset, caller) in entries)
                {
                    var index = SectionIndexOf(sections, offset);
                    if (index < 0) continue;
                    Events.Add(new EventItem(index, caller, sections[index].Nodes.Count, offset,
                        SectionEnd(sections, index), shared[index]));
                }
            }
            else
            {
                for (var i = 0; i < sections.Count; i++)
                {
                    var section = sections[i];
                    Events.Add(new EventItem(i,
                        string.IsNullOrEmpty(section.Label) ? $"段 {i}" : section.Label,
                        section.Nodes.Count,
                        (int)section.StartOffset,
                        SectionEnd(sections, i),
                        1));
                }
            }

            UiLog.Write($"LoadFunction {function.Display}: sections={_sections.Count} events={Events.Count}");
            OnPropertyChanged(nameof(HasEvents));
            Status = $"{function.Display}：{Events.Count} 个事件{(note.Length > 0 ? $"（{note}）" : string.Empty)}";

            // 普通函数（不是事件图）没有「段」的概念：直接按整函数渲染
            if (!function.IsEventGraph)
            {
                RenderFunction(function.Name);
                return;
            }

            // 这个资产没有事件图（或事件图里没有可解析的段）：
            // 自动退到第一个普通函数，而不是留一张空画布让人以为坏了。
            if (Events.Count == 0 && function.IsEventGraph)
            {
                var fallback = Functions.FirstOrDefault(f => !f.IsEventGraph);
                if (fallback is not null)
                {
                    Status = $"{CurrentAsset}：没有事件图，已打开 {fallback.Name}";
                    SelectedFunction = fallback;
                    return;
                }

                Status = $"{CurrentAsset}：没有事件图，也没有可打开的函数";
                ClearGraph();
                return;
            }

            SelectedEvent = Events.FirstOrDefault();
            if (SelectedEvent is null) ClearGraph();
        }
        catch (Exception e)
        {
            if (version != _loadVersion) return;
            Status = $"反编译失败：{e.GetType().Name}: {e.Message}";
            UiLog.Write($"反编译失败：{function.Name}: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>当前资产里的 ubergraph 函数名（`ExecuteUbergraph_` 前缀）。</summary>
    private string UbergraphNameOf(KismetWorkspace ws) =>
        ws.Functions.FirstOrDefault(f => f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("这个资产没有 ubergraph");

    /// <summary>入口偏移落在哪一段：取「起始偏移 &lt;= 目标」里最大的那个段。</summary>
    private static int SectionIndexOf(IReadOnlyList<UberSectionNode> sections, int offset)
    {
        var best = -1;
        for (var i = 0; i < sections.Count; i++)
            if (sections[i].StartOffset <= offset) best = i;
        return best;
    }

    private static int SectionEnd(IReadOnlyList<UberSectionNode> sections, int index) =>
        index + 1 < sections.Count ? (int)sections[index + 1].StartOffset : int.MaxValue;

    /// <summary>
    /// 渲染一个事件：**按入口偏移切片**，只显示这个事件自己的那段。
    /// 切片失败（反射不可用 / 偏移落在指令中间）时退回整段，并在状态栏说明。
    /// </summary>
    private string _lastRenderKey = string.Empty;

    /// <summary>最近一次新反编译器的失败原因（失败时不回退旧投影，直接把原因显示出来）。</summary>
    private string _lastDecompilerError = string.Empty;

    private void RenderEvent(EventItem item)
    {
        // 同一张图重复渲染是纯浪费（实测会被触发 2~3 次），用 key 挡掉
        var key = $"{CurrentAsset}|{SelectedFunction?.Name}|{item.SectionIndex}|{item.EntryOffset}|" +
                  $"{Settings.Layout.ColumnWidth}|{Settings.Display.TitleStyle}";
        UiLog.Write($"RenderEvent 进入 key={key}");
        if (key == _lastRenderKey && Nodes.Count > 0) return;
        _lastRenderKey = key;

        if (item.SectionIndex < 0 || item.SectionIndex >= _sections.Count)
        {
            ClearGraph();
            return;
        }

        // 优先走新反编译器（移植的 UEBlueprintGraphViewer）。
        // **失败时绝不回退旧投影**：旧投影画出来的图看起来完全正常，会让人以为「图就是这样」，
        // 实际是新反编译器崩了（上一轮就是这么被瞒过去的：47 节点变成 52 节点的单行图）。
        if (Settings.Display.UseNewDecompiler)
        {
            if (TryRenderWithNewDecompiler(item)) return;

            ClearGraph();
            Status = $"反编译失败：{_lastDecompilerError}";
            Diagnostics = $"{Status}　画布已清空（未回退旧投影，避免显示一张看似正常的错图）";
            return;
        }

        var section = _sections[item.SectionIndex];
        IReadOnlyList<KNode>? slice = null;
        if (_ubergraphName.Length > 0)
            slice = SectionSlicer.Slice(GetWorkspace(CurrentAsset), _ubergraphName, item.EntryOffset, item.EndOffset);

        if (slice is { Count: 0 }) slice = null;   // 空切片按失败处理，回退整段
        var nodes = slice ?? section.Nodes;
        var (graphNodes, connections) = GraphBuilder.Build(nodes, BuildOptions());
        ApplyGraph(graphNodes, connections);


        var how = slice is not null
            ? $"入口 @{item.EntryOffset}"
            : $"整段（切片失败，段起始 @{section.StartOffset}）";
        var sharedNote = item.SharedBy > 1 ? $"，共享段（{item.SharedBy} 个入口进入）" : string.Empty;
        Status = $"{item.Label}：{graphNodes.Count} 节点 / {connections.Count} 连线（{how}{sharedNote}）";
    }

    /// <summary>渲染一个普通函数（没有段）：从函数开头走，不按入口偏移切片。</summary>
    private void RenderFunction(string functionName)
    {
        if (Settings.Display.UseNewDecompiler && TryRenderWithNewDecompiler(functionName, null, functionName))
            return;

        // 回退：旧投影
        var ws = GetWorkspace(CurrentAsset);
        var roots = ws.Decompile(functionName);
        var (graphNodes, connections) = GraphBuilder.Build(roots, BuildOptions());
        ApplyGraph(graphNodes, connections);
        Status = $"{functionName}：{graphNodes.Count} 节点 / {connections.Count} 连线（旧投影）";
    }

    /// <summary>
    /// 用新反编译器渲染：字节码 → 基本块 → UE 式节点 → 画布 VM。
    /// 返回 false 表示这条路不通（拿不到 UAsset / 没有节点 / 抛异常），调用方回退旧投影。
    /// </summary>
    private bool TryRenderWithNewDecompiler(EventItem item) =>
        TryRenderWithNewDecompiler(
            _ubergraphName.Length > 0 ? _ubergraphName : SelectedFunction?.Name ?? string.Empty,
            _ubergraphName.Length > 0 ? item.EntryOffset : null,
            item.Label);

    private bool TryRenderWithNewDecompiler(string functionName, int? startOffsetValue, string label)
    {
        try
        {
            var ws = GetWorkspace(CurrentAsset);
            var asset = AssetAccess.GetAsset(ws);
            if (asset is null) return false;

            if (string.IsNullOrEmpty(functionName)) return false;

            var function = AssetAccess.FindFunction(asset, functionName);
            if (function is null) return false;

            var entryPoints = _ubergraphName.Length > 0
                ? ws.UbergraphCallSites(_ubergraphName).Select(s => (uint)s.Offset).ToList()
                : [];
            uint? startOffset = startOffsetValue is { } s ? (uint)s : null;

            var signatures = _index is null ? null : new IndexSignatures(_index);
                                    var variableTypes = _index is null ? null : new IndexVariableTypes(_index, UsmapOf());
            var result = GraphCompiler.Build(asset, function, entryPoints, startOffset, 4000, signatures, variableTypes);
            if (result.Nodes.Count == 0) return false;

            GraphAdapter.ShowPinTypes = Settings.Display.ShowPinTypes;
            var (nodes, connections) = GraphAdapter.ToView(result.Nodes);
            ApplyGraph(nodes, connections);

            var problems = result.Problems.Count > 0 ? $"，{result.Problems.Count} 个问题" : string.Empty;
            Status = $"{label}：新反编译器 {nodes.Count} 节点 / {connections.Count} 连线{problems}";
            UiLog.Write($"新反编译器 {label}: {nodes.Count} 节点 / {connections.Count} 连线，问题 {result.Problems.Count}");
            return true;
        }
        catch (Exception e)
        {
            UiLog.Write($"新反编译器失败：{e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}");
            _lastDecompilerError = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    private void ClearGraph()
    {
        Nodes.Clear();
        Connections.Clear();
        SelectedNode = null;
    }

    private GraphBuilder.Options BuildOptions() => new(
        Settings.Layout.ColumnWidth,
        Settings.Layout.RowHeight,
        Settings.Layout.DataOffsetY,
        string.Equals(Settings.Display.TitleStyle, "pretty", StringComparison.OrdinalIgnoreCase),
        name => _index?.FindScriptFunction(name).FirstOrDefault());

    private void ApplyGraph(IReadOnlyList<GraphNodeVm> nodes, IReadOnlyList<ConnectionVm> connections)
    {
        // `Nodes` / `Connections` 是绑定到 UI 的 ObservableCollection：从后台线程改它们，
        // WPF 会抛「Operations that change non-concurrent collections must have exclusive access」，
        // 而外层 catch 会把它吞成「新反编译器失败（回退旧投影）」——于是画布上显示的是**旧投影**，
        // 看起来像图本身坏了（实测跨度 10520×780 的单行图就是这么来的）。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyGraph(nodes, connections));
            return;
        }

        Nodes.Clear();
        Connections.Clear();
        SelectedNode = null;

        foreach (var node in nodes)
        {
            node.PropertyChanged += OnNodePropertyChanged;
            Nodes.Add(node);
        }

        foreach (var connection in connections) Connections.Add(connection);

        Diagnostics = $"图 {nodes.Count} 节点 / {connections.Count} 连线   " +
                      $"布局 {Settings.Layout.ColumnWidth:0}/{Settings.Layout.RowHeight:0}   " +
                      $"标题 {(string.Equals(Settings.Display.TitleStyle, "pretty", StringComparison.OrdinalIgnoreCase) ? "美化" : "内部名")}";
        UiLog.Write($"已渲染 {CurrentAsset} / {SelectedEvent?.Label ?? SelectedFunction?.Name}: {nodes.Count} 节点 / {connections.Count} 连线");
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeVm.IsSelected) && sender is GraphNodeVm node && node.IsSelected)
            SelectedNode = node;
    }

    #endregion

    #region 跳转

    /// <summary>
    /// 双击调用节点：跳到被调函数。先在当前资产里找，再按索引跨资产找。
    /// 找不到就明说，不静默。
    /// </summary>
    public bool JumpToCallee(GraphNodeVm? node)
    {
        if (node?.CalleeName is not { Length: > 0 } callee || _index is null) return false;

        var here = _index.Find(CurrentAsset);
        if (here is not null && here.Functions.Any(f => f.Name == callee && f.HasBytecode))
        {
            var target = Functions.FirstOrDefault(f => f.Name == callee);
            if (target is not null)
            {
                _back.Push((CurrentAsset, string.Empty));
                SelectedFunction = target;
                OnPropertyChanged(nameof(CanGoBack));
                return true;
            }
        }

        var hit = _index.FindFunction(callee).FirstOrDefault(h => h.Function.HasBytecode);
        if (hit.Asset is null)
        {
            Status = $"跳转失败：索引里没有带字节码的 {callee}";
            return false;
        }

        _back.Push((CurrentAsset, string.Empty));
        OpenAsset(hit.Asset.VirtualPath);
        var targetFn = Functions.FirstOrDefault(f => f.Name == callee);
        if (targetFn is not null) SelectedFunction = targetFn;
        OnPropertyChanged(nameof(CanGoBack));
        return true;
    }

    #endregion

    #region 设置

    /// <summary>改完设置后调：重新按新参数排一遍当前图（不重启）。</summary>
    public void ApplySettings()
    {
        Profile = GameProfile.FromSettings(Settings);
        SaveSettingsQuietly();

        if (SelectedEvent is not null) RenderEvent(SelectedEvent);
        Status = "设置已应用";
    }

    /// <summary>
    /// 用设置窗口编辑好的副本替换当前设置。窗口里改的是 <see cref="StudioSettings.Clone"/> 出来的副本，
    /// 点「取消」时这份替换就不会发生——所以这里只负责「接管 + 落盘 + 重排」。
    /// </summary>
    public void ReplaceSettings(StudioSettings replacement)
    {
        Settings = replacement;
        ApplySettings();
    }

    public void ReloadSettings()
    {
        Settings = StudioSettings.Load(SettingsPath);
        Profile = GameProfile.FromSettings(Settings);
        ApplySettings();
    }

    private void SaveSettingsQuietly()
    {
        try { Settings.Save(SettingsPath); } catch { /* 配置写不进去不影响使用 */ }
    }

    #endregion

    private KismetWorkspace GetWorkspace(string assetPath)
    {
        if (_workspaces.TryGetValue(assetPath, out var cached)) return cached;

        var ws = AssetAccess.OpenWorkspace(Profile, assetPath);
        _workspaces[assetPath] = ws;
        return ws;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Solstice-winter.slnx"))) return dir.FullName;
        }

        return Environment.CurrentDirectory;
    }

    private static string FindIndex(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        return Path.Combine(FindRepoRoot(), ".cache", "index.json");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>极简 ICommand：够用就行，不引第三方 MVVM 框架。</summary>
internal sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>
/// UI 运行日志：模型不支持读图，所以「界面真的渲染了」只能靠这行字证明。
/// 写到 %TEMP%\solstice-winter-ui\ui.log，不参与任何业务逻辑。
internal static class UiLog
{

    /// <summary>
    /// 把 WPF 的**数据绑定错误**接进日志。
    ///
    /// <para>
    /// 绑定失败在 WPF 里是静默的：只写调试输出，界面上看不出来。
    /// `GraphView.xaml` 里「两段 Setter 互相覆盖」那个 bug 就是靠肉眼找不到的。
    /// 接进日志后，`--open` 跑一次就能看到有没有绑定报错。
    /// </para>
    /// </summary>
    public static void AttachBindingTrace()
    {
        try
        {
            System.Diagnostics.PresentationTraceSources.Refresh();
            var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(new BindingLogListener());
            source.Switch.Level = System.Diagnostics.SourceLevels.Error
                                     | System.Diagnostics.SourceLevels.Warning;

            // 记一行「挂上了」——否则日志里 0 条绑定错误无法区分「没问题」和「没挂上」。
            Write("绑定追踪已启用（Error+Warning）");
        }
        catch (Exception e)
        {
            Write($"绑定追踪挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    private sealed class BindingLogListener : System.Diagnostics.TraceListener
    {
        public override void Write(string? message) { }

        // 绑定错误一条就是一行，`WriteLine` 才带换行
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Write($"[绑定] {message.Trim()}");
        }
    }

    // 写到仓库 .cache（沙箱里 %TEMP% 每个进程都不同，日志会随进程消失）
    private static readonly string Path = ResolvePath();

    private static string ResolvePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Solstice-winter.slnx")))
            dir = dir.Parent;

        var root = dir?.FullName ?? System.IO.Path.GetTempPath();
        return System.IO.Path.Combine(root, ".cache", "ui.log");
    }

    public static void Write(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响 UI
        }
    }
}





















