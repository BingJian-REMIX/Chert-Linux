using System.Net;
using System.Text;
using Xunit;
using MCLCS.Core.Music;
using MCLCS.Core.Profiles;

namespace MCLCS.Linux.Tests;

/// <summary>
/// 多厂家接入的单元测试：六家 server 映射、聚合搜索、按曲目厂家取直链、
/// 厂家自建 API 档案与凭证隔离。
///
/// <para><b>为什么单独一个文件</b>：这些用例围绕「厂家」这个维度，
/// 与原有「协议解析」维度的用例关注点不同，混在一起会让两类断言互相干扰。</para>
/// <para>全部走 <see cref="StubHttp"/>，不需要真实网络。</para>
/// </summary>
public class OnlineMusicPlatformTests
{
    private sealed class StubHttp : HttpMessageHandler
    {
        private readonly Func<string, string> _responder;
        public List<string> Urls { get; } = new();
        public string LastUrl => Urls.Count == 0 ? "" : Urls[^1];

        public StubHttp(Func<string, string> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responder(url), Encoding.UTF8, "application/json")
            });
        }
    }

    private static string ServerOf(string url)
    {
        var i = url.IndexOf("server=", StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = url[(i + 7)..];
        var j = rest.IndexOf('&');
        return j < 0 ? rest : rest[..j];
    }

    /// <summary>造一个「每个厂家返回一首同名不同 id 的歌」的假实例。</summary>
    private static string PerServerBody(string url) =>
        "[" + "{\"id\":\"" + ServerOf(url) + "-1\",\"name\":\"晴天\",\"artist\":[\"周杰伦\"],\"album\":\"叶惠美\"}" + "]";

    // ---------- 厂家 → Meting server 参数 ----------

    [Theory]
    [InlineData(MusicApiPlatform.Netease, "netease")]
    [InlineData(MusicApiPlatform.Tencent, "tencent")]
    [InlineData(MusicApiPlatform.Kugou, "kugou")]
    [InlineData(MusicApiPlatform.Kuwo, "kuwo")]
    [InlineData(MusicApiPlatform.Baidu, "baidu")]
    [InlineData(MusicApiPlatform.Xiami, "xiami")]
    public async Task Meting_maps_every_platform_to_its_server(MusicApiPlatform platform, string expected)
    {
        var stub = new StubHttp(PerServerBody);
        var provider = new MetingMusicProvider(new HttpClient(stub), "https://api.example.com/meting", platform);

        var tracks = await provider.SearchSongsAsync("晴天");

        Assert.Contains("server=" + expected, stub.LastUrl);
        Assert.Single(tracks);
        Assert.Equal(platform, tracks[0].Platform);        // 结果必须自带来源厂家
    }

    [Fact]
    public async Task Meting_all_platforms_fans_out_and_interleaves()
    {
        var stub = new StubHttp(PerServerBody);
        var provider = new MetingMusicProvider(new HttpClient(stub), "https://api.example.com/meting",
            MusicApiPlatform.All);

        var tracks = await provider.SearchSongsAsync("晴天", limit: 12);

        // 六家都要被问到
        var servers = stub.Urls.Select(ServerOf).Distinct().ToList();
        Assert.Equal(6, servers.Count);
        Assert.Contains("netease", servers);
        Assert.Contains("kugou", servers);

        // 交错合并：前六条应来自六个不同厂家，而不是先塞满一家
        Assert.Equal(6, tracks.Count);
        Assert.Equal(6, tracks.Select(t => t.Platform).Distinct().Count());
    }

    [Fact]
    public async Task Meting_all_platforms_survives_one_dead_server()
    {
        // 酷狗实例挂了（返回非 JSON），其余五家照常
        var stub = new StubHttp(url =>
            ServerOf(url) == "kugou" ? "<html>502 Bad Gateway</html>" : PerServerBody(url));

        var provider = new MetingMusicProvider(new HttpClient(stub), "https://api.example.com/meting",
            MusicApiPlatform.All);

        var tracks = await provider.SearchSongsAsync("晴天", limit: 12);

        // 一家挂掉不该让整个搜索空手而归
        Assert.NotEmpty(tracks);
        Assert.DoesNotContain(tracks, t => t.Platform == MusicApiPlatform.Kugou);
    }

    [Fact]
    public async Task Meting_all_platforms_throws_when_every_server_fails()
    {
        var stub = new StubHttp(_ => "<html>502</html>");
        var provider = new MetingMusicProvider(new HttpClient(stub), "https://api.example.com/meting",
            MusicApiPlatform.All);

        // 全灭时要抛出真实原因，而不是让界面显示「没有找到匹配的歌曲」
        await Assert.ThrowsAsync<MusicApiException>(() => provider.SearchSongsAsync("晴天"));
    }

    // ---------- 按曲目自己的厂家取直链 ----------

    [Fact]
    public async Task Facade_resolves_url_from_the_tracks_own_platform()
    {
        var stub = new StubHttp(url =>
            "{\"url\":\"https://cdn.example.com/" + ServerOf(url) + ".mp3\"}");

        var prefs = new MusicApiPrefs
        {
            Kind = MusicApiKind.Meting,
            BaseUrl = "https://api.example.com/meting",
            Platform = MusicApiPlatform.All          // 设置里选的是「全部平台」
        }.Normalized();

        var service = new OnlineMusicService(prefs, new HttpClient(stub));

        // 一条来自酷狗的聚合结果：直链必须回酷狗去换，而不是拿去问网易云
        var kugouTrack = new OnlineTrack { Id = "kg-1", Title = "晴天", Platform = MusicApiPlatform.Kugou };
        var url = await service.GetPlayableUrlAsync(kugouTrack);

        Assert.Equal("https://cdn.example.com/kugou.mp3", url);
        Assert.Contains("server=kugou", stub.LastUrl);
    }

    // ---------- 厂家自建 API 档案 ----------

    [Fact]
    public void Vendor_profiles_cover_netease_kugou_and_qq_only()
    {
        Assert.NotNull(VendorApiProfiles.For(MusicApiPlatform.Netease));
        Assert.NotNull(VendorApiProfiles.For(MusicApiPlatform.Kugou));
        Assert.NotNull(VendorApiProfiles.For(MusicApiPlatform.Tencent));

        // 酷我 / 百度 / 虾米没有可用的自建项目 —— 必须明确返回 null 好让上层退回 Meting
        Assert.Null(VendorApiProfiles.For(MusicApiPlatform.Kuwo));
        Assert.Null(VendorApiProfiles.For(MusicApiPlatform.Baidu));
        Assert.Null(VendorApiProfiles.For(MusicApiPlatform.Xiami));

        Assert.False(VendorApiProfiles.CanLogin(MusicApiPlatform.Kuwo));
        Assert.True(VendorApiProfiles.CanLogin(MusicApiPlatform.Kugou));
    }

    [Fact]
    public void Vendor_profiles_use_each_projects_default_port()
    {
        Assert.Equal("http://localhost:3000", VendorApiProfiles.For(MusicApiPlatform.Netease)!.DefaultBaseUrl);
        Assert.Equal("http://localhost:3000", VendorApiProfiles.For(MusicApiPlatform.Kugou)!.DefaultBaseUrl);
        Assert.Equal("http://localhost:8080", VendorApiProfiles.For(MusicApiPlatform.Tencent)!.DefaultBaseUrl);

        // QQ 音乐走的是 FastAPI web 层：登录态放在 Cookie 头，不是 query 参数
        Assert.Equal(VendorCookieStyle.Header, VendorApiProfiles.For(MusicApiPlatform.Tencent)!.CookieStyle);
        Assert.Equal(VendorCookieStyle.Query, VendorApiProfiles.For(MusicApiPlatform.Netease)!.CookieStyle);
    }

    [Fact]
    public void Vendor_mode_falls_back_to_meting_for_platforms_without_project()
    {
        var prefs = new MusicApiPrefs
        {
            Kind = MusicApiKind.VendorApi,
            Platform = MusicApiPlatform.Kuwo        // 没有自建项目
        }.Normalized();

        var service = new OnlineMusicService(prefs);

        // 退回 Meting 后至少还能搜能播，但不该谎称能登录
        Assert.False(service.CanLogin);
        Assert.False(service.VendorLoginSupported);
        Assert.Null(service.VendorProfile);
    }

    // ---------- 凭证按厂家隔离 ----------

    [Fact]
    public void Credentials_are_stored_per_vendor()
    {
        var prefs = new MusicApiPrefs { Kind = MusicApiKind.VendorApi }.Normalized();

        prefs.VendorFor(MusicApiPlatform.Netease).Credential = ApiCredentialProtector.Protect("MUSIC_U=netease");
        prefs.VendorFor(MusicApiPlatform.Kugou).Credential = ApiCredentialProtector.Protect("token=kugou");

        Assert.Equal("MUSIC_U=netease", ApiCredentialProtector.TryUnprotect(
            prefs.VendorFor(MusicApiPlatform.Netease).Credential));
        Assert.Equal("token=kugou", ApiCredentialProtector.TryUnprotect(
            prefs.VendorFor(MusicApiPlatform.Kugou).Credential));

        // 换厂家不该读到上一家的凭证
        Assert.NotEqual(prefs.VendorFor(MusicApiPlatform.Netease).Credential,
                        prefs.VendorFor(MusicApiPlatform.Kugou).Credential);
    }

    [Fact]
    public void Service_login_state_follows_selected_vendor()
    {
        var prefs = new MusicApiPrefs
        {
            Kind = MusicApiKind.VendorApi,
            Platform = MusicApiPlatform.Netease,
            Credential = ApiCredentialProtector.Protect("MUSIC_U=netease"),
            AccountName = "网易云用户"
        }.Normalized();

        var service = new OnlineMusicService(prefs);
        Assert.True(service.IsLoggedIn);                 // 网易云已登录
        Assert.Equal("网易云用户", service.Account?.Nickname);

        // 切到酷狗：那一家的凭证还没存过，按未登录处理
        prefs.Platform = MusicApiPlatform.Kugou;
        service.Reload(prefs);

        Assert.False(service.IsLoggedIn);
        Assert.Null(service.Account);
    }

    // ---------- 通用厂家实现：酷狗扫码状态码 ----------

    [Fact]
    public async Task Community_provider_maps_kugou_qr_status_codes()
    {
        var profile = VendorApiProfiles.For(MusicApiPlatform.Kugou)!;

        // 酷狗：0 过期 / 1 待扫 / 2 待确认 / 4 成功（成功时带 token 与 userid）
        Assert.Equal(QrLoginState.Expired, (await Poll(profile, "{\"data\":{\"status\":0}}")).State);
        Assert.Equal(QrLoginState.Pending, (await Poll(profile, "{\"data\":{\"status\":1}}")).State);
        Assert.Equal(QrLoginState.Scanned, (await Poll(profile, "{\"data\":{\"status\":2}}")).State);

        var done = await Poll(profile, "{\"data\":{\"status\":4,\"token\":\"tk\",\"userid\":\"123\"}}");
        Assert.Equal(QrLoginState.Confirmed, done.State);
        Assert.Contains("token=tk", done.Cookie);
        Assert.Contains("userid=123", done.Cookie);
    }

    [Fact]
    public async Task Community_provider_maps_netease_qr_codes()
    {
        var profile = VendorApiProfiles.For(MusicApiPlatform.Netease)!;

        Assert.Equal(QrLoginState.Expired, (await Poll(profile, "{\"code\":800}")).State);
        Assert.Equal(QrLoginState.Pending, (await Poll(profile, "{\"code\":801}")).State);
        Assert.Equal(QrLoginState.Scanned, (await Poll(profile, "{\"code\":802}")).State);

        var done = await Poll(profile, "{\"code\":803,\"cookie\":\"MUSIC_U=x\"}");
        Assert.Equal(QrLoginState.Confirmed, done.State);
        Assert.Equal("MUSIC_U=x", done.Cookie);
    }

    private static async Task<QrLoginPollResult> Poll(VendorApiProfile profile, string body)
    {
        var stub = new StubHttp(_ => body);
        var provider = new CommunityApiProvider(new HttpClient(stub), profile, "http://localhost:3000");
        return await provider.PollQrLoginAsync("k");
    }

    // ---------- 宽容解析 ----------

    [Fact]
    public async Task Community_provider_reads_results_from_any_nesting()
    {
        var profile = VendorApiProfiles.For(MusicApiPlatform.Kugou)!;

        // 厂家返回结构各不相同：这里故意把曲目数组藏在 data.lists 里
        const string body = "{\"status\":1,\"data\":{\"lists\":["
            + "{\"hash\":\"abc\",\"songname\":\"晴天\",\"singername\":\"周杰伦\",\"duration\":240}]}}";

        var stub = new StubHttp(_ => body);
        var provider = new CommunityApiProvider(new HttpClient(stub), profile, "http://localhost:3000");

        var tracks = await provider.SearchSongsAsync("晴天");

        Assert.Single(tracks);
        Assert.Equal("abc", tracks[0].Id);            // 酷狗的曲目标识叫 hash
        Assert.Equal("晴天", tracks[0].Title);
        Assert.Equal("周杰伦", tracks[0].Artist);
        Assert.Equal(240, tracks[0].DurationSec);     // 秒，不该被当成毫秒
        Assert.Equal(MusicApiPlatform.Kugou, tracks[0].Platform);
    }

    [Fact]
    public async Task Community_provider_finds_play_url_wherever_it_is()
    {
        var profile = VendorApiProfiles.For(MusicApiPlatform.Tencent)!;
        var stub = new StubHttp(_ => "{\"data\":{\"mid\":\"003aAYrm\",\"purl\":\"https://dl.example.com/a.m4a\"}}");
        var provider = new CommunityApiProvider(new HttpClient(stub), profile, "http://localhost:8080");

        var url = await provider.GetPlayableUrlAsync(
            new OnlineTrack { Id = "003aAYrm", Platform = MusicApiPlatform.Tencent },
            MusicApiQuality.Exquisite);

        Assert.Equal("https://dl.example.com/a.m4a", url);
    }

    [Fact]
    public void Platform_display_names_are_chinese()
    {
        Assert.Equal("网易云音乐", MusicApiPlatformInfo.DisplayName(MusicApiPlatform.Netease));
        Assert.Equal("QQ音乐", MusicApiPlatformInfo.DisplayName(MusicApiPlatform.Tencent));
        Assert.Equal("酷狗音乐", MusicApiPlatformInfo.DisplayName(MusicApiPlatform.Kugou));
        Assert.Equal("酷我音乐", MusicApiPlatformInfo.DisplayName(MusicApiPlatform.Kuwo));
        Assert.Equal("全部平台", MusicApiPlatformInfo.DisplayName(MusicApiPlatform.All));

        // 「全部平台」是搜索范围，不能参与自身展开
        Assert.DoesNotContain(MusicApiPlatform.All, MusicApiPlatformInfo.Searchable);
        Assert.Equal(6, MusicApiPlatformInfo.Searchable.Count);
    }
}
