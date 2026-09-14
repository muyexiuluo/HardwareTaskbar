// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第二阶段统一数据模型：单 GPU 快照
namespace HardwareTaskbar.Models;

/// <summary>
/// 单张 GPU 的一次读取快照（不可变）。
/// 所有读取失败的温度用 null 表示，禁止用 0 冒充（规则 5）。
/// </summary>
public sealed class GpuState
{
    /// <summary>排序后显示序号（GPU0/GPU1…），按 UUID 升序固定。</summary>
    public int Slot { get; init; }

    /// <summary>显卡名称（NVML 设备名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>NVML UUID，唯一且重启不变。</summary>
    public string Uuid { get; init; } = string.Empty;

    /// <summary>GPU 利用率 0-100；null = 读取失败。</summary>
    public int? Utilization { get; init; }

    /// <summary>GPU 核心温度 ℃；null = 读取失败。</summary>
    public int? Temperature { get; init; }

    /// <summary>显存已用（字节）。</summary>
    public long MemoryUsed { get; init; }

    /// <summary>显存总量（字节）。</summary>
    public long MemoryTotal { get; init; }

    public double MemoryUsedGb => MemoryUsed / 1073741824.0;
    public double MemoryTotalGb => MemoryTotal / 1073741824.0;
}
