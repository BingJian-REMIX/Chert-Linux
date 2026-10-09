using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using MCLCS.Core.Mvvm;
using MCLCS.Linux.App.Themes;
using MCLCS.Linux.App.Views;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>节日中心某一项内容推荐（把类型枚举换成可显示文案）。</summary>
public sealed class SeasonalPickCard
{
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public string KindText { get; init; } = "";
    public string? Url { get; init; }
}

/// <summary>
/// 节日中心（对齐 WPF SeasonalHubViewModel，清单 #31 / #42 / #43 / #44）：
/// 汇总当前节日的限时活动倒计时、服务器推荐、内容推荐，
/// 全部来自远程 config.json，无需发新版即可更新。
/// Linux 端不提供叠加层特效与节日音频（分别依赖 DWM 与 WPF MediaPlayer）。
/// </summary>
public class SeasonalHubViewModel : ObservableObject
{
    private string _seasonText = "";
    private bool _hasSeason;
    private string _statusMessage = "";
    private ObservableCollection<SeasonalEventCard> _events = new();
    private ObservableCollection<SeasonalServerEntry> _servers = new();
    private ObservableCollection<SeasonalPickCard> _picks = new();
    private bool _hasEvents;
    private bool _hasServers;
    private bool _hasPicks;
    private bool _isBusy;

    public string SeasonText
    {
        get => _seasonText;
        set => SetField(ref _seasonText, value);
    }

    public bool HasSeason
    {
        get => _hasSeason;
        set => SetField(ref _hasSeason, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool HasEvents
    {
        get => _hasEvents;
        private set => SetField(ref _hasEvents, value);
    }

    public bool HasServers
    {
        get => _hasServers;
        private set => SetField(ref _hasServers, value);
    }

    public bool HasPicks
    {
        get => _hasPicks;
        private set => SetField(ref _hasPicks, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public ObservableCollection<SeasonalEventCard> Events
    {
        get => _events;
        set => SetField(ref _events, value);
    }

    public ObservableCollection<SeasonalServerEntry> Servers
    {
        get => _servers;
        set => SetField(ref _servers, value);
    }

    public ObservableCollection<SeasonalPickCard> Picks
    {
        get => _picks;
        set => SetField(ref _picks, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenUrlCommand { get; }

    /// <summary>清单 #34：彩蛋小游戏入口（愚人节期间在节日中心露出）。</summary>
    public ICommand PlayEasterEggCommand { get; }

    private bool _easterEggVisible;
    /// <summary>当前节日为愚人节时显示彩蛋入口。</summary>
    public bool EasterEggVisible
    {
        get => _easterEggVisible;
        set => SetField(ref _easterEggVisible, value);
    }

    public SeasonalHubViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        PlayEasterEggCommand = new RelayCommand(_ => PlayEasterEgg());
        _ = RefreshAsync();
    }

    /// <summary>重新拉取远程配置并重建三栏内容。</summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await SeasonalService.RefreshAsync();
        }
        catch
        {
            // 刷新失败不阻断：下面按「无配置」渲染空态
        }

        var config = SeasonalService.CurrentConfig;
        var key = SeasonalService.CurrentSeasonKey;

        HasSeason = !string.IsNullOrWhiteSpace(key);
        SeasonText = HasSeason ? $"当前节日：{Prettify(key!)}" : "当前没有生效的节日";

        // 清单 #34：愚人节期间露出彩蛋入口
        EasterEggVisible = !string.IsNullOrWhiteSpace(key) &&
                           (key!.Contains("april", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("fool", StringComparison.OrdinalIgnoreCase));

        if (config is null)
        {
            Events = new ObservableCollection<SeasonalEventCard>();
            Servers = new ObservableCollection<SeasonalServerEntry>();
            Picks = new ObservableCollection<SeasonalPickCard>();
            HasEvents = HasServers = HasPicks = false;
            StatusMessage = "节日配置尚未载入，可点「刷新」重试";
            return;
        }

        var now = DateTimeOffset.Now;
        Events = new ObservableCollection<SeasonalEventCard>(
            SeasonalContentService.BuildEventCards(config, key ?? "", now));
        Servers = new ObservableCollection<SeasonalServerEntry>(
            SeasonalContentService.BuildServers(config, key ?? ""));
        Picks = new ObservableCollection<SeasonalPickCard>(
            SeasonalContentService.BuildPicks(config, key ?? "")
                .Select(p => new SeasonalPickCard
                {
                    Title = p.Title,
                    Note = p.Note ?? "",
                    KindText = KindText(p.KindEnum),
                    Url = p.ResolvedUrl
                }));

        HasEvents = Events.Count > 0;
        HasServers = Servers.Count > 0;
        HasPicks = Picks.Count > 0;

        var total = Events.Count + Servers.Count + Picks.Count;
        StatusMessage = total == 0
            ? "当前节日暂无活动 / 服务器 / 内容推荐"
            : $"共 {Events.Count} 个活动、{Servers.Count} 个服务器、{Picks.Count} 项推荐";
        IsBusy = false;
    }

    private static void PlayEasterEgg()
    {
        try
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var win = new EasterEggGameWindow();
            if (owner is not null) win.Show(owner); else win.Show();
        }
        catch { /* 彩蛋非关键 */ }
    }

    /// <summary>把 mid_autumn 这类 key 收拾成人能读的样子（而非原样露出下划线）。</summary>
    private static string Prettify(string key)
    {
        var parts = key.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? key
            : string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string KindText(SeasonalPickKind kind) => kind switch
    {
        SeasonalPickKind.Mod => "Mod",
        SeasonalPickKind.Modpack => "整合包",
        SeasonalPickKind.ResourcePack => "材质包",
        SeasonalPickKind.Shader => "光影",
        SeasonalPickKind.Version => "游戏版本",
        _ => "其它"
    };

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }
}
