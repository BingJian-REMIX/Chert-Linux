using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MCLCS.Linux.App.Themes;

/// <summary>
/// 单个节日档期定义（对应远程 <c>config.json</c> 的 <c>seasons[]</c> 元素）。
/// <para>
/// <c>Start</c> / <c>End</c> 均为 <b>MM-dd</b> 格式（不带年份，逐年复用）。
/// 当 <c>Start</c> 的数值大于 <c>End</c> 时视为跨年区间（如 12-25 → 01-05）。
/// </para>
/// </summary>
public sealed class SeasonEntry
{
    /// <summary>节日标识。约定为小写单词，映射到 <c>Themes/Seasonal/{Key}Overlay.xaml</c>（如 <c>midautumn</c>）。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>生效起始日期（MM-dd）。</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    /// <summary>生效结束日期（MM-dd，含当日）。</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    /// <summary>同一天命中多个节日时的优先级，数值大者优先。</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }
}

/// <summary>
/// 节日特效配置（模型 + 拉取 + 本地缓存 + 兜底）。
/// <para>
/// 来源优先级：<c>active_override</c>（远程紧急开关） &gt; 日期匹配 &gt; 无命中则不显示特效。
/// 网络失败 / JSON 解析失败时依次退化为「本地缓存」→「内置默认」，绝不抛给调用方。
/// </para>
/// </summary>
public sealed class HolidayConfig
{
    /// <summary>远程配置地址（GitHub Pages，与更新源同源）。</summary>
    public const string RemoteUrl = "https://remix-laser-raising-studio.github.io/launcher/config.json";

    /// <summary>远程拉取超时，避免网络不佳时拖慢启动观感。</summary>
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(6);

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("seasons")]
    public List<SeasonEntry> Seasons { get; set; } = new();

    /// <summary>远程紧急开关：非空时强制启用该节日（忽略日期），用于临时纠正或紧急上线。</summary>
    [JsonPropertyName("active_override")]
    public string? ActiveOverride { get; set; }

    /// <summary>清单 #42：限时活动（倒计时卡片数据源）。</summary>
    [JsonPropertyName("events")]
    public List<SeasonalEventEntry> Events { get; set; } = new();

    /// <summary>清单 #43：节日服务器推荐。</summary>
    [JsonPropertyName("servers")]
    public List<SeasonalServerEntry> Servers { get; set; } = new();

    /// <summary>清单 #44：按节日主题整理的版本 / Mod / 材质包推荐。</summary>
    [JsonPropertyName("recommended")]
    public List<SeasonalPickEntry> Recommended { get; set; } = new();

    /// <summary>清单 #31：节日公告（纯静态 JSON）。</summary>
    [JsonPropertyName("announcement")]
    public SeasonalAnnouncement? Announcement { get; set; }

    /// <summary>清单 #28：节日音频（BGM / 点击音效），均为可选。</summary>
    [JsonPropertyName("audio")]
    public SeasonalAudioConfig? Audio { get; set; }

    /// <summary>本地缓存文件路径（游戏目录下）。</summary>
    public static string CachePath(string gameRoot) => Path.Combine(gameRoot, "seasonal_config_cache.json");

    /// <summary>
    /// 内置默认配置：远程与缓存都不可用时兜底。
    /// 中秋取 09-08 ~ 10-07，覆盖农历八月十五在公历上可能的浮动区间。
    /// </summary>
    public static HolidayConfig BuiltInDefault() => new()
    {
        Version = 1,
        ActiveOverride = null,
        Seasons = new List<SeasonEntry>
        {
            new() { Key = "midautumn", Start = "09-08", End = "10-07", Priority = 10 }
        }
    };

    /// <summary>
    /// 选出当前应生效的节日（纯函数，便于单测）。未命中返回 <c>null</c>。
    /// </summary>
    public SeasonEntry? PickActive(DateTimeOffset now)
    {
        // 1) 紧急开关最高优先：命中任一已知档期即生效
        if (!string.IsNullOrWhiteSpace(ActiveOverride))
        {
            var forced = Seasons.FirstOrDefault(s =>
                string.Equals(s.Key, ActiveOverride, StringComparison.OrdinalIgnoreCase));
            if (forced is not null) return forced;
        }

        // 2) 日期匹配：命中多个时取 priority 最大者
        SeasonEntry? best = null;
        foreach (var s in Seasons)
        {
            if (!IsActive(s, now)) continue;
            if (best is null || s.Priority > best.Priority) best = s;
        }
        return best;
    }

    /// <summary>判断某档期是否覆盖给定时刻（支持跨年区间）。</summary>
    private static bool IsActive(SeasonEntry season, DateTimeOffset now)
    {
        if (!TryParseMd(season.Start, out var startMd) || !TryParseMd(season.End, out var endMd))
            return false;

        var nowMd = now.Month * 100 + now.Day;

        if (startMd <= endMd)
            return nowMd >= startMd && nowMd <= endMd;

        // 跨年区间（如 12-25 → 01-05）：命中 start 之后或 end 之前
        return nowMd >= startMd || nowMd <= endMd;
    }

    /// <summary>解析 MM-dd 为可比较数值（month*100+day）。</summary>
    private static bool TryParseMd(string value, out int md)
    {
        md = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Trim().Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return false;
        if (month is < 1 or > 12 || day is < 1 or > 31) return false;

        md = month * 100 + day;
        return true;
    }

    /// <summary>
    /// 载入配置：远程优先，失败退到本地缓存，再退到内置默认。本方法不抛异常。
    /// </summary>
    public static async Task<HolidayConfig> LoadAsync(string gameRoot, HttpClient? http = null)
    {
        var cachePath = CachePath(gameRoot);

        var remote = await TryFetchAsync(http).ConfigureAwait(false);
        if (remote is not null)
        {
            TryWriteCache(gameRoot, cachePath, remote);
            return remote;
        }

        var cached = TryReadCache(cachePath);
        return cached ?? BuiltInDefault();
    }

    private static async Task<HolidayConfig?> TryFetchAsync(HttpClient? http)
    {
        var ownsClient = http is null;
        var client = http ?? new HttpClient();
        try
        {
            using var cts = new CancellationTokenSource(RemoteTimeout);
            using var resp = await client.GetAsync(RemoteUrl, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<HolidayConfig>(json);
        }
        catch
        {
            // 网络 / 解析失败 → 交由上层退化为缓存或默认
            return null;
        }
        finally
        {
            if (ownsClient) client.Dispose();
        }
    }

    private static void TryWriteCache(string gameRoot, string cachePath, HolidayConfig config)
    {
        try
        {
            Directory.CreateDirectory(gameRoot);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(config));
        }
        catch
        {
            // 缓存写入失败不影响本次运行
        }
    }

    private static HolidayConfig? TryReadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            var json = File.ReadAllText(cachePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<HolidayConfig>(json);
        }
        catch
        {
            return null;
        }
    }
}
