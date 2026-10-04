using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MCLCS.Core.Music;

/// <summary>
/// NeteaseCloudMusicApi（Node 版）协议的在线音源实现（<see cref="MusicApiKind.NeteaseApi"/>）。
///
/// <para><b>与 Meting 的差异</b>：本源<b>支持二维码登录</b>，因此能拿到「我的歌单」；
/// 代价是几乎没有可用的公共实例，需要用户自建服务（默认地址 <c>http://localhost:3000</c>）。</para>
///
/// <para><b>登录凭证怎么传递</b>：本协议的登录态就是一个 cookie 串，
/// 每个需要鉴权的请求都以 <c>cookie=...</c> query 参数带回。
/// 因此「登录」在本实现里只是「拿到并保存那串 cookie」，不涉及任何 OAuth 流程 ——
/// 这也是它比官方接口简单得多的原因。</para>
///
/// <para><b>失败策略</b>：与 Meting 一致，收敛为 <see cref="MusicApiException"/>；
/// 但「歌词 / 封面 / 账户信息」这类装饰性数据失败时返回 null，不打扰用户。</para>
/// </summary>
public sealed class NeteaseCloudMusicApiProvider : IOnlineMusicLoginProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public NeteaseCloudMusicApiProvider(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
    }

    public string Name => "网易云 API（自建）";

    public bool SupportsLogin => true;

    public bool SupportsPlaylistSearch => true;

    // ---- URL / 请求 ----

    /// <summary>拼完整 URL。path 必须以 '/' 开头，query 里放已转义好的参数。</summary>
    private string BuildUrl(string path, string query = "")
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new MusicApiException("未配置网易云 API 服务地址", isConfigurationError: true);

        var q = string.IsNullOrEmpty(query) ? "" : "?" + query.TrimStart('?', '&');
        return _baseUrl + path + q;
    }

    /// <summary>把 cookie 附加到查询串（未登录时原样返回）。</summary>
    private static string WithCookie(string query, string cookie) =>
        string.IsNullOrWhiteSpace(cookie) ? query : query + "&cookie=" + Uri.EscapeDataString(cookie);

    /// <summary>音质 → 本协议的 level 参数。</summary>
    private static string LevelFor(MusicApiQuality q) => q switch
    {
        MusicApiQuality.Standard => "standard",
        MusicApiQuality.Higher => "higher",
        MusicApiQuality.Lossless => "lossless",
        _ => "exhigh"
    };

    private async Task<JsonDocument> GetJsonAsync(string path, string query, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await _http.GetStringAsync(BuildUrl(path, query), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MusicApiException("网易云 API 服务不可达：" + ex.Message);
        }

        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new MusicApiException("网易云 API 返回了无法解析的内容：" + ex.Message);
        }
    }

    /// <summary>取 code 字段；网易云用 code==200 表示成功。</summary>
    private static bool IsOk(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty("code", out var c)) return true;    // 不返回 code 的端点按成功处理
        return c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n == 200;
    }

    // ---- 歌曲搜索 / 歌单 ----

    public async Task<IReadOnlyList<OnlineTrack>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<OnlineTrack>();
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new MusicApiException("未配置网易云 API 服务地址", isConfigurationError: true);

        // cloudsearch 比 search 覆盖面广（含部分版权曲目）；两个端点返回结构一致
        var query = $"keywords={Uri.EscapeDataString(keyword.Trim())}&limit={Math.Clamp(limit, 1, 100)}";
        using var doc = await GetJsonAsync("/cloudsearch", query, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<OnlineTrack>();
        if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var list = new List<OnlineTrack>();
        foreach (var s in songs.EnumerateArray())
        {
            var t = ParseSong(s);
            if (t is not null) list.Add(t);
        }
        return list;
    }

    public async Task<IReadOnlyList<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<OnlinePlaylist>();

        // type=1000：歌单
        var query = $"keywords={Uri.EscapeDataString(keyword.Trim())}&type=1000&limit={Math.Clamp(limit, 1, 50)}";
        using var doc = await GetJsonAsync("/search", query, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<OnlinePlaylist>();
        if (!result.TryGetProperty("playlists", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlinePlaylist>();

        var list = new List<OnlinePlaylist>();
        foreach (var p in arr.EnumerateArray())
        {
            var pl = ParsePlaylist(p);
            if (pl is not null) list.Add(pl);
        }
        return list;
    }

    public async Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return Array.Empty<OnlineTrack>();

        var query = $"id={Uri.EscapeDataString(playlistId)}";
        using var doc = await GetJsonAsync("/playlist/detail", query, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("playlist", out var playlist)) return Array.Empty<OnlineTrack>();

        // 新版接口在超长歌单里会把 tracks 掏空，只留 trackIds 骨架。
        // 这时必须再走一次 /song/detail 才能拿到歌名 —— 否则用户看到的是「导入成功但列表空白」。
        if (playlist.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array
            && tracks.GetArrayLength() > 0)
        {
            var list = new List<OnlineTrack>();
            foreach (var s in tracks.EnumerateArray())
            {
                var t = ParseSong(s);
                if (t is not null) list.Add(t);
            }
            return list;
        }

        if (!playlist.TryGetProperty("trackIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var idList = new List<string>();
        foreach (var e in ids.EnumerateArray())
        {
            var id = MusicJson.Str(e, "id");
            if (!string.IsNullOrEmpty(id)) idList.Add(id!);
        }
        if (idList.Count == 0) return Array.Empty<OnlineTrack>();

        // /song/detail 一次最多 1000 个 id，普通歌单远不到这个量
        using var detail = await GetJsonAsync("/song/detail", "ids=" + string.Join(",", idList), ct)
            .ConfigureAwait(false);
        if (!detail.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var result = new List<OnlineTrack>();
        foreach (var s in songs.EnumerateArray())
        {
            var t = ParseSong(s);
            if (t is not null) result.Add(t);
        }
        return result;
    }

    public async Task<string?> GetPlayableUrlAsync(OnlineTrack track, MusicApiQuality quality,
        string cookie = "", CancellationToken ct = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Id)) return null;

        var query = WithCookie($"id={Uri.EscapeDataString(track.Id)}&level={LevelFor(quality)}", cookie);
        using var doc = await GetJsonAsync("/song/url/v1", query, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var e in arr.EnumerateArray())
        {
            var url = MusicJson.Str(e, "url");
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        // 没有任何可用 url：绝大多数情况是版权 / 会员限制（试听片段），无法在本端解决
        return null;
    }

    public async Task<string?> GetLyricAsync(OnlineTrack track, CancellationToken ct = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Id)) return null;
        try
        {
            using var doc = await GetJsonAsync("/lyric", "id=" + Uri.EscapeDataString(track.Id), ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("lrc", out var lrc)) return null;
            return MusicJson.Str(lrc, "lyric");
        }
        catch (MusicApiException) { return null; }       // 歌词属装饰性，失败不打扰用户
    }

    // ---- 登录 ----

    public async Task<QrLoginSession> CreateQrLoginAsync(CancellationToken ct = default)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        // 第一步：拿 key（新接口叫 unikey，旧实现叫 code，两个都兼容）
        string? key = null;
        using (var kDoc = await GetJsonAsync("/login/qr/key", "timestamp=" + ts, ct).ConfigureAwait(false))
        {
            if (kDoc.RootElement.TryGetProperty("data", out var data))
            {
                key = MusicJson.Str(data, "unikey") ?? MusicJson.Str(data, "key") ?? MusicJson.Str(data, "code");
            }
            if (string.IsNullOrWhiteSpace(key))
                throw new MusicApiException("未能取得登录二维码标识，请检查服务地址是否正确");
        }

        // 第二步：生成二维码图片（qrimg=true 时服务端直接返回 data URL，省掉本地生成 PNG 的依赖）
        using var qDoc = await GetJsonAsync("/login/qr/create",
            $"key={Uri.EscapeDataString(key!)}&qrimg=true&timestamp={ts}", ct).ConfigureAwait(false);

        string dataUrl = "", fallback = "";
        if (qDoc.RootElement.TryGetProperty("data", out var qData))
        {
            dataUrl = MusicJson.Str(qData, "qrimg") ?? "";
            fallback = MusicJson.Str(qData, "qrurl") ?? "";
        }

        return new QrLoginSession
        {
            Key = key!,
            QrDataUrl = dataUrl,
            // 服务端没给图（老版本）时留空，由界面层直接用 qrurl 自行生成二维码
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
    }

    public async Task<QrLoginPollResult> PollQrLoginAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new QrLoginPollResult { State = QrLoginState.Failed, Message = "缺少登录标识" };

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        using var doc = await GetJsonAsync("/login/qr/check",
            $"key={Uri.EscapeDataString(key)}&timestamp={ts}", ct).ConfigureAwait(false);

        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -1;
        var message = MusicJson.Str(root, "message") ?? "";

        return code switch
        {
            800 => new QrLoginPollResult { State = QrLoginState.Expired, Message = "二维码已过期，请重新获取" },
            801 => new QrLoginPollResult { State = QrLoginState.Pending, Message = "等待扫码" },
            802 => new QrLoginPollResult { State = QrLoginState.Scanned, Message = "已扫描，请在手机上确认" },
            803 => new QrLoginPollResult
            {
                State = QrLoginState.Confirmed,
                Cookie = MusicJson.Str(root, "cookie") ?? "",
                Message = "登录成功"
            },
            _ => new QrLoginPollResult
            {
                State = QrLoginState.Failed,
                Message = string.IsNullOrWhiteSpace(message) ? $"未知状态（code={code}）" : message
            }
        };
    }

    public async Task<OnlineAccount?> GetAccountAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        try
        {
            using var doc = await GetJsonAsync("/user/account", WithCookie("", cookie), ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("profile", out var profile)) return null;

            return new OnlineAccount
            {
                UserId = MusicJson.Str(profile, "userId") ?? "",
                Nickname = MusicJson.Str(profile, "nickname") ?? "",
                AvatarUrl = MusicJson.Str(profile, "avatarUrl") ?? ""
            };
        }
        catch (MusicApiException) { return null; }      // 凭证失效：按未登录处理，不再抛出
    }

    public async Task<IReadOnlyList<OnlinePlaylist>> GetMyPlaylistsAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return Array.Empty<OnlinePlaylist>();

        // userId 是取自己的歌单所必需的前置参数
        var account = await GetAccountAsync(cookie, ct).ConfigureAwait(false);
        var uid = account?.UserId;
        if (string.IsNullOrWhiteSpace(uid)) return Array.Empty<OnlinePlaylist>();

        using var doc = await GetJsonAsync("/user/playlist",
            WithCookie($"uid={Uri.EscapeDataString(uid!)}&limit=200", cookie), ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("playlist", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlinePlaylist>();

        var list = new List<OnlinePlaylist>();
        foreach (var p in arr.EnumerateArray())
        {
            var pl = ParsePlaylist(p);
            if (pl is null) continue;
            pl.IsMine = true;
            list.Add(pl);
        }
        return list;
    }

    // ---- 解析 ----

    /// <summary>
    /// 解析一首歌。网易云的字段名在不同端点间并不统一：
    /// playlist/detail 用 <c>ar</c>/<c>al</c>/<c>dt</c>，song/detail 与搜索用
    /// <c>artists</c>/<c>album</c>/<c>duration</c>，这里两套都读取。
    /// </summary>
    private static OnlineTrack? ParseSong(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;

        var id = MusicJson.Str(s, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var title = MusicJson.Str(s, "name") ?? "";
        var artist = MusicJson.Names(s, "artists") ?? MusicJson.Names(s, "ar");

        var albumEl = s.TryGetProperty("album", out var album) ? album
                    : s.TryGetProperty("al", out var al) ? al
                    : (JsonElement?)null;
        var albumName = albumEl is { } aEl ? (MusicJson.Str(aEl, "name") ?? "") : "";
        var cover = albumEl is { } aEl2 ? (MusicJson.Str(aEl2, "picUrl") ?? "") : "";

        // 时长：dt 是毫秒（playlist/detail），duration 是毫秒（search），两者同源
        var durationMs = 0.0;
        if (s.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number) dt.TryGetDouble(out durationMs);
        else if (s.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number) dur.TryGetDouble(out durationMs);

        return new OnlineTrack
        {
            Id = id!,
            Title = title,
            Artist = artist ?? "",
            Album = albumName,
            DurationSec = durationMs > 0 ? durationMs / 1000.0 : 0,
            CoverUrl = cover
        };
    }

    private static OnlinePlaylist? ParsePlaylist(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;

        var id = MusicJson.Str(p, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var creator = "";
        if (p.TryGetProperty("creator", out var c) && c.ValueKind == JsonValueKind.Object)
            creator = MusicJson.Str(c, "nickname") ?? "";

        var count = 0;
        if (p.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number)
            tc.TryGetInt32(out count);

        return new OnlinePlaylist
        {
            Id = id!,
            Name = MusicJson.Str(p, "name") ?? "",
            Creator = creator,
            TrackCount = count,
            CoverUrl = MusicJson.Str(p, "coverImgUrl") ?? ""
        };
    }
}
