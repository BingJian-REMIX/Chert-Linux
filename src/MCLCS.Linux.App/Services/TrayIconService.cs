using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MCLCS.Core.Profiles;

namespace MCLCS.Linux.App.Services;

/// <summary>
/// 系统托盘图标（Linux 版，对齐 WPF <c>TrayIconService</c> 的用途）。
/// <para>
/// Avalonia 用 <see cref="TrayIcon"/> 实现，底层走状态通知协议（XEmbed / StatusNotifierItem）。
/// 桌面环境不提供托盘时（部分纯 Wayland 会话、极简 WM）图标可能不显示 —— 这是平台能力差异，
/// 因此全部操作都做静默兜底：托盘不可用不影响启动，也不会抛异常。
/// </para>
/// </summary>
public static class TrayIconService
{
    private static TrayIcon? _icon;
    private static MainWindow? _owner;

    /// <summary>托盘是否已在运行（图标已挂载）。</summary>
    public static bool IsActive => _icon is not null;

    /// <summary>
    /// 初始化托盘（在 MainWindow 构造后调用一次）。
    /// 图标与右键菜单：显示主窗口 / 退出。
    /// </summary>
    public static void Initialize(MainWindow owner)
    {
        _owner = owner;
        try
        {
            if (_icon is not null) return;

            Bitmap? bitmap = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://MCLCS.Linux.App/Resources/mclcs.png"));
                bitmap = new Bitmap(stream);
            }
            catch { /* 图标缺失不致命，用无图标托盘项 */ }

            var menu = new NativeMenu();

            var showItem = new NativeMenuItem("显示主窗口");
            showItem.Click += (_, _) => ShowMainWindow();
            menu.Add(showItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) => ExitApplication();
            menu.Add(exitItem);

            _icon = new TrayIcon
            {
                Icon = bitmap is null ? null : new WindowIcon(bitmap),
                ToolTipText = "MCLCS 启动器",
                Menu = menu,
                // 左键单击 = 显示主窗口（Avalonia 的 Clicked 在部分后端不可靠，故同时挂菜单）
                IsVisible = true
            };
            _icon.Clicked += (_, _) => ShowMainWindow();

            // Avalonia 11 里托盘图标是 Application 上的附加属性，必须挂进去才会渲染
            var app = Application.Current;
            if (app is null) { _icon = null; return; }
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            if (!icons.Contains(_icon)) icons.Add(_icon);
            TrayIcon.SetIcons(app, icons);
        }
        catch
        {
            _icon = null;
        }
    }

    /// <summary>把主窗口重新显示出来（并置于前台）。</summary>
    public static void ShowMainWindow()
    {
        try
        {
            var w = _owner ?? App.MainWindow;
            if (w is null) return;
            w.Show();
            w.WindowState = WindowState.Normal;
            w.Activate();
        }
        catch { /* 托盘属非关键功能 */ }
    }

    /// <summary>真正退出应用（托盘菜单「退出」用）。</summary>
    public static void ExitApplication()
    {
        try
        {
            Dispose();
            if (Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 窗口被请求关闭时的处理：开启「最小化到托盘」时改为隐藏窗口（进程继续在托盘运行），
    /// 否则返回 false 让默认关闭流程真正退出。
    /// </summary>
    /// <returns>true = 已接管（不要关闭窗口）；false = 交给默认流程（真正退出）。</returns>
    public static bool HandleWindowClosing()
    {
        try
        {
            var minimize = false;
            try
            {
                minimize = ProfileStore.Load(MCLCS.Core.Utils.GameConstants.DefaultGameRoot).MinimizeToTray;
            }
            catch { /* 配置不可读时按「直接退出」处理 */ }

            if (!minimize || !IsActive) return false;

            var w = _owner ?? App.MainWindow;
            if (w is null) return false;
            w.Hide();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>退出前移除托盘图标，避免进程退出后残留。</summary>
    public static void Dispose()
    {
        try
        {
            if (_icon is null) return;
            if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons && icons.Contains(_icon))
                icons.Remove(_icon);
            _icon.Dispose();
            _icon = null;
        }
        catch
        {
            _icon = null;
        }
    }
}
