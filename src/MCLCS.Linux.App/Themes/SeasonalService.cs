using System;
using System.Threading.Tasks;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.Themes;

/// <summary>
/// 节日档期的运行时状态（替代 WPF 的 SeasonalThemeManager 中与界面特效耦合的部分）。
/// 只负责「取配置 + 判断当前生效节日」，供节日中心页读取；
/// WPF 那套叠加层 / 音频 / 闪屏依赖 DWM 与 WPF 的 MediaPlayer，Linux 端不移植。
/// </summary>
public static class SeasonalService
{
    private static HolidayConfig? _config;

    /// <summary>最近一次成功载入的节日配置（未载入时为 null）。</summary>
    public static HolidayConfig? CurrentConfig => _config;

    /// <summary>当前生效的节日 key；未生效时为空。</summary>
    public static string CurrentSeasonKey { get; private set; } = "";

    /// <summary>
    /// 启动时（或手动刷新时）拉取配置并计算当前节日。
    /// 全程静默失败：网络不通时退回本地缓存 / 内置默认，绝不抛给调用方。
    /// </summary>
    public static async Task RefreshAsync()
    {
        try
        {
            var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
            if (!profile.SeasonalEffectsEnabled)
            {
                // 用户在设置里关掉了节日特效：不算「当前节日」，节日中心显示空态
                _config = null;
                CurrentSeasonKey = "";
                return;
            }

            var config = await HolidayConfig.LoadAsync(GameConstants.DefaultGameRoot,
                LauncherService.Instance.ApiClient);
            _config = config;
            CurrentSeasonKey = config.PickActive(DateTimeOffset.Now)?.Key ?? "";
        }
        catch
        {
            _config = null;
            CurrentSeasonKey = "";
        }
    }
}
