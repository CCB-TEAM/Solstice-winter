using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SolsticeWinter.App.Views;

namespace SolsticeWinter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(string? indexPath = null, string? startupFunction = null, string? startupAsset = null)
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => OpenAssetDialog()), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _viewModel.GoBack()), Key.Left, ModifierKeys.Alt));

        Loaded += async (_, _) =>
        {
            UiLog.Write($"Loaded 触发，indexPath={indexPath ?? "<null>"}");
            try
            {
                await _viewModel.InitializeAsync(indexPath, startupFunction, startupAsset);
            }
            catch (Exception e)
            {
                UiLog.Write($"InitializeAsync 抛出：{e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                throw;
            }
        };
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 从 **VFS** 打开：资产列表来自索引（导出目录 / pak），带函数签名与变量类型。
    ///
    /// <para>这是原来的「打开…」，改名是为了和「从本地打开」并列——两条路的数据源不同。</para>
    /// </summary>
    private void OnOpenFromVfs(object sender, RoutedEventArgs e) => OpenAssetDialog();

    private void OpenAssetDialog()
    {
        var picker = new AssetPicker(_viewModel.Assets.ToList(), _viewModel.CurrentAsset) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPath is { Length: > 0 } path)
            _viewModel.OpenAsset(path, pushBack: true);
    }

    /// <summary>
    /// 从 **本地磁盘**打开：直接选一个 <c>.uasset</c>，不要求它在导出目录里。
    ///
    /// <para>
    /// 代价是签名/变量类型仍按文件名查索引——文件不在索引里时会退回 `argN` 与 Unknown 类型。
    /// 这是数据缺失，不是打开方式的问题（需要完整信息就放进导出目录后走 VFS）。
    /// </para>
    /// </summary>
    private void OnOpenFromLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "从本地打开 uasset",
            Filter = "UE 资产|*.uasset|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        // .uexp 是必需的（cooked 资产的数据主体），缺了先说清楚再继续
        var uexp = System.IO.Path.ChangeExtension(dialog.FileName, ".uexp");
        if (!System.IO.File.Exists(uexp))
        {
            var answer = MessageBox.Show(
                $"同目录下没有找到 .uexp：\n{uexp}\n\ncooked 资产的大部分数据在 .uexp 里，缺了会解析出空包。\n仍然尝试打开吗？",
                "缺少 .uexp", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;
        }

        _viewModel.OpenLocalAsset(dialog.FileName);
    }

    /// <summary>打开设置窗口（形态参考 FModel：标签页 + 应用/确定/取消）。</summary>
    private void OnOpenSettingsWindow(object sender, RoutedEventArgs e)
    {
        var window = new Views.SettingsWindow(_viewModel.Settings, _viewModel.SettingsPath) { Owner = this };

        // 「应用」不关窗：立刻落盘并重排，方便边看边调
        window.Applied += (_, settings) => _viewModel.ReplaceSettings(settings.Clone());

        if (window.ShowDialog() == true && window.Result is { } result)
            _viewModel.ReplaceSettings(result);
    }

    /// <summary>最近资产是动态的：每次展开菜单重建一次，避免和 Settings 里的列表不同步。</summary>
    private void OnAssetMenuOpened(object sender, RoutedEventArgs e)
    {
        while (AssetMenu.Items.Count > 3) AssetMenu.Items.RemoveAt(3);

        var recent = _viewModel.Settings.RecentAssets.Take(5).ToList();
        if (recent.Count == 0) return;

        foreach (var path in recent)
        {
            var item = new MenuItem { Header = path[(path.LastIndexOf('/') + 1)..], ToolTip = path };
            item.Click += (_, _) => _viewModel.OpenAsset(path, pushBack: true);
            AssetMenu.Items.Add(item);
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => _viewModel.GoBack();

    private void OnResetView(object sender, RoutedEventArgs e) => Graph.ResetView();

    // 字号 / 标题风格都搬进设置窗口了（原来是菜单里的快捷项）。
    // 这里不再保留一份「菜单改设置」的第二条路径——两处都能改会让人分不清哪个生效。

    private void OnOpenLog(object sender, RoutedEventArgs e) => Reveal(
        Path.Combine(Path.GetTempPath(), "solstice-winter-ui", "crash.log"));

    private void OnOpenSettings(object sender, RoutedEventArgs e) => Reveal(_viewModel.SettingsPath);

    private void OnAbout(object sender, RoutedEventArgs e) => MessageBox.Show(
        "Solstice-winter\n\n只读渲染：事件图 / 函数图、事件切换、双击跳转。\n编辑未接。\n\n" +
        $"配置文件：{_viewModel.SettingsPath}",
        "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void Reveal(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"文件还不存在：\n{path}", "Solstice-winter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show($"打不开：{e.Message}", "Solstice-winter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}


