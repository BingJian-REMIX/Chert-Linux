using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Auth;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.Models;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Recommend;
using MCLCS.Core.Statistics;
using MCLCS.Core.Utils;
using MCLCS.Linux.App;
using MCLCS.Linux.App.Views.Pages;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 主页（快速开始）：版本选择 + 一键启动 + 游玩统计 + 智能推荐预览。
/// 对齐 WPF HomeView 的快速启动区，复用 Core.GameLauncher / PlaytimeTracker /
/// RecommendationEngine；与 GameHomeView（完整启动器）分工：本页偏聚合与快捷。
/// </summary>
public class HomeViewModel : ObservableObject
{
    private readonly string _gameRoot = GameConstants.DefaultGameRoot;

    private ObservableCollection<VersionListEntry> _versions = new();
    public ObservableCollection<VersionListEntry> Versions
    {
        get => _versions;
        set => SetField(ref _versions, value);
    }

    private VersionListEntry? _selectedVersion;
    public VersionListEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value)) OnPropertyChanged(nameof(CanLaunch));
        }
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
        set { if (SetField(ref _busy, value)) OnPropertyChanged(nameof(CanLaunch)); }
    }
    public bool CanLaunch => !Busy && SelectedVersion is not null;

    private PlayStats? _playStats;
    public PlayStats? PlayStats
    {
        get => _playStats;
        set
        {
            if (SetField(ref _playStats, value))
            {
                OnPropertyChanged(nameof(IsAnnualReportVisible));
                OnPropertyChanged(nameof(AnnualReportHint));
            }
        }
    }

    public string RecentVersionText => PlayStats?.RecentVersion ?? "—";
    public string WeeklyPlayText
    {
        get
        {
            var m = PlayStats?.WeeklyPlayMinutes ?? 0;
            return m >= 60 ? $"{m / 60} 小时 {m % 60} 分" : $"{m} 分";
        }
    }
    public string CrashCountText => (PlayStats?.CrashCount ?? 0).ToString();

    private ObservableCollection<RecommendationItem> _recommendations = new();
    public ObservableCollection<RecommendationItem> Recommendations
    {
        get => _recommendations;
        set => SetField(ref _recommendations, value);
    }

    private string _recommendStatus = "";
    public string RecommendStatus
    {
        get => _recommendStatus;
        set => SetField(ref _recommendStatus, value);
    }

    public ICommand RefreshCommand => new RelayCommand(_ => RefreshVersions());
    public ICommand LaunchCommand => new AsyncRelayCommand(_ => LaunchAsync(), _ => CanLaunch);
    public ICommand OpenVersionSettingsCommand => new AsyncRelayCommand(_ => OpenVersionSettingsAsync());

    /// <summary>是否在主页显示年度报告入口：仅在首次启动游戏后每年的同日展示。</summary>
    public bool IsAnnualReportVisible
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PlayStats?.FirstLaunchUtc)) return false;
            if (!DateTime.TryParse(PlayStats.FirstLaunchUtc, out var firstUtc)) return false;
            var first = firstUtc.Date;
            var today = DateTime.UtcNow.Date;
            return today.Month == first.Month && today.Day == first.Day;
        }
    }

    /// <summary>年度报告入口提示文案。</summary>
    public string AnnualReportHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PlayStats?.FirstLaunchUtc)) return "";
            if (!DateTime.TryParse(PlayStats.FirstLaunchUtc, out var firstUtc)) return "";
            return $"今天是首次启动 {firstUtc.Year} 周年纪念日，点击查看年度报告";
        }
    }

    public HomeViewModel()
    {
        RefreshVersions();
        LoadPlayStats();
        _ = RefreshRecommendAsync();
        _ = LoadSeasonAsync();
    }

    /// <summary>枚举 versions/ 下含 &lt;id&gt;/&lt;id&gt;.json 的目录。</summary>
    public void RefreshVersions()
    {
        var list = new ObservableCollection<VersionListEntry>();
        try
        {
            var versionsDir = PathEx.VersionsDir(_gameRoot);
            if (Directory.Exists(versionsDir))
            {
                foreach (var dir in Directory.GetDirectories(versionsDir))
                {
                    var id = Path.GetFileName(dir);
                    var json = PathEx.VersionJsonPath(_gameRoot, id);
                    if (!File.Exists(json)) continue;
                    string type = "";
                    try
                    {
                        var v = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(json));
                        type = v?.Type ?? "";
                    }
                    catch { /* 忽略解析错误 */ }
                    list.Add(new VersionListEntry { Id = id, Type = type });
                }
            }
        }
        catch (Exception ex)
        {
            Status = $"读取版本失败：{ex.Message}";
        }

        Versions = list;
        Status = Versions.Count > 0
            ? $"共发现 {Versions.Count} 个已安装版本"
            : "暂无已安装版本，请前往「下载 → 原版」安装";
        if (SelectedVersion is null) SelectedVersion = Versions.FirstOrDefault();
    }

    /// <summary>打开「版本设置」对话框（8 大模块：隔离/加载器/Java/分辨率/Mod 管理/锁定等），针对当前选中版本。</summary>
    private async Task OpenVersionSettingsAsync()
    {
        var sel = SelectedVersion;
        if (sel is null)
        {
            Status = "请先选择一个版本";
            return;
        }

        await VersionSettingsDialog.OpenAsync(_gameRoot, sel.Id, sel.Type, RefreshVersions);
    }

    private void LoadPlayStats() => PlayStats = PlaytimeTracker.Load(_gameRoot);

    private async Task RefreshRecommendAsync()
    {
        try
        {
            RecommendStatus = "正在生成推荐…";
            var profile = ProfileStore.Load(_gameRoot);
            using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var items = await RecommendationEngine.BuildAsync(_gameRoot, profile, client, ct: CancellationToken.None);
            Recommendations = new ObservableCollection<RecommendationItem>(items.Take(4));
            RecommendStatus = Recommendations.Count > 0 ? "" : "暂无推荐";
        }
        catch (Exception ex)
        {
            RecommendStatus = $"推荐失败：{ex.Message}";
        }
    }

    /// <summary>节日中心入口：仅在有生效节日时露出（远程 config.json 决定）。</summary>
    private bool _hasSeason;
    public bool HasSeason
    {
        get => _hasSeason;
        set => SetField(ref _hasSeason, value);
    }

    private string _seasonText = "";
    public string SeasonText
    {
        get => _seasonText;
        set => SetField(ref _seasonText, value);
    }

    private async Task LoadSeasonAsync()
    {
        try
        {
            await Themes.SeasonalService.RefreshAsync();
            var key = Themes.SeasonalService.CurrentSeasonKey;
            HasSeason = !string.IsNullOrWhiteSpace(key);
            SeasonText = HasSeason ? $"节日进行中：{key}" : "";
        }
        catch
        {
            HasSeason = false;
        }
    }

    private async Task LaunchAsync()
    {
        var id = SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(id)) { Status = "请先选择一个版本"; return; }

        var profile = ProfileStore.Load(_gameRoot);

        // 每版本覆盖层：从 versions/<id>/profile.json 读取，未设置则回落全局
        var vp = VersionProfileStore.Load(_gameRoot, id);
        var effectiveDir = VersionProfileStore.EffectiveGameDir(_gameRoot, id, vp);

        var java = await ResolveJavaAsync(vp.JavaPath ?? profile.JavaPath, _gameRoot, id);
        if (java is null)
        {
            Status = "未检测到 Java，请在「设置 → 启动」或该版本设置中配置 Java 路径";
            return;
        }

        Busy = true;
        try
        {
            Status = $"正在启动 {id} …";

            var extraJvm = new List<string>(profile.ExtraJvmArgs);
            extraJvm.AddRange(vp.ExtraJvmArgs);

            (int W, int H)? resolution = null;
            if (vp.ResolutionWidth is > 0 && vp.ResolutionHeight is > 0)
                resolution = (vp.ResolutionWidth.Value, vp.ResolutionHeight.Value);
            else if (profile.ResolutionWidth is > 0 && profile.ResolutionHeight is > 0)
                resolution = (profile.ResolutionWidth.Value, profile.ResolutionHeight.Value);

            var opts = new LaunchOptions
            {
                MaxMemoryMb = vp.MaxMemoryMb ?? (profile.MaxMemoryMb > 0 ? profile.MaxMemoryMb : 2048),
                MinMemoryMb = vp.MinMemoryMb ?? profile.MinMemoryMb,
                ExtraJvmArgs = extraJvm,
                Resolution = resolution,
                Fullscreen = vp.Fullscreen,
                GameDir = effectiveDir
            };

            var account = AccountStore.GetLastUsed(_gameRoot);
            if (account is { } a && !string.IsNullOrEmpty(a.Uuid))
            {
                opts.Username = a.Username;
                opts.Uuid = a.Uuid;
                opts.AccessToken = a.AccessToken;
                opts.UserType = a.AuthType == "microsoft" ? "msa" : a.AuthType;
            }
            else
            {
                var name = account?.Username ?? "Player";
                opts.Username = name;
                opts.Uuid = OfflineAuthenticator.GenerateOfflineUuid(name);
                opts.AccessToken = "0";
                opts.UserType = "mojang";
            }

            var result = await GameLauncher.LaunchAsync(profile.GameRoot, id, java, opts, null);
            if (!result.Crashed && result.ExitCode == 0)
            {
                PlaytimeTracker.RecordLaunch(_gameRoot, id);
                OnPropertyChanged(nameof(IsAnnualReportVisible));
                OnPropertyChanged(nameof(AnnualReportHint));
            }
            Status = result.Crashed
                ? $"游戏已退出（崩溃，退出码 {result.ExitCode}）"
                : $"游戏已退出（退出码 {result.ExitCode}）";
        }
        catch (Exception ex)
        {
            Status = $"启动失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
            LoadPlayStats();
        }
    }

    private static async Task<JavaInfo?> ResolveJavaAsync(string? javaPath, string gameRoot, string versionId)
    {
        var list = await JavaDetector.DetectAsync();
        if (list.Count == 0) return null;
        var java = JavaDetector.SelectForVersion(list, gameRoot, versionId, javaPath);
        if (java is null) return null;

        // 【修复】版本不符合该版本所需时明确提示：此前会静默用不兼容的 Java 启动
        // （拿 Java 8 跑 1.21 会当场崩，用户只看到游戏起不来、不知道为什么）。
        var required = JavaDetector.RequiredMajorForVersionId(gameRoot, versionId);
        var ok = required == 8 ? java.MajorVersion == 8 : java.MajorVersion >= required;
        if (!ok)
        {
            try
            {
                ToastService.Instance.Show(new ToastOptions
                {
                    Title = "Java 版本可能不兼容",
                    Message = $"该版本需要 Java {required}，实际将使用 Java {java.MajorVersion}（{java.JavaExe}）。"
                              + "若无法进入游戏，请在「设置 → 启动」更换 Java。",
                    DurationMs = 6000,
                    Danger = true
                });
            }
            catch { /* 提示失败不阻断启动 */ }
        }
        return java;
    }
}
