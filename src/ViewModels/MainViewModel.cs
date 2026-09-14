// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 主悬浮窗 ViewModel
// 2026-09-04 恢复旧版款式（老大 18:04 明确要求）：双行五列——
//   行1: 显卡: 1  x.xG/xx.xG  温度: xx°C  利用率: x%  CPU: x%   下载: ↓xxKB/s
//   行2: 显卡: 2  x.xG/xx.xG  温度: xx°C  利用率: x%  CPU温度: xx°C 上传: ↑xxKB/s
// 文本格式与颜色语义全部照抄旧版 C++（scripts/taskbar_monitor_v13.cpp）：
//   TCol: >=80℃红 #FF5050, >=60℃黄 #FFD232, 其余白
//   SCol: <1MB/s 白, <5MB/s 蓝 #50A0FF, 其余绿 #50FF78
//   失败值: N/A / -- 灰 #A0A5B4（禁止用 0 冒充，规则 5）
// 与旧版唯一区别：CPU温度 现在是 LHM/PawnIO 实测值（旧版固定 N/A）。
using CommunityToolkit.Mvvm.ComponentModel;
using HardwareTaskbar.Models;
using HardwareTaskbar.Services;

namespace HardwareTaskbar.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ---- 行 1（GPU0 行）----
    [ObservableProperty] private string _topGpuValue = "--";
    [ObservableProperty] private string _topTempValue = "--";
    [ObservableProperty] private string _topTempColor = "#FFFFFF";
    [ObservableProperty] private string _topUtilValue = "--";
    [ObservableProperty] private string _topCpuValue = "--";
    [ObservableProperty] private string _topDownValue = "↓--";
    [ObservableProperty] private string _topDownColor = "#FFFFFF";

    // ---- 行 2（GPU1 行）----
    [ObservableProperty] private string _botGpuValue = "--";
    [ObservableProperty] private string _botTempValue = "--";
    [ObservableProperty] private string _botTempColor = "#FFFFFF";
    [ObservableProperty] private string _botUtilValue = "--";
    [ObservableProperty] private string _botCpuTempValue = "N/A";
    [ObservableProperty] private string _botCpuTempColor = "#A0A5B4";
    [ObservableProperty] private string _botUpValue = "↑--";
    [ObservableProperty] private string _botUpColor = "#FFFFFF";

    // ---- 显示开关（与 config.json 对应，语义兼容旧版列级显隐）----
    [ObservableProperty] private bool _showGpu0 = true;
    [ObservableProperty] private bool _showGpu1 = true;
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showNetwork = true;

    [ObservableProperty]
    private double _fontSize = 13;

    // ---- 值列宽度（2026-09-04 老大要求“列间距太大，按各自数据缩小”）----
    // 13px 微软雅黑实测最大值（scripts/hw_textmeasure.cs）：显存 77 / 温度 37 / 利用率 35 /
    // CPU 35 / CPU温度 37 / 网速(↓9.99GB/s) 62；基准列宽 = 实测 + 8px 余量。
    // 实际列宽 = 基准 × FontSize/13 随字号缩放（设置页字号 10-24，定死宽度会截断）；
    // 固定列宽防“网速数据变化整行左右跳动”，字号变化时整体重算。
    [ObservableProperty] private double _gpuColW = 85;
    [ObservableProperty] private double _tempColW = 45;
    [ObservableProperty] private double _utilColW = 43;
    [ObservableProperty] private double _cpuColW = 43;
    [ObservableProperty] private double _cpuTempColW = 45;
    [ObservableProperty] private double _netColW = 70;

    partial void OnFontSizeChanged(double value) => RecomputeColumnWidths();

    private void RecomputeColumnWidths()
    {
        double scale = FontSize / 13.0;
        GpuColW = 85 * scale;
        TempColW = 45 * scale;
        UtilColW = 43 * scale;
        CpuColW = 43 * scale;
        CpuTempColW = 45 * scale;
        NetColW = 70 * scale;
    }

    /// <summary>全部区块都隐藏时，整个悬浮窗也应隐藏。</summary>
    public bool AllHidden => !ShowGpu0 && !ShowGpu1 && !ShowCpu && !ShowNetwork;

    /// <summary>消费一次硬件快照并刷新双行显示。必须在 UI 线程调用（由 App 调度）。</summary>
    public void Update(HardwareState state)
    {
        var g0 = state.Gpus.FirstOrDefault(g => g.Slot == 0);
        var g1 = state.Gpus.FirstOrDefault(g => g.Slot == 1);

        TopGpuValue = MemPair(g0);
        TopTempValue = TempStr(g0?.Temperature);
        TopTempColor = TempColor(g0?.Temperature);
        TopUtilValue = UtilStr(g0?.Utilization);
        TopCpuValue = state.CpuUsage is { } cu ? $"{cu:F0}%" : "--";
        TopDownValue = "↓" + SpeedStr(state.DownloadSpeed);
        TopDownColor = SpeedColor(state.DownloadSpeed);

        BotGpuValue = MemPair(g1);
        BotTempValue = TempStr(g1?.Temperature);
        BotTempColor = TempColor(g1?.Temperature);
        BotUtilValue = UtilStr(g1?.Utilization);
        BotCpuTempValue = state.CpuTemperature is { } ct ? $"{ct:F0}°C" : "N/A";
        BotCpuTempColor = TempColor(state.CpuTemperature);
        BotUpValue = "↑" + SpeedStr(state.UploadSpeed);
        BotUpColor = SpeedColor(state.UploadSpeed);
    }

    /// <summary>应用配置（修改立即生效）。</summary>
    public void ApplySettings(AppSettings c)
    {
        ShowGpu0 = c.ShowGpu0;
        ShowGpu1 = c.ShowGpu1;
        ShowCpu = c.ShowCpu;
        ShowNetwork = c.ShowNetwork;
        FontSize = c.FontSize;
        RecomputeColumnWidths();
        OnPropertyChanged(nameof(AllHidden));
    }

    // ---------- 旧版格式（taskbar_monitor_v13 照抄）----------

    /// <summary>显存 "8.7G/48.0G"（旧版 FM：MB/1024 一位小数）。</summary>
    private static string MemPair(GpuState? g)
        => g is null ? "--" : $"{g.MemoryUsedGb:F1}G/{g.MemoryTotalGb:F1}G";

    private static string TempStr(double? t) => t is { } v ? $"{v:F0}°C" : "--";

    /// <summary>旧版 TCol 语义；null = 读失败，灰显。</summary>
    private static string TempColor(double? t)
    {
        if (t is not { } v) return "#A0A5B4";
        return v >= 80 ? "#FF5050" : v >= 60 ? "#FFD232" : "#FFFFFF";
    }

    private static string UtilStr(int? u) => u is { } v ? $"{v}%" : "--";

    /// <summary>旧版 FS：GB/s、MB/s、KB/s、B/s 自适应。</summary>
    private static string SpeedStr(double b)
    {
        if (b >= 1e9) return $"{b / 1e9:F2}GB/s";
        if (b >= 1e6) return $"{b / 1e6:F2}MB/s";
        if (b >= 1e3) return $"{b / 1e3:F0}KB/s";
        return $"{b:F0}B/s";
    }

    /// <summary>旧版 SCol 语义。</summary>
    private static string SpeedColor(double b)
    {
        double m = b / 1e6;
        return m < 1 ? "#FFFFFF" : m < 5 ? "#50A0FF" : "#50FF78";
    }
}
