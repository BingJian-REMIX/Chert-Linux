using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCLCS.Core.Localization;
using MCLCS.Core.Profiles;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Views;

namespace MCLCS.Linux.App.Services;

/// <summary>
/// 文件变更自动检测服务（规格 2.3-16 / 用户需求：启动器启动或焦点回到启动器时自动检测）：
/// 对默认游戏目录下的 mods / resourcepacks / shaderpacks 做两段式检测（先比元数据、变了再哈希），
/// 发现新增文件则弹右下角 Toast（"查看详情"打开 <see cref="FileWatchWindow"/>）。
/// 由 MainWindow 的 Opened / Activated 事件驱动；受「启用文件监控」开关控制。
/// </summary>
public sealed class FileWatchService
{
    public static readonly FileWatchService Instance = new();

    private bool _scanning;
    private DateTime _lastScan = DateTime.MinValue;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);

    /// <summary>后台扫描：去抖 + 开关门控 + 新文件 Toast。异常静默吞掉，不污染主流程。</summary>
    public async Task RunScanAsync()
    {
        if (_scanning) return;
        var now = DateTime.Now;
        if (now - _lastScan < Debounce) return;   // 焦点频繁抖动时最多每 30s 扫一次

        _scanning = true;
        _lastScan = now;
        try
        {
            var gameRoot = GameConstants.DefaultGameRoot;
            if (!ProfileStore.Load(gameRoot).FileWatchEnabled) return;

            // ★ 不能只扫 gameRoot：隔离版的 mods / resourcepacks / shaderpacks 都在
            //   versions/<id>/ 下，只查共享位置等于对隔离版视而不见（WPF 侧同样踩过）。
            //   每个目录各持一份基线快照，互不干扰。
            var added = await Task.Run(() =>
            {
                var all = new List<FileChange>();
                foreach (var dir in FileChangeDetector.AllWatchDirs(gameRoot))
                    all.AddRange(FileChangeDetector.NewFilesOnly(FileChangeDetector.DetectTwoStage(dir)));
                return all;
            });
            if (added.Count == 0) return;

            var preview = string.Join("、", added.Take(3).Select(c => c.Path));
            var more = added.Count > 3 ? $" 等共 {added.Count} 个" : "";
            MCLCS.Linux.App.ToastService.Instance.Show(new ToastOptions
            {
                Title = LocaleManager.T("tool.filewatch"),
                Message = LocaleManager.Tf("tool.filewatch.toast", preview + more),
                DurationMs = 8000,
                ActionText = LocaleManager.T("tool.filewatch.detail"),
                Action = OpenDetails
            });
        }
        catch
        {
            // 检测失败不打扰用户（目录权限 / IO 异常等）
        }
        finally
        {
            _scanning = false;
        }
    }

    private static void OpenDetails()
    {
        var win = new FileWatchWindow();
        if (App.MainWindow is { } owner) win.Show(owner);
        else win.Show();
    }
}
