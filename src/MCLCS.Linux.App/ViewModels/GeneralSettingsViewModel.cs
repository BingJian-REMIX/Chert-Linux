using System;
using System.Windows.Input;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → 常规（对齐 WPF Settings→General）：语言切换（即时生效）+ 启动/界面相关开关，
/// 全部持久化到 LauncherProfile。其中<b>动画开关已接入运行时</b>（驱动 MotionFx.AnimationsEnabled，
/// 设置页分类切换等过渡动画即刻生效）；文件监控 / 开机自启 / 最小化托盘 / 高清图标
/// 目前在 Linux 端尚未接入运行时消费者，仅落盘 profile（与下载页 ServerPackCache 同策略），后续逐步接线。
/// </summary>
public class GeneralSettingsViewModel : ObservableObject
{
    private readonly LauncherProfile _profile;

    private bool _animationsEnabled = true;
    private bool _fileWatchEnabled = true;
    private bool _autoStartLauncher;
    private bool _minimizeToTray;
    private bool _highDpiIcons;

    // ---- 语言（即时生效）----
    public string SelectedLanguage
    {
        get => LocaleManager.CurrentLocale;
        set
        {
            var norm = LocaleManager.NormalizeLocaleCode(value);
            if (!string.Equals(LocaleManager.CurrentLocale, norm, StringComparison.OrdinalIgnoreCase))
            {
                LocaleManager.CurrentLocale = norm;
                _profile.Language = norm;
                OnPropertyChanged();
            }
        }
    }

    public string LangChinese => LocaleManager.T("lbl.chinese");
    public string LangEnglish => LocaleManager.T("lbl.english");

    // ---- 启动 / 界面开关（持久化 profile）----
    /// <summary>
    /// 动画总开关。切换后立即同步到运行时 <see cref="MotionFx.AnimationsEnabled"/>（即时生效），
    /// 并随 <see cref="SaveCommand"/> 落盘 profile。此前该开关只落盘、无消费者，
    /// 关闭动画对页面过渡无任何作用。
    /// </summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (SetField(ref _animationsEnabled, value))
                MotionFx.AnimationsEnabled = value;
        }
    }
    public bool FileWatchEnabled { get => _fileWatchEnabled; set => SetField(ref _fileWatchEnabled, value); }
    public bool AutoStartLauncher { get => _autoStartLauncher; set => SetField(ref _autoStartLauncher, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetField(ref _minimizeToTray, value); }
    public bool HighDpiIcons
    {
        get => _highDpiIcons;
        set
        {
            if (SetField(ref _highDpiIcons, value))
                Converters.IconManager.HighDpi = value; // 即时生效：图标加载切 2x 目录（对齐 WPF IconManager）
        }
    }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetField(ref _status, value); }

    public ICommand SaveCommand => new RelayCommand(_ => Save());

    public GeneralSettingsViewModel()
    {
        _profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
        _animationsEnabled = _profile.AnimationsEnabled;
        _fileWatchEnabled = _profile.FileWatchEnabled;
        _autoStartLauncher = _profile.AutoStartLauncher;
        _minimizeToTray = _profile.MinimizeToTray;
        _highDpiIcons = _profile.HighDpiIcons;
    }

    private void Save()
    {
        try
        {
            _profile.Language = SelectedLanguage;
            _profile.AnimationsEnabled = _animationsEnabled;
            _profile.FileWatchEnabled = _fileWatchEnabled;
            _profile.AutoStartLauncher = _autoStartLauncher;
            _profile.MinimizeToTray = _minimizeToTray;
            _profile.HighDpiIcons = _highDpiIcons;
            ProfileStore.Save(_profile);
            Status = "常规设置已保存";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
