// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第一阶段（控制台）网络速度读取
using System.Net.NetworkInformation;

namespace HardwareTaskbar.Services;

/// <summary>
/// 网络速度读取。
/// 规则 4：必须用字节差值 / 时间差 计算速度，禁止直接用累计值。
/// 规则 2 技术选型：System.Net.NetworkInformation（不用 WMI）。
/// 只统计物理网卡（以太网/无线），排除回环和虚拟适配器。
/// </summary>
public sealed class NetworkMonitor
{
    private DateTime _lastSampleTime = DateTime.MinValue;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private bool _hasLastSample;

    /// <summary>读取自上次 Read 以来的 ↓下载 / ↑上传 字节数与时间差。</summary>
    public (double BytesPerSecDown, double BytesPerSecUp, double ElapsedMs) Read()
    {
        long received = 0, sent = 0;
        foreach (var nic in GetPhysicalNics())
        {
            var stats = nic.GetIPStatistics();
            if (stats == null) continue;
            // Statistics 是累计值，注意 32 位溢出：GetIPStatistics 内部按 64 位累加，
            // 但部分平台溢出后回绕。这里用 checked 外的差值 + 负数检测兜底。
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        var now = DateTime.UtcNow;
        double elapsedMs = (now - _lastSampleTime).TotalMilliseconds;
        double down = 0, up = 0;

        if (_hasLastSample && elapsedMs > 0)
        {
            // 差值计算（规则 4）；负数说明计数器回绕/重置，本轮按 0 处理并重新基准
            long deltaR = received - _lastBytesReceived;
            long deltaS = sent - _lastBytesSent;
            if (deltaR < 0 || deltaS < 0)
            {
                _hasLastSample = false;
            }
            else
            {
                down = deltaR * 1000.0 / elapsedMs;
                up = deltaS * 1000.0 / elapsedMs;
            }
        }

        _lastBytesReceived = received;
        _lastBytesSent = sent;
        _lastSampleTime = now;
        _hasLastSample = true;
        return (down, up, elapsedMs);
    }

    private static List<NetworkInterface> GetPhysicalNics()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                 n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                 n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet))
            .ToList();
    }
}
