// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 悬浮窗定位与跟随逻辑
// 2026-09-04 改版（老大要求"直接在任务栏显示"）：
//   - 锚点 = Shell_TrayWnd 内的 TrayNotifyWnd（托盘区，实测 Win11 24H2 2560x1440:
//     Shell_TrayWnd=(0,1392)-(2560,1440)，TrayNotifyWnd=(2217,1392)-(2560,1440)）
//   - 位置 = 任务栏条内右对齐：x = 托盘左缘 - 窗宽 - 8，垂直贴任务栏条（条内顶部对齐），
//     不再贴在任务栏上方（旧 WPF 版 y=tbTop-h 飘在上方带黑块，老大不认）
//   - 窗口高 = 任务栏条高（DIP），双行内容在条内垂直居中
//   - 不允许鼠标拖动（老大 2026-09-04 要求）：位置永远锚定任务栏托盘区，无自由拖动
//   - "隐身"菜单：勾选=隐藏，取消勾选=显示；隐身态看门狗不定位不强制显示
//   - 保留：250ms 轮询任务栏矩形、Win+D 最小化恢复、WM_DISPLAYCHANGE、跨屏 DPI 换算
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using HardwareTaskbar.ViewModels;
using Point = System.Windows.Point;

namespace HardwareTaskbar.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _repositionTimer; // 250ms 轮询任务栏矩形（覆盖位置变化）
    private System.Windows.Controls.ContextMenu? _ctxMenu;

    /// <summary>当前隐身勾选态（文字右键菜单用）。</summary>
    public bool StealthState => _stealth;

    /// <summary>打开设置窗口（文字右键菜单用，与托盘同一入口）。</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>退出（文字右键菜单用，与托盘同一入口）。</summary>
    public event Action? ExitRequested;

    /// <summary>右键菜单勾选态变化（托盘菜单同步用）。</summary>
    public event Action<bool>? StealthChangedFromMenu;

    /// <summary>把外部隐身变化同步到文字右键菜单的勾选项（托盘菜单切换后调用）。</summary>
    public void SyncStealthMenu(bool on)
    {
        if (_stealthMenuItem is not null) _stealthMenuItem.IsChecked = on;
    }

    private System.Windows.Controls.MenuItem? _stealthMenuItem;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ---- Z-order 强制置顶（2026-09-04：Win11 任务栏本体 + XAML 岛层都在 topmost 组，
    //      点击任务栏激活它 = 任务栏跳到 topmost 组最前 = 盖住本窗口；
    //      点"暂停→再暂停"能恢复是因为 WPF 重新 Show 会顶回最前，所以每 150ms
    //      无条件 SetWindowPos(HWND_TOPMOST) 把窗口抬回 topmost 组顶部，不移动不激活零闪烁）----
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // 不抢焦点：点任务栏时本窗口绝不能变成激活窗口（WS_EX_NOACTIVATE = 0x08000000）
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---- 被盖检测（2026-09-04：Win11 24H2 任务栏 XAML 岛层在 topmost 组里压住本窗口，
    //      SetWindowPos(HWND_TOPMOST) 抬不过去；唯一可靠恢复 = WPF 重走 Show 路径
    //      （老大手动"勾→取消暂停"能恢复就是这个原理）。每 150ms 用 WindowFromPoint
    //      检测窗口中心点，被别的窗口盖住就强制重显）----
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    // 注意：GetDpiForWindow 是 Win10+ 的 user32.dll API（不是 shcore.dll）
    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd, out uint dpiX, out uint dpiY);

    // 任务栏所在显示器的 DPI（定位换算必须用它，不能用窗口自己的 DPI——
    // 窗口跨屏时两边 DPI 不同，用窗口 DPI 会把任务栏矩形算飞，窗口飘到副屏）
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags); // MONITOR_DEFAULTTONEAREST=2

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, uint dpiType, out uint dpiX, out uint dpiY); // MD_EFFECTIVE_DPI=0

    // ---- 跟随任务栏生死（2026-09-08 老大定案：任务栏在=显示，任务栏收起=消失）----
    // 任务栏生死跟随需要：IsWindowVisible（Shell_TrayWnd 自动隐藏时不可见）
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    // 主显示器判定必须用 GetMonitorInfo（自包含，不依赖任何 WPF/WinForms 屏幕 API）。
    // 2026-09-08 事故根因：这里原来用 SystemParameters.PrimaryScreenHeight*dpi 当主屏高，
    // 但 PrimaryScreenHeight 是 DIP（@200% 缩放 = 960），×2.0 = 1920；而 Win11 24H2 对
    // Per-Monitor V2 进程返回的 GetWindowRect 是逻辑像素（任务栏实测 0..1392）。
    // 1392 > 1919 恒假 → TaskbarVisible 恒 false → Reposition/BumpTopmost/看门狗
    // 全部把窗口推到 (-32000) 且永远不回来（窗口坐标实锤 -21845,-21845，只剩托盘）。
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>任务栏当前是否可见（全屏检测的唯一依据：任务栏在=显示，收起=消失）。
    /// App 的 Explorer 看门狗与 BumpTopmost 的强制重显都看这个，避免和跟随逻辑打架。
    /// 判定：Shell_TrayWnd 可见 且 其矩形与主显示器矩形有交集（双保险防"推到屏外"假象）。</summary>
    public bool TaskbarVisible
    {
        get
        {
            IntPtr t = FindWindow("Shell_TrayWnd", null);
            if (t == IntPtr.Zero) return false;
            if (!IsWindowVisible(t)) return false;
            if (!GetWindowRect(t, out RECT tr)) return false;
            MONITORINFO mi = new();
            mi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
            IntPtr mon = MonitorFromWindow(t, 2); // MONITOR_DEFAULTTONEAREST
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
            RECT pr = mi.rcMonitor; // 主显示器矩形，与 tr 同为逻辑像素，可直接比较
            return tr.Top < pr.Bottom - 1 && tr.Bottom > pr.Top + 1;
        }
    }




    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // 初始不显示，等定位成功再 Show
        Visibility = Visibility.Hidden;

        // 任务栏位置/分辨率变化 + Z-order 看门狗：150ms 轮询，变了就重定位，
        // 每轮无条件把窗口抬回 topmost 组最前（防任务栏 XAML 岛层盖住）；
        // 并跟随任务栏生死：任务栏收起时 Hide，回来时自动恢复（老大 2026-09-08 定案）
        _repositionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _repositionTimer.Tick += (_, _) => Reposition();
        _repositionTimer.Start();

        // 系统 DPI 变化（缩放改变/多显示器移动）——lambda 避开 WinForms/WPF 类型名歧义
        SystemParameters.StaticPropertyChanged += (s, _) =>
        {
            _lastPos = null;
            Dispatcher.BeginInvoke(new Action(Reposition));
        };

        // Explorer 重启 → Shell_TrayWnd 消失再重现；250ms 轮询会自然恢复，
        // 但这里显式监听 WM_DISPLAYCHANGE 兜底
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int ex = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            }
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        };

        // 不允许鼠标拖动（老大 2026-09-04 要求：任何情况都不允许拖拽位置）；
        // 位置由 Reposition 锚定托盘区，托盘"隐身"控制显隐

        // 文字右键菜单（2026-09-13 老大要求，与托盘对齐：设置…/隐身/退出，无重置位置）
        BuildContextMenu();
        MouseRightButtonUp += OnRightClick;
    }

    private void BuildContextMenu()
    {
        _ctxMenu = new System.Windows.Controls.ContextMenu();
        var settings = new System.Windows.Controls.MenuItem { Header = "设置…" };
        settings.Click += (_, _) => OpenSettingsRequested?.Invoke();
        _stealthMenuItem = new System.Windows.Controls.MenuItem { Header = "隐身" };
        _stealthMenuItem.Click += (_, _) =>
        {
            bool? cur = _stealthMenuItem.IsChecked;
            bool on = !(cur ?? false);
            _stealthMenuItem.IsChecked = on;
            StealthChangedFromMenu?.Invoke(on);
        };
        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _ctxMenu.Items.Add(settings);
        _ctxMenu.Items.Add(_stealthMenuItem);
        _ctxMenu.Items.Add(new System.Windows.Controls.Separator());
        _ctxMenu.Items.Add(exit);
    }

    private void OnRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 只在文字命中处弹；空白透明区不响应（窗口本身逐像素 alpha，空白处点不到）
        _stealthMenuItem!.IsChecked = _stealth;
        _ctxMenu!.PlacementTarget = this;
        _ctxMenu.IsOpen = true;
        e.Handled = true;
    }

    private Point? _lastPos;

    /// <summary>清自定义位置，立即重新锚定到任务栏内右对齐。</summary>
    public void ResetPosition()
    {
        _lastPos = null;
        Reposition();
    }

    /// <summary>把窗口嵌到当前任务栏条内、右对齐锚定托盘区（屏幕底边或顶边，随任务栏位置）。</summary>
    private void Reposition()
    {
        // 隐身：不定位、不强制显示（2026-09-04 修复：原来看门狗每 150ms 见窗口隐藏就强制
        // Visibility=Visible，把托盘"隐身"秒撤销，表现为点隐身不消失）
        if (_stealth) return;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return; // Explorer 还没起来，下一轮再试
        if (!GetWindowRect(taskbar, out RECT r)) return;

        // 跟随任务栏生死（老大 2026-09-08 定案）：本窗依附任务栏显示，
        // 任务栏在（可见）→ 显示；任务栏收起（自动隐藏，全屏时系统会收起任务栏）→ 藏起来。
        // 不枚举任何应用窗口（之前 Z 序/前台窗口两版都被 Win11 常驻全屏层坑过）。
        // 任务栏不在屏幕上（自动隐藏/全屏收起：不可见，或矩形被推到屏外）→ 隐藏
        if (!IsWindowVisible(taskbar) || !TaskbarVisible)
        {
            if (Visibility == Visibility.Visible)
            {
                // 同隐身手法：物理移出屏幕再 Hide，防 DWM 残影
                Left = -32000;
                Top = -32000;
                _lastPos = null; // 任务栏回来时强制重新定位
                Hide();
            }
            return;
        }

        // Win+D/显示桌面会把 Topmost 窗口也最小化，必须每轮检测并恢复，
        // 否则窗口永久消失（Visibility 在最小化时仍是 Visible，旧看门狗抓不到）
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        // 用任务栏所在显示器的 DPI 把物理像素矩形换算成 DIP
        // （WPF 的 Left/Top 是虚拟屏幕 DIP 坐标，与窗口当前 DPI 无关）
        double tbDpi = GetTaskbarDpiScale(taskbar);
        double screenHPx = SystemParameters.PrimaryScreenHeight * tbDpi;
        // 任务栏在屏幕下半部分 → 底部任务栏；否则顶部
        bool taskbarAtBottom = r.Top > screenHPx / 2;
        double tbTop = r.Top / tbDpi;
        double tbH = (r.Bottom - r.Top) / tbDpi;

        // 锚点：托盘区 TrayNotifyWnd（旧版右对齐锚）；找不到时退化为任务栏右缘
        double anchorRight = r.Right / tbDpi;
        double anchorLeft = r.Right / tbDpi;
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr))
        {
            anchorLeft = tr.Left / tbDpi;
            anchorRight = tr.Right / tbDpi;
        }

        // 高度由内容自适应（WidthAndHeight），垂直居中于任务栏条内
        double winW = ActualWidth > 0 ? ActualWidth : 200;
        double winH = ActualHeight > 0 ? ActualHeight : 36;
        // 任务栏条内右对齐，紧贴托盘区（2026-09-04 老大要求：右缘贴系统托盘“向上箭头”，
        // 间隙 = 1 个字符宽，随字号缩放；TrayNotifyWnd 左缘即箭头位置，实测箭头非独立 HWND）
        double gap = _vm != null && _vm.FontSize > 0 ? _vm.FontSize : 13;
        double x = anchorLeft - winW - gap;
        double y = taskbarAtBottom ? (tbTop + (tbH - winH) / 2) : (r.Bottom / tbDpi - tbH + (tbH - winH) / 2);

        if (_lastPos is null || Math.Abs(_lastPos.Value.X - x) > 0.5 || Math.Abs(_lastPos.Value.Y - y) > 0.5)
        {
            Left = x;
            Top = y;
            _lastPos = new Point(x, y);
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;
        }
        // 位置没变但窗口被隐藏/最小化 → 补一次显示
        else if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;
        }

        // 每轮无条件抬 Z-order + 被盖检测：点任务栏后任务栏 XAML 岛层会盖住本窗口，
        // 检测窗口中心点被谁占着，不是自己就强制走 WPF 重显路径（唯一可靠的恢复手段）
        BumpTopmost();
    }

    /// <summary>隐身模式（托盘"隐身"勾选态）。
    /// 2026-09-04 修复：窗口嵌在任务栏条内时，单纯 Hide() 会让 DWM 保留该区域的旧合成帧
    /// （残影），直到任务栏被激活重绘才消失（老大实测：点隐身不立刻消失，点一下任务栏空白才消失）。
    /// 所以隐身 = 先物理移到屏幕外(-32000)再 Hide；取消 = 拉回锚点再 Show。</summary>
    private bool _stealth;
    /// <summary>当前是否处于手动隐身态（探针/诊断用）。</summary>
    public bool IsStealth => _stealth;
    public void SetStealth(bool on)
    {
        _stealth = on;
        if (on)
        {
            // 移到屏幕外：窗口离开屏幕，DWM 必须重绘任务栏区域，残影不可能留存
            Left = -32000;
            Top = -32000;
            _lastPos = null; // 取消隐身时强制重新定位
            Hide();
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            // 先 Show（此时还在 -32000 屏外，用户看不到闪烁），再拉回任务栏锚点
            Show();
            Activate(); // WPF 重走显示路径 = 把窗口顶到 topmost 组最前
            _lastPos = null;
            Reposition();
        }
    }

    /// <summary>窗口在屏幕上的实际矩形（物理像素，隐身验证用；隐身态应在 -32000 屏外）。</summary>
    public (int L, int T, int R, int B)? GetWindowRectPx()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return null;
        return GetWindowRect(h, out var r) ? (r.Left, r.Top, r.Right, r.Bottom) : null;
    }

    /// <summary>把窗口抬回 topmost 组最前（不动位置不激活，零闪烁）。
    /// 点任务栏消失问题已验证此法有效（老大 19:27 确认）。不用 WindowFromPoint 被盖检测——
    /// WPF 透明窗口是逐像素 alpha 分层窗口，透明像素不参与命中测试，检测会误报
    /// （2026-09-04 实测 12s 误报 19 次 = 屏幕闪烁）。</summary>
    private void BumpTopmost()
    {
        if (_stealth) return; // 隐身：不显示不抬
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        if (Visibility != Visibility.Visible)
        {
            // 被隐藏：任务栏不可见时是预期隐藏（全屏/自动隐藏），不恢复；否则恢复显示
            if (TaskbarVisible)
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Show();
            }
            return;
        }
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>重显脉冲：完整走 WPF Hide→Show→Activate 路径。
    /// 窗口在任务栏区域内首次 Show 时 DWM 不合成内容（空白透明，数据到了也不画），
    /// 必须一次完整重显才合成（与手动"暂停→取消"同原理）。启动后由 App 首帧调用两次。</summary>
    public void PulseShow()
    {
        if (_stealth || !TaskbarVisible) return; // 任务栏收起时不脉冲重显（与跟随逻辑一致）
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Hide();
        Show();
        Activate();
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>任务栏所在显示器的有效 DPI 比例（物理像素 → DIP 的除数）。</summary>
    private static double GetTaskbarDpiScale(IntPtr taskbarHwnd)
    {
        IntPtr mon = MonitorFromWindow(taskbarHwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return 1.0;
    }

    private double GetDpiScale()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetDpiForWindow(handle, out uint dpiX, out _) == 0)
            return dpiX / 96.0;
        return 1.0;
    }

    private const int WM_DISPLAYCHANGE = 0x007E;
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            _lastPos = null;
            Dispatcher.BeginInvoke(new Action(Reposition));
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _repositionTimer.Stop();
        base.OnClosed(e);
    }
}
