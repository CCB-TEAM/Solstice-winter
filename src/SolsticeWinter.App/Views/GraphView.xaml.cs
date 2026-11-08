using System.Windows;
using System.Windows.Controls;
using SolsticeWinter.App.ViewModels;

namespace SolsticeWinter.App.Views;

public partial class GraphView : UserControl
{
    public GraphView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    /// <summary>视图菜单「重置视图」：回到原点、100% 缩放。</summary>
    public void ResetView()
    {
        Editor.ViewportLocation = new Point(0, 0);
        Editor.ViewportZoom = 1;
    }

    /// <summary>
    /// 把视口缩放到能看见全部节点。
    ///
    /// <para>
    /// UE 编辑器打开图表时也会 fit。这里尤其必要：实测 28 个分支的函数布局跨度约 21000×7700，
    /// 不 fit 的话默认视口在 (0,0)，打开只看到一片空白（截图全黑就是这个原因）。
    /// </para>
    /// </summary>
    public void FitToView()
    {
        if (DataContext is not MainViewModel vm || vm.Nodes.Count == 0) return;
        if (Editor.ActualWidth < 10 || Editor.ActualHeight < 10) return;

        var minX = vm.Nodes.Min(n => n.Location.X);
        var minY = vm.Nodes.Min(n => n.Location.Y);
        var maxX = vm.Nodes.Max(n => n.Location.X + 320);
        var maxY = vm.Nodes.Max(n => n.Location.Y + 220);

        var key = $"{vm.Nodes.Count}|{(int)(maxX - minX)}|{(int)(maxY - minY)}|{(int)Editor.ActualWidth}";
        if (key == _lastFitKey) return;
        _lastFitKey = key;

        var zoom = Math.Min(Editor.ActualWidth / Math.Max(1, maxX - minX),
                            Editor.ActualHeight / Math.Max(1, maxY - minY));

        Editor.ViewportZoom = Math.Clamp(zoom * 0.92, 0.02, 1.0);
        Editor.ViewportLocation = new Point(minX - 40, minY - 40);

        UiLog.Write($"适配视图: 节点 {vm.Nodes.Count} 跨度 {(int)(maxX - minX)}x{(int)(maxY - minY)} zoom={Editor.ViewportZoom:0.000} 视口=({(int)Editor.ViewportLocation.X},{(int)Editor.ViewportLocation.Y})");
    }
    private string _lastFitKey = string.Empty;

    private void HookViewModel()
    {
        if (DataContext is not MainViewModel vm) return;

        // Nodes 是同一个集合（Clear + Add），属性不会变，所以监听 CollectionChanged。
        // 渲染完成后延迟一拍再 fit——此时 Nodify 的 ItemContainer 已经量过尺寸。
        vm.Nodes.CollectionChanged += (_, _) =>
        {
            if (vm.Nodes.Count == 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FitToView();
                Dispatcher.BeginInvoke(new Action(FitToView), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
    }
}


