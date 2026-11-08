using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SolsticeWinter.App.ViewModels;

/// <summary>
/// 一个引脚。
///
/// <para>
/// <see cref="Anchor"/> 由画布控件回填（Nodify 的 <c>Connector.Anchor</c> 用 <c>OneWayToSource</c> 绑过来），
/// 所以图构建阶段不需要算坐标——这一点让「换画布库」的成本很低。
/// </para>
/// </summary>
public sealed class ConnectorVm : INotifyPropertyChanged
{
    private Point _anchor;
    private bool _isConnected;

    public required string Title { get; set; }
    public required bool IsInput { get; init; }
    public bool IsExec { get; init; }
    public string TypeName { get; init; } = "unknown";
    public bool IsUnknown => TypeName is "unknown" or "";

    /// <summary>未连线时显示的默认值/字面量（UE 编辑器里引脚上那个小方框）。</summary>
    public string Value { get; init; } = string.Empty;
    public bool HasValue => Value.Length > 0;

    /// <summary>是否显示类型文字（设置里的「显示引脚类型」）。</summary>
    public bool ShowType { get; init; }
    public Brush Brush { get; init; } = Brushes.Gray;

    public Point Anchor
    {
        get => _anchor;
        set { _anchor = value; OnPropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一条连线。<see cref="Output"/> 是上游引脚，<see cref="Input"/> 是下游引脚。</summary>
public sealed class ConnectionVm(ConnectorVm output, ConnectorVm input)
{
    public ConnectorVm Output { get; } = output;
    public ConnectorVm Input { get; } = input;

    public ConnectionVm(ConnectorVm output, ConnectorVm input, bool isExec) : this(output, input)
    {
        IsExec = isExec;
        output.IsConnected = true;
        input.IsConnected = true;
    }

    public bool IsExec { get; }
}

/// <summary>一个画布节点。</summary>
public sealed class GraphNodeVm : INotifyPropertyChanged
{
    private Point _location;

    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>反编译器给的原名（美化前）；tooltip 与「切回内部名」用。</summary>
    public string RawTitle { get; init; } = string.Empty;

    /// <summary>调用节点的被调函数名；双击它跳到被调函数。非调用节点为 null。</summary>
    public string? CalleeName { get; init; }

    /// <summary>节点种类，决定配色与图标：Exec / Call / Data / Branch / Set / Return / Opaque。</summary>
    public string Kind { get; init; } = "Call";

    /// <summary>标题栏颜色。非空时优先于 <see cref="Kind"/> 的调色板查表。</summary>
    public Brush? HeaderBrush { get; init; }

    public string Tooltip { get; init; } = string.Empty;
    public bool IsOpaque { get; init; }

    public Point Location
    {
        get => _location;
        set { _location = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ConnectorVm> Inputs { get; } = [];
    public ObservableCollection<ConnectorVm> Outputs { get; } = [];

    /// <summary>给模板用的合并视图：exec 引脚排前面。</summary>
    public IEnumerable<ConnectorVm> AllInputs => Inputs.OrderBy(p => p.IsExec ? 0 : 1);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}



