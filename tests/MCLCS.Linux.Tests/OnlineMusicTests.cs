using System.Globalization;
using System.Net;
using System.Text;
using Xunit;
using MCLCS.Core.Music;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Profiles;

namespace MCLCS.Linux.Tests;

/// <summary>
/// 在线音源的单元测试。
///
/// <para><b>全部不需要真实网络</b>：用 <see cref="StubHttp"/> 把响应内容喂给 provider。
/// 这样既能覆盖「同一协议不同实例字段名不一致」这条最容易出错的路径，
/// 也不会因为公共服务抖动而随机变红。</para>
/// </summary>
public class OnlineMusicTests
{
    // ---------- 假 HTTP：按注册内容返回固定响应 ----------

    private sealed class StubHttp : HttpMessageHandler
    {
        private readonly Func<string, string> _responder;

        /// <summary>最近一次请求的完整 URL（用来断言查询串拼得对不对）。</summary>
        public string LastUrl { get; private set; } = "";

        public StubHttp(string body) : this(_ => body) { }

        public StubHttp(Func<string, string> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responder(LastUrl), Encoding.UTF8, "application/json")
            });
        }
    }

    private static HttpClient Client(string body, StubHttp? stub = null)
    {
        stub ??= new StubHttp(body);
        return new HttpClient(stub);
    }

    private static HttpClient Client(Func<string, string> responder, StubHttp? stub = null)
    {
        stub ??= new StubHttp(responder);
        return new HttpClient(stub);
    }

    // ---------- 凭证混淆 ----------

    [Fact]
    public void Credential_roundtrip()
    {
        const string cookie = "MUSIC_U=abc123; __csrf=deadbeef; NMTID=xyz";
        var stored = ApiCredentialProtector.Protect(cookie);

        Assert.NotEqual(cookie, stored);                       // 绝不能明文落盘
        Assert.StartsWith("mclcs1:", stored);
        Assert.Equal(cookie, ApiCredentialProtector.TryUnprotect(stored));
    }

    [Fact]
    public void Credential_empty_and_broken_are_treated_as_not_logged_in()
    {
        Assert.Equal("", ApiCredentialProtector.Protect(""));
        Assert.Null(ApiCredentialProtector.TryUnprotect(null));
        Assert.Null(ApiCredentialProtector.TryUnprotect(""));
        Assert.Null(ApiCredentialProtector.TryUnprotect("随便一串没前缀的东西"));
        Assert.Null(ApiCredentialProtector.TryUnprotect("mclcs1:不是合法的base64"));
    }

    // ---------- 设置容错 ----------

    [Fact]
    public void Prefs_normalize_trims_slash_and_clamps_enums()
    {
        var prefs = new MusicApiPrefs
        {
            BaseUrl = "  https://example.com/api/  ",
            Kind = (MusicApiKind)77,
            Platform = (MusicApiPlatform)88,
            Quality = (MusicApiQuality)99
        }.Normalized();

        Assert.Equal("https://example.com/api", prefs.BaseUrl);
        Assert.Equal(MusicApiKind.Meting, prefs.Kind);
        Assert.Equal(MusicApiPlatform.Netease, prefs.Platform);
        Assert.Equal(MusicApiQuality.Exquisite, prefs.Quality);
    }

    [Fact]
    public void Prefs_default_url_differs_per_kind()
    {
        // 网易云 API 几乎必须自建，所以默认值指向 localhost 而不是某个公共实例
        Assert.Contains("localhost", MusicApiPrefs.DefaultUrlFor(MusicApiKind.NeteaseApi));
        Assert.DoesNotContain("localhost", MusicApiPrefs.DefaultUrlFor(MusicApiKind.Meting));
    }

    // ---------- Meting 协议解析 ----------

    [Fact]
    public async Task Meting_search_parses_name_and_artist_array()
    {
        // 搜索接口字段名：name + artist（数组）+ pic_id
        const string body = "[{\"id\":\"123\",\"name\":\"晴天\",\"artist\":[\"周杰伦\"],\"album\":\"叶惠美\",\"pic_id\":\"999\"}]";
        var stub = new StubHttp(body);
        var provider = new MetingMusicProvider(Client(body, stub), "https://api.example.com/meting");

        var songs = await provider.SearchSongsAsync("晴天");

        var t = Assert.Single(songs);
        Assert.Equal("123", t.Id);
        Assert.Equal("晴天", t.Title);
        Assert.Equal("周杰伦", t.Artist);
        Assert.Equal("叶惠美", t.Album);
        // 只有 pic_id 时，封面要拼成 type=pic 的完整地址
        Assert.Equal("https://api.example.com/meting?server=netease&type=pic&id=999", t.CoverUrl);

        // 搜索必须同时带 id 与 name —— 不同实例认的参数不一样。
        // Uri 会把百分号编码解码成原字符，所以用解码后的中文断言「关键词没被改坏」。
        Assert.Contains("type=search", stub.LastUrl);
        Assert.Contains("id=晴天", stub.LastUrl);
        Assert.Contains("name=晴天", stub.LastUrl);
        Assert.DoesNotContain("%25", stub.LastUrl);      // ★ 不能被二次转义
    }

    [Fact]
    public async Task Meting_playlist_parses_detail_shape()
    {
        // 详情接口的字段名与搜索接口不同：title + author + url
        const string body = "[{\"id\":\"456\",\"title\":\"稻香\",\"author\":\"周杰伦\",\"album\":\"魔杰座\",\"url\":\"https://cdn.example.com/a.mp3\"}]";
        var provider = new MetingMusicProvider(Client(body), "https://api.example.com/meting");

        var tracks = await provider.GetPlaylistTracksAsync("playlist-1");

        var t = Assert.Single(tracks);
        Assert.Equal("稻香", t.Title);
        Assert.Equal("周杰伦", t.Artist);
        Assert.Equal("https://cdn.example.com/a.mp3", t.PreviewUrl);
    }

    [Theory]
    [InlineData("[{\"url\":\"https://cdn.example.com/x.mp3\",\"br\":320000}]", "https://cdn.example.com/x.mp3")]
    [InlineData("https://cdn.example.com/raw.mp3", "https://cdn.example.com/raw.mp3")]
    [InlineData("[]", null)]
    public async Task Meting_playable_url_handles_json_and_plain_text(string body, string? expected)
    {
        var provider = new MetingMusicProvider(Client(body), "https://api.example.com/meting");
        var url = await provider.GetPlayableUrlAsync(new OnlineTrack { Id = "1" }, MusicApiQuality.Exquisite);
        Assert.Equal(expected, url);
    }

    [Fact]
    public async Task Meting_login_is_not_supported_but_search_playlist_returns_empty()
    {
        var provider = new MetingMusicProvider(Client("[]"), "https://api.example.com/meting");
        Assert.False(provider.SupportsLogin);
        Assert.Empty(await provider.SearchPlaylistsAsync("任意"));   // 没这个能力，但不该抛
    }

    [Fact]
    public async Task Provider_without_base_url_reports_configuration_error()
    {
        var provider = new MetingMusicProvider(Client("[]"), "");
        await Assert.ThrowsAsync<MusicApiException>(() => provider.SearchSongsAsync("晴天"));
    }

    // ---------- 网易云协议解析 ----------

    [Fact]
    public async Task Netease_song_detail_parses_ar_al_dt_shape()
    {
        // playlist/detail 的字段名：ar / al / dt
        const string body = "{\"code\":200,\"playlist\":{\"tracks\":[{\"id\":\"321\",\"name\":\"夜曲\",\"ar\":[{\"name\":\"周杰伦\"}],\"al\":{\"name\":\"十一月的萧邦\",\"picUrl\":\"https://p.example.com/p.jpg\"},\"dt\":267000}]}}";
        var provider = new NeteaseCloudMusicApiProvider(Client(body), "https://ncm.example.com");

        var tracks = await provider.GetPlaylistTracksAsync("p1");

        var t = Assert.Single(tracks);
        Assert.Equal("夜曲", t.Title);
        Assert.Equal("周杰伦", t.Artist);
        Assert.Equal("十一月的萧邦", t.Album);
        Assert.Equal(267, t.DurationSec);
        Assert.Equal("https://p.example.com/p.jpg", t.CoverUrl);
    }

    [Fact]
    public async Task Netease_playlist_detail_falls_back_to_song_detail_when_tracks_empty()
    {
        // 长歌单会把 tracks 掏空，只剩 trackIds 骨架 —— 必须再查 /song/detail
        var responder = new Func<string, string>(url => url.Contains("/playlist/detail")
            ? "{\"code\":200,\"playlist\":{\"trackIds\":[{\"id\":\"1\"},{\"id\":\"2\"}]}}"
            : "{\"code\":200,\"songs\":[{\"id\":\"1\",\"name\":\"龙卷风\",\"artists\":[{\"name\":\"周杰伦\"}],\"duration\":245000},{\"id\":\"2\",\"name\":\"安静\",\"artists\":[{\"name\":\"周杰伦\"}],\"duration\":325000}]}");

        var provider = new NeteaseCloudMusicApiProvider(Client(responder), "https://ncm.example.com");

        var tracks = await provider.GetPlaylistTracksAsync("huge");

        Assert.Equal(2, tracks.Count);
        Assert.Equal("龙卷风", tracks[0].Title);      // 顺序必须与 trackIds 一致
        Assert.Equal(245, tracks[0].DurationSec);
    }

    [Fact]
    public async Task Netease_qr_login_flow_returns_cookie_on_803()
    {
        // key → create → check(803) 三步
        var responder = new Func<string, string>(url =>
        {
            if (url.Contains("/login/qr/key")) return "{\"code\":200,\"data\":{\"unikey\":\"k1\"}}";
            if (url.Contains("/login/qr/create")) return "{\"code\":200,\"data\":{\"qrimg\":\"data:image/png;base64,iVBOR\"}}";
            if (url.Contains("/login/qr/check")) return "{\"code\":803,\"cookie\":\"MUSIC_U=abc\",\"message\":\"授权登录成功\"}";
            return "{}";
        });
        var provider = new NeteaseCloudMusicApiProvider(Client(responder), "https://ncm.example.com");

        var session = await provider.CreateQrLoginAsync();
        Assert.Equal("k1", session.Key);
        Assert.StartsWith("data:image/png;base64,", session.QrDataUrl);

        var poll = await provider.PollQrLoginAsync(session.Key);
        Assert.Equal(QrLoginState.Confirmed, poll.State);
        Assert.Equal("MUSIC_U=abc", poll.Cookie);
    }

    [Theory]
    [InlineData(801, QrLoginState.Pending)]
    [InlineData(802, QrLoginState.Scanned)]
    [InlineData(800, QrLoginState.Expired)]
    public async Task Netease_qr_poll_maps_every_code(int code, QrLoginState expected)
    {
        var provider = new NeteaseCloudMusicApiProvider(
            Client("{\"code\":" + code.ToString(CultureInfo.InvariantCulture) + ",\"message\":\"m\"}"),
            "https://ncm.example.com");

        var poll = await provider.PollQrLoginAsync("k");
        Assert.Equal(expected, poll.State);
    }

    // ---------- 门面 ----------

    [Fact]
    public void Service_switches_provider_by_kind_and_drops_login_when_unsupported()
    {
        var prefs = new MusicApiPrefs
        {
            Kind = MusicApiKind.NeteaseApi,
            BaseUrl = "https://ncm.example.com",
            Credential = ApiCredentialProtector.Protect("MUSIC_U=abc")
        };
        var service = new OnlineMusicService(prefs, Client("{}"));

        Assert.True(service.CanLogin);
        Assert.True(service.IsLoggedIn);          // 支持登录的协议应能还原凭证

        // 切到不支持登录的协议：凭证必须丢弃 —— 留着也没地方用，还会误导 UI
        service.Reload(new MusicApiPrefs { Kind = MusicApiKind.Meting, BaseUrl = "https://m.example.com" });
        Assert.False(service.CanLogin);
        Assert.False(service.IsLoggedIn);
        Assert.IsType<MetingMusicProvider>(service.Provider);
    }

    [Fact]
    public void Service_is_unavailable_without_base_url()
    {
        var service = new OnlineMusicService(new MusicApiPrefs { BaseUrl = "" }, Client("{}"));
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void Track_remote_detection_skips_local_metadata()
    {
        // 在线曲目的 Path 是直链：不能被当成本地文件去读标签
        // Linux 端 Track 没有 FileName 列（列表显示用 Display），这里只验证远程判定与标签读取守卫
        var remote = new Track { Path = "https://cdn.example.com/a.mp3", Title = "在线曲" };
        Assert.True(remote.IsRemote);
        remote.LoadMetadata();                     // 不该抛，也不该改标题
        Assert.Equal("在线曲", remote.Title);

        var local = new Track { Path = "D:/music/a.mp3" };
        Assert.False(local.IsRemote);
    }
}
