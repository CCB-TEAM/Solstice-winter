using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SolsticeWinter.Core;

namespace SolsticeWinter.App.Views;

/// <summary>
/// 设置窗口。形态参考 FModel 的 Settings：标签页分组 + 底部 应用/确定/取消。
///
/// <para>
/// <b>编辑的是副本</b>（<see cref="StudioSettings.Clone"/>）：点「取消」时原设置原封不动。
/// 「应用」立即把副本交给调用方落盘并重排，「确定」= 应用 + 关闭。
/// </para>
///
/// <para>
/// 绑定直接走 <c>DataContext = 副本</c>，所以 XAML 里是 <c>{Binding Sources.ExportDirectory}</c>
/// 这种路径；省掉一堆 ViewModel 包装属性，代价是这个窗口只能做「字段编辑」。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly StudioSettings _editing;

    /// <summary>点「应用」时触发（带当前编辑副本）。调用方负责落盘和重排。</summary>
    public event EventHandler<StudioSettings>? Applied;

    /// <summary>点「确定」后的最终结果；取消时为 null。</summary>
    public StudioSettings? Result { get; private set; }

    public SettingsWindow(StudioSettings current, string settingsPath)
    {
        InitializeComponent();

        _editing = current.Clone();
        DataContext = _editing;
        SettingsPathText.Text = settingsPath;

        // 没配数据源时让「应用」别误点，避免默认值被当成用户选择
        StatusText.Text = "改完点「应用」立即生效";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = _editing;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // 数值框都用 UpdateSourceTrigger=PropertyChanged，点按钮时值已经在模型里了，
        // 不需要再处理焦点提交（WPF 里「最后编辑的数字被吞掉」就是 LostFocus 引起的）。
        Applied?.Invoke(this, _editing);
        StatusText.Text = $"已应用 {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>文件夹选择（导出目录 / pak 目录 / 游戏目录）。`Tag` 指向要写回的文本框。</summary>
    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TextBox box }) return;

        var dialog = new OpenFolderDialog { Title = "选择目录" };
        if (!string.IsNullOrWhiteSpace(box.Text) && System.IO.Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    /// <summary>文件选择（usmap / jmap / AssetRegistry.bin）。</summary>
    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TextBox box }) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "配置文件|*.usmap;*.json;*.bin|所有文件|*.*",
        };

        var current = box.Text;
        if (!string.IsNullOrWhiteSpace(current) && System.IO.File.Exists(current))
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);

        if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
    }
}

