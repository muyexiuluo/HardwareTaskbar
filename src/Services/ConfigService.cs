// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 第五阶段：config.json 配置（Newtonsoft.Json，不用注册表）
using System;
using System.IO;
using Newtonsoft.Json;

namespace HardwareTaskbar.Services;

/// <summary>配置模型，字段与 config.json 一一对应。</summary>
public sealed class AppSettings
{
    /// <summary>刷新间隔（毫秒），最低 500。</summary>
    public int RefreshInterval { get; set; } = 1000;

    public bool ShowGpu0 { get; set; } = true;
    public bool ShowGpu1 { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;

    /// <summary>悬浮窗字体大小（DIP）。</summary>
    public double FontSize { get; set; } = 13;

    /// <summary>开机自启（计划任务，非注册表 Run 键）。</summary>
    public bool StartWithWindows { get; set; }
}

/// <summary>
/// config.json 读写 + 外部修改监听。
/// 规则：不用注册表保存配置；修改后立即生效（本类在保存/外部变更时触发 ExternalChanged）。
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly object _gate = new();
    private System.IO.FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private AppSettings _current = new();

    /// <summary>配置文件路径：exe 同目录（第六阶段发布三件套之一）。</summary>
    public string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>配置变化（保存或外部修改）后触发，参数 = 新配置。订阅者负责切 UI 线程。</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>加载配置；仅当文件缺失/损坏时写默认值（不覆盖现有配置，避免启动即回写破坏外部修改）。</summary>
    public void Load()
    {
        lock (_gate)
        {
            bool needDefault = true;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    _current = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(ConfigPath)) ?? new AppSettings();
                    needDefault = false;
                }
            }
            catch
            {
                _current = new AppSettings();
            }
            if (needDefault)
                SaveLocked(_current); // 只有缺文件/损坏才落一份默认
        }
    }

    /// <summary>保存配置并触发 Changed（立即生效）。</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            _current = settings;
            SaveLocked(settings);
        }
        Changed?.Invoke(settings);
    }

    private void SaveLocked(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(s, Formatting.Indented));
        }
        catch
        {
            // 配置写失败不致命，保留内存值
        }
    }

    /// <summary>监听 config.json 外部修改（带 300ms 防抖），变化后触发 Changed。</summary>
    public void StartWatching()
    {
        try
        {
            _watcher = new System.IO.FileSystemWatcher(Path.GetDirectoryName(ConfigPath)!, "config.json")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) =>
            {
                _debounce?.Dispose();
                _debounce = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        lock (_gate)
                        {
                            var newCfg = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(ConfigPath));
                            if (newCfg != null)
                            {
                                _current = newCfg;
                            }
                        }
                        Changed?.Invoke(Current);
                    }
                    catch
                    {
                        // 文件正在写，忽略
                    }
                }, null, 300, Timeout.Infinite);
            };
        }
        catch
        {
            // 监听失败不影响主功能
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
