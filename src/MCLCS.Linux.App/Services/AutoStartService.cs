using System.IO;
using System.Linq;

namespace MCLCS.Linux.App.Services;

/// <summary>
/// 开机自启（Linux 版，对齐 WPF <c>AutoStartService</c> 的接口）。
/// <para>
/// Windows 端写注册表 <c>Run</c> 项，Linux 端按 XDG 规范写
/// <c>~/.config/autostart/MCLCS-Linux.desktop</c> —— 这是桌面环境（GNOME / KDE / Xfce 等）
/// 通用的自启动目录，无需 root 权限。
/// </para>
/// <para>
/// 已知限制：纯 Wayland 会话与部分极简窗口管理器不实现 XDG Autostart，
/// 此时写入文件不会报错，但也不会真正自启；属平台能力差异，不做额外兜底。
/// </para>
/// </summary>
public static class AutoStartService
{
    /// <summary>桌面文件名（同时决定 autostart 目录里的条目名）。</summary>
    private const string DesktopFileName = "MCLCS-Linux.desktop";

    /// <summary>当前进程对应的可执行文件路径（自包含单文件发布时即发行包里的 MCLCS.Linux.App）。</summary>
    public static string ExePath => System.Environment.ProcessPath ?? "";

    /// <summary>XDG 用户级自启动目录。</summary>
    private static string AutostartDir
    {
        get
        {
            var config = System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
                config = Path.Combine(System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart");
        }
    }

    private static string DesktopFilePath => Path.Combine(AutostartDir, DesktopFileName);

    /// <summary>当前是否已设置为开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(DesktopFilePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入自启动条目。</summary>
    public static void Enable()
    {
        try
        {
            Directory.CreateDirectory(AutostartDir);
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            var icon = ResolveIconPath(exe);
            var content = string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                "Version=1.0",
                "Name=MCLCS Launcher",
                "Comment=Minecraft 启动器（燧石启动器 Linux 版）",
                $"Exec=\"{exe}\"",
                "Terminal=false",
                // X-Minecraft 之类的分类不作要求，Game 分类便于在菜单里归类
                "Categories=Game;Utility;",
                string.IsNullOrWhiteSpace(icon) ? "" : $"Icon={icon}",
                "X-GNOME-Autostart-enabled=true",
                "");
            File.WriteAllText(DesktopFilePath, content);
        }
        catch
        {
            // 自启动属非关键功能：写入失败不应影响启动
        }
    }

    /// <summary>移除自启动条目。</summary>
    public static void Disable()
    {
        try
        {
            if (File.Exists(DesktopFilePath)) File.Delete(DesktopFilePath);
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>
    /// 按 profile 同步（开机 / 设置保存时调用）。
    /// 已启用但条目缺失（重装 / 移动目录）时补写；已禁用但残留条目时清理。
    /// </summary>
    public static void Apply(bool enabled)
    {
        if (enabled)
        {
            if (IsEnabled()) RefreshIfStale();
            else Enable();
        }
        else
        {
            Disable();
        }
    }

    /// <summary>条目存在但指向的 Exec 已不是当前程序（移动过安装目录）时重写。</summary>
    private static void RefreshIfStale()
    {
        try
        {
            var exe = ExePath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var lines = File.ReadAllLines(DesktopFilePath);
            var exec = lines.FirstOrDefault(l => l.StartsWith("Exec=", System.StringComparison.Ordinal)) ?? "";
            if (!exec.Contains(exe, System.StringComparison.Ordinal)) Enable();
        }
        catch
        {
            // 读取失败就当没问题，避免反复重写
        }
    }

    /// <summary>同目录下有 mclcs.png 时用它做图标，否则不写 Icon 字段。</summary>
    private static string ResolveIconPath(string exe)
    {
        try
        {
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return "";
            var png = Path.Combine(dir, "mclcs.png");
            return File.Exists(png) ? png : "";
        }
        catch
        {
            return "";
        }
    }
}
