// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第一阶段（控制台）CPU 占用率读取
using System.Diagnostics;
using System.Runtime.Versioning;

namespace HardwareTaskbar.Services;

[SupportedOSPlatform("windows")]

/// <summary>
/// CPU 占用率读取（PerformanceCounter，等价 psutil.cpu_percent 采样法）。
/// 注意："% Processor Time" 是"自上次采样以来"的百分比，必须间隔采样才有意义，
/// 所以本类每次 Read() 与上次采样间隔内的占用率一起返回。
/// 温度不走这里（规则 2：禁 WMI），由 SensorService 里的 LibreHardwareMonitor 提供。
/// </summary>
public sealed class CpuMonitor : IDisposable
{
    private readonly PerformanceCounter _counter;

    public CpuMonitor()
    {
        _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total")
        {
            // 避免采样间隔内的死区
            MachineName = "."
        };
        _counter.NextValue(); // 首次采样无效，丢弃
    }

    /// <summary>读取自上次 Read 以来的 CPU 占用率 0-100；失败返回 null。</summary>
    public float? Read()
    {
        try
        {
            return _counter.NextValue();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _counter.Dispose();
}
