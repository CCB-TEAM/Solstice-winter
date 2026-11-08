using System.IO;
using System.Windows;
using System.Windows.Threading;
using SolsticeWinter.App.Graph;

namespace SolsticeWinter.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "solstice-winter-ui", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 绑定错误在 WPF 里是静默的（只写调试输出），接进 ui.log 才能被 --open 发现。
        UiLog.AttachBindingTrace();

        DispatcherUnhandledException += (_, args) =>
        {
            Log("Dispatcher", args.Exception);
            ShowCompactError(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);

        try
        {
            // 无窗口自检：SolsticeWinter.App.exe --graph <虚拟路径> <函数名>
            if (e.Args.Length >= 4 && e.Args[0] == "--screenshot")
            {
                var window = new MainWindow(null, e.Args[2], e.Args[1]);
                window.Show();

                // 等图渲染完（布局 + Nodify 首帧），再截画布
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(25),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        var editor = window.FindName("Editor") as System.Windows.FrameworkElement
                                     ?? FindEditor(window);
                        if (editor is null)
                        {
                            Environment.ExitCode = 1;
                            Shutdown(1);
                            return;
                        }


                        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)Math.Max(1, editor.ActualWidth), (int)Math.Max(1, editor.ActualHeight),
                            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        target.Render(editor);

                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
                        using (var stream = File.Create(e.Args[3])) encoder.Save(stream);
                        Environment.ExitCode = 0;
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "solstice-winter-shot-error.txt"), ex.ToString());
                        Environment.ExitCode = 1;
                    }

                    Shutdown(Environment.ExitCode);
                };
                timer.Start();
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--outstat")
            {
                Environment.ExitCode = BlockProbe.OutStat(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--locals")
            {
                Environment.ExitCode = BlockProbe.Locals(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 4 && e.Args[0] == "--expr")
            {
                Environment.ExitCode = BlockProbe.Expr(e.Args[1], e.Args[2], uint.Parse(e.Args[3]));
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--open")
            {
                new MainWindow(null, e.Args[2], e.Args[1]).Show();
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--check-source")
            {
                // 端到端验证「数据源 → 读字节 → 反编译」这条路。
                // 第二个参数可选：dir / pak / overlay，用来临时覆盖设置里的数据源（不写盘）。
                try
                {
                    var settings = SolsticeWinter.Core.StudioSettings.Load(
                        SolsticeWinter.Core.StudioSettings.DefaultPath(FindRepoRoot() ?? Path.GetTempPath()));

                    if (e.Args.Length >= 3) settings.Sources.DataSource = e.Args[2];

                    var profile = SolsticeWinter.Core.GameProfile.FromSettings(settings);
                    var source = Graph.AssetAccess.SourceFor(profile);
                    var paths = source.EnumerateVirtualPaths();

                    Console.WriteLine($"mode={profile.DataSource} pak={profile.PakPath ?? "(无)"}");
                    Console.WriteLine($"source={source.Name}");
                    Console.WriteLine($"assets={paths.Count}");

                    var workspace = Graph.AssetAccess.OpenWorkspace(profile, e.Args[1]);
                    var asset = Graph.AssetAccess.GetAsset(workspace);
                    var functions = asset is null
                        ? -1
                        : asset.Exports.OfType<UAssetAPI.ExportTypes.FunctionExport>().Count();

                    Console.WriteLine($"open {e.Args[1]}: functions={functions} names={workspace.Functions.Count}");
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
                    Environment.ExitCode = 1;
                }

                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--check-local")
            {
                // 验证「从本地打开」这条路：直接读一个 .uasset（不走索引、不走导出目录）。
                try
                {
                    var settings = SolsticeWinter.Core.StudioSettings.Load(
                        SolsticeWinter.Core.StudioSettings.DefaultPath(FindRepoRoot() ?? Path.GetTempPath()));
                    var profile = SolsticeWinter.Core.GameProfile.FromSettings(settings);

                    var workspace = Graph.AssetAccess.OpenWorkspaceFromFile(profile, e.Args[1]);
                    var asset = Graph.AssetAccess.GetAsset(workspace);
                    var functionCount = asset is null ? -1 : asset.Exports.OfType<UAssetAPI.ExportTypes.FunctionExport>().Count();

                    Console.WriteLine($"local-open OK: functions={functionCount} names={workspace.Functions.Count}");
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"local-open FAIL: {ex.GetType().Name}: {ex.Message}");
                    Environment.ExitCode = 1;
                }

                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 1 && e.Args[0] == "--check-settings")
            {
                // 冒烟：真的把设置窗口构造一遍。XAML 解析、StaticResource 解析、转换器实例化
                // 都在这条路径上发生——构建通过不代表运行时能加载（资源键写错只在运行时炸）。
                try
                {
                    var dir = new DirectoryInfo(AppContext.BaseDirectory);
                    while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Solstice-winter.slnx")))
                        dir = dir.Parent;

                    var settingsPath = SolsticeWinter.Core.StudioSettings.DefaultPath(
                        dir?.FullName ?? Path.GetTempPath());
                    var settings = SolsticeWinter.Core.StudioSettings.Load(settingsPath);
                    _ = new Views.SettingsWindow(settings, settingsPath);

                    Console.WriteLine("设置窗口构造成功");
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"设置窗口构造失败：{ex.GetType().Name}: {ex.Message}");
                    Environment.ExitCode = 1;
                }

                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--blocks")
            {
                Environment.ExitCode = BlockProbe.Blocks(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--check-all")
            {
                Environment.ExitCode = BlockProbe.CheckAll(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--validate")
            {
                Environment.ExitCode = BlockProbe.Validate(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--argn")
            {
                Environment.ExitCode = BlockProbe.ArgNames(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--vmdump")
            {
                Environment.ExitCode = BlockProbe.VmDump(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--entrypins")
            {
                Environment.ExitCode = BlockProbe.EntryPins(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--macros")
            {
                Environment.ExitCode = BlockProbe.Macros(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--trace")
            {
                Environment.ExitCode = BlockProbe.TraceWalk(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--hist")
            {
                Environment.ExitCode = BlockProbe.Histogram(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--export")
            {
                Environment.ExitCode = BlockProbe.Export(e.Args[1], e.Args[2], e.Args.Length > 3 ? e.Args[3] : null);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--audit")
            {
                Environment.ExitCode = BlockProbe.Audit(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--batch")
            {
                Environment.ExitCode = BlockProbe.Batch(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--walk")
            {
                Environment.ExitCode = BlockProbe.Walk(e.Args[1], e.Args[2], e.Args.Length > 3 ? e.Args[3] : null);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--blocks")
            {
                Environment.ExitCode = BlockProbe.Run(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--check-all")
            {
                Environment.ExitCode = BlockProbe.CheckAll(e.Args[1]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 2 && e.Args[0] == "--validate")
            {
                Environment.ExitCode = GraphProbe.ValidateReport(e.Args[1..]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 4 && e.Args[0] == "--slice")
            {
                Environment.ExitCode = GraphProbe.SliceReport(e.Args[1], e.Args[2], e.Args[3]);
                Shutdown(Environment.ExitCode);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--graph")
            {
                Environment.ExitCode = GraphProbe.Run(e.Args[1], e.Args[2]);
                Shutdown(Environment.ExitCode);
                return;
            }

            Log("Startup", null, $"args=[{string.Join(",", e.Args)}]");
            AssetAccess.CleanStaleWorkDirs();
            new MainWindow(e.Args.FirstOrDefault()).Show();
            Log("Window shown", null);
        }
        catch (Exception ex)
        {
            Log("Startup", ex);
            ShowCompactError(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 只弹一句人话 + 日志路径。整段堆栈写进日志，不糊到屏幕上——
    /// 用户看到一屏红字只会以为工具烂了，看不到真正的那一行原因。
    /// </summary>
    private static System.Windows.FrameworkElement? FindEditor(System.Windows.DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Nodify.NodifyEditor editor) return editor;
            if (FindEditor(child) is { } found) return found;
        }

        return null;
    }

    private static void ShowCompactError(Exception ex)
    {
        var first = ex.GetBaseException();
        var text = $"启动失败：{first.GetType().Name}\n{first.Message}\n\n完整堆栈已写入：\n{LogPath}";
        MessageBox.Show(text, "Solstice-winter", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Log(string where, Exception? ex, string? note = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {where} {note}\n{ex}\n\n");
        }
        catch
        {
            // 日志写不进去就算了，别因为日志再炸一次
        }
    }

    /// <summary>从程序目录向上找 <c>Solstice-winter.slnx</c>，找不到返回 null。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Solstice-winter.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}

























