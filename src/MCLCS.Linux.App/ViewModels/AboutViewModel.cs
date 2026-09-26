using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → 关于：静态信息页，展示应用名、版本、简介与仓库链接。
/// </summary>
public class AboutViewModel : ObservableObject
{
    public string AppName => "朗希夏";
    public string Version => "0.1.0 (Linux 移植)";
    public string Tagline => "跨平台 Minecraft 启动器与工具集";
    public string Description =>
        "MCLCS-Linux 是 MCLCS-WPF 在 Avalonia 上的 Linux 移植版，功能与标准版同步："
        + "启动 / 安装 / 账号 / Java / 日志 / 清理 / 备份 / 截图 / Mod / 存档 / 皮肤 / AI 助手等。";

    public string RepositoryCnb => "https://cnb.cool/RLRS-Studio/MCLCS-Linux";
    public string RepositoryGithub => "https://github.com/BingJian-REMIX/MCLCS-Linux";

    public string Framework => "Avalonia 11.2.8 · .NET 10.0";

    public string LocalizedTitle => LocaleManager.T("settings.about");
}
