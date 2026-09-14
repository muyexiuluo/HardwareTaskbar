// 由 大龙虾 于 2026-09-04 创建，用于 HardwareTaskbar 开机自启（任务计划程序，管理员静默运行）
// 说明：requireAdministrator 程序放 Startup 文件夹每次开机弹 UAC，不可接受；
// 用计划任务 "At log on" + Run whether user is logged on or not（最高权限）静默启动。
using System.Diagnostics;
using System.Text;

namespace HardwareTaskbar.Services;

public static class AutostartService
{
    private const string TaskName = "HardwareTaskbarStartup";

    /// <summary>创建/删除开机自启计划任务。需要管理员权限（本程序 manifest 已声明）。</summary>
    public static void SetEnabled(bool enabled)
    {
        string exePath = AppContext.BaseDirectory + "HardwareTaskbar.exe";
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (enabled)
        {
            // 先删后建，保证幂等
            DeleteQuietly(psi);
            psi.ArgumentList.Add("/Create");
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add($"/TN {TaskName}");
            psi.ArgumentList.Add("/SC ONLOGON");
            psi.ArgumentList.Add("/RU Administrators");
            psi.ArgumentList.Add("/RL HIGHEST");
            psi.ArgumentList.Add($"/TR \"\"{exePath}\"\"");
        }
        else
        {
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add($"/TN {TaskName}");
        }

        using var p = Process.Start(psi) ?? throw new SystemException("无法启动 schtasks");
        p.WaitForExit(10000);
        if (p.ExitCode != 0)
            throw new SystemException($"schtasks 失败(Exit={p.ExitCode}): {p.StandardError.ReadToEnd()}");
    }

    /// <summary>当前是否已配置开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("/Query");
            psi.ArgumentList.Add($"/TN {TaskName}");
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            string outp = p.StandardOutput.ReadToEnd();
            return p.ExitCode == 0 && outp.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteQuietly(ProcessStartInfo template)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add($"/TN {TaskName}");
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch
        {
            // 不存在则忽略
        }
    }
}
