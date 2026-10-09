using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Launcher;
using MCLCS.Core.Models;
using MCLCS.Core.Mods;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Recommend;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 智能推荐结果页 ViewModel（对齐 WPF RecommendationViewModel）：
/// 卡片流展示 RecommendationEngine 产出的推荐（依赖补全 / 热门榜单 / 更新 / 场景），
/// 支持一键安装、不感兴趣、打开项目页。
/// </summary>
public class RecommendationViewModel : ObservableObject
{
    private ObservableCollection<RecommendationItem> _items = new();
    private string _statusMessage = "";
    private bool _isBusy;

    public ObservableCollection<RecommendationItem> Items
    {
        get => _items;
        set => SetField(ref _items, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand NotInterestedCommand { get; }
    public ICommand DetailsCommand { get; }

    public RecommendationViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsBusy);
        InstallCommand = new AsyncRelayCommand(p => InstallAsync(p as RecommendationItem), _ => !IsBusy);
        NotInterestedCommand = new RelayCommand(p => NotInterested(p as RecommendationItem));
        DetailsCommand = new RelayCommand(p => Details(p as RecommendationItem));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
            using var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var list = await RecommendationEngine.BuildAsync(
                LauncherService.Instance.GameRoot, profile, client, ct: default);
            Items = new ObservableCollection<RecommendationItem>(list);
            StatusMessage = Items.Count > 0
                ? $"为你推荐 {Items.Count} 个内容（依赖补全类已用醒目标记）"
                : "暂无推荐，安装更多 Mod 后会有同类推荐";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载推荐失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(RecommendationItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.ProjectId)) return;
        IsBusy = true;
        try
        {
            var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
            var loader = DetectLoader();
            var gameVersion = RuleEngine.ExtractGameVersion(profile.LastVersionId);
            var modsDir = PathEx.ModsDir(LauncherService.Instance.GameRoot);
            StatusMessage = $"正在安装 {item.Title} …";
            var ok = await LauncherService.Instance.DownloadModAsync(item.ProjectId, modsDir, gameVersion, loader);
            StatusMessage = ok ? $"已安装 {item.Title}" : $"安装 {item.Title} 失败";
            if (ok) Items.Remove(item);
        }
        catch (Exception ex)
        {
            StatusMessage = $"安装出错：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotInterested(RecommendationItem? item)
    {
        if (item is null) return;
        Items.Remove(item);
        StatusMessage = $"已对 {item.Title} 标记不感兴趣";
    }

    private void Details(RecommendationItem? item)
    {
        if (item is null) return;
        var slug = string.IsNullOrEmpty(item.Slug) ? item.ProjectId : item.Slug;
        if (string.IsNullOrEmpty(slug)) return;
        var url = $"https://modrinth.com/mod/{slug}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            StatusMessage = $"请手动访问：{url}";
        }
    }

    /// <summary>按已装 Mod 推断加载器，供下载时挑对应版本。</summary>
    private static LoaderType DetectLoader()
    {
        var mods = new ModManager(LauncherService.Instance.GameRoot, new HttpClient(), null!).ListInstalledMods();
        var loaders = new HashSet<string>();
        foreach (var m in mods)
            if (!string.IsNullOrEmpty(m.Loader)) loaders.Add(m.Loader.ToLowerInvariant());

        if (loaders.Contains("fabric")) return LoaderType.Fabric;
        if (loaders.Contains("forge")) return LoaderType.Forge;
        if (loaders.Contains("neoforge")) return LoaderType.NeoForge;
        if (loaders.Contains("quilt")) return LoaderType.Quilt;
        return LoaderType.Any;
    }
}
