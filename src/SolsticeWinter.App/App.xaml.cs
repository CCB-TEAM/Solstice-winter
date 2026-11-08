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
}

























