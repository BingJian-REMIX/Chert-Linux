using System;
using System.IO;
using System.Windows.Input;
using MCLCS.Core.Music;
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
        ApiRestoreDefaultUrlCommand = new RelayCommand(_ => ApiRestoreDefaultUrl());
    }

    public ICommand BrowseClientCommand { get; }

    /// <summary>把在线音源地址恢复为当前协议推荐的默认值。</summary>
    public ICommand ApiRestoreDefaultUrlCommand { get; }

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

    // ---- 在线音源（对齐 WPF SettingsViewModel 的同名一组）----
    // 每一项改完立即 NotifyApiChanged()：换协议/地址要求播放器重建 provider，
    // 否则用户改完还得重启才见效。

    /// <summary>总开关。关闭时音乐页不再显示「在线」入口。</summary>
    public bool ApiEnabled
    {
        get => ApiPrefs.Enabled;
        set
        {
            var p = ApiPrefs;
            if (p.Enabled == value) return;
            p.Enabled = value;
            SaveApiPrefs(p);
            OnPropertyChanged();
            NotifyApiChanged();
        }
    }

    /// <summary>
    /// 数据源（ComboBox 下标）：0 = Meting 聚合（免登录），1 = 厂家自建 API（可扫码登录）。
    /// <para>枚举里仍保留 <c>NeteaseApi</c> 只为兼容旧配置，界面不再单独暴露它 ——
    /// 它与「厂家自建 API + 网易云」等价，留两个入口只会让用户困惑该选哪个。</para>
    /// <para>切到 Meting 会<b>连带重置服务地址</b>：两套协议请求形态完全不同，
    /// 沿用旧地址必然解析失败。</para>
    /// </summary>
    public int ApiKindIndex
    {
        get => ApiPrefs.Kind == MusicApiKind.Meting ? 0 : 1;
        set
        {
            var p = ApiPrefs;
            var kind = value == 0 ? MusicApiKind.Meting : MusicApiKind.VendorApi;
            if (p.Kind == kind) return;

            p.Kind = kind;
            p.Normalized();
            if (kind == MusicApiKind.Meting)
                p.BaseUrl = MusicApiPrefs.DefaultMetingUrl;   // 厂家地址不能拿去问 Meting 实例

            SaveApiPrefs(p);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApiBaseUrl));
            OnPropertyChanged(nameof(ApiPlatformVisible));
            OnPropertyChanged(nameof(ApiCanLogin));
            OnPropertyChanged(nameof(ApiVendorHint));
            OnPropertyChanged(nameof(ApiVendorLoginSupported));
            NotifyApiChanged();
        }
    }

    /// <summary>
    /// 服务根地址（不含结尾斜杠）。
    /// <para>Meting 模式下它是<b>聚合实例</b>地址；厂家模式下是<b>当前厂家自己那个服务</b>的地址
    /// —— 两者含义不同，分开存取。</para>
    /// </summary>
    public string ApiBaseUrl
    {
        get
        {
            var p = ApiPrefs;
            return p.Kind == MusicApiKind.Meting ? p.BaseUrl : p.UrlFor(VendorPlatformOf(p));
        }
        set
        {
            var v = (value ?? "").Trim().TrimEnd('/');
            var p = ApiPrefs;

            if (p.Kind == MusicApiKind.Meting)
            {
                if (p.BaseUrl == v) return;
                p.BaseUrl = v;
            }
            else
            {
                var platform = VendorPlatformOf(p);
                if (p.UrlFor(platform) == v) return;
                p.SetUrl(platform, v);
            }

            SaveApiPrefs(p);
            OnPropertyChanged();
            NotifyApiChanged();
        }
    }

    /// <summary>
    /// 厂家 / 搜索范围（下拉下标，与 <see cref="MusicApiPlatform"/> 数值顺序一致）。
    /// <para>「全部平台」只在 Meting 下有意义；厂家模式下选它会退回网易云 ——
    /// 登录这类动作必须落在具体厂家上。</para>
    /// </summary>
    public int ApiPlatformIndex
    {
        get => (int)ApiPrefs.Platform;
        set
        {
            if (!Enum.IsDefined(typeof(MusicApiPlatform), value)) return;
            var p = ApiPrefs;
            var v = (MusicApiPlatform)value;
            if (p.Platform == v) return;
            p.Platform = v;
            SaveApiPrefs(p);
            OnPropertyChanged();
            // 每家的服务地址与登录能力都不同，切厂家后这些展示必须跟着变
            OnPropertyChanged(nameof(ApiBaseUrl));
            OnPropertyChanged(nameof(ApiVendorHint));
            OnPropertyChanged(nameof(ApiVendorLoginSupported));
            OnPropertyChanged(nameof(ApiCanLogin));
            OnPropertyChanged(nameof(ApiLoggedIn));
            OnPropertyChanged(nameof(ApiAccountLabel));
            NotifyApiChanged();
        }
    }

    /// <summary>厂家下拉是否可见（两种数据源都要选厂家，故恒为 true）。</summary>
    public bool ApiPlatformVisible => true;

    /// <summary>
    /// 「真正要对话的那个厂家」：全部平台只是搜索范围，服务地址 / 登录必须落到具体厂家。
    /// </summary>
    private static MusicApiPlatform VendorPlatformOf(MusicApiPrefs p) =>
        p.Platform == MusicApiPlatform.All ? MusicApiPlatform.Netease : p.Platform;

    /// <summary>
    /// 当前厂家的自建 API 说明（建议部署的项目 + 注意事项）。
    /// 没有可用自建项目的厂家会明确告知「已退回 Meting 聚合」，而不是让用户对着连不上的输入框猜。
    /// </summary>
    public string ApiVendorHint
    {
        get
        {
            var p = ApiPrefs;
            if (p.Kind == MusicApiKind.Meting) return "";

            var profile = VendorApiProfiles.For(VendorPlatformOf(p));
            return profile is null
                ? "该厂家暂无可用的自建 API 项目，已自动退回 Meting 聚合（免登录，搜索与播放仍可用）。"
                : "建议部署：" + profile.Project + "。" + profile.Notes;
        }
    }

    /// <summary>当前厂家是否真的能扫码登录（酷我 / 百度 / 虾米没有可用项目）。</summary>
    public bool ApiVendorLoginSupported
    {
        get
        {
            var p = ApiPrefs;
            if (p.Kind == MusicApiKind.Meting) return false;
            return VendorApiProfiles.CanLogin(VendorPlatformOf(p));
        }
    }

    /// <summary>在线播放音质。</summary>
    public int ApiQualityIndex
    {
        get => (int)ApiPrefs.Quality;
        set
        {
            if (!Enum.IsDefined(typeof(MusicApiQuality), value)) return;
            var p = ApiPrefs;
            var v = (MusicApiQuality)value;
            if (p.Quality == v) return;
            p.Quality = v;
            SaveApiPrefs(p);
            OnPropertyChanged();
            NotifyApiChanged();
        }
    }

    public bool ApiCanLogin => MusicPlayerViewModel.Instance.CanOnlineLogin;

    public bool ApiLoggedIn => MusicPlayerViewModel.Instance.IsOnlineLoggedIn;

    public string ApiAccountLabel => MusicPlayerViewModel.Instance.ApiAccountLabel;

    public ICommand ApiLoginCommand => MusicPlayerViewModel.Instance.StartApiLoginCommand;

    public ICommand ApiLogoutCommand => MusicPlayerViewModel.Instance.ApiLogoutCommand;

    public ICommand ApiCancelLoginCommand => MusicPlayerViewModel.Instance.CancelApiLoginCommand;

    /// <summary>
    /// 播放器单例。设置页要直接绑它的二维码与登录状态 ——
    /// 这些属于播放会话而非配置项，没必要再代理一层。
    /// </summary>
    public MusicPlayerViewModel MusicPlayer => MusicPlayerViewModel.Instance;

    private void ApiRestoreDefaultUrl()
    {
        var p = ApiPrefs;
        ApiBaseUrl = MusicApiPrefs.DefaultUrlFor(p.Kind, VendorPlatformOf(p));
    }

    // ---- 内部 ----

    private LauncherProfile Load() => ProfileStore.Load(_gameRoot);

    private MusicClientPrefs Prefs =>
        (Load().MusicClient ?? new MusicClientPrefs()).Normalized();

    private MusicApiPrefs ApiPrefs =>
        (Load().MusicApi ?? new MusicApiPrefs()).Normalized();

    private void SaveApiPrefs(MusicApiPrefs p)
    {
        var profile = Load();
        profile.MusicApi = p;
        try
        {
            ProfileStore.Save(profile);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

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

    /// <summary>在线音源设置变化后通知播放器重建数据源并刷新登录态。</summary>
    private static void NotifyApiChanged()
    {
        try { MusicPlayerViewModel.Instance.OnApiPrefsChanged(); }
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
