using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Resources;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 服务器资源包缓存可视化（规格 3.13，移植自 WPF ServerPackViewModel）：列出进服时缓存的资源包，
/// 支持清理、导出到本地 / 资源包目录、查看占用。依赖 Core.ServerResourcePackCache 与 Linux UIService（异步）。
/// </summary>
public class ServerPackViewModel : ObservableObject
{
    private ObservableCollection<ServerPackItem> _packs = new();
    private ServerPackItem? _selected;
    private PackCacheStats _stats = new();
    private int _capacityMb;
    private string _statusMessage = "";
    private bool _isBusy;

    public ObservableCollection<ServerPackItem> Packs
    {
        get => _packs;
        set => SetField(ref _packs, value);
    }

    public ServerPackItem? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public PackCacheStats Stats
    {
        get => _stats;
        set => SetField(ref _stats, value);
    }

    /// <summary>容量上限（MB），同步到 profile，影响 LRU 淘汰阈值。</summary>
    public int CapacityMb
    {
        get => _capacityMb;
        set
        {
            if (!SetField(ref _capacityMb, value)) return;
            SaveCapacity();
        }
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

    /// <summary>缓存是否为空（驱动空状态提示的可见性，替代 WPF 的 DataTrigger）。</summary>
    public bool IsEmpty => Packs.Count == 0;

    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportToResourcePacksCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public ServerPackViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => Selected is not null);
        ExportToResourcePacksCommand = new RelayCommand(_ => ExportToResourcePacks(), _ => Selected is not null);
        RemoveCommand = new AsyncRelayCommand(_ => RemoveAsync(), _ => !IsBusy);
        ClearCommand = new AsyncRelayCommand(_ => ClearAsync(), _ => !IsBusy);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());

        _capacityMb = ProfileStore.Load(GameRoot).ServerPackCacheMb;
        _ = RefreshAsync();
    }

    private static string GameRoot => LauncherService.Instance.GameRoot;

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var (index, stats) = await Task.Run(() =>
            {
                var i = ServerResourcePackCache.LoadIndex(GameRoot);
                var s = ServerResourcePackCache.Stats(GameRoot);
                return (i, s);
            });

            Stats = stats;
            Packs = new ObservableCollection<ServerPackItem>(
                index.OrderByDescending(e => e.LastUsed)
                     .Select(e => new ServerPackItem(e)));
            StatusMessage = index.Count == 0
                ? "还没有缓存任何服务器资源包，进服时会自动缓存"
                : $"共 {index.Count} 份，占用 {stats.TotalMb} MB";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取缓存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (Selected is null) { StatusMessage = "请先选择要导出的资源包"; return; }

        var dir = await UIService.PickFolderAsync("选择导出目录");
        if (string.IsNullOrWhiteSpace(dir)) return;

        var dest = ServerResourcePackCache.Export(GameRoot, Selected.Entry, dir);
        if (dest is null)
        {
            StatusMessage = "导出失败（文件可能已被占用或删除）";
            ToastService.Instance.Show(new ToastOptions { Title = "导出失败", Message = Selected.DisplayName });
            return;
        }

        StatusMessage = $"已导出：{Path.GetFileName(dest)}";
        ToastService.Instance.Show(new ToastOptions { Title = "已导出", Message = Path.GetFileName(dest) });
    }

    private void ExportToResourcePacks()
    {
        if (Selected is null) { StatusMessage = "请先选择要导出的资源包"; return; }

        var dest = ServerResourcePackCache.ExportToResourcePacks(GameRoot, Selected.Entry);
        if (dest is null)
        {
            StatusMessage = "导出到资源包目录失败";
            ToastService.Instance.Show(new ToastOptions { Title = "导出失败", Message = Selected.DisplayName });
            return;
        }

        StatusMessage = "已导出到 resourcepacks，可在游戏内直接启用";
        ToastService.Instance.Show(new ToastOptions { Title = "已导出到资源包", Message = Path.GetFileName(dest) });
    }

    private async Task RemoveAsync()
    {
        var sel = Selected;
        if (sel is null) { StatusMessage = "请先选择要删除的缓存"; return; }

        if (!await UIService.ConfirmAsync($"删除缓存「{sel.DisplayName}」？\n文件与索引会一并移除。", "确认删除"))
            return;

        var ok = await Task.Run(() => ServerResourcePackCache.Remove(GameRoot, sel.Entry.Key));
        StatusMessage = ok ? "已删除" : "删除失败（索引中找不到）";
        await RefreshAsync();
    }

    private async Task ClearAsync()
    {
        if (Packs.Count == 0) { StatusMessage = "没有可清理的缓存"; return; }

        if (!await UIService.ConfirmAsync(
                $"清空全部服务器资源包缓存（共 {Packs.Count} 份，{Stats.TotalMb} MB）？\n下次进服会重新下载。",
                "确认清空"))
            return;

        var n = await Task.Run(() => ServerResourcePackCache.Clear(GameRoot));
        StatusMessage = $"已清空 {n} 份缓存";
        ToastService.Instance.Show(new ToastOptions { Title = "已清空", Message = $"{n} 份服务器资源包缓存" });
        await RefreshAsync();
    }

    private void OpenFolder()
    {
        try
        {
            var dir = ServerResourcePackCache.CacheDir(GameRoot);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    private void SaveCapacity()
    {
        try
        {
            var p = ProfileStore.Load(GameRoot);
            p.ServerPackCacheMb = _capacityMb;
            ProfileStore.Save(p);
            StatusMessage = "容量上限已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }
}

/// <summary>服务器资源包缓存的一行（视图友好）。</summary>
public class ServerPackItem : ObservableObject
{
    public CachedServerPack Entry { get; }

    public ServerPackItem(CachedServerPack entry) => Entry = entry;

    /// <summary>人类可读文件名（来自 SuggestExportName）。</summary>
    public string DisplayName => ServerResourcePackCache.SuggestExportName(Entry);

    public string ServerAddress => string.IsNullOrWhiteSpace(Entry.ServerAddress)
        ? "（未知来源）" : Entry.ServerAddress!;

    public string SizeText => Entry.SizeMb >= 1 ? $"{Entry.SizeMb} MB" : $"{Entry.SizeBytes / 1024} KB";

    public int Hits => Entry.Hits;

    public DateTime LastUsed => Entry.LastUsed;

    public string LastUsedText => LastUsed.ToString("yyyy-MM-dd HH:mm");

    public DateTime CachedAt => Entry.CachedAt;

    public string Url => Entry.Url;
}
