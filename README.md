# HardwareTaskbar

Windows 11 硬件任务栏监控悬浮窗：把 CPU / 双显卡（温度、显存、利用率）/ 网络速度的实时数据以小号文字贴在系统任务栏上，带托盘图标、文字右键菜单、隐身模式、设置窗口、可选开机自启。

- 语言/框架：C# WPF + .NET 8（net8.0-windows，win-x64）
- 硬件读取：[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 net8.0 本地构建（`thirdparty/librehwmonitor`，含 2 处补丁：ArcticFanController 编译修复 + System.Threading.AccessControl 10→8 适配），网络速率用 PDH
- 权限：读取 CPU 温度需要 ring0 MSR 访问，**程序以管理员权限运行**（App.manifest 声明 requireAdministrator）

## 目录结构

```
.
├── HardwareTaskbar.exe   ← 直接跑这个（framework-dependent，需 .NET 8 Desktop Runtime）
├── *.dll                 依赖库（LHM、DiskInfoToolkit、HidSharp、BlackSharp.Core 等）
├── config.json           运行时配置（字号、显示开关、刷新间隔、自启）
├── src\                  源码工程（dotnet build / publish 在这里跑）
└── thirdparty\librehwmonitor\   LHM 0.9.6 源码（已打补丁，bin\Release\x64\net8.0\ 有构建产物）
```

## 使用

1. 右键 `HardwareTaskbar.exe` → 以管理员身份运行
2. 数据文字出现在任务栏右侧（放得下右侧就贴右侧，否则自动落左侧）
3. 文字上右键：设置 / 隐身 / 退出；托盘图标菜单功能相同
4. 设置窗口里可勾选「开机自启」（创建 schtasks 计划任务，点「应用」生效）

## 构建

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained false -o ..
```

注意：`HardwareTaskbar.csproj` 通过相对路径 `..\thirdparty\librehwmonitor\bin\Release\x64\net8.0\` 引用 LHM 的 DLL，**不要拆散 src 与 thirdparty 的相对位置**；若 LHM DLL 缺失，先在 `thirdparty\librehwmonitor` 里编译 LibreHardwareMonitorLib（net8.0）。

## 配置项（config.json）

| 字段 | 说明 |
|---|---|
| RefreshInterval | 刷新间隔（毫秒） |
| ShowGpu0 / ShowGpu1 | 显示第 1 / 2 张显卡 |
| ShowCpu | 显示 CPU |
| ShowNetwork | 显示网络速率 |
| FontSize | 字号 |
| StartWithWindows | 开机自启（以计划任务真实状态为准） |

## 许可证

主程序代码 MIT；LHM 及其上游依赖遵循各自许可证（见 `thirdparty/librehwmonitor/LICENSE` 与 `THIRD-PARTY-NOTICES.txt`）。
