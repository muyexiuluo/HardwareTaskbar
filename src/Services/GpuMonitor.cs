// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar GPU NVML 读取（第二阶段起返回 Models/GpuState）
using System.Runtime.InteropServices;
using System.Text;
using HardwareTaskbar.Models;

namespace HardwareTaskbar.Services;

/// <summary>
/// NVML 封装。全局单例（规则 6：NVML 初始化只能执行一次，全局复用）。
/// 所有 GPU 数据直接走 NVML API，禁止 nvidia-smi / WMI（规则 1、2）。
/// GPU 顺序 = NVML 原生索引顺序（与 nvidia-smi 一致；老大编号：显卡1 = index 0 =
/// RTX 6000 Ada 48G，显卡2 = index 1 = RTX 4080 16G）。2026-09-04 起，原按 UUID 排序（反了）。
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private const string NvmlLib = "nvml";

    private static readonly Lazy<GpuMonitor> _instance = new(() => new GpuMonitor());

    /// <summary>全局唯一实例，NVML 初始化只发生一次。</summary>
    public static GpuMonitor Instance => _instance.Value;

    private readonly List<IntPtr> _handles = new();   // NVML 索引顺序（nvidia-smi 同款）
    private readonly List<string> _uuids = new();
    private readonly List<string> _busIds = new();
    private int _nvmlInitError = Native.NVML_SUCCESS;
    private bool _disposed;

    private GpuMonitor()
    {
        _nvmlInitError = Native.nvmlInit_v2();
        if (_nvmlInitError != Native.NVML_SUCCESS)
            return;

        uint count;
        if (Native.nvmlDeviceGetCount_v2(out count) != Native.NVML_SUCCESS)
        {
            Native.nvmlShutdown();
            _nvmlInitError = Native.NVML_ERROR_UNKNOWN;
            return;
        }

        // 1) 枚举全部设备句柄 + 唯一标识
        var pairs = new List<(IntPtr Handle, string Uuid, string BusId)>();
        for (uint i = 0; i < count; i++)
        {
            if (Native.nvmlDeviceGetHandleByIndex_v2(i, out IntPtr h) != Native.NVML_SUCCESS)
                continue;
            pairs.Add((h, GetUuid(h), GetPciBusId(h)));
        }

        // 2) 保持 NVML 原生索引顺序（= nvidia-smi 显示顺序）（2026-09-04 修复：
        //    原来按 UUID 排序把 16G 卡排到了前面，与老大的 显卡1/2 编号正好相反）
        //    本机：index 0 = RTX 6000 Ada 48G = 显卡1，index 1 = RTX 4080 16G = 显卡2。
        foreach (var (handle, uuid, busId) in pairs)
        {
            _handles.Add(handle);
            _uuids.Add(uuid);
            _busIds.Add(busId);
        }
    }

    /// <summary>NVML 初始化错误码；0 = 成功。</summary>
    public int InitError => _nvmlInitError;

    /// <summary>各卡 UUID（NVML 索引顺序）。</summary>
    public IReadOnlyList<string> Uuids => _uuids;

    private static string GetUuid(IntPtr handle)
    {
        var sb = new StringBuilder(96);
        if (Native.nvmlDeviceGetUUID(handle, sb, sb.Capacity) != Native.NVML_SUCCESS)
            return string.Empty; // 读取失败用空串，排序时自然靠后
        return sb.ToString();
    }

    private static string GetPciBusId(IntPtr handle)
    {
        if (Native.nvmlDeviceGetPciInfo(handle, out Native.NvmlPciInfo info) != Native.NVML_SUCCESS)
            return string.Empty; // 本机驱动读空，仅展示用
        var sb = new StringBuilder(16);
        foreach (byte b in info.pciBusId)
        {
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>一次性读取全部 GPU（NVML 索引顺序，返回统一数据模型）。</summary>
    public IReadOnlyList<GpuState> ReadAll()
    {
        if (_nvmlInitError != Native.NVML_SUCCESS)
            return Array.Empty<GpuState>();

        var result = new List<GpuState>();
        for (int slot = 0; slot < _handles.Count; slot++)
        {
            IntPtr h = _handles[slot];
            var mem = GetMemory(h);
            result.Add(new GpuState
            {
                Slot = slot,
                Name = GetDeviceName(h),
                Uuid = _uuids[slot],
                Utilization = GetUtilization(h),
                Temperature = GetTemperature(h),
                MemoryUsed = mem.used,
                MemoryTotal = mem.total
            });
        }
        return result;
    }

    private static string GetDeviceName(IntPtr h)
    {
        var sb = new StringBuilder(64);
        return Native.nvmlDeviceGetName(h, sb, sb.Capacity) == Native.NVML_SUCCESS
            ? sb.ToString()
            : "Unknown GPU";
    }

    private static int? GetUtilization(IntPtr h)
    {
        // 注意：本机驱动（32.0.16.1088 / 580 系）只导出无版本号的
        // nvmlDeviceGetUtilizationRates，v2/v3/v4 均未导出，必须用基础版本。
        return Native.nvmlDeviceGetUtilizationRates(h, out Native.NvmlUtilization u) == Native.NVML_SUCCESS
            ? (int?)u.gpu
            : null;
    }

    private static int? GetTemperature(IntPtr h)
    {
        // sensor 0 = GPU 核心温度
        return Native.nvmlDeviceGetTemperature(h, 0, out int t) == Native.NVML_SUCCESS
            ? (int?)t
            : null;
    }

    private static (long used, long total) GetMemory(IntPtr h)
    {
        // 注意：本机驱动（32.0.16.1088 / 580 系）nvmlDeviceGetMemoryInfo_v2 返回
        // NVML_ERROR_NOT_SUPPORTED(2)，必须用无版本号的 v1 入口。
        if (Native.nvmlDeviceGetMemoryInfo(h, out Native.NvmlMemory m) != Native.NVML_SUCCESS)
            return (0, 0);
        return ((long)m.used, (long)m.total);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_nvmlInitError == Native.NVML_SUCCESS)
            Native.nvmlShutdown();
    }

    /// <summary>NVML 常量与 P/Invoke（全部走原生 API）。</summary>
    private static class Native
    {
        public const int NVML_SUCCESS = 0;
        public const int NVML_ERROR_UNKNOWN = -1;

        [DllImport(NvmlLib, EntryPoint = "nvmlInit_v2")]
        internal static extern int nvmlInit_v2();

        [DllImport(NvmlLib)]
        internal static extern int nvmlShutdown();

        [DllImport(NvmlLib, EntryPoint = "nvmlDeviceGetCount_v2")]
        internal static extern int nvmlDeviceGetCount_v2(out uint count);

        [DllImport(NvmlLib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(NvmlLib, CharSet = CharSet.Ansi)]
        internal static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, int length);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlUtilization
        {
            public uint gpu;
            public uint memory;
        }

        [DllImport(NvmlLib)]
        internal static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(NvmlLib)]
        internal static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out int temp);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlMemory
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [DllImport(NvmlLib, EntryPoint = "nvmlDeviceGetMemoryInfo")]
        internal static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memInfo);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NvmlPciInfo
        {
            public uint bus;
            public uint device;
            public uint domain;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] pciBusId;
        }

        [DllImport(NvmlLib)]
        internal static extern int nvmlDeviceGetPciInfo(IntPtr device, out NvmlPciInfo info);

        [DllImport(NvmlLib)]
        internal static extern int nvmlDeviceGetUUID(IntPtr device, StringBuilder uuid, int bufferLen);
    }
}
