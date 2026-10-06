using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MCLCS.Core.Auth;
using MCLCS.Linux.App.Views;
using MCLCS.Core.Download;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.Models;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Recommend;
using MCLCS.Core.Save;
using MCLCS.Core.Servers;
using MCLCS.Core.Statistics;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 游戏主页视图模型（对齐 WPF GameViewModel 的快速启动区）：
/// 选择已安装版本 / 账户 / 内存，调用 Core.GameLauncher.LaunchAsync 启动游戏。
/// Java 运行环境在「设置 → 启动」中配置（profile.JavaPath），启动时按路径解析（对齐 WPF）。
/// 离线账户用 Core.OfflineAuthenticator 生成与官方一致的离线 UUID；微软/第三方账号则复用存储的令牌。
/// </summary>
public class GameHomeViewModel : ObservableObject
{
    private readonly string _gameRoot = GameConstants.DefaultGameRoot;

    // ===== 已安装版本 =====
    private ObservableCollection<VersionEntryVm> _versions = new();
    public ObservableCollection<VersionEntryVm> Versions
    {
        get => _versions;
        set => SetField(ref _versions, value);
    }

    private VersionEntryVm? _selectedVersion;
    public VersionEntryVm? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetField(ref _selectedVersion, value)) return;
            OnPropertyChanged(nameof(CanLaunch));
            SyncAccountForVersion();
        }
    }

    // ===== 账户 =====
    private ObservableCollection<AccountEntry> _accounts = new();
    public ObservableCollection<AccountEntry> Accounts
    {
        get => _accounts;
        set => SetField(ref _accounts, value);
    }

    private AccountEntry? _selectedAccount;
    public AccountEntry? SelectedAccount
    {
        get => _selectedAccount;
        set => SetField(ref _selectedAccount, value);
    }

    /// <summary>离线昵称输入（无账户或想用临时名时）。</summary>
    private string _username = "";
    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    // ===== 内存 =====
    private int _memoryMb = 2048;
    public int MemoryMb
    {
        get => _memoryMb;
        set => SetField(ref _memoryMb, value);
    }

    // ===== 状态 =====
    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(CanLaunch));
        }
    }

    /// <summary>启动按钮可用性：未运行且已选版本（Java 在「设置 → 启动」中配置）。</summary>
    public bool CanLaunch => !IsBusy && SelectedVersion is not null;

    // ===== 局域网游戏（对齐 WPF LanServerScanner）=====
    private ObservableCollection<LanServer> _lanServers = new();
    public ObservableCollection<LanServer> LanServers
    {
        get => _lanServers;
        set => SetField(ref _lanServers, value);
    }

    private string _lanStatus = LocaleManager.T("status.ready");
    public string LanStatus
    {
        get => _lanStatus;
        set => SetField(ref _lanStatus, value);
    }

    // ===== 服务器列表（对齐 WPF ServerListStore）=====
    private ObservableCollection<ServerEntry> _servers = new();
    public ObservableCollection<ServerEntry> Servers
    {
        get => _servers;
        set => SetField(ref _servers, value);
    }

    // ===== 智能推荐（对齐 WPF RecommendationEngine）=====
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

    // ===== 统计（对齐 WPF PlaytimeTracker）=====
    private PlayStats? _playStats;
    public PlayStats? PlayStats
    {
        get => _playStats;
        set => SetField(ref _playStats, value);
    }

    /// <summary>最近版本（统计卡片）。</summary>
    public string RecentVersionText => PlayStats?.RecentVersion ?? "—";
    /// <summary>本周时长文本。</summary>
    public string WeeklyPlayText
    {
        get
        {
            var m = PlayStats?.WeeklyPlayMinutes ?? 0;
            return m >= 60 ? $"{m / 60} 小时 {m % 60} 分" : $"{m} 分";
        }
    }
    /// <summary>崩溃次数（年）。</summary>
    public string CrashCountText => (PlayStats?.CrashCount ?? 0).ToString();

    public ICommand RefreshVersionsCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand RefreshLanCommand { get; }
    public ICommand RefreshRecommendCommand { get; }
    public ICommand RefreshServersCommand { get; }

    public GameHomeViewModel()
    {
        RefreshVersionsCommand = new RelayCommand(_ => RefreshVersions());
        LaunchCommand = new AsyncRelayCommand(_ => LaunchAsync());
        RefreshLanCommand = new AsyncRelayCommand(_ => RefreshLanAsync());
        RefreshRecommendCommand = new AsyncRelayCommand(_ => RefreshRecommendAsync());
        RefreshServersCommand = new RelayCommand(_ => RefreshServers());
        RefreshVersions();
        LoadAccounts();
        RefreshServers();
        LoadPlayStats();
        _ = RefreshLanAsync();
        _ = RefreshRecommendAsync();
    }

    /// <summary>扫描局域网游戏（对齐 WPF LanServerScanner.ScanAsync）。</summary>
    public async Task RefreshLanAsync()
    {
        try
        {
            LanStatus = LocaleManager.T("java.scanning");
            var list = await LanServerScanner.ScanAsync(durationMs: 2500, ct: CancellationToken.None);
            LanServers = new ObservableCollection<LanServer>(list);
            LanStatus = list.Count > 0
                ? $"{list.Count} 个局域网游戏"
                : LocaleManager.T("game.no_lan");
        }
        catch (Exception ex)
        {
            LanStatus = $"扫描失败：{ex.Message}";
        }
    }

    /// <summary>刷新智能推荐（对齐 WPF RecommendationEngine.BuildAsync）。</summary>
    public async Task RefreshRecommendAsync()
    {
        try
        {
            RecommendStatus = "正在生成推荐…";
            var profile = ProfileStore.Load(_gameRoot);
            using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var items = await RecommendationEngine.BuildAsync(_gameRoot, profile, client, ct: CancellationToken.None);
            Recommendations = new ObservableCollection<RecommendationItem>(items.Take(8));
            RecommendStatus = Recommendations.Count > 0 ? "" : "暂无推荐";
        }
        catch (Exception ex)
        {
            RecommendStatus = $"推荐失败：{ex.Message}";
        }
    }

    /// <summary>加载服务器列表（对齐 WPF ServerListStore.Load）。</summary>
    private void RefreshServers()
    {
        try
        {
            Servers = new ObservableCollection<ServerEntry>(ServerListStore.Load(_gameRoot));
        }
        catch
        {
            Servers = new ObservableCollection<ServerEntry>();
        }
    }

    /// <summary>加载游玩统计（对齐 WPF PlaytimeTracker.Load）。</summary>
    private void LoadPlayStats()
    {
        PlayStats = PlaytimeTracker.Load(_gameRoot);
    }

    /// <summary>枚举 versions/ 下含 &lt;id&gt;/&lt;id&gt;.json 的目录（对齐 WPF LauncherService.ListInstalledVersions）。</summary>
    public void RefreshVersions()
    {
        var list = new ObservableCollection<VersionEntryVm>();
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
                    list.Add(new VersionEntryVm { Id = id, Type = type });
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

    private void LoadAccounts()
    {
        Accounts = new ObservableCollection<AccountEntry>(AccountStore.Load(_gameRoot));
        SyncAccountForVersion();
    }

    /// <summary>
    /// 依据当前所选版本解析应使用的账号：优先该版本绑定的账号，否则回落全局「最后使用」。
    /// 实现「每版本独立账户绑定」——切换版本时账户下拉自动跟随。
    /// </summary>
    private void SyncAccountForVersion()
    {
        var id = SelectedVersion?.Id;
        var bound = !string.IsNullOrWhiteSpace(id)
            ? VersionProfileStore.Load(_gameRoot, id).BoundAccountId
            : null;
        var resolved = AccountStore.GetForVersion(_gameRoot, bound);
        // 确保 ComboBox SelectedItem 引用与 ItemsSource 中的实例一致
        SelectedAccount = resolved is null ? null : Accounts.FirstOrDefault(a => a.Id == resolved.Id) ?? resolved;
        if (SelectedAccount is not null) Username = SelectedAccount.Username;
    }

    private async Task LaunchAsync()
    {
        var id = SelectedVersion?.Id;
        if (string.IsNullOrWhiteSpace(id)) { Status = "请先选择一个版本"; return; }

        var profile = ProfileStore.Load(_gameRoot);
        var java = await ResolveJavaAsync(profile.JavaPath, profile.GameRoot, id);
        if (java is null)
        {
            Status = "未检测到 Java，请在「设置 → 启动」中配置 Java 路径";
            return;
        }

        IsBusy = true;
        try
        {
            var opts = new LaunchOptions { MaxMemoryMb = MemoryMb };
            var name = SelectedAccount?.Username ?? Username;
            if (SelectedAccount is { } a && !string.IsNullOrEmpty(a.Uuid))
            {
                // 已存有 UUID 的账户（微软 / 第三方 / 已登录离线）：复用存储令牌
                opts.Username = a.Username;
                opts.Uuid = a.Uuid;
                opts.AccessToken = a.AccessToken;
                opts.UserType = a.AuthType == "microsoft" ? "msa" : a.AuthType;
            }
            else
            {
                // 纯离线：用官方算法生成离线 UUID
                opts.Username = name;
                opts.Uuid = OfflineAuthenticator.GenerateOfflineUuid(name);
                opts.AccessToken = "0";
                opts.UserType = "mojang";
            }

            Status = $"正在启动 {id} …";

            // 链接 core：按「设置 → 启动」开关在启动时真正调用 Core 模块
            var gameRoot = profile.GameRoot;
            if (profile.LaunchCompatCheckEnabled)
            {
                var incompatible = SaveCompatibilityDetector.Scan(gameRoot, id)
                    .Where(r => !r.Compatible).ToList();
                if (incompatible.Count > 0)
                {
                    var proceed = await ShowSaveCompatPromptAsync(gameRoot, id, incompatible);
                    if (!proceed)
                    {
                        Status = "已取消启动（存档兼容性问题未处理）。";
                        return;
                    }
                }
            }
            if (profile.Prewarm.Mode != PrewarmMode.Off)
            {
                var plan = LaunchPrewarmer.BuildPlan(gameRoot, id, profile.Prewarm);
                _ = LaunchPrewarmer.RunAsync(plan, profile.Prewarm);
            }

            using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var result = await GameLauncher.LaunchAsync(gameRoot, id, java, opts, null);
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
            IsBusy = false;
        }
    }

    /// <summary>弹出存档兼容性提示（§二.4，移植自 WPF）。返回 true 表示用户选择继续启动（已降级或忽略），false 表示取消。</summary>
    private async Task<bool> ShowSaveCompatPromptAsync(string gameRoot, string versionId,
        List<MCLCS.Core.Save.SaveCompatibilityReport> incompatible)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            var vm = new SaveCompatPromptViewModel(gameRoot, versionId, incompatible);
            var dlg = new SaveCompatPromptView(vm);
            await dlg.ShowDialog(owner);
            return vm.Proceed;
        }
        // 拿不到窗口则不阻断启动（降级/忽略由用户后续手动处理）
        return true;
    }

    /// <summary>从设置路径解析 JavaInfo；未配置则按游戏版本自动挑选合适 Java（避免高版本 Java 启动低版本 MC 失败）。</summary>
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

/// <summary>已安装版本展示模型。</summary>
public class VersionEntryVm
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Display => string.IsNullOrEmpty(Type) ? Id : $"{Id} ({Type})";
}
