using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MCLCS.Core.Music;

/// <summary>
/// Meting 聚合协议的在线音源实现（<see cref="MusicApiKind.Meting"/>）。
///
/// <para><b>为什么它排在首位</b>：三个核心能力（搜索、歌单曲目、取直链）全部<b>无需登录无需签名</b>，
/// 且有多个长期存活的公共实例，用户不必自建服务就能用。代价是<b>没有「我的歌单」</b>——
/// 聚合协议不暴露登录态，因此 <see cref="SupportsLogin"/> 恒为 false。</para>
///
/// <para><b>解析为什么写得这么脏</b>：同一个协议在各实例（PHP 版 / Node 版 / 二次封装）之间
/// 字段名并不统一 —— 搜索结果用 <c>name</c>+<c>artist</c>，单曲详情却用 <c>title</c>+<c>author</c>；
/// <c>artist</c> 有时是数组、有时是字符串。这里全部做兼容读取，而不是假设某一种实现。
/// 一旦某个字段缺失就退化为空串/0，绝不因此丢掉整条结果。</para>
///
/// <para><b>失败策略</b>：网络与解析异常统一收敛成 <see cref="MusicApiException"/>，
/// 由上层决定是「提示换地址」还是「静默降级」。</para>
/// </summary>
public sealed class MetingMusicProvider : IOnlineMusicProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly MusicApiPlatform _platform;

    public MetingMusicProvider(HttpClient http, string baseUrl, MusicApiPlatform platform = MusicApiPlatform.Netease)
    {
        _http = http;
        // ★ 不要在这里 TrimEnd('/') 之外做 URL 校验：有的实例路径里就带 query，
        //   统一在 BuildUrl 里判断用 '?' 还是 '&' 连接。
        _baseUrl = (baseUrl ?? "").Trim();
        _platform = platform;
    }

    public string Name => $"Meting · {MusicApiPlatformInfo.DisplayName(_platform)}";

    public bool SupportsLogin => false;

    public bool SupportsPlaylistSearch => false;

    private string Server => ServerFor(_platform);

    /// <summary>厂家 → Meting 的 server 参数。未知厂家一律退回 netease（比发出一个非法请求好）。</summary>
    private static string ServerFor(MusicApiPlatform p) => p switch
    {
        MusicApiPlatform.Tencent => "tencent",
        MusicApiPlatform.Kugou => "kugou",
        MusicApiPlatform.Kuwo => "kuwo",
        MusicApiPlatform.Baidu => "baidu",
        MusicApiPlatform.Xiami => "xiami",
        _ => "netease"
    };

    // ---- URL 构造 ----

    /// <summary>
    /// 拼请求 URL。<paramref name="extra"/> 里已经是 <c>k=v</c> 形式。
    /// <para>基础地址自身可能已含 query（如 <c>/meting/api?a=1</c>），
    /// 所以分隔符要看有没有 '?' 来决定。</para>
    /// </summary>
    private string BuildUrl(string type, string id, params string[] extra)
        => BuildUrlFor(_platform, type, id, extra);

    private string BuildUrlFor(MusicApiPlatform platform, string type, string id, params string[] extra)
    {
        var sb = new System.Text.StringBuilder(_baseUrl);
        sb.Append(_baseUrl.Contains('?') ? '&' : '?');
        sb.Append("server=").Append(ServerFor(platform));
        sb.Append("&type=").Append(type);

        if (!string.IsNullOrEmpty(id))
            sb.Append("&id=").Append(Uri.EscapeDataString(id));

        foreach (var kv in extra)
        {
            if (string.IsNullOrEmpty(kv)) continue;
            sb.Append('&').Append(kv);
        }
        return sb.ToString();
    }

    /// <summary>音质 → Meting 的 br 参数（单位：bps）。</summary>
    private static int BrFor(MusicApiQuality q) => q switch
    {
        MusicApiQuality.Standard => 128000,
        MusicApiQuality.Higher => 192000,
        MusicApiQuality.Lossless => 999000,
        _ => 320000
    };

    private async Task<string> GetRawAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new MusicApiException("未配置在线音源服务地址", isConfigurationError: true);

        try
        {
            return await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MusicApiException("在线音源服务不可达：" + ex.Message);
        }
    }

    /// <summary>取 JSON 字符串。响应不是 JSON（比如实例返回 404 HTML）时抛 MusicApiException。</summary>
    private static JsonDocument Parse(string raw)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new MusicApiException("在线音源返回了无法解析的内容：" + ex.Message);
        }
    }

    // ---- 接口实现 ----

    public Task<IReadOnlyList<OnlineTrack>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default)
    {
        if (_platform != MusicApiPlatform.All)
            return SearchOnAsync(_platform, keyword, limit, ct);

        return SearchAllPlatformsAsync(keyword, limit, ct);
    }

    /// <summary>单个厂家的搜索（也是聚合搜索的基本单元）。</summary>
    private async Task<IReadOnlyList<OnlineTrack>> SearchOnAsync(
        MusicApiPlatform platform, string keyword, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<OnlineTrack>();

        // ★ 兼容：老实例认 `id=关键词`，新实例认 `name=关键词`。
        //   两个都带上不影响任一侧取值，比「猜某一个」稳。
        //   id 由 BuildUrl 内部负责转义，这里必须传原文 —— 传已转义串会导致二次转义
        //   （%，会被再编成 %25），服务端收到的就是一串百分号而不是实际关键词。
        var url = BuildUrlFor(platform, "search",
            keyword.Trim(),
            "name=" + Uri.EscapeDataString(keyword.Trim()));

        var raw = await GetRawAsync(url, ct).ConfigureAwait(false);
        using var doc = Parse(raw);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var list = new List<OnlineTrack>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var track = ParseTrack(e, isDetail: false, platform: platform);
            if (track is not null) list.Add(track);
            if (list.Count >= Math.Clamp(limit, 1, 100)) break;
        }
        return list;
    }

    /// <summary>
    /// 并发搜所有厂家再合并。
    /// <para><b>为什么并发而不是串行</b>：串行 N 家意味着 N 倍等待，聚合搜索会慢到不可用。
    /// 各家之间彼此独立，并发是唯一合理选择。</para>
    /// <para><b>为什么必须容错单个厂家</b>：公共实例常年处于「某几家挂了」的状态；
    /// 一家 502 就让整个搜索空手而归，等于把可用性交给了最弱的那一环。
    /// 这里只要有一家成功就返回结果，全部失败才把最后一家的异常抛出去。</para>
    /// <para><b>为什么交错合并</b>：直接按厂家顺序拼接会把后面的厂家全挤到列表末尾，
    /// 用户翻两屏也看不到。轮转取一条能让各家结果都出现在首屏。</para>
    /// </summary>
    private async Task<IReadOnlyList<OnlineTrack>> SearchAllPlatformsAsync(
        string keyword, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<OnlineTrack>();

        var platforms = MusicApiPlatformInfo.Searchable;
        var want = Math.Clamp(limit, 1, 100);

        // 每家先各取一部分（向上取整，保证凑满 want），单家至少 3 条避免「只有个位数」
        var per = Math.Clamp((int)Math.Ceiling(want / (double)platforms.Count), 3, 30);

        MusicApiException? lastError = null;
        var buckets = new List<List<OnlineTrack>>();

        async Task<IReadOnlyList<OnlineTrack>> Safe(MusicApiPlatform p)
        {
            try
            {
                return await SearchOnAsync(p, keyword, per, ct).ConfigureAwait(false);
            }
            catch (MusicApiException ex)
            {
                lastError = ex;
                return Array.Empty<OnlineTrack>();
            }
        }

        var results = await Task.WhenAll(platforms.Select(Safe)).ConfigureAwait(false);

        foreach (var r in results)
            buckets.Add(r is null ? new List<OnlineTrack>() : r.ToList());

        var merged = Interleave(buckets, want);
        if (merged.Count == 0 && lastError is not null)
            throw lastError;      // 全灭：把真实原因抛出去，别让 UI 显示「没有结果」

        return merged;
    }

    /// <summary>轮转交错合并各厂家的结果桶，最多取 <paramref name="max"/> 条。</summary>
    private static List<OnlineTrack> Interleave(List<List<OnlineTrack>> buckets, int max)
    {
        var merged = new List<OnlineTrack>();
        var index = new int[buckets.Count];

        while (merged.Count < max)
        {
            var addedThisRound = false;
            for (var i = 0; i < buckets.Count && merged.Count < max; i++)
            {
                if (index[i] >= buckets[i].Count) continue;
                merged.Add(buckets[i][index[i]++]);
                addedThisRound = true;
            }
            if (!addedThisRound) break;      // 所有桶都取完了
        }
        return merged;
    }

    public Task<IReadOnlyList<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20, CancellationToken ct = default)
    {
        // Meting 协议没有「搜索歌单」这个 type；只能靠用户直接填歌单 ID。
        // 这里返回空而不是抛异常 —— 上层据此隐藏歌单搜索区，UI 不会出现「总是失败」的按钮。
        return Task.FromResult<IReadOnlyList<OnlinePlaylist>>(Array.Empty<OnlinePlaylist>());
    }

    public async Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return Array.Empty<OnlineTrack>();

        var raw = await GetRawAsync(BuildUrl("playlist", playlistId), ct).ConfigureAwait(false);
        using var doc = Parse(raw);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var list = new List<OnlineTrack>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            // 歌单曲目：平台就是当前 provider 的平台（聚合只作用于搜索）
            var track = ParseTrack(e, isDetail: true, platform: _platform);
            if (track is not null) list.Add(track);
        }
        return list;
    }

    public async Task<string?> GetPlayableUrlAsync(OnlineTrack track, MusicApiQuality quality,
        string cookie = "", CancellationToken ct = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Id)) return null;

        var raw = await GetRawAsync(BuildUrl("url", track.Id, "br=" + BrFor(quality)), ct)
            .ConfigureAwait(false);

        // 有些实例直接返回一个裸 URL 字符串，连 JSON 都不是 —— 先判断再解析
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('[') && !trimmed.StartsWith('{'))
            return trimmed.Length > 4 && trimmed.StartsWith("http") ? trimmed : null;

        using var doc = Parse(trimmed);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var url = GetString(e, "url");
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
            return null;
        }
        return GetString(doc.RootElement, "url");
    }

    public async Task<string?> GetLyricAsync(OnlineTrack track, CancellationToken ct = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Id)) return null;

        string raw;
        try
        {
            raw = await GetRawAsync(BuildUrl("lrc", track.Id), ct).ConfigureAwait(false);
        }
        catch (MusicApiException) { return null; }   // 歌词属装饰性，失败不打扰用户

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('[') && !trimmed.StartsWith('{'))
            // 部分实例直接返回 LRC 原文
            return trimmed.Contains("[") ? trimmed : null;

        try
        {
            using var doc = Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var lrc = GetString(e, "lyric") ?? GetString(e, "lrc");
                    if (!string.IsNullOrWhiteSpace(lrc)) return lrc;
                }
                return null;
            }
            return GetString(doc.RootElement, "lyric") ?? GetString(doc.RootElement, "lrc");
        }
        catch (MusicApiException) { return null; }
    }

    // ---- 解析辅助 ----

    /// <summary>
    /// 从一条记录解析曲目。
    /// <paramref name="isDetail"/> 为 true 时走「详情字段」优先（title/author），
    /// 为 false 时走「搜索字段」优先（name/artist）—— 两类接口的实际返回确实不一样。
    /// </summary>
    private OnlineTrack? ParseTrack(JsonElement e, bool isDetail, MusicApiPlatform platform)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var id = GetString(e, "id") ?? GetString(e, "url_id") ?? GetString(e, "song_id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        string title, artist;
        if (isDetail)
        {
            title = GetString(e, "title") ?? GetString(e, "name") ?? "";
            artist = GetArtists(e, "author") ?? GetArtists(e, "artist") ?? "";
        }
        else
        {
            title = GetString(e, "name") ?? GetString(e, "title") ?? "";
            artist = GetArtists(e, "artist") ?? GetArtists(e, "author") ?? "";
        }

        // 详情接口常常直接给播放地址（有效期短），先留着作为 PreviewUrl 兜底
        var url = GetString(e, "url");
        var cover = GetString(e, "pic") ?? GetString(e, "picUrl");
        if (string.IsNullOrWhiteSpace(cover))
        {
            var picId = GetString(e, "pic_id");
            if (!string.IsNullOrWhiteSpace(picId))
                cover = BuildUrlFor(platform, "pic", picId);   // 图片本身就是本实例的一个 type
        }

        return new OnlineTrack
        {
            Id = id!,
            Platform = platform,
            Title = title,
            Artist = artist,
            Album = GetString(e, "album") ?? "",
            DurationSec = ParseDurationSeconds(e),
            CoverUrl = cover ?? "",
            PreviewUrl = string.IsNullOrWhiteSpace(url) ? null : url
        };
    }

    /// <summary>
    /// 读时长。Meting 基本不给 duration，但部分封装会塞 <c>duration</c>（毫秒）或 <c>dt</c>。
    /// 取不到就 0 —— 列表显示 <c>--:--</c>，比显示错误的时长体面。
    /// </summary>
    private static double ParseDurationSeconds(JsonElement e)
    {
        foreach (var name in new[] { "duration", "dt", "time" })
        {
            if (!e.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var num) && num > 0)
                return num > 6000 ? num / 1000.0 : num;      // >100 分钟几乎肯定是毫秒
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0)
                return parsed > 6000 ? parsed / 1000.0 : parsed;
        }
        return 0;
    }

    private static string? GetString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        return null;
    }

    /// <summary>歌手字段：字符串或数组都要吃下。多个用「 / 」连起来。</summary>
    private static string? GetArtists(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        if (v.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                // 数组元素可能是字符串，也可能是 {name:"..."} 对象
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var n = GetString(item, "name");
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
                }
            }
            return names.Count == 0 ? null : string.Join(" / ", names);
        }

        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }
}
