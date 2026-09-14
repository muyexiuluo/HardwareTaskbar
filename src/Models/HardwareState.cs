// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第二阶段统一数据模型：整机硬件状态快照
namespace HardwareTaskbar.Models;

/// <summary>
/// 整机硬件状态的一次完整快照（不可变）。
/// UI/ViewModel 永远只接收这个类型，不允许接触任何硬件 API（规则 8）。
/// </summary>
public sealed class HardwareState
{
    /// <summary>CPU 总占用率 0-100；null = 读取失败。</summary>
    public double? CpuUsage { get; init; }

    /// <summary>CPU Package 温度 ℃；null = 读取失败（禁止显示 0℃）。</summary>
    public double? CpuTemperature { get; init; }

    /// <summary>下载速度（字节/秒），由差值计算。</summary>
    public double DownloadSpeed { get; init; }

    /// <summary>上传速度（字节/秒），由差值计算。</summary>
    public double UploadSpeed { get; init; }

    /// <summary>全部 GPU（按 UUID 排序，Slot 从 0 起）。</summary>
    public IReadOnlyList<GpuState> Gpus { get; init; } = Array.Empty<GpuState>();

    /// <summary>采样时刻（本机时间）。</summary>
    public DateTime Timestamp { get; init; }

    public static double ToMbPerSec(double bytesPerSec) => bytesPerSec / 1048576.0;
}
