// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第三阶段：后台监控循环
// 规则（第三阶段）：Task + CancellationToken；每 1000ms 一次；所有传感器一次性读取；
// 禁止 DispatcherTimer 直接读硬件、禁止 UI 线程调 NVML、禁止多个 Timer 同时刷新。
using HardwareTaskbar.Models;

namespace HardwareTaskbar.Services;

/// <summary>
/// 后台硬件刷新循环。独立线程（Task），固定间隔读取全部传感器，
/// 每次产出新的 HardwareState 快照并通过 StateChanged 事件通知订阅者。
/// 流程：后台线程 → 读 GPU/CPU/Network（一次性）→ 生成 HardwareState → 通知 UI。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly object _sensorsGate = new();
    private SensorService? _sensors;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private int _refreshMs;
    private volatile bool _running;

    /// <summary>每次刷新后触发（参数 = 最新状态快照）。在后台线程触发，订阅者自行切 UI 线程。</summary>
    public event EventHandler<HardwareState>? StateChanged;

    /// <summary>当前刷新间隔（毫秒）。运行时可改，下一轮生效。</summary>
    public int RefreshIntervalMs
    {
        get => _refreshMs;
        set => _refreshMs = Math.Max(500, value); // 规则 10：不低于 500ms
    }

    public bool IsRunning => _running;

    private volatile string? _lastError;

    /// <summary>最近一轮读取的异常（诊断用；正常时无值）。volatile 跨线程可见。</summary>
    public string? LastError => _lastError;

    public HardwareMonitor(int refreshMs = 1000)
    {
        RefreshIntervalMs = refreshMs;
    }

    /// <summary>懒加载 SensorService：LHM/PawnIO 初始化耗时数秒，
    /// 必须在后台线程执行（首次读时初始化），不能阻塞 UI 线程。</summary>
    private SensorService GetSensors()
    {
        if (_sensors == null)
        {
            lock (_sensorsGate)
            {
                _sensors ??= SensorService.Instance;
            }
        }
        return _sensors;
    }

    /// <summary>启动后台循环（幂等：已在运行则忽略）。</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _task = Task.Run(() => Loop(_cts.Token));
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 一次性读取全部传感器（GPU + CPU + 网络）；
                // 首次调用会在此线程初始化 LHM（3-7 秒），不阻塞 UI
                var state = GetSensors().GetCurrentState();
                StateChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                // 单轮读取失败不杀循环；下一轮重试。记录异常供探针/排障读取
                _lastError = ex.ToString();
            }

            try
            {
                Task.Delay(RefreshIntervalMs, token).Wait();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (AggregateException)
            {
                // 取消时 Task.Delay().Wait() 会把 OperationCanceledException 包进
                // AggregateException 抛出（2026-09-04 僵尸进程 bug 的根源），不能让它穿透
                if (token.IsCancellationRequested) break;
            }
        }
        _running = false;
    }

    /// <summary>停止后台循环。</summary>
    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        // 任务在取消时可能处于 canceled/faulted 状态，Wait 会抛 AggregateException；
        // 退出路径必须无条件走完，这里吞掉一切异常
        try { _task?.Wait(2000); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        // SensorService 是全局单例，由 App 统一释放，这里不关
    }
}
