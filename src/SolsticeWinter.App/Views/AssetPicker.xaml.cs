using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SolsticeWinter.App.Views;

/// <summary>
/// 资产选择器：输入过滤 + 列表 + 双击打开。
/// 只负责「选出虚拟路径」，不碰索引与工作区（那些在 MainViewModel）。
/// </summary>
public partial class AssetPicker : Window
{
    private readonly IReadOnlyList<AssetItem> _all;

    public AssetPicker(IReadOnlyList<AssetItem> assets, string? preselect = null)
    {
        InitializeComponent();
        _all = assets;
        Apply(string.Empty);

        if (preselect is not null)
            Results.SelectedItem = _all.FirstOrDefault(a => a.VirtualPath == preselect);
    }

    public string? SelectedPath { get; private set; }

    private void Apply(string filter)
    {
        var rows = filter.Length == 0
            ? _all.Take(300).ToList()
            : _all.Where(a => a.VirtualPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(300)
                .ToList();

        Results.ItemsSource = rows;
        Count.Text = $"{rows.Count} / {_all.Count}";
        if (rows.Count > 0) Results.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Apply(Search.Text.Trim());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnOpen(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (Results.SelectedItem is not AssetItem item) return;
        SelectedPath = item.VirtualPath;
        DialogResult = true;
    }
}
