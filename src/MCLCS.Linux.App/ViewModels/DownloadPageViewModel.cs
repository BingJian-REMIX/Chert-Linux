using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Download;
using MCLCS.Core.Installers;
using MCLCS.Core.Localization;
using MCLCS.Core.Models;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 下载页 ViewModel（对齐 MCLCS-WPF DownloadPageViewModel）：按全局侧边栏副标签
/// （minecraft / mod / shader / resourcepack / modpack / map）切换内容，
/// 顶部版本 + 加载器筛选（地图为分类 / 版本 / 排序 / 分页），卡片网格（外联封面由 ExternalIcon 渲染），
/// 底部下载队列（进度 / 暂停 / 取消）。Mod 类走 Modrinth，地图走像素茶艺（PixelMap），
/// 整合包在线浏览走 IModpackSource（当前仅 Modrinth 常驻可用）。
/// 单例：页面跨导航复用同一份队列与状态（对齐 WPF 的 Current）。
/// </summary>
public class DownloadPageViewModel : ObservableObject
{
    /// <summary>当前下载页 VM 实例。供状态栏下载进度访问同一份队列。</summary>
    public static DownloadPageViewModel Instance { get; } = new();

    private string _currentSubTab = "mod";
    private bool _isMap;
    private string _query = "";
    private string _selectedGameVersion = "";
    private string _selectedLoader = "Any";
    private string _statusMessage = "";
    private bool _isBusy;
    private DownloadCardItem? _selectedCard;

    // 地图专属
    private string _selectedCategory = "";
    private string _selectedMapVersion = "";
    private MapSort _mapSort = MapSort.Published;
    private int _mapPage = 1;
    private int _mapTotalPages = 1;
    private bool _mapFacetsLoaded;

    // 地图详情弹窗
    private PixelMapDetail? _currentMapDetail;
    private bool _isDetailOpen;
    private string _detailHint = "";

    // 整合包在线浏览
    private bool _isModpack;
    private ObservableCollection<ModpackSourceEntry> _modpackSources = new();
    private string _selectedModpackSource = "modrinth";
    private ModpackDetail? _currentModpackDetail;
    private bool _isModpackDetailOpen;
    private ModpackVersion? _selectedModpackVersion;
    private bool _installIsolated = true;
    private string _currentModpackSourceId = "modrinth";
    private string _modpackDetailHint = "";

    // Minecraft 版本下载
    private bool _isMinecraft;
    private List<VersionEntry>? _versionEntries;
    private string _selectedVersionType = "all";
    private DownloadCardItem? _currentInstallVersion;
    private bool _isInstallOpen;
    private string _selectedInstallLoader = "none";
    private bool _isInstalling;
    private string _installStatus = "";

    public ObservableCollection<DownloadCardItem> Cards { get; } = new();
    public ObservableCollection<DownloadQueueItem> Queue { get; } = new();

    /// <summary>Minecraft 版本分组（折叠卡片用）。</summary>
    public ObservableCollection<VersionGroup> VersionGroups { get; } = new();

    public ObservableCollection<string> Loaders { get; } =
        new() { "Any", "Fabric", "Forge", "Quilt", "NeoForge" };

    public ObservableCollection<string> GameVersions { get; } = new() { "" };
    public ObservableCollection<PixelMapCategory> MapCategories { get; } = new();
    public ObservableCollection<string> MapVersions { get; } = new() { "" };

    public string CurrentSubTab
    {
        get => _currentSubTab;
        set => SetField(ref _currentSubTab, value);
    }

    public bool IsMap
    {
        get => _isMap;
        private set { if (SetField(ref _isMap, value)) RaiseFilterVisibilityChanged(); }
    }

    public bool IsMinecraft
    {
        get => _isMinecraft;
        private set { if (SetField(ref _isMinecraft, value)) RaiseFilterVisibilityChanged(); }
    }

    public bool IsModpack
    {
        get => _isModpack;
        private set { if (SetField(ref _isModpack, value)) RaiseFilterVisibilityChanged(); }
    }

    private void RaiseFilterVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowModFilters));
        OnPropertyChanged(nameof(ModpackSourceVisible));
        OnPropertyChanged(nameof(ModrinthFilterVisible));
    }

    public bool ModpackSourceVisible => IsModpack;
    public bool ModrinthFilterVisible => !IsMap && !IsMinecraft && !IsModpack;
    public bool ShowModFilters => !IsMap && !IsMinecraft;

    public string SelectedVersionType
    {
        get => _selectedVersionType;
        set
        {
            if (SetField(ref _selectedVersionType, value) && IsMinecraft)
                ApplyVersionFilter();
        }
    }

    public DownloadCardItem? CurrentInstallVersion
    {
        get => _currentInstallVersion;
        set => SetField(ref _currentInstallVersion, value);
    }

    public bool IsInstallOpen
    {
        get => _isInstallOpen;
        set => SetField(ref _isInstallOpen, value);
    }

    public string SelectedInstallLoader
    {
        get => _selectedInstallLoader;
        set => SetField(ref _selectedInstallLoader, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetField(ref _isInstalling, value);
    }

    public string InstallStatus
    {
        get => _installStatus;
        set => SetField(ref _installStatus, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetField(ref _query, value) && IsMinecraft)
                ApplyVersionFilter();
        }
    }

    public string SelectedGameVersion
    {
        get => _selectedGameVersion;
        set => SetField(ref _selectedGameVersion, value);
    }

    public string SelectedLoader
    {
        get => _selectedLoader;
        set => SetField(ref _selectedLoader, value);
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set => SetField(ref _selectedCategory, value);
    }

    public string SelectedMapVersion
    {
        get => _selectedMapVersion;
        set => SetField(ref _selectedMapVersion, value);
    }

    public MapSort MapSort
    {
        get => _mapSort;
        set => SetField(ref _mapSort, value);
    }

    public string MapSortKey
    {
        get => _mapSort == MapSort.Views ? "views" : "published";
        set => MapSort = value == "views" ? MapSort.Views : MapSort.Published;
    }

    public int MapPage
    {
        get => _mapPage;
        set => SetField(ref _mapPage, value);
    }

    public int MapTotalPages
    {
        get => _mapTotalPages;
        set => SetField(ref _mapTotalPages, value);
    }

    public DownloadCardItem? SelectedCard
    {
        get => _selectedCard;
        set => SetField(ref _selectedCard, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value)) return;
            // 忙碌态变化后刷新命令可用性：此前从不刷新，按钮一旦变灰就再也不亮
            RaiseCommandsCanExecuteChanged();
        }
    }

    /// <summary>刷新受 IsBusy 影响的命令（CanExecute 依赖忙碌态，但没人通知）。</summary>
    private void RaiseCommandsCanExecuteChanged()
    {
        ICommand[] commands =
        {
            SearchCommand, StartQueueCommand, InstallModpackCommand, DownloadDetailCommand,
            DownloadExtraCommand
        };
        foreach (var c in commands)
        {
            if (c is RelayCommand rc) rc.RaiseCanExecuteChanged();
            else if (c is AsyncRelayCommand arc) arc.RaiseCanExecuteChanged();
        }
    }

    public PixelMapDetail? CurrentMapDetail
    {
        get => _currentMapDetail;
        set
        {
            if (!SetField(ref _currentMapDetail, value)) return;
            OnPropertyChanged(nameof(DetailCanDownload));
            OnPropertyChanged(nameof(DetailHasExtra));
        }
    }

    public bool DetailCanDownload => CurrentMapDetail?.CanDownload == true;
    public bool DetailHasExtra => CurrentMapDetail?.HasAdditionalResources == true;

    public string DetailHint
    {
        get => _detailHint;
        set => SetField(ref _detailHint, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetField(ref _isDetailOpen, value);
    }

    public ObservableCollection<ModpackSourceEntry> ModpackSources
    {
        get => _modpackSources;
        set => SetField(ref _modpackSources, value);
    }

    public string SelectedModpackSource
    {
        get => _selectedModpackSource;
        set
        {
            if (SetField(ref _selectedModpackSource, value))
                _ = SearchAsync();
        }
    }

    public ModpackDetail? CurrentModpackDetail
    {
        get => _currentModpackDetail;
        set
        {
            if (!SetField(ref _currentModpackDetail, value)) return;
            OnPropertyChanged(nameof(ModpackDetailCanInstall));
            OnPropertyChanged(nameof(ModpackDetailSourceNote));
            OnPropertyChanged(nameof(ModpackDetailLoaders));
            OnPropertyChanged(nameof(ModpackDetailLatestVersion));
        }
    }

    public bool IsModpackDetailOpen
    {
        get => _isModpackDetailOpen;
        set => SetField(ref _isModpackDetailOpen, value);
    }

    public ModpackVersion? SelectedModpackVersion
    {
        get => _selectedModpackVersion;
        set
        {
            if (SetField(ref _selectedModpackVersion, value))
            {
                OnPropertyChanged(nameof(ModpackDetailCanInstall));
                _modpackDetailHint = "";
                OnPropertyChanged(nameof(ModpackDetailHint));
            }
        }
    }

    public bool InstallIsolated
    {
        get => _installIsolated;
        set => SetField(ref _installIsolated, value);
    }

    public string CurrentModpackSourceId
    {
        get => _currentModpackSourceId;
        set => SetField(ref _currentModpackSourceId, value);
    }

    public bool ModpackDetailCanInstall => !string.IsNullOrEmpty(SelectedModpackVersion?.FileUrl);
    public string ModpackDetailSourceNote => "";
    public string ModpackDetailLoaders
    {
        get
        {
            if (CurrentModpackDetail is null) return "";
            var loaders = CurrentModpackDetail.Versions
                .Select(v => v.Loader)
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => char.ToUpperInvariant(l![0]) + l[1..])
                .Distinct()
                .ToList();
            return string.Join(" / ", loaders);
        }
    }

    public string ModpackDetailLatestVersion => CurrentModpackDetail?.Versions.FirstOrDefault()?.GameVersion ?? "";

    public string ModpackDetailHint
    {
        get => _modpackDetailHint;
        set => SetField(ref _modpackDetailHint, value);
    }

    public ICommand SetSubTabCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand EnqueueCommand { get; }
    public ICommand StartQueueCommand { get; }
    public ICommand PauseItemCommand { get; }
    public ICommand CancelItemCommand { get; }

    /// <summary>继续：把「已暂停」的项放回队列（此前暂停后只剩取消，取消即从 0 重下）。</summary>
    public ICommand ResumeItemCommand { get; }

    /// <summary>重试：失败 / 已取消的项再来一次（配合断点续传，不会从头开始）。</summary>
    public ICommand RetryItemCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand ChangeMapPageCommand { get; }
    public ICommand SetModpackSourceCommand { get; }
    public ICommand OpenModpackDetailCommand { get; }
    public ICommand InstallModpackCommand { get; }
    public ICommand CloseModpackDetailCommand { get; }
    public ICommand OpenModpackPageCommand { get; }
    public ICommand DownloadDetailCommand { get; }
    public ICommand DownloadExtraCommand { get; }
    public ICommand OpenMapPageCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand EnqueueInstallCommand { get; }
    public ICommand CloseInstallCommand { get; }

    private DownloadPageViewModel()
    {
        SetSubTabCommand = new RelayCommand(p => SetSubTab(p as string));
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(), _ => !IsBusy);
        EnqueueCommand = new RelayCommand(p => EnqueueSelected(p as DownloadCardItem));
        StartQueueCommand = new AsyncRelayCommand(_ => StartQueueAsync(), _ => !IsBusy);
        PauseItemCommand = new RelayCommand(p => PauseItem(p as DownloadQueueItem));
        CancelItemCommand = new RelayCommand(p => CancelItem(p as DownloadQueueItem));
        ResumeItemCommand = new RelayCommand(p => ResumeItem(p as DownloadQueueItem));
        RetryItemCommand = new RelayCommand(p => RetryItem(p as DownloadQueueItem));
        OpenDetailCommand = new RelayCommand(p => OpenDetail(p as DownloadCardItem));
        ChangeMapPageCommand = new RelayCommand(p => ChangeMapPage(p as string));
        SetModpackSourceCommand = new RelayCommand(p => SetModpackSource(p as string));
        OpenModpackDetailCommand = new RelayCommand(p => _ = OpenModpackDetailAsync(p as DownloadCardItem));
        InstallModpackCommand = new AsyncRelayCommand(_ => InstallModpackAsync(), _ => ModpackDetailCanInstall && !IsBusy);
        CloseModpackDetailCommand = new RelayCommand(_ => IsModpackDetailOpen = false);
        OpenModpackPageCommand = new RelayCommand(_ => OpenModpackPage());
        DownloadDetailCommand = new AsyncRelayCommand(_ => DownloadDetailAsync(), _ => DetailCanDownload && !IsBusy);
        DownloadExtraCommand = new AsyncRelayCommand(_ => DownloadExtraAsync(), _ => DetailHasExtra && !IsBusy);
        OpenMapPageCommand = new RelayCommand(_ => OpenMapPage());
        CloseDetailCommand = new RelayCommand(_ => IsDetailOpen = false);
        EnqueueInstallCommand = new RelayCommand(_ => EnqueueVersionInstall());
        CloseInstallCommand = new RelayCommand(_ => IsInstallOpen = false);

        Queue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(HasQueue));
        };

        _ = LoadGameVersionsAsync();
    }

    public int QueueCount => Queue.Count;
    public bool HasQueue => Queue.Count > 0;

    /// <summary>切换副标签（由 MainWindow 侧边栏路由调用）。</summary>
    public void SetSubTab(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) id = "minecraft";
        if (id is not ("minecraft" or "mod" or "shader" or "resourcepack" or "modpack" or "map")) id = "minecraft";

        CurrentSubTab = id;
        IsMap = id == "map";
        IsMinecraft = id == "minecraft";
        IsModpack = id == "modpack";

        if (id == "map" && !_mapFacetsLoaded)
            _ = LoadMapFacetsAsync();

        if (id == "modpack")
            RefreshModpackSources();

        _ = SearchAsync();
    }

    private void RefreshModpackSources()
    {
        var list = LauncherService.Instance.ModpackSources
            .Select(s => new ModpackSourceEntry
            {
                Id = s.Id,
                DisplayName = s.DisplayName,
                IsAvailable = s.IsAvailable,
                UnavailableReason = s.UnavailableReason
            })
            .ToList();

        ModpackSources = new ObservableCollection<ModpackSourceEntry>(list);

        if (ModpackSources.FirstOrDefault(s => s.Id == _selectedModpackSource && s.IsAvailable) is null)
        {
            var first = ModpackSources.FirstOrDefault(s => s.IsAvailable) ?? ModpackSources.FirstOrDefault();
            _selectedModpackSource = first?.Id ?? "modrinth";
            OnPropertyChanged(nameof(SelectedModpackSource));
        }
    }

    private void SetModpackSource(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (ModpackSources.FirstOrDefault(s => s.Id == id && s.IsAvailable) is null) return;
        SelectedModpackSource = id!;
    }

    private async Task LoadGameVersionsAsync()
    {
        try
        {
            var versions = await LauncherService.Instance.GetVanillaVersionsAsync();
            GameVersions.Clear();
            GameVersions.Add("");
            foreach (var v in versions) GameVersions.Add(v);
        }
        catch
        {
            if (GameVersions.Count == 0) GameVersions.Add("");
        }
    }

    private async Task LoadMapFacetsAsync()
    {
        try
        {
            var client = LauncherService.Instance.Pixelmap;
            var cats = await client.GetCategoriesAsync();
            MapCategories.Clear();
            MapCategories.Add(new PixelMapCategory { Id = "", Name = "全部分类" });
            foreach (var c in cats) MapCategories.Add(c);

            var vers = await client.GetVersionsAsync();
            MapVersions.Clear();
            MapVersions.Add("");
            foreach (var v in vers) MapVersions.Add(v);

            _mapFacetsLoaded = true;
        }
        catch
        {
            // 地图站不可用时保留空列表
        }
    }

    private async Task SearchAsync(bool resetPage = true)
    {
        IsBusy = true;
        try
        {
            Cards.Clear();
            if (IsMinecraft) await LoadVersionsAsync();
            else if (IsMap) await SearchMapsAsync(resetPage);
            else if (IsModpack) await SearchModpackAsync();
            else await SearchModrinthAsync();

            StatusMessage = Cards.Count > 0
                ? IsMap && MapTotalPages > 1
                    ? $"找到 {Cards.Count} 个结果（第 {MapPage} / {MapTotalPages} 页）"
                    : $"找到 {Cards.Count} 个结果"
                : "未找到结果";
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Minecraft 版本下载 ----

    public class VersionGroup
    {
        public string Header { get; init; } = "";
        public ObservableCollection<DownloadCardItem> Items { get; } = new();
        public int Count => Items.Count;
    }

    private async Task LoadVersionsAsync()
    {
        if (_versionEntries is null)
        {
            try { _versionEntries = await LauncherService.Instance.GetVanillaVersionsDetailedAsync(); }
            catch { _versionEntries = new List<VersionEntry>(); }
        }
        ApplyVersionFilter();
    }

    private void ApplyVersionFilter()
    {
        VersionGroups.Clear();
        if (_versionEntries is null) return;
        var q = (Query ?? "").Trim().ToLowerInvariant();
        var type = SelectedVersionType;

        var groups = new Dictionary<string, List<VersionEntry>>();
        foreach (var v in _versionEntries)
        {
            var bucket = v.Type switch
            {
                "release" => "release",
                "snapshot" => "snapshot",
                _ => "old"
            };
            if (type != "all" && bucket != type) continue;
            if (q.Length > 0 && !v.Id.ToLowerInvariant().Contains(q)) continue;

            var key = GroupKey(v.Id, bucket);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<VersionEntry>();
                groups[key] = list;
            }
            list.Add(v);
        }

        foreach (var key in groups.Keys.OrderBy(GroupOrder).ThenByDescending(MajorMinor))
        {
            var g = new VersionGroup { Header = $"{key}（{groups[key].Count}）" };
            foreach (var v in groups[key].OrderByDescending(x => x.Id, StringComparer.OrdinalIgnoreCase))
            {
                g.Items.Add(new DownloadCardItem
                {
                    Id = v.Id,
                    Title = v.Id,
                    Author = "Mojang",
                    Summary = key == "快照 & 预览" ? "快照" : (key is "旧版本" or "其他") ? "旧版" : "正式版",
                    IconUrl = null,
                    FallbackToken = "download",
                    Source = "Minecraft",
                    SubTab = "minecraft",
                    VersionType = v.Type
                });
            }
            VersionGroups.Add(g);
        }
    }

    private static string GroupKey(string id, string bucket)
    {
        if (bucket == "snapshot") return "快照 & 预览";
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^(\d+)\.(\d+)");
        if (m.Success) return $"{m.Groups[1].Value}.{m.Groups[2].Value}";
        return bucket == "old" ? "旧版本" : "其他";
    }

    private static int GroupOrder(string header) =>
        header == "快照 & 预览" ? 0 : header is "旧版本" or "其他" ? (header == "旧版本" ? 2 : 3) : 1;

    private static int MajorMinor(string header)
    {
        var m = System.Text.RegularExpressions.Regex.Match(header, @"^(\d+)\.(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b))
            return a * 1000 + b;
        return 0;
    }

    private async Task SearchModrinthAsync()
    {
        var (type, fallback) = CurrentSubTab switch
        {
            "shader" => (ModrinthProjectType.Shader, "shader"),
            "resourcepack" => (ModrinthProjectType.ResourcePack, "tex"),
            "modpack" => (ModrinthProjectType.Modpack, "pack"),
            _ => (ModrinthProjectType.Mod, "mod")
        };

        var loader = ParseLoader(SelectedLoader);
        var hits = await LauncherService.Instance.SearchModsAsync(
            Query,
            string.IsNullOrEmpty(SelectedGameVersion) ? null : SelectedGameVersion,
            loader, type);

        foreach (var h in hits)
            Cards.Add(new DownloadCardItem
            {
                Id = h.ProjectId,
                Title = h.Title,
                Author = h.Slug,
                Summary = h.Description,
                IconUrl = string.IsNullOrEmpty(h.IconUrl) ? null : h.IconUrl,
                FallbackToken = fallback,
                Source = "Modrinth",
                SubTab = CurrentSubTab
            });
    }

    private async Task SearchModpackAsync()
    {
        var items = await LauncherService.Instance.SearchModpacksAsync(
            Query,
            string.IsNullOrEmpty(SelectedGameVersion) ? null : SelectedGameVersion,
            SelectedLoader == "Any" ? null : SelectedLoader,
            _selectedModpackSource,
            CancellationToken.None);

        foreach (var it in items)
        {
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(it.LatestGameVersion)) meta.Add("MC " + it.LatestGameVersion);
            if (it.Loaders.Count > 0) meta.Add(it.LoaderSummary);
            if (!string.IsNullOrEmpty(it.DownloadsText)) meta.Add(it.DownloadsText);

            Cards.Add(new DownloadCardItem
            {
                Id = it.Id,
                Title = it.Title,
                Author = it.Author,
                Summary = it.Summary,
                IconUrl = it.IconUrl,
                FallbackToken = "pack",
                Source = it.Source,
                SubTab = "modpack",
                MetaText = string.Join(" · ", meta)
            });
        }
    }

    private async Task SearchMapsAsync(bool resetPage = true)
    {
        var client = LauncherService.Instance.Pixelmap;
        if (resetPage) _mapPage = 1;

        var result = await client.SearchAsync(
            keyword: string.IsNullOrWhiteSpace(Query) ? null : Query,
            category: string.IsNullOrEmpty(SelectedCategory) ? null : SelectedCategory,
            version: string.IsNullOrEmpty(SelectedMapVersion) ? null : SelectedMapVersion,
            page: _mapPage, limit: 24, sort: _mapSort);

        MapTotalPages = result.PageCount;
        MapPage = result.Page;

        foreach (var it in result.Items)
            Cards.Add(new DownloadCardItem
            {
                Id = it.Slug,
                Title = it.Title,
                Author = it.Author,
                Summary = it.Summary,
                IconUrl = it.PreviewImage,
                FallbackToken = "map",
                Source = "PixelMap",
                SubTab = "map",
                Slug = it.Slug,
                Views = it.Views,
                VersionSummary = it.VersionSummary,
                CategorySummary = it.CategorySummary,
                HasExtra = it.ExtensionExist
            });
    }

    private void ChangeMapPage(string? dir)
    {
        if (dir == "next" && _mapPage < _mapTotalPages) _mapPage++;
        else if (dir == "prev" && _mapPage > 1) _mapPage--;
        else return;

        MapPage = _mapPage;
        _ = SearchAsync(resetPage: false);
    }

    private static LoaderType ParseLoader(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "Any") return LoaderType.Any;
        return Enum.TryParse<LoaderType>(name, ignoreCase: true, out var v) ? v : LoaderType.Any;
    }

    private void EnqueueSelected(DownloadCardItem? card)
    {
        try { EnqueueSelectedCore(card); }
        catch (Exception ex) { StatusMessage = $"加入队列失败：{ex.Message}"; }
    }

    private void EnqueueSelectedCore(DownloadCardItem? card)
    {
        if (card is null) { StatusMessage = "请先选择一项"; return; }

        string dir;
        string kind;
        switch (card.SubTab)
        {
            case "shader":
                dir = PathEx.ShaderPacksDir(GameConstants.DefaultGameRoot);
                kind = "mod";
                break;
            case "resourcepack":
                dir = PathEx.ResourcePacksDir(GameConstants.DefaultGameRoot);
                kind = "mod";
                break;
            case "modpack":
                dir = Path.Combine(GameConstants.DefaultGameRoot, "modpacks");
                kind = "modpack";
                break;
            case "map":
                dir = PathEx.SavesDir(GameConstants.DefaultGameRoot);
                kind = "map";
                break;
            default:
                dir = PathEx.ModsDir(GameConstants.DefaultGameRoot);
                kind = "mod";
                break;
        }

        var loader = ParseLoader(SelectedLoader);

        // 去重：重复点「加入队列」会把同一资源塞两份 —— 下载两遍、覆盖同一个文件。
        var dedupKey = $"{kind}|{card.Id}|{dir}|{SelectedGameVersion ?? ""}|{loader}|none";
        var dup = Queue.FirstOrDefault(q => q.IsActive && q.DedupKey == dedupKey);
        if (dup is not null)
        {
            StatusMessage = $"已在队列中（{dup.Status}），无需重复添加：{card.Title}";
            return;
        }

        Queue.Add(new DownloadQueueItem
        {
            ProjectId = card.Id,
            Title = card.Title,
            Summary = card.Summary,
            TargetDir = dir,
            GameVersion = string.IsNullOrEmpty(SelectedGameVersion) ? null : SelectedGameVersion,
            Loader = loader,
            Kind = kind,
            Slug = card.Slug,
            Source = card.SubTab == "modpack" ? card.Source : "modrinth"
        });

        StatusMessage = $"已加入队列：{card.Title}（共 {Queue.Count} 项）";
    }

    private async Task StartQueueAsync()
    {
        if (IsBusy) return;     // 已有轮次在跑，新加入的项由该轮次顺带处理
        IsBusy = true;
        try
        {
            while (true)
            {
                // 只自动拾取「排队中」：「已暂停」必须用户点「继续」才回来，
                // 否则暂停会在下一轮被立刻拉起，等于没有暂停。
                var pending = Queue.Where(q => q.Status == "排队中").ToList();
                if (pending.Count == 0) break;

                await RunPendingAsync(pending);
            }

            // 失败项保留在队列里：此前失败原因无从查起、也没法重试
            var failed = Queue.Count(q => q.Status == "失败");
            var done = Queue.Count(q => q.Status == "已完成");

            if (MainViewModel.Instance is { } mv)
            {
                mv.DownloadProgress = 0;
                mv.DownloadText = failed > 0
                    ? $"下载完成，{failed} 项失败"
                    : done > 0 ? $"下载队列完成（{done} 项）" : "下载队列完成";
            }

            if (failed > 0)
            {
                try
                {
                    ToastService.Instance.Show(new ToastOptions
                    {
                        Title = "下载未完成",
                        Message = $"{failed} 项下载失败，已保留在队列中：可点「重试」继续（已下载的部分会续传）。",
                        DurationMs = 6000,
                        Danger = true
                    });
                }
                catch { /* 提示失败不影响队列结果 */ }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>并发跑一批队列项，上限跟随设置里的「最大并发下载数」（此前是死板的串行）。</summary>
    private async Task RunPendingAsync(List<DownloadQueueItem> batch)
    {
        var limit = Math.Max(1, MCLCS.Core.Profiles.ProfileStore.Load(GameConstants.DefaultGameRoot).MaxConcurrentDownloads);
        using var sem = new SemaphoreSlim(limit);
        await Task.WhenAll(batch.Select(async item =>
        {
            await sem.WaitAsync();
            try { await RunOneAsync(item); }
            finally { sem.Release(); }
        }));
    }

    /// <summary>执行单个队列项并回写状态。</summary>
    private async Task RunOneAsync(DownloadQueueItem local)
    {
        local.Cts = new CancellationTokenSource();
        local.Status = local.Kind == "version" ? "安装中" : "下载中";
        local.Progress = 0;

        var ok = false;
        try
        {
            ok = local.Kind switch
            {
                "modpack" => (await LauncherService.Instance.InstallModpackAsync(
                    local.Source, local.ProjectId, local.GameVersion, local.Title,
                    Progress(local), local.Cts.Token)) is not null,
                "map" => await LauncherService.Instance.DownloadMapAsync(
                    local.Slug ?? "", Progress(local), local.Cts.Token),
                "version" => (await LauncherService.Instance.InstallVersionAsync(
                    local.ProjectId, local.InstallLoader, Progress(local), local.Cts.Token)) is not null,
                _ => await LauncherService.Instance.DownloadModAsync(
                    local.ProjectId, local.TargetDir, local.GameVersion, local.Loader,
                    Progress(local), local.Cts.Token)
            };

            if (local.Cts.Token.IsCancellationRequested && local.Status != "已暂停")
                local.Status = "已取消";
            else
                local.Status = ok ? "已完成" : "失败";
        }
        catch (OperationCanceledException)
        {
            local.Status = "已取消";
        }
        catch (Exception ex)
        {
            // 把原因留在项上并弹一条 Toast：「失败静默三连」到此结束
            local.ErrorMessage = ex.Message;
            local.Status = "失败";
            try
            {
                ToastService.Instance.Show(new ToastOptions
                {
                    Title = "下载失败",
                    Message = $"{local.Title}：{ex.Message}",
                    DurationMs = 6000,
                    Danger = true
                });
            }
            catch { /* 提示失败不影响队列结果 */ }
        }

        local.Progress = local.Status == "已完成" ? 100 : local.Progress;
    }

    /// <summary>继续一个已暂停的项。</summary>
    private void ResumeItem(DownloadQueueItem? item)
    {
        if (item is null || item.Status != "已暂停") return;
        item.Status = "排队中";
        if (!IsBusy) _ = StartQueueAsync();
    }

    /// <summary>重试一个失败 / 已取消的项。</summary>
    private void RetryItem(DownloadQueueItem? item)
    {
        if (item is null || item.Status is not ("失败" or "已取消")) return;
        item.ErrorMessage = "";
        item.Status = "排队中";
        item.Progress = 0;
        if (!IsBusy) _ = StartQueueAsync();
    }

    private static IProgress<double> Progress(DownloadQueueItem item) =>
        new Progress<double>(p =>
        {
            item.Progress = p * 100;
            if (MainViewModel.Instance is { } mv)
            {
                mv.DownloadProgress = p * 100;
                mv.DownloadText = $"下载 {item.Title}：{p:P0}";
            }
        });

    private void PauseItem(DownloadQueueItem? item)
    {
        if (item is null) return;
        item.Cts?.Cancel();
        if (item.Status == "下载中") item.Status = "已暂停";
    }

    private void CancelItem(DownloadQueueItem? item)
    {
        if (item is null) return;
        item.Cts?.Cancel();
        item.Status = "已取消";
    }

    private void OpenDetail(DownloadCardItem? card)
    {
        if (card is null) return;

        if (card.SubTab == "modpack")
        {
            _ = OpenModpackDetailAsync(card);
            return;
        }

        if (card.Source == "Minecraft")
        {
            CurrentInstallVersion = card;
            SelectedInstallLoader = "none";
            IsInstallOpen = true;
            return;
        }

        if (card.Source == "PixelMap")
        {
            _ = LoadMapDetailAsync(card);
        }
        else if (!string.IsNullOrEmpty(card.Id))
        {
            var url = $"https://modrinth.com/project/{card.Id}";
            try { OpenUrl(url); }
            catch { StatusMessage = "无法打开外部浏览器"; }
        }
    }

    private void EnqueueVersionInstall()
    {
        if (CurrentInstallVersion is null) return;
        var card = CurrentInstallVersion;
        var loader = SelectedInstallLoader;

        Queue.Add(new DownloadQueueItem
        {
            ProjectId = card.Id,
            Title = card.Title,
            Summary = LoaderLabel(loader),
            TargetDir = GameConstants.DefaultGameRoot,
            Loader = LoaderType.Any,
            Kind = "version",
            InstallLoader = loader
        });

        StatusMessage = $"已加入安装队列：Minecraft {card.Title}（{LoaderLabel(loader)}）";
        IsInstallOpen = false;

        if (!IsBusy) _ = StartQueueAsync();
    }

    private static string LoaderLabel(string loader) => loader switch
    {
        "fabric" => "Fabric（自动配对最新 Fabric API）",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => "原版（无加载器）"
    };

    // ---- 整合包详情弹窗 ----

    private async Task OpenModpackDetailAsync(DownloadCardItem? card)
    {
        if (card is null) return;
        IsBusy = true;
        try
        {
            var detail = await LauncherService.Instance.GetModpackDetailAsync(card.Source, card.Id, CancellationToken.None);
            if (detail is null) { StatusMessage = "无法获取整合包详情"; return; }

            CurrentModpackSourceId = card.Source;
            CurrentModpackDetail = detail;
            SelectedModpackVersion = detail.Versions.FirstOrDefault();
            InstallIsolated = true;

            ModpackDetailHint = detail.HasVersions
                ? (ModpackDetailCanInstall
                    ? ""
                    : "该版本无可用的直链下载。")
                : "该整合包暂无可安装版本。";

            IsModpackDetailOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"整合包详情加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallModpackAsync()
    {
        if (CurrentModpackDetail is null || SelectedModpackVersion is null) return;
        IsBusy = true;
        ModpackDetailHint = "正在安装整合包…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (MainViewModel.Instance is { } mv)
                {
                    mv.DownloadProgress = p * 100;
                    mv.DownloadText = $"安装 {CurrentModpackDetail.Title}：{p:P0}";
                }
            });

            var isolated = InstallIsolated;
            var result = await LauncherService.Instance.InstallModpackVersionAsync(
                CurrentModpackSourceId, SelectedModpackVersion, isolated,
                CurrentModpackDetail.Title, progress, CancellationToken.None);

            if (result is null)
            {
                ModpackDetailHint = "安装失败：所选版本无可用下载链接。";
                StatusMessage = "整合包安装失败";
                return;
            }

            var where = result.Isolated ? $"隔离目录 versions/{result.VersionId}" : "共享目录";
            var renamed = result.Renamed ? "（因重名已自动改名）" : "";
            ModpackDetailHint = $"已安装：{result.Name} → {where}{renamed}（{result.ModCount} 个 Mod）";
            StatusMessage = $"整合包已安装：{result.Name}";
        }
        catch (Exception ex)
        {
            ModpackDetailHint = $"安装失败：{ex.Message}";
            StatusMessage = $"整合包安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenModpackPage()
    {
        var url = CurrentModpackDetail?.PageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { OpenUrl(url); }
        catch { StatusMessage = "无法打开外部浏览器"; }
    }

    private async Task LoadMapDetailAsync(DownloadCardItem card)
    {
        IsBusy = true;
        try
        {
            var detail = await LauncherService.Instance.Pixelmap.GetDetailAsync(card.Slug ?? "", CancellationToken.None);
            if (detail is null) { StatusMessage = "无法获取地图详情"; return; }

            CurrentMapDetail = detail;
            DetailHint = detail.CanDownload
                ? detail.HasAdditionalResources
                    ? "该地图附带资源包 / 光影，可一并安装。"
                    : ""
                : "作者未授权启动器直接下载，请前往地图站页面获取。";
            IsDetailOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"详情加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadDetailAsync()
    {
        if (CurrentMapDetail is null) return;
        IsBusy = true;
        DetailHint = "正在下载地图…";
        try
        {
            var ok = await LauncherService.Instance.DownloadMapAsync(CurrentMapDetail.Slug, null, CancellationToken.None);
            StatusMessage = ok ? $"地图已安装：{CurrentMapDetail.Title}" : "地图下载/安装失败";
            DetailHint = ok
                ? $"已解压进 saves：{CurrentMapDetail.Title}"
                : "地图下载或解压失败，可尝试打开地图站页面手动下载。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"地图下载失败：{ex.Message}";
            DetailHint = $"下载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadExtraAsync()
    {
        if (CurrentMapDetail is null) return;
        IsBusy = true;
        DetailHint = "正在下载附加资源…";
        try
        {
            var result = await LauncherService.Instance.DownloadMapExtraAsync(
                CurrentMapDetail, null, CancellationToken.None);

            if (result is null)
            {
                DetailHint = "该地图没有提供附加资源直链。";
                StatusMessage = "无附加资源";
                return;
            }

            if (!result.Ok)
            {
                DetailHint = $"附加资源安装失败：{result.Error}";
                StatusMessage = "附加资源安装失败";
                return;
            }

            var hint = $"附加资源已安装：{result.Summary}";
            if (result.HasUnknown)
                hint += "（部分文件未能识别类型，已保留在 downloads/extras）";
            DetailHint = hint;
            StatusMessage = hint;
        }
        catch (Exception ex)
        {
            DetailHint = $"附加资源下载失败：{ex.Message}";
            StatusMessage = $"附加资源下载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenMapPage()
    {
        var url = CurrentMapDetail?.PageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { OpenUrl(url); }
        catch { StatusMessage = "无法打开外部浏览器"; }
    }

    /// <summary>在系统浏览器打开链接（Linux 用 xdg-open 兜底）。</summary>
    private static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        // Linux / macOS：交由桌面环境打开
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsLinux() ? "xdg-open" : "open",
            Arguments = url,
            UseShellExecute = false
        });
    }
}
