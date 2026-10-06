using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Auth;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.Models;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>已安装版本条目（版本列表页专用，避免与 GameHomeViewModel.VersionEntryVm 重名）。</summary>
public class VersionListEntry
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Display => string.IsNullOrEmpty(Type) ? Id : $"{Id} ({Type})";
}

/// <summary>
/// 已安装版本管理（对齐 WPF VersionListViewModel）：枚举 <c>versions/</c> 下含
/// <c>&lt;id&gt;/&lt;id&gt;.json</c> 的目录，支持刷新与一键启动（复用 Core.GameLauncher）。
/// </summary>
public class VersionListViewModel : ObservableObject
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
        set => SetField(ref _selectedVersion, value);
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

    public bool CanLaunch => !Busy && SelectedVersion is not null;

    public ICommand RefreshCommand => new RelayCommand(_ => Refresh());
    public ICommand LaunchCommand => new AsyncRelayCommand(o => LaunchEntryAsync(o as VersionListEntry), _ => CanLaunch);
    public ICommand OpenSettingsCommand => new AsyncRelayCommand(o => OpenSettingsAsync(o as VersionListEntry));

    private async Task LaunchEntryAsync(VersionListEntry? entry)
    {
        if (entry is not null) SelectedVersion = entry;
        await LaunchAsync();
    }

    private async Task OpenSettingsAsync(VersionListEntry? entry)
    {
        if (entry is null) return;
        await VersionSettingsDialog.OpenAsync(_gameRoot, entry.Id, entry.Type, Refresh);
    }

    public VersionListViewModel()
    {
        Refresh();
    }

    /// <summary>枚举 versions/ 下含 &lt;id&gt;/&lt;id&gt;.json 的目录。</summary>
    public void Refresh()
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

            var account = AccountStore.GetForVersion(_gameRoot, vp.BoundAccountId);
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
