using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Download;
using MCLCS.Core.Models;
using MCLCS.Core.Mods;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>版本设置（对话框主体 ViewModel）：覆盖 8 大模块，并真实接入加载器安装与 Modrinth 模组管理。</summary>
public class VersionSettingsViewModel : ObservableObject
{
    private readonly string _gameRoot;
    private readonly string _versionId;
    private readonly string _versionType;
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly IDownloader _downloader;
    private readonly ILogger _logger;

    public string VersionId => _versionId;
    public string VersionType => _versionType;
    public string BaseMcVersion => VersionProfileStore.BaseMcVersion(_gameRoot, _versionId);

    public event Action? VersionsChanged;

    // ---- ① 基础信息 ----
    private string _displayName = "";
    public string DisplayName { get => _displayName; set => SetField(ref _displayName, value); }

    // ---- ④ 隔离 / 游戏目录 ----
    public IsolationMode Isolation { get; set; } = IsolationMode.Auto;
    private string? _customGameDir;
    public string? CustomGameDir
    {
        get => _customGameDir;
        set
        {
            if (!SetField(ref _customGameDir, value)) return;
            OnPropertyChanged(nameof(EffectiveGameDirDisplay));
            RefreshInstalled();
        }
    }

    /// <summary>
    /// 有效工作目录：与启动器保持一致——自定义目录优先；选定自动隔离或版本实际已通过
    /// .mclcs-isolated marker 隔离时指向 versions/&lt;id&gt;，否则回落共享根目录。
    /// 关键修正：之前仅按 profile.Isolation 字段解析，当版本已隔离但 profile 标 Shared（孤儿 marker）
    /// 时误把展示/操作目录落到根游戏目录。
    /// </summary>
    public string EffectiveGameDirDisplay => ComputeEffectiveGameDir();

    private string ComputeEffectiveGameDir()
    {
        if (Isolation == IsolationMode.Custom && !string.IsNullOrWhiteSpace(CustomGameDir))
            return CustomGameDir!;
        if (Isolation == IsolationMode.Auto || VersionIsolation.IsIsolated(_gameRoot, _versionId))
            return Path.Combine(_gameRoot, "versions", _versionId);
        return _gameRoot;
    }

    // ---- ③ 模组加载器 ----
    public ModLoaderKind DetectedLoader { get; }
    public string DetectedLoaderText =>
        DetectedLoader switch
        {
            ModLoaderKind.Fabric => "Fabric",
            ModLoaderKind.Forge => "Forge",
            ModLoaderKind.Quilt => "Quilt",
            ModLoaderKind.NeoForge => "NeoForge",
            _ => "原版（未安装加载器）"
        };
    public bool IsVanilla => DetectedLoader == ModLoaderKind.None;

    // ---- ⑤ Java 与性能 ----
    private string? _javaPath;
    public string? JavaPath { get => _javaPath; set => SetField(ref _javaPath, value); }
    private double? _maxMemoryMb;
    public double? MaxMemoryMb { get => _maxMemoryMb; set => SetField(ref _maxMemoryMb, value); }
    private double? _minMemoryMb;
    public double? MinMemoryMb { get => _minMemoryMb; set => SetField(ref _minMemoryMb, value); }
    private string _extraJvmArgsText = "";
    public string ExtraJvmArgsText { get => _extraJvmArgsText; set => SetField(ref _extraJvmArgsText, value); }

    // ---- ⑥ 分辨率与窗口 ----
    private double? _resolutionWidth;
    public double? ResolutionWidth { get => _resolutionWidth; set => SetField(ref _resolutionWidth, value); }
    private double? _resolutionHeight;
    public double? ResolutionHeight { get => _resolutionHeight; set => SetField(ref _resolutionHeight, value); }
    private bool _fullscreen;
    public bool Fullscreen { get => _fullscreen; set => SetField(ref _fullscreen, value); }

    // ---- ④ 隔离模式（ComboBox 绑定用字符串值） ----
    public string IsolationValue
    {
        get => Isolation.ToString();
        set
        {
            if (Enum.TryParse<IsolationMode>(value, out var m)) Isolation = m;
            OnPropertyChanged(nameof(EffectiveGameDirDisplay));
            OnPropertyChanged(nameof(IsCustomDir));
            RefreshInstalled();
        }
    }
    /// <summary>是否处于「自定义目录」隔离模式（控制自定义目录输入框可见性）。</summary>
    public bool IsCustomDir => Isolation == IsolationMode.Custom;

    // ---- ⑦ Mod 分页（ComboBox 绑定用字符串值） ----
    public string ModTabValue
    {
        get => ModTab.ToString();
        set { if (Enum.TryParse<ModTabKind>(value, out var t)) { ModTab = t; OnPropertyChanged(nameof(IsModsTab)); } }
    }
    /// <summary>当前是否在 Mods 分页（控制「检查更新」按钮可见性）。</summary>
    public bool IsModsTab => ModTab == ModTabKind.Mods;

    // ---- ⑧ 版本锁定 ----
    private bool _locked;
    public bool Locked { get => _locked; set => SetField(ref _locked, value); }

    // ---- ⑨ 账户绑定 ----
    public ObservableCollection<AccountEntry> Accounts { get; } = new();
    private AccountEntry? _boundAccount;
    /// <summary>启动该版本时优先使用的账号；为空表示「跟随全局（最后使用）」。</summary>
    public AccountEntry? BoundAccount
    {
        get => _boundAccount;
        set => SetField(ref _boundAccount, value);
    }
    public ICommand ClearAccountBindingCommand => new RelayCommand(_ => BoundAccount = null);

    // ---- 模组管理 ⑦ ----
    public enum ModTabKind { Mods, ResourcePacks, Shaders }
    private ModTabKind _modTab = ModTabKind.Mods;
    public ModTabKind ModTab { get => _modTab; set { if (SetField(ref _modTab, value)) RefreshInstalled(); } }

    public ObservableCollection<InstalledItemViewModel> InstalledItems { get; } = new();
    public ObservableCollection<ModSearchHit> SearchResults { get; } = new();

    private string _searchQuery = "";
    public string SearchQuery { get => _searchQuery; set => SetField(ref _searchQuery, value); }
    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }
    private string _status = "";
    public string Status { get => _status; set => SetField(ref _status, value); }

    public VersionSettingsViewModel(string versionId, string versionType, string gameRoot)
    {
        _versionId = versionId;
        _versionType = versionType;
        _gameRoot = gameRoot;
        _downloader = new HttpDownloader(_http, 8, null);
        _logger = new VmLogger(s => Status = s);

        var p = VersionProfileStore.Load(gameRoot, versionId);
        _displayName = p.DisplayName;
        Isolation = p.Isolation;
        _customGameDir = p.CustomGameDir;
        _javaPath = p.JavaPath;
        _maxMemoryMb = p.MaxMemoryMb;
        _minMemoryMb = p.MinMemoryMb;
        _extraJvmArgsText = string.Join("\n", p.ExtraJvmArgs);
        _resolutionWidth = p.ResolutionWidth;
        _resolutionHeight = p.ResolutionHeight;
        _fullscreen = p.Fullscreen;
        _locked = p.Locked;

        foreach (var a in AccountStore.Load(gameRoot))
            Accounts.Add(a);
        _boundAccount = string.IsNullOrWhiteSpace(p.BoundAccountId)
            ? null
            : Accounts.FirstOrDefault(a => a.Id == p.BoundAccountId);

        DetectedLoader = VersionProfileStore.DetectLoader(gameRoot, versionId);
        RefreshInstalled();
    }

    private string EffectiveDir =>
        VersionProfileStore.EffectiveGameDir(_gameRoot, _versionId,
            new VersionProfile { Isolation = Isolation, CustomGameDir = CustomGameDir });

    // ---- 持久化 ----
    public void Save()
    {
        var p = new VersionProfile
        {
            DisplayName = DisplayName.Trim(),
            Isolation = Isolation,
            CustomGameDir = CustomGameDir?.Trim(),
            JavaPath = JavaPath?.Trim(),
            MaxMemoryMb = (int?)MaxMemoryMb,
            MinMemoryMb = (int?)MinMemoryMb,
            ExtraJvmArgs = ExtraJvmArgsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            ResolutionWidth = (int?)ResolutionWidth,
            ResolutionHeight = (int?)ResolutionHeight,
            Fullscreen = Fullscreen,
            Locked = Locked,
            BoundAccountId = BoundAccount?.Id
        };
        VersionProfileStore.Save(_gameRoot, _versionId, p);
        VersionProfileStore.ApplyIsolation(_gameRoot, _versionId, p);
        Status = "已保存版本设置";
    }

    /// <summary>
    /// 锁定守卫：若当前版本已锁定，弹出警示并返回 true（调用方应中止改写操作）。
    /// 锁定只阻止「改写版本文件」（安装加载器 / 增删 Mod），不阻止启动游戏。
    /// </summary>
    private bool GuardLocked(string action)
    {
        if (!VersionProfileStore.IsLocked(_gameRoot, _versionId)) return false;
        var msg = $"版本「{_versionId}」已锁定，无法{action}。请先在「版本锁定」中关闭锁定。";
        Status = msg;
        MCLCS.Linux.App.ToastService.Instance.Show(new MCLCS.Linux.App.ToastOptions
        {
            Title = "版本已锁定",
            Message = msg,
            DurationMs = 5000,
            Danger = true
        });
        return true;
    }

    // ---- ③ 安装加载器 ----
    public ICommand InstallLoaderCommand => new AsyncRelayCommand(InstallLoaderAsync);
    private async Task InstallLoaderAsync(object? loader)
    {
        var name = loader as string;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (GuardLocked($"安装 {name} 加载器")) return;
        Busy = true; Status = $"正在安装 {name}（基于 {BaseMcVersion}）…";
        try
        {
            var newId = await LauncherService.Instance.InstallVersionAsync(BaseMcVersion, name!, null, default);
            if (!string.IsNullOrEmpty(newId))
            {
                Status = $"{name} 安装完成：新实例 {newId}（请在版本列表切换到它）";
                VersionsChanged?.Invoke();
            }
            else
            {
                Status = $"{name} 安装失败";
            }
        }
        catch (Exception ex)
        {
            Status = $"安装 {name} 失败：{ex.Message}";
        }
        finally { Busy = false; }
    }

    // ---- ⑦ 文件夹 ----
    public ICommand OpenFolderCommand => new RelayCommand(OpenFolder);
    private void OpenFolder(object? kind)
    {
        var dir = EffectiveDir;
        var target = kind switch
        {
            "mods" => Path.Combine(dir, "mods"),
            "resourcepacks" => Path.Combine(dir, "resourcepacks"),
            "shaderpacks" => Path.Combine(dir, "shaderpacks"),
            "saves" => Path.Combine(dir, "saves"),
            _ => dir
        };
        Directory.CreateDirectory(target);
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch { Status = $"无法打开文件夹：{target}"; }
    }

    // ---- ⑦ 已装列表 ----
    public ICommand RefreshModsCommand => new RelayCommand(_ => RefreshInstalled());
    private void RefreshInstalled()
    {
        InstalledItems.Clear();
        var dir = EffectiveDir;
        switch (ModTab)
        {
            case ModTabKind.Mods:
                var mgr = new ModManager(dir, _http, _downloader);
                foreach (var m in mgr.ListInstalledMods())
                    InstalledItems.Add(new InstalledItemViewModel
                    {
                        FileName = m.FileName,
                        DisplayName = string.IsNullOrEmpty(m.Name) ? m.FileName : m.Name,
                        Subtitle = $"v{m.InstalledVersion} · {m.Loader}",
                        Kind = "mod"
                    });
                break;
            case ModTabKind.ResourcePacks:
                AddFolderItems(Path.Combine(dir, "resourcepacks"), "resourcepack");
                break;
            case ModTabKind.Shaders:
                AddFolderItems(Path.Combine(dir, "shaderpacks"), "shaderpack");
                break;
        }
    }
    private void AddFolderItems(string dir, string kind)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(dir).OrderBy(Path.GetFileName))
            InstalledItems.Add(new InstalledItemViewModel
            {
                FileName = Path.GetFileName(f),
                DisplayName = Path.GetFileName(f),
                Subtitle = Directory.Exists(f) ? "文件夹" : "压缩包",
                Kind = kind
            });
    }

    public ICommand RemoveItemCommand => new AsyncRelayCommand(RemoveItemAsync);
    private async Task RemoveItemAsync(object? fileName)
    {
        var name = fileName as string;
        if (string.IsNullOrEmpty(name)) return;
        if (GuardLocked("移除文件")) return;
        var dir = EffectiveDir;
        var target = ModTab switch
        {
            ModTabKind.Mods => Path.Combine(dir, "mods", name),
            ModTabKind.ResourcePacks => Path.Combine(dir, "resourcepacks", name),
            ModTabKind.Shaders => Path.Combine(dir, "shaderpacks", name),
            _ => Path.Combine(dir, name)
        };

        // 【修复】删版本有二次确认，删 Mod / 材质包 / 光影却没有：误点一下文件就没了
        var confirm = await DialogService.Instance.ShowAsync(new DialogOptions
        {
            Title = "移除文件",
            Content = $"确定要移除「{name}」吗？\n\n{target}\n\n该操作不可恢复。",
            Buttons = new[]
            {
                new DialogButton("移除", "remove", DialogButtonKind.Primary, isDefault: true),
                new DialogButton("取消", "cancel")
            }
        });
        if (!string.Equals(confirm as string, "remove", StringComparison.Ordinal)) return;

        try { if (File.Exists(target)) File.Delete(target); else if (Directory.Exists(target)) Directory.Delete(target, true); }
        catch (Exception ex) { Status = $"删除失败：{ex.Message}"; }
        RefreshInstalled();
    }

    // ---- ⑦ 搜索 + 添加（Modrinth） ----
    public ICommand SearchModsCommand => new AsyncRelayCommand(_ => SearchModsAsync());
    private async Task SearchModsAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        Busy = true; Status = "正在搜索 Modrinth…";
        SearchResults.Clear();
        try
        {
            var type = ModTab switch
            {
                ModTabKind.ResourcePacks => ModrinthProjectType.ResourcePack,
                ModTabKind.Shaders => ModrinthProjectType.Shader,
                _ => ModrinthProjectType.Mod
            };
            var loader = DetectedLoader switch
            {
                ModLoaderKind.Fabric => LoaderType.Fabric,
                ModLoaderKind.Forge => LoaderType.Forge,
                ModLoaderKind.Quilt => LoaderType.Quilt,
                ModLoaderKind.NeoForge => LoaderType.NeoForge,
                _ => LoaderType.Any
            };
            var client = new ModrinthClient(_http);
            var result = await client.SearchAsync(SearchQuery, BaseMcVersion, loader, type, limit: 25);
            foreach (var h in result.Hits)
                SearchResults.Add(new ModSearchHit { ProjectId = h.ProjectId, Title = h.Title, Slug = h.Slug, IconUrl = h.IconUrl });
            Status = SearchResults.Count > 0 ? $"找到 {SearchResults.Count} 个结果" : "无匹配结果";
        }
        catch (Exception ex) { Status = $"搜索失败：{ex.Message}"; }
        finally { Busy = false; }
    }

    public ICommand AddModCommand => new AsyncRelayCommand(AddModAsync);
    private async Task AddModAsync(object? param)
    {
        if (param is not ModSearchHit hit) return;
        if (GuardLocked("添加模组 / 资源包 / 光影")) return;
        Busy = true; Status = $"正在添加 {hit.Title}…";
        try
        {
            var client = new ModrinthClient(_http);
            var versions = await client.GetVersionsAsync(hit.ProjectId, default);
            if (versions.Count == 0) { Status = "无可用版本"; return; }

            var loader = DetectedLoader switch
            {
                ModLoaderKind.Fabric => LoaderType.Fabric,
                ModLoaderKind.Forge => LoaderType.Forge,
                ModLoaderKind.Quilt => LoaderType.Quilt,
                ModLoaderKind.NeoForge => LoaderType.NeoForge,
                _ => LoaderType.Any
            };
            // 优先选与基版本完全匹配的文件
            ModrinthFile? file = null;
            foreach (var v in versions)
            {
                var f = client.SelectBestFile(v, BaseMcVersion, loader);
                if (f is not null) { file = f; break; }
            }
            file ??= client.SelectBestFile(versions[0], null, LoaderType.Any);
            if (file is null) { Status = "未找到可下载文件"; return; }

            var sub = ModTab switch
            {
                ModTabKind.ResourcePacks => "resourcepacks",
                ModTabKind.Shaders => "shaderpacks",
                _ => "mods"
            };
            var destDir = Path.Combine(EffectiveDir, sub);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, file.FileName);
            await _downloader.DownloadAsync(new DownloadItem(new[] { file.Url }, dest, file.Hashes.Sha1), null, default);
            Status = $"已添加：{file.FileName}";
            RefreshInstalled();
        }
        catch (Exception ex) { Status = $"添加失败：{ex.Message}"; }
        finally { Busy = false; }
    }

    public ICommand CheckUpdatesCommand => new AsyncRelayCommand(_ => CheckUpdatesAsync());
    private async Task CheckUpdatesAsync()
    {
        if (ModTab != ModTabKind.Mods) { Status = "仅 Mods 支持更新检查"; return; }
        Busy = true; Status = "正在检查更新…";
        try
        {
            var mgr = new ModManager(EffectiveDir, _http, _downloader);
            var mods = await mgr.CheckForUpdatesAsync(default);
            var updates = mods.Count(m => m.HasUpdate);
            Status = updates > 0 ? $"{updates} 个 Mod 有可用更新" : "所有 Mod 均为最新";
        }
        catch (Exception ex) { Status = $"检查失败：{ex.Message}"; }
        finally { Busy = false; }
    }

    private class VmLogger : ILogger
    {
        private readonly Action<string> _log;
        public VmLogger(Action<string> log) => _log = log;
        public void Log(string message) => _log(message);
    }
}

/// <summary>已安装项（Mod / 资源包 / 光影）的展示模型。</summary>
public class InstalledItemViewModel : ObservableObject
{
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>Modrinth 搜索命中项的展示模型。</summary>
public class ModSearchHit : ObservableObject
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string IconUrl { get; set; } = "";
}
