// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第二阶段：唯一数据门面 SensorService
// 规则 8：UI/ViewModel 永远只接收 HardwareState，不允许调用任何硬件 API。
// 本类对外只暴露一个方法：GetCurrentState()
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using HardwareTaskbar.Models;

namespace HardwareTaskbar.Services;

/// <summary>
/// 硬件数据统一门面（全局单例）。
/// 内部持有全部读取器（GPU/CPU/网络/LHM），一次性读取聚合为 HardwareState。
/// 规则 6/7：NVML 与 LHM Computer 各自只初始化一次，全局复用。
/// </summary>
public sealed class SensorService : IDisposable
{
    private static readonly Lazy<SensorService> _instance = new(() => new SensorService());

    /// <summary>全局唯一实例。</summary>
    public static SensorService Instance => _instance.Value;

    private readonly GpuMonitor _gpu = GpuMonitor.Instance;
    private readonly CpuMonitor _cpuUsage = new CpuMonitor();
    private readonly Computer _computer;
    private readonly IHardware? _cpu;
    private readonly ISensor? _packageTempSensor;
    private readonly NetworkMonitor _network = new();
    private readonly object _gate = new();

    private SensorService()
    {
        // LHM：Computer 只创建一次（规则 7）。只开 CPU（温度），其余控制器全关，启动快。
        _computer = new Computer { IsCpuEnabled = true };
        _computer.Open();

        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
            {
                _cpu = hw;
                _packageTempSensor = hw.Sensors
                    .FirstOrDefault(s =>
                        s.SensorType == SensorType.Temperature &&
                        s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
                break;
            }
        }

        // 预热：PerformanceCounter 与网络差值首轮需要一个基准
        _cpuUsage.Read();
        _network.Read();
    }

    /// <summary>
    /// 一次性读取全部传感器，返回完整 HardwareState 快照。
    /// GPU 失败 → Gpus 为空列表；CPU 占用/温度失败 → 对应字段 null（禁止 0℃，规则 5）。
    /// </summary>
    public HardwareState GetCurrentState()
    {
        var gpus = _gpu.ReadAll();

        double? cpuUsage = _cpuUsage.Read() is { } u ? (double?)u : null;
        double? cpuTemp = ReadCpuPackageTemperature();

        var (down, up, _) = _network.Read();

        return new HardwareState
        {
            CpuUsage = cpuUsage,
            CpuTemperature = cpuTemp,
            DownloadSpeed = down,
            UploadSpeed = up,
            Gpus = gpus,
            Timestamp = DateTime.Now
        };
    }

    private double? ReadCpuPackageTemperature()
    {
        lock (_gate)
        {
            if (_cpu == null || _packageTempSensor == null)
                return null;

            _cpu.Update();
            var value = _packageTempSensor.Value;
            // 合理性范围检查：超出即视为无效，返回 null（规则 5：禁止显示 0℃）
            return value is > 0 and < 125 ? (double?)value : null;
        }
    }

    public void Dispose()
    {
        _cpuUsage.Dispose();
        _computer.Close();
    }
}
