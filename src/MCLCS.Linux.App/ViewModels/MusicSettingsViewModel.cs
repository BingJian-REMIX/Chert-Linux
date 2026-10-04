using System;
using System.IO;
using System.Windows.Input;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → 音乐（对齐 WPF SettingsView 的 GridMusic 分区）。
///
/// 播放偏好（降音量 / 自动续播）+ 本地客户端模式四项设置（总开关 / 宽限期 /
/// 歌词固定方式 / 客户端模式仍用 API 歌词）。
///
/// 策略：<b>改一项即存盘并即时生效</b>（与 WPF 一致），不设「保存」按钮 ——
/// 这些都是轻量标量，用户拖滑块时不该再点一次保存。
/// </summary>
public class MusicSettingsViewModel : ObservableObject
{
    private readonly string _gameRoot = GameConstants.DefaultGameRoot;

    public MusicSettingsViewModel()
    {
        BrowseClientCommand = new RelayCommand(_ => BrowseClient());
    }

    public ICommand BrowseClientCommand { get; }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>供视图层回写操作结果（保存成功/失败提示）。</summary>
    public void SetStatus(string message) => Status = message;

    // ---- 播放偏好 ----

    /// <summary>游戏启动时自动降低音乐音量。</summary>
    public bool MusicAutoDuck
    {
        get => Load().MusicAutoDuck;
        set
        {
            if (Load().MusicAutoDuck == value) return;
            Mutate(p => p.MusicAutoDuck = value);
            OnPropertyChanged();
        }
    }

    /// <summary>启动器启动时自动续播上次音乐。</summary>
    public bool MusicResumeOnLaunch
    {
        get => Load().MusicResumeOnLaunch;
        set
        {
            if (Load().MusicResumeOnLaunch == value) return;
            Mutate(p => p.MusicResumeOnLaunch = value);
            OnPropertyChanged();
        }
    }

    // ---- 本地客户端模式 ----

    /// <summary>总开关。关闭时音乐页隐藏「本地客户端」入口，后三项灰显。</summary>
    public bool ClientModeEnabled
    {
        get => Prefs.Enabled;
        set
        {
            var p = Prefs;
            if (p.Enabled == value) return;
            p.Enabled = value;
            SavePrefs(p);
            OnPropertyChanged();
            // 联动：后三项可用性跟着总开关走
            OnPropertyChanged(nameof(ClientGraceMinutes));
            OnPropertyChanged(nameof(ClientGraceText));
            OnPropertyChanged(nameof(LyricPinModeIndex));
            OnPropertyChanged(nameof(ClientModeLyricEnabled));
            NotifyPlayerPrefsChanged();
        }
    }

    /// <summary>客户端宽限期（分钟，0–30）。0 = 不自动关闭。</summary>
    public int ClientGraceMinutes
    {
        get => Prefs.GraceMinutes;
        set
        {
            var p = Prefs;
            // 先钳到规格区间再判等 —— 用户拖滑块拖出越界值时不应反复写盘
            var clamped = Math.Clamp(value, MusicClientPrefs.MinGraceMinutes, MusicClientPrefs.MaxGraceMinutes);
            if (p.GraceMinutes == clamped) return;
            p.GraceMinutes = clamped;
            SavePrefs(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClientGraceText));
            NotifyPlayerPrefsChanged();
        }
    }

    /// <summary>宽限期展示文本。</summary>
    public string ClientGraceText
    {
        get
        {
            var m = Prefs.GraceMinutes;
            return m == 0 ? "不自动关闭" : $"{m} 分钟";
        }
    }

    /// <summary>歌词固定方式（ComboBox 选中索引）。</summary>
    public int LyricPinModeIndex
    {
        get => (int)Prefs.LyricPin;
        set
        {
            if (Prefs.LyricPin == (LyricPinMode)value) return;
            var p = Prefs;
            p.LyricPin = (LyricPinMode)Math.Clamp(value, 0, 2);
            SavePrefs(p);
            OnPropertyChanged();
            NotifyPlayerPrefsChanged();
        }
    }

    /// <summary>客户端模式下仍用 API 获取歌词。</summary>
    public bool ClientModeLyricEnabled
    {
        get => Prefs.LyricEnabled;
        set
        {
            var p = Prefs;
            if (p.LyricEnabled == value) return;
            p.LyricEnabled = value;
            SavePrefs(p);
            OnPropertyChanged();
            NotifyPlayerPrefsChanged();
        }
    }

    /// <summary>已选择的客户端程序路径（只读展示，路径本身在音乐页/此处选择后写入 profile）。</summary>
    public string ClientExePath => Load().MusicClientExePath;

    // ---- 内部 ----

    private LauncherProfile Load() => ProfileStore.Load(_gameRoot);

    private MusicClientPrefs Prefs =>
        (Load().MusicClient ?? new MusicClientPrefs()).Normalized();

    private void SavePrefs(MusicClientPrefs p)
    {
        var profile = Load();
        profile.MusicClient = p;
        try
        {
            ProfileStore.Save(profile);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    private void Mutate(Action<LauncherProfile> apply)
    {
        var profile = Load();
        apply(profile);
        try
        {
            ProfileStore.Save(profile);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>设置变化后通知播放器单例刷新（如客户端入口可用性、歌词显隐）。</summary>
    private static void NotifyPlayerPrefsChanged()
    {
        try { MusicPlayerViewModel.Instance.OnClientPrefsChanged(); }
        catch { /* 播放器未就绪时忽略 */ }
    }

    private void BrowseClient()
    {
        // 实际文件选择需在 UI 线程弹 StorageProvider，这里通过 VM 暴露命令给 code-behind 调用
        BrowseClientRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>视图订阅此事件以弹出文件选择器（VM 不直接依赖 Avalonia 存储 API）。</summary>
    public event EventHandler? BrowseClientRequested;
}
