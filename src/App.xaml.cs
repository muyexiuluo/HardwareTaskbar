// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 主入口：托盘 + 悬浮窗 + 后台刷新接线
// 规则：
//   - 后台线程读硬件（HardwareMonitor），UI 线程只消费 HardwareState（规则 3/8）
//   - 托盘图标 + 右键菜单（设置/暂停/退出），不注入 Explorer、不 Hook 任务栏
//   - Explorer 崩溃重启 → WinEvent 监听 Explorer 进程新建 → 重建托盘图标（规则 9）
// 用法：
//   HardwareTaskbar.exe                  正常启动（托盘 + 悬浮窗）
//   HardwareTaskbar.exe --selftest       控制台自检一轮后退出，结果写 selftest.log（验收用）
using System;
using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using HardwareTaskbar.Models;
using HardwareTaskbar.Services;
using HardwareTaskbar.ViewModels;
using HardwareTaskbar.Views;
// WPF 与 WinForms 同名类型歧义（UseWindowsForms=true 隐式 using System.Windows.Forms）
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace HardwareTaskbar;

public partial class App : System.Windows.Application
{
    private static readonly Mutex _instanceMutex =
        new(true, @"Local\HardwareTaskbar_SingleInstance", out _isNewInstance);
    private static readonly bool _isNewInstance;

    public static App? Instance { get; private set; }

    private readonly ConfigService _config = new();
    private HardwareMonitor? _monitor;
    private HardwareState? _lastState; // monitor 线程产出的最新快照（探针/渲染用）
    private MainViewModel? _vm;
    private MainWindow? _window;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayStealthItem; // 2026-09-13：提为字段，供 OnStartup 里的文字菜单同步回调使用
    private SettingsWindow? _settingsWindow;
    private bool _stealth;
    private bool _exiting;
    private DateTime _startUtc;
    private bool _firstFrameLogged;
    private System.Windows.Threading.DispatcherTimer? _watchdogTimer; // 1s 心跳：Explorer 崩溃恢复 + 控制通道 + 探针

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 控制台自检模式（不启动 WPF/托盘）----
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            Environment.Exit(0);
        }

        if (!_isNewInstance)
        {
            MessageBox.Show("HardwareTaskbar 已在运行（看系统托盘）。", "硬件任务栏监控",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Instance = this;
        _startUtc = DateTime.UtcNow;

        // 全局异常兑底：写入 launch-error.log，便于无控制台诊断
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"),
                $"Dispatcher: {args.Exception}"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"),
                $"AppDomain: {args.ExceptionObject}"); } catch { }
        };
        System.Windows.Forms.Application.ThreadException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"),
                $"WinForms: {args.Exception}"); } catch { }
        };

        // 1) 配置
        _config.Load();
        _config.StartWatching();
        _config.Changed += cfg =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_monitor != null) _monitor.RefreshIntervalMs = cfg.RefreshInterval;
                _vm?.ApplySettings(cfg);
                _settingsWindow?.SyncFrom(cfg);
            });

        // 2) ViewModel + 悬浮窗
        _vm = new MainViewModel();
        _vm.ApplySettings(_config.Current);
        _window = new MainWindow(_vm);
        // 文字右键菜单接线（2026-09-13：与托盘共用同一套状态/入口）
        _window.OpenSettingsRequested += () => Dispatcher.BeginInvoke(new Action(OpenSettings));
        _window.ExitRequested += () => Dispatcher.BeginInvoke(new Action(ExitApp));
        _window.StealthChangedFromMenu += on =>
        {
            _stealth = on;
            _trayStealthItem.Checked = on; // 托盘勾选态同步
            _window.SetStealth(on);
        };

        // 3) 后台刷新线程（唯一数据泵；LHM 初始化在后台线程懒加载，不拖慢窗口显示）
        _monitor = new HardwareMonitor(_config.Current.RefreshInterval);
        _monitor.StateChanged += (_, state) =>
            Dispatcher.BeginInvoke(() =>
            {
                _lastState = state;
                _vm?.Update(state);

                // 首帧真实数据到达 → 完整重显脉冲（2026-09-04 修复"启动不显示数据"：
                // DWM 对任务栏区域窗口的首次合成发生在占位符阶段，文本更新后不再合成，
                // 数据永远停在 "--"；完整 WPF Hide→Show→Activate 才触发重新合成
                // ——与手动"暂停→取消暂停"能恢复是同一原理）
                if (_lastState != null && _firstFrameLogged == false)
                {
                    _firstFrameLogged = true;
                    try { _window?.PulseShow(); } catch { }
                    // 首帧数据到达探针：记录 LHM 懒加载耗时 + 数据；
                    // 自拍照放到下一个渲染帧之后（BeginInvoke Background），确保拍到真实数值
                    try
                    {
                        string first = $"[{DateTime.Now:HH:mm:ss}] FIRST-DATA after {Math.Round((DateTime.UtcNow - _startUtc).TotalSeconds, 1)}s\n"
                            + string.Join("\n", RenderState(state));
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-first-data.log"), first);
                        if (Environment.GetEnvironmentVariable("HWTB_SELFIE") == "1")
                            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                SaveWindowSelfie(@"launch-data.png")));
                    }
                    catch { }
                }
            });
        _monitor.Start();

        // 4) 托盘图标
        RebuildTrayIcon();

        // 5) Explorer 崩溃恢复：
        //    WinEvent(EVENT_SYSTEM_FOREGROUND 不可靠)，这里用进程轮询检测 explorer 的 PID 变化，
        //    变化 = 重启 → 重建托盘图标 + 重置窗口位置。
        // 2026-09-08 事故：原来用 WinForms Timer（System.Windows.Forms.Timer），
        // 在管理员提权的 WPF 进程里它的 tick 从未触发（实锤：hwtb-ctrl.cmd 写了没被消费，
        // Explorer 恢复路径也死），换成 WPF DispatcherTimer（与窗口同线程同泵，必然触发）。
        _watchdogTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdogTimer.Tick += (_, _) => CheckExplorerRestart();
        _watchdogTimer.Start();

        // ---- 控制通道（2026-09-08：管理员进程收不到普通权限程序的 UIPI 输入，托盘菜单点不动；
        //      信号文件不受此限——升级时写 hwtb-ctrl.cmd 即可让它自杀，不再依赖人工点托盘）----
        _watchdogTimer.Tick += (_, _) =>
        {
            try
            {
                string ctrl = Path.Combine(AppContext.BaseDirectory, "hwtb-ctrl.cmd");
                if (File.Exists(ctrl))
                {
                    string cmd = File.ReadAllText(ctrl).Trim();
                    File.Delete(ctrl);
                    if (cmd.StartsWith("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-exit.log"),
                            $"[{DateTime.Now:HH:mm:ss}] exit via hwtb-ctrl.cmd");
                        Dispatcher.BeginInvoke(new Action(ExitApp));
                    }
                }
            }
            catch { }
            // 任务栏探针（2026-09-08：验证"任务栏在=显示、收起=隐藏"真实行为，
            // 全屏时 Shell_TrayWnd 的可见性/矩形怎么变，看 tb_probe.log，HWTB_PROBE=1 开启）
            try
            {
                if (Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
                {
                    IntPtr tb = CtrlFindWindow("Shell_TrayWnd", null);
                    string tbState = tb == IntPtr.Zero ? "none"
                        : (CtrlGetWindowRect(tb, out int l, out int t, out int r, out int b)
                            ? $"vis={CtrlIsWindowVisible(tb)} rect=({l},{t})-({r},{b})" : "vis=? rect=?");
                    string winState = _window == null ? "null"
                        : $"vis={_window.Visibility} stealth={_window.IsStealth} pos={_window.GetWindowRectPx()}";
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tb_probe.log"),
                        $"[{DateTime.Now:HH:mm:ss}] tb={tbState} | win={winState}\n");
                }
            }
            catch { }
        };

        _window.Show();

        // 启动 3 秒自检（2026-09-08 事故后加的验收底线：每次启动都留证据，
        // PASS=窗口已在任务栏区域内可见；FAIL=被推到屏外（上次事故的形态））
        var verifyTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        verifyTimer.Tick += (_, _) =>
        {
            verifyTimer.Stop();
            TryWriteVerifyLog();
        };
        verifyTimer.Start();

        // 启动探针：仅当 HWTB_PROBE=1 时生效（验收/诊断用，正常用户无感）
        if (Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
        {
            var probe = new DispatcherTimer(DispatcherPriority.Background);
            probe.Interval = TimeSpan.FromSeconds(1);
            probe.Tick += (_, _) =>
            {
                probe.Stop();
                try
                {
                    string info = $"[{DateTime.Now:HH:mm:ss}] OK\n"
                        + WindowSnapshotInfo() + "\n"
                        + (_lastState != null
                            ? string.Join("\n", RenderState(_lastState))
                            : "(state: waiting for first monitor tick — LHM initializing on background thread)");
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-ok.log"), info);
                    if (Environment.GetEnvironmentVariable("HWTB_SELFIE") == "1")
                        SaveWindowSelfie(@"launch-1s.png");
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"), $"probe: {ex}"); } catch { }
                }
            };
            probe.Start();

            // 第二探针（4 秒）：验证外部修改 config.json 是否立即生效
            var probe2 = new DispatcherTimer(DispatcherPriority.Background);
            probe2.Interval = TimeSpan.FromSeconds(4);
            probe2.Tick += (_, _) =>
            {
                probe2.Stop();
                try
                {
                    string info = $"[{DateTime.Now:HH:mm:ss}] PROBE2\n"
                        + WindowSnapshotInfo() + "\n"
                        + $"config now: RefreshInterval={_config.Current.RefreshInterval} ShowGpu0={_config.Current.ShowGpu0} ShowGpu1={_config.Current.ShowGpu1} ShowCpu={_config.Current.ShowCpu} ShowNetwork={_config.Current.ShowNetwork} FontSize={_config.Current.FontSize}";
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-probe2.log"), info);
                    if (Environment.GetEnvironmentVariable("HWTB_SELFIE") == "1")
                        SaveWindowSelfie(@"launch-4s.png");
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-probe2.log"), $"probe2: {ex}"); } catch { }
                }
            };
            probe2.Start();
        }

        // 第三探针（12 秒）：LHM 初始化完成后拍有数据的自拍照
        var probe3 = new DispatcherTimer(DispatcherPriority.Background);
        probe3.Interval = TimeSpan.FromSeconds(12);
        probe3.Tick += (_, _) =>
        {
            probe3.Stop();
            try
            {
                string info3 = $"[{DateTime.Now:HH:mm:ss}] PROBE3\n"
                    + WindowSnapshotInfo() + "\n"
                    + (_lastState != null ? string.Join("\n", RenderState(_lastState)) : "(no state)");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-probe3.log"), info3);
                if (Environment.GetEnvironmentVariable("HWTB_SELFIE") == "1")
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SaveWindowSelfie(@"launch-12s.png")));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-probe3.log"), $"probe3: {ex}"); } catch { }
            }
        };
        probe3.Start();

        // 隐身回归测试：--stealth-test（仅 HWTB_PROBE=1）
        // 2026-09-04 重写：验证条件从 Visibility 属性改为窗口实际屏幕坐标（GetWindowRectPx）——
        // 属性 Hidden 但 DWM 残影还在屏幕上 = 假 PASS（20:51 版测试被坑）。真标准：
        // 隐身 ON → 窗口必须在 -32000 屏外（屏幕上绝无像素）；OFF → 必须回到任务栏区域
        if (e.Args.Contains("--stealth-test") && Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
        {
            var st = new DispatcherTimer(DispatcherPriority.Background);
            st.Interval = TimeSpan.FromMilliseconds(500);
            int _stTicks = 0;
            string _stLog = "";
            string RectDesc() => _window?.GetWindowRectPx() is { } r ? $"L={r.L} T={r.T}" : "n/a";
            st.Tick += (_, _) =>
            {
                try
                {
                    int t = _stTicks++;
                    switch (t)
                    {
                        case 10:
                            _stealth = true; _window?.SetStealth(true);
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] stealth ON\n"; break;
                        case 11:
                        {
                            // 真标准：窗口必须物理移到屏幕外（屏幕上绝无像素）；
                            // Visibility 属性不作数——Hidden 时 WPF 有时仍报 Visible（假 PASS 之源）
                            var r = _window?.GetWindowRectPx();
                            bool off = r is not null && r.Value.L < -1000;
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] rect={RectDesc()} vis={_window?.Visibility} offscreen={off} {(off ? "PASS" : "FAIL")}\n"; break;
                        }
                        case 20:
                            _stealth = false; _window?.SetStealth(false);
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] stealth OFF\n"; break;
                        case 21:
                        {
                            var r2 = _window?.GetWindowRectPx();
                            bool on = r2 is not null && r2.Value.L > 0 && r2.Value.L < 4000 && _window is { Visibility: Visibility.Visible };
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] rect={RectDesc()} vis={_window?.Visibility} back-on-taskbar={on} {(on ? "PASS" : "FAIL")}\n"; break;
                        }
                        case 30:
                            _stealth = true; _window?.SetStealth(true);
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] stealth ON (round2)\n"; break;
                        case 40:
                        {
                            var r3 = _window?.GetWindowRectPx();
                            bool still = r3 is not null && r3.Value.L < -1000;
                            _stLog += $"[{DateTime.Now:HH:mm:ss}] 5s-later rect={RectDesc()} still-offscreen={still} {(still ? "PASS" : "FAIL")}\n";
                            _stealth = false; _window?.SetStealth(false); // 还原，不留隐藏态
                            break;
                        }
                        case 41:
                            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-stealth-test.log"), _stLog);
                            st.Stop(); return;
                        default:
                            return;
                    }
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-stealth-test.log"), "stealth-test: " + ex); } catch { }
                    st.Stop();
                }
            };
            st.Start();
        }
        if (e.Args.Contains("--exit-test") && Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
        {
            var exitProbe = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            exitProbe.Tick += (_, _) => { exitProbe.Stop(); ExitApp(); };
            exitProbe.Start();
        }
    }

    /// <summary>启动自检落盘（2026-09-08：窗口必须实际在任务栏矩形内，-32000 屏外 = FAIL）。</summary>
    private void TryWriteVerifyLog()
    {
        try
        {
            string line;
            if (_window == null)
            {
                line = "window=null FAIL";
            }
            else
            {
                var r = _window.GetWindowRectPx();
                bool on = r is not null && r.Value.L > 0 && r.Value.L < 4000
                    && _window.Visibility == System.Windows.Visibility.Visible;
                line = $"window vis={_window.Visibility} stealth={_window.IsStealth} rect={(r is null ? "n/a" : $"({r.Value.L},{r.Value.T})-({r.Value.R},{r.Value.B})")} taskbarVisible={_window.TaskbarVisible} {((on) ? "PASS" : "FAIL")}";
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-verify.log"),
                $"[{DateTime.Now:HH:mm:ss}] pid={Environment.ProcessId} {line}");
        }
        catch { }
    }

    private string WindowSnapshotInfo()
    {
        if (_window == null || _tray == null || _monitor == null) return "snapshot: n/a";
        return $"window: visible={_window.Visibility} state={_window.WindowState} left={_window.Left:F0} top={_window.Top:F0} w={_window.ActualWidth:F0} h={_window.ActualHeight:F0} | tray={_tray.Visible} | monitor={_monitor.IsRunning}";
    }

    /// <summary>把悬浮窗渲染成 PNG 自拍照（程序内 RenderTargetBitmap，不受 GDI/DWM 限制）。
    /// 注意：必须在 UI 线程的下一渲染帧之后调用（由调用方用 BeginInvoke 延迟），
    /// 否则拍到的是绑定更新前的上一帧文本。</summary>
    private void SaveWindowSelfie(string fileName)
    {
        try
        {
            var win = _window!;
            int w = (int)Math.Ceiling(win.ActualWidth);
            int h = (int)Math.Ceiling(win.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(win);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, fileName));
            png.Save(fs);
        }
        catch
        {
            // 自拍照失败不影响主流程
        }
    }

    private uint _lastExplorerPid = 0;

    private void CheckExplorerRestart()
    {
        uint pid = 0;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            pid = (uint)p.Id;
            p.Dispose();
        }

        if (pid == 0)
        {
            // Explorer 还没起来（刚崩了）：隐藏悬浮窗，避免悬空
            if (_window is { Visibility: Visibility.Visible })
                _window.Hide();
            return;
        }

        if (_lastExplorerPid != 0 && pid != _lastExplorerPid)
        {
            // Explorer 重启过：重建托盘图标（规则 9）
            RebuildTrayIcon();
            if (_window != null) _window.ResetPosition();
        }
        _lastExplorerPid = pid;

        // Explorer 在 → 确保悬浮窗可见（全屏自动隐藏态除外：那是预期隐藏，不是异常）
        if (_window is { Visibility: Visibility.Hidden } && _window.TaskbarVisible && !_stealth)
            _window.Show();
    }

    private void RebuildTrayIcon()
    {
        _tray?.Dispose();
        _tray = null;
        _trayStealthItem = null;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "硬件任务栏监控"
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        // 隐身：勾选=隐藏，取消勾选=显示（2026-09-04 老大要求；原"暂停显示"逻辑反了）
        // 2026-09-13：删"重置位置"（老大：没用）；"设置…/隐身/退出"与文字右键菜单完全对齐
        var stealthItem = new System.Windows.Forms.ToolStripMenuItem("隐身");
        stealthItem.Checked = _stealth;
        stealthItem.Click += (_, _) =>
        {
            _stealth = !_stealth;
            stealthItem.Checked = _stealth;
            _window?.SetStealth(_stealth);
            _window?.SyncStealthMenu(_stealth); // 文字右键菜单勾选态同步
        };
        _trayStealthItem = stealthItem;
        menu.Items.Add(stealthItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.Visible = true;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.SyncFrom(_config.Current);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_config.Current);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }


    // 控制通道/探针用 P/Invoke（App 侧；MainWindow 里已有同功能声明，这里独立一份避免跨类访问）
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CtrlFindWindow(string cls, string name);
    [DllImport("user32.dll")]
    private static extern bool CtrlIsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool CtrlGetWindowRect(IntPtr h, out int l, out int t, out int r, out int b);
    private void ExitApp()
    {
        _exiting = true;
        if (Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-exit.log"),
                $"[{DateTime.Now:HH:mm:ss}] exit-start"); } catch { }
        }
        // 退出路径全链路防抛（2026-09-04：任一环节抛异常 = 托盘没了但窗口/Shutdown 不执行 = 僵尸进程）
        try
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _watchdogTimer?.Stop();
            _watchdogTimer = null;
        }
        catch { }
        try { _monitor?.Dispose(); } catch { }
        try { SensorService.Instance.Dispose(); } catch { }
        try { _config.Dispose(); } catch { }
        Shutdown();
    }

    /// <summary>供设置窗口调用：把配置落盘 + 触发立即生效。</summary>
    public void ApplyConfig(AppSettings cfg)
    {
        _config.Save(cfg);
    }

    /// <summary>托盘图标：运行时绘制（蓝色 CPU 柱 + 红色 GPU 点），无需外部资源文件。</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // GPU0 柱
            g.FillRectangle(Brushes.SteelBlue, 4, 12, 6, 16);
            // GPU1 柱（高）
            g.FillRectangle(Brushes.DeepSkyBlue, 13, 6, 6, 22);
            // CPU 圆点
            g.FillEllipse(Brushes.OrangeRed, 22, 16, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>控制台自检：与 WPF 无关，跑一轮硬件读取并写 selftest.log。</summary>
    private static void RunSelfTest()
    {
        var sb = new System.Text.StringBuilder();
        void W(string s) { Console.WriteLine(s); sb.AppendLine(s); }

        W("=== HardwareTaskbar（控制台验证）===");
        W("数据源：NVML(原生) / PerformanceCounter / LibreHardwareMonitorLib / NetworkInformation");
        W("selftest 模式：读一轮即退出\n");

        using var sensors = SensorService.Instance;
        W(string.Join("\n", RenderState(sensors.GetCurrentState())));

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), sb.ToString());
        }
        catch { }
    }

    private static string[] RenderState(HardwareState state)
    {
        var lines = new List<string>();
        foreach (var g in state.Gpus)
        {
            string util = g.Utilization is { } u ? $"{u}%" : "N/A";
            string mem = $"{g.MemoryUsedGb:F1}/{g.MemoryTotalGb:F0}GB";
            string temp = g.Temperature is { } t ? $"{t}℃" : "N/A";
            lines.Add($"GPU{g.Slot}  {Pad(util, 5)}  {Pad(mem, 10)}  {temp}");
        }
        lines.Add(string.Empty);
        lines.Add($"CPU   {Pad(state.CpuUsage is { } p ? $"{p:F0}%" : "N/A", 5)}  " +
                  (state.CpuTemperature is { } ct ? $"{ct:F0}℃" : "N/A"));
        lines.Add(string.Empty);
        lines.Add($"↓ {HardwareState.ToMbPerSec(state.DownloadSpeed):F1} MB/s");
        lines.Add($"↑ {HardwareState.ToMbPerSec(state.UploadSpeed):F1} MB/s");
        lines.Add($"\n[{state.Timestamp:HH:mm:ss}]  卡数:{state.Gpus.Count}");
        return lines.ToArray();
    }

    private static string Pad(string s, int width) => s.Length >= width ? s : s.PadRight(width);

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            try { _watchdogTimer?.Stop(); } catch { }
            try { _monitor?.Dispose(); } catch { }
        }
        base.OnExit(e);
        // 退出探针：exit-ok 落盘 = 完整退出路径走通（与 exit-start 配对验证）
        if (Environment.GetEnvironmentVariable("HWTB_PROBE") == "1")
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-exit.log"),
                $"[{DateTime.Now:HH:mm:ss}] exit-ok"); } catch { }
        }
    }
}
