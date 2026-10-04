using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MCLCS.Core.Music;

/// <summary>
/// 在线歌词来源。实现类只需给出「用歌名 + 歌手能定位到一首歌」的能力。
/// <para>抽象出来是为了将来加 QQ 音乐 / 酷狗 / 自建源时不必改编排与调用方。</para>
/// </summary>
public interface ILyricProvider
{
    /// <summary>展示用名称（设置页里显示「歌词来源：网易云」）。</summary>
    string Name { get; }

    /// <summary>按歌名 / 歌手搜索并返回原始 LRC 文本；查不到返回 null。</summary>
    Task<string?> FetchLrcAsync(string title, string? artist, CancellationToken ct = default);
}

/// <summary>
/// 网易云音乐歌词来源。
/// <para>
/// 选它作默认源的原因：两个老公开接口（/api/search/get 与 /api/song/lyric）无需鉴权、
/// 无需签名，返回体就是标准 LRC，可直接喂 <see cref="LyricEngine.ParseLrc"/>。
/// </para>
/// <para>
/// 已知限制：接口非官方，网易云改版时可能失效。所有失败路径都返回 null（调用方静默隐藏歌词），
/// 绝不抛给用户看 —— 按规格「无歌词时静默隐藏，不弹错误提示」。
/// </para>
/// </summary>
public sealed class NeteaseLyricProvider : ILyricProvider, IDisposable
{
    private const string SearchApi = "https://music.163.com/api/search/get";
    private const string LyricApi = "https://music.163.com/api/song/lyric";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public NeteaseLyricProvider(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient();
        // 移动 UA：网易云对桌面 UA 的接口常返回空 body
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        if (_ownsClient) _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public string Name => "网易云音乐";

    public async Task<string?> FetchLrcAsync(string title, string? artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var songId = await SearchSongIdAsync(title, artist, ct).ConfigureAwait(false);
        if (songId is null or <= 0) return null;

        var url = $"{LyricApi}?id={songId}&lv=1&kv=1&tv=-1";
        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lrc", out var lrc)) return null;
            var text = lrc.GetProperty("lyric").GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<long?> SearchSongIdAsync(string title, string? artist, CancellationToken ct)
    {
        // 网易云的 s 参数支持「歌名 歌手」联合搜索，比只搜歌名精确得多
        var keyword = Uri.EscapeDataString(
            string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}");
        var url = $"{SearchApi}?s={keyword}&type=1&offset=0&total=true&limit=10";

        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;

            long? loose = null;
            var wantArtist = Normalize(artist);

            foreach (var s in songs.EnumerateArray())
            {
                if (!s.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetInt64();
                if (id <= 0) continue;

                var gotArtist = Normalize(JoinArtists(s));
                if (wantArtist.Length == 0) return id;                 // 没填歌手：首个即可
                if (gotArtist.Contains(wantArtist, StringComparison.Ordinal)
                    || wantArtist.Contains(gotArtist, StringComparison.Ordinal))
                    return id;                                        // 精确命中歌手

                loose ??= id;                                           // 退而求其次
            }
            // ★ 不能无条件返回首个结果：实测「晴天 周杰伦」会命中「晴天(深情版)」翻唱，
            //   歌手完全不符 —— 那份歌词与正在播的歌毫无关系，比没有歌词更糟。
            return loose;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string JoinArtists(JsonElement song)
    {
        if (!song.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var names = new List<string>();
        foreach (var a in arr.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n))
            {
                var v = n.GetString();
                if (!string.IsNullOrWhiteSpace(v)) names.Add(v);
            }
        }
        return string.Join(" / ", names);
    }

    /// <summary>归一化用于比较：去空白、转小写。「周杰伦」与「周杰倫」仍会不同（可接受）。</summary>
    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().Replace(" ", "").ToLowerInvariant();

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

/// <summary>
/// 歌词获取入口：按顺序尝试各来源，第一个拿到非空 LRC 就返回。
/// <para>缓存以「歌手|歌名」为键，避免切歌时重复请求；失败结果不缓存（下次切回还能重试）。</para>
/// </summary>
public sealed class LyricService
{
    private readonly List<ILyricProvider> _providers;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LyricService(IEnumerable<ILyricProvider>? providers = null)
    {
        _providers = providers?.ToList() ?? new List<ILyricProvider> { new NeteaseLyricProvider() };
    }

    /// <summary>当前来源名称（用于 UI 展示；多来源时取第一个）。</summary>
    public string ProviderName => _providers.Count > 0 ? _providers[0].Name : "无";

    /// <summary>取歌词 LRC 原文；全部来源都失败返回 null（调用方应静默隐藏歌词区）。</summary>
    public async Task<string?> GetLrcAsync(string? title, string? artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{artist}|{title}";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }
        finally { _gate.Release(); }

        foreach (var p in _providers)
        {
            string? lrc;
            try { lrc = await p.FetchLrcAsync(title, artist, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(lrc)) continue;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { _cache[key] = lrc; }
            finally { _gate.Release(); }
            return lrc;
        }
        return null;
    }

    /// <summary>清空缓存（切换歌源 / 手动刷新歌词时调用）。</summary>
    public void ClearCache()
    {
        _gate.Wait();
        try { _cache.Clear(); }
        finally { _gate.Release(); }
    }
}
