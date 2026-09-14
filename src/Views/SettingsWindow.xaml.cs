// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第五阶段：设置窗口逻辑（改完立即生效）
using System;
using System.Windows;
using HardwareTaskbar.Services;
using MessageBox = System.Windows.MessageBox;

namespace HardwareTaskbar.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        _draft = new AppSettings
        {
            RefreshInterval = current.RefreshInterval,
            ShowGpu0 = current.ShowGpu0,
            ShowGpu1 = current.ShowGpu1,
            ShowCpu = current.ShowCpu,
            ShowNetwork = current.ShowNetwork,
            FontSize = current.FontSize,
            StartWithWindows = current.StartWithWindows
        };
        LoadToControls();
    }

    private void LoadToControls()
    {
        TxtRefresh.Text = _draft.RefreshInterval.ToString();
        ChkGpu0.IsChecked = _draft.ShowGpu0;
        ChkGpu1.IsChecked = _draft.ShowGpu1;
        ChkCpu.IsChecked = _draft.ShowCpu;
        ChkNet.IsChecked = _draft.ShowNetwork;
        SldFont.Value = _draft.FontSize;
        // 2026-09-13：开机自启改复选框，状态以计划任务实际状态为准（AutostartService.IsEnabled）
        ChkAutostart.IsChecked = AutostartService.IsEnabled();
        _draft.StartWithWindows = ChkAutostart.IsChecked == true;
    }

    /// <summary>外部配置变化后同步界面（不提交）。</summary>
    public void SyncFrom(AppSettings current)
    {
        _draft.RefreshInterval = current.RefreshInterval;
        _draft.ShowGpu0 = current.ShowGpu0;
        _draft.ShowGpu1 = current.ShowGpu1;
        _draft.ShowCpu = current.ShowCpu;
        _draft.ShowNetwork = current.ShowNetwork;
        _draft.FontSize = current.FontSize;
        _draft.StartWithWindows = current.StartWithWindows;
        LoadToControls();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtRefresh.Text, out int interval) || interval < 500)
        {
            MessageBox.Show("刷新间隔必须是不低于 500 的整数（毫秒）。", "设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _draft.RefreshInterval = interval;
        _draft.ShowGpu0 = ChkGpu0.IsChecked == true;
        _draft.ShowGpu1 = ChkGpu1.IsChecked == true;
        _draft.ShowCpu = ChkCpu.IsChecked == true;
        _draft.ShowNetwork = ChkNet.IsChecked == true;
        _draft.FontSize = SldFont.Value;

        // 2026-09-13 老大要求：开机自启只在点「应用」时生效
        // 勾选 = 建计划任务，取消勾选 = 删计划任务；与当前状态相同则不动（幂等）
        bool wantAutostart = ChkAutostart.IsChecked == true;
        if (wantAutostart != AutostartService.IsEnabled())
        {
            try
            {
                AutostartService.SetEnabled(wantAutostart);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("设置开机自启失败：" + ex.Message, "设置",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                // 自启设置失败不阻断其他项应用，但复选框回滚到真实状态
                ChkAutostart.IsChecked = AutostartService.IsEnabled();
            }
        }
        _draft.StartWithWindows = wantAutostart;

        App.Instance?.ApplyConfig(_draft);   // 立即生效（内存 + 写 config.json）
        LoadToControls();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
