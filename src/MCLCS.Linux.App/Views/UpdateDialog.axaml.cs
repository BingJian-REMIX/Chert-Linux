using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MCLCS.Core.Download;
using MCLCS.Core.Update;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.Views;

/// <summary>
/// 更新可用时的模态弹窗（移植自 WPF UpdateDialog）：展示新版本号与更新日志（来自 latest.json 的 changelog），
/// 提供「下载更新」（用启动器内置 HttpDownloader 拉取 CNB Release Linux 包直链，下载完成后打开所在文件夹，
/// 由用户解压覆盖；Windows 端用 PowerShell 自替换，Linux 端无 PowerShell，故改为下载后打开目录）与「稍后」按钮。
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateCheckResult _result;

    public UpdateDialog()
    {
        InitializeComponent();
    }

    public UpdateDialog(UpdateCheckResult result) : this()
    {
        _result = result;

        TitleText.Text = $"发现新版本 v{result.LatestVersion}";

        // 紧急更新（status=emgent）：显示红色横幅并置边框高亮，副标题强调立即安装。
        if (result.Status == "emgent")
        {
            EmergencyBanner.IsVisible = true;
            CardBorder.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        }
        SubtitleText.Text = $"当前 {result.CurrentVersion} → 最新 {result.LatestVersion}" +
                            (result.Status == "emgent"
                                ? "（紧急更新，请尽快安装）"
                                : (result.Mandatory ? "（建议立即更新）" : ""));

        ChangelogBox.Text = string.IsNullOrWhiteSpace(result.Changelog)
            ? "（无法获取更新日志，请点击下方「下载更新」在发布页查看详情）"
            : result.Changelog;
    }

    /// <summary>检查更新并在有可用版本时弹出对话框（供启动后 / 「检查更新」按钮调用）。</summary>
    public static async Task CheckAndShowAsync(Window? owner)
    {
        try
        {
            var result = await LauncherUpdater.CheckAsync(GameConstants.LauncherVersion);
            if (!result.Available)
            {
                if (owner is not null)
                    ToastService.Instance.Show(new ToastOptions
                    {
                        Title = "更新检查",
                        Message = string.IsNullOrWhiteSpace(result.Error) ? "已是最新版本" : $"检查失败：{result.Error}"
                    });
                return;
            }

            var dlg = new UpdateDialog(result);
            if (owner is not null) await dlg.ShowDialog(owner);
            else dlg.Show();
        }
        catch (Exception ex)
        {
            ToastService.Instance.Show(new ToastOptions { Title = "更新检查失败", Message = ex.Message });
        }
    }

    /// <summary>点击「下载更新」：用内置下载器拉取 Linux 包直链，下载完成后打开所在文件夹并提供提示。</summary>
    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        var url = _result.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            TryOpenBrowser(GameConstants.GitHubRepoUrl + "/releases");
            Close();
            return;
        }

        DownloadButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ProgressPanel.IsVisible = true;
        StatusText.Text = "正在通过内置下载器获取更新包…";

        var version = _result.LatestVersion ?? GameConstants.LauncherVersion;
        var updRoot = Path.Combine(Path.GetTempPath(), "MCLCS", "update");
        Directory.CreateDirectory(updRoot);
        var zipPath = Path.Combine(updRoot, $"MCLCS-v{version}-Linux-x64.zip");

        var progress = new Progress<double>(p =>
        {
            ProgressBar.Value = p;
            StatusText.Text = $"下载中… {Math.Round(p * 100)}%";
        });

        try
        {
            var downloader = new HttpDownloader(new HttpClient { Timeout = TimeSpan.FromSeconds(120) }, 8, null);
            await downloader.DownloadAsync(
                new DownloadItem(new[] { url }, zipPath, null), progress, default);

            StatusText.Text = "下载完成，已打开更新包所在文件夹，请解压覆盖安装目录。";
            ToastService.Instance.Show(new ToastOptions
            {
                Title = "更新包已就绪",
                Message = $"已下载到 {zipPath}"
            });

            // 打开下载目录（Linux 下由文件管理器接管）
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(zipPath) ?? updRoot,
                    UseShellExecute = true
                });
            }
            catch { /* 打开文件管理器失败不影响下载结果 */ }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"更新失败：{ex.Message}";
            TryOpenBrowser(url);
            DownloadButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打开浏览器失败不影响弹窗 */ }
    }

    private void Later_Click(object? sender, RoutedEventArgs e) => Close();
}
