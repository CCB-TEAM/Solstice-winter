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

    private void OnOpenAsset(object sender, RoutedEventArgs e) => OpenAssetDialog();

    private void OpenAssetDialog()
    {
        var picker = new AssetPicker(_viewModel.Assets.ToList(), _viewModel.CurrentAsset) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPath is { Length: > 0 } path)
            _viewModel.OpenAsset(path, pushBack: true);
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

    private void OnFontUp(object sender, RoutedEventArgs e) => BumpFont(+1);
    private void OnFontDown(object sender, RoutedEventArgs e) => BumpFont(-1);

    private void BumpFont(double delta)
    {
        _viewModel.Settings.Display.FontSize = Math.Clamp(_viewModel.Settings.Display.FontSize + delta, 8, 24);
        _viewModel.ApplySettings();
    }

    private void OnTitlePretty(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.Display.TitleStyle = "pretty";
        _viewModel.ApplySettings();
    }

    private void OnTitleRaw(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.Display.TitleStyle = "raw";
        _viewModel.ApplySettings();
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e) => _viewModel.ReloadSettings();

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


