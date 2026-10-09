using System.Collections.ObjectModel;
using System.Windows.Input;
using MCLCS.Core.Download;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → 下载（对齐 WPF 下载设置）：下载源偏好、最大并发下载数、服务器资源包缓存开关；
/// 三项均持久化到 LauncherProfile，并由运行时真正消费（下载源驱动 MirrorPolicy 重排、并发数传入 HttpDownloader）。
/// 另保留镜像端点清单展示与连通性测试。
/// </summary>
public class DownloadSettingsViewModel : ObservableObject
{
    private readonly string _profileRoot = GameConstants.DefaultGameRoot;
    private readonly LauncherProfile _profile;

    // ---- 下载源偏好 ----
    public ObservableCollection<DownloadSourcePreference> SourceOptions { get; } = new(
        Enum.GetValues<DownloadSourcePreference>());

    private DownloadSourcePreference _downloadSource = DownloadSourcePreference.MirrorFirst;
    public DownloadSourcePreference DownloadSource
    {
        get => _downloadSource;
        set => SetField(ref _downloadSource, value);
    }

    private int _maxConcurrent = 8;
    public int MaxConcurrentDownloads
    {
        get => _maxConcurrent;
        set => SetField(ref _maxConcurrent, value);
    }

    private bool _serverPackCacheEnabled = true;
    public bool ServerPackCacheEnabled
    {
        get => _serverPackCacheEnabled;
        set => SetField(ref _serverPackCacheEnabled, value);
    }

    // ---- 全局限速（清单 #67）----

    private int _speedLimitKbps;
    /// <summary>全局下载限速（KB/s），0 = 不限速。保存后立即写入 Core 的 DownloadSpeedLimiter。</summary>
    public int DownloadSpeedLimitKbps
    {
        get => _speedLimitKbps;
        set => SetField(ref _speedLimitKbps, value);
    }

    // ---- CurseForge 接入（对齐 WPF 设置 → 下载）----

    private bool _curseForgeEnabled = true;
    /// <summary>CurseForge 源总开关。关闭后整合包来源中的 CurseForge 不可选，Modrinth 不受影响。</summary>
    public bool CurseForgeEnabled
    {
        get => _curseForgeEnabled;
        set { if (SetField(ref _curseForgeEnabled, value)) OnPropertyChanged(nameof(CurseForgeStatusText)); }
    }

    private string _curseForgeApiKey = "";
    /// <summary>用户自定义 API Key（留空 = 使用构建时注入的内置 Key）。</summary>
    public string CurseForgeApiKey
    {
        get => _curseForgeApiKey;
        set { if (SetField(ref _curseForgeApiKey, value)) OnPropertyChanged(nameof(CurseForgeStatusText)); }
    }

    private string _curseForgeApiRoot = "";
    /// <summary>API Root 覆盖（留空 = 官方 https://api.curseforge.com；可填第三方镜像）。</summary>
    public string CurseForgeApiRoot
    {
        get => _curseForgeApiRoot;
        set { if (SetField(ref _curseForgeApiRoot, value)) OnPropertyChanged(nameof(CurseForgeStatusText)); }
    }

    /// <summary>当前 Key 来源提示（内置 / 自定义 / 未配置 / 已关闭）。</summary>
    public string CurseForgeStatusText
    {
        get
        {
            if (!CurseForgeEnabled) return "已关闭";
            if (!string.IsNullOrWhiteSpace(CurseForgeApiKey)) return "使用自定义 Key";
            return CurseForgeConfig.BuiltInApiKey.Length > 0 ? "使用内置 Key" : "未配置 Key（CurseForge 源不可用）";
        }
    }

    // ---- 镜像端点清单 / 连通性测试 ----
    private ObservableCollection<string> _mirrorUrls = new();
    public ObservableCollection<string> MirrorUrls
    {
        get => _mirrorUrls;
        set => SetField(ref _mirrorUrls, value);
    }

    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    public DownloadSettingsViewModel()
    {
        _profile = ProfileStore.Load(_profileRoot);
        _downloadSource = _profile.DownloadSource;
        _maxConcurrent = _profile.MaxConcurrentDownloads;
        _serverPackCacheEnabled = _profile.ServerPackCacheEnabled;
        _speedLimitKbps = _profile.DownloadSpeedLimitKbps;
        _curseForgeEnabled = _profile.CurseForge.Enabled;
        _curseForgeApiKey = _profile.CurseForge.ApiKey ?? "";
        _curseForgeApiRoot = _profile.CurseForge.ApiRoot ?? "";
        Refresh();
    }

    public ICommand RefreshCommand => new RelayCommand(_ => Refresh());
    public ICommand TestCommand => new AsyncRelayCommand(_ => TestAsync());
    public ICommand SaveCommand => new RelayCommand(_ => Save());
    public ICommand TestCurseForgeCommand => new AsyncRelayCommand(_ => TestCurseForgeAsync());

    private void Save()
    {
        try
        {
            _profile.DownloadSource = DownloadSource;
            _profile.MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 64);
            _profile.ServerPackCacheEnabled = ServerPackCacheEnabled;
            _profile.DownloadSpeedLimitKbps = Math.Max(0, DownloadSpeedLimitKbps);
            _profile.CurseForge = new CurseForgeSettings
            {
                Enabled = CurseForgeEnabled,
                ApiKey = CurseForgeApiKey?.Trim() ?? "",
                ApiRoot = CurseForgeApiRoot?.Trim() ?? ""
            };
            ProfileStore.Save(_profile);
            // 立即生效：下载源偏好驱动 MirrorPolicy 重排（最大并发在下次构造下载器时读取）
            MirrorPolicy.Preference = DownloadSource;
            // 限速 / CurseForge 配置同步进 Core，无需重启
            LauncherService.Instance.ApplyDownloadPreferences();
            LauncherService.Instance.ApplyCurseForgeSettings();
            Status = "下载设置已保存";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    private void Refresh()
    {
        try
        {
            var urls = MirrorPolicy.VersionManifestUrls().ToList();
            MirrorUrls = new ObservableCollection<string>(urls);
            Status = $"当前 {urls.Count} 个版本清单镜像端点";
        }
        catch (Exception ex)
        {
            Status = $"读取镜像策略失败：{ex.Message}";
        }
    }

    /// <summary>测试 CurseForge 连接：先把界面上的 Key / API Root 同步进 Core，再发一次最小请求验证（仅测试，不落盘）。</summary>
    private async Task TestCurseForgeAsync()
    {
        Busy = true;
        Status = "正在测试 CurseForge 连接…";
        try
        {
            CurseForgeConfig.LaunchArgumentOverride = null;
            CurseForgeConfig.UserApiKey = string.IsNullOrWhiteSpace(CurseForgeApiKey) ? null : CurseForgeApiKey.Trim();
            CurseForgeConfig.ApiRoot = CurseForgeApiRoot?.Trim() ?? "";
            CurseForgeConfig.Enabled = true;

            var client = new CurseForgeClient(LauncherService.Instance.ApiClient);
            var ok = await client.TestConnectionAsync();

            Status = ok ? "CurseForge 连接正常" : $"CurseForge 连接失败：{client.LastError ?? "未知原因"}";
            OnPropertyChanged(nameof(CurseForgeStatusText));
        }
        catch (Exception ex)
        {
            Status = $"CurseForge 连接失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task TestAsync()
    {
        Busy = true;
        Status = "正在测试镜像连通性…";
        try
        {
            var urls = MirrorPolicy.VersionManifestUrls().ToList();
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var results = new List<string>();
            foreach (var u in urls)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var resp = await client.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
                    sw.Stop();
                    results.Add($"{resp.StatusCode}  {sw.ElapsedMilliseconds}ms  {u}");
                }
                catch (Exception ex)
                {
                    results.Add($"FAIL  {ex.GetType().Name}  {u}");
                }
            }
            Status = "连通性测试完成";
            MirrorUrls = new ObservableCollection<string>(results);
        }
        catch (Exception ex)
        {
            Status = $"测试失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}
