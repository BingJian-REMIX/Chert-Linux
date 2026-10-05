using MCLCS.Core.Profiles;

namespace MCLCS.Core.Music;

/// <summary>
/// 厂家自建 API 的二维码状态判读方式。
/// <para>各项目的状态码完全是各自定义的一套数字，没有共同标准 ——
/// 与其在代码里写一堆 if 猜，不如把「怎么判」作为档案的一部分显式声明。</para>
/// </summary>
public enum VendorQrStyle
{
    /// <summary>网易云：<c>code</c> 为 800 过期 / 801 待扫 / 802 待确认 / 803 成功。</summary>
    Netease = 0,

    /// <summary>酷狗：<c>data.status</c> 为 0 过期 / 1 待扫 / 2 待确认 / 4 授权成功。</summary>
    Kugou = 1,

    /// <summary>
    /// QQ 音乐（QQMusicApi）：没有公开的状态码约定，靠响应里是否出现凭证字段来判断，
    /// 另用常见字段名（status / code / state）做辅助判读。
    /// </summary>
    Tencent = 2
}

/// <summary>登录态的传递方式。</summary>
public enum VendorCookieStyle
{
    /// <summary>作为 <c>cookie=</c> query 参数（网易云 / 酷狗系）。</summary>
    Query = 0,

    /// <summary>作为 Cookie 请求头（QQMusicApi 的 FastAPI web 层）。</summary>
    Header = 1
}

/// <summary>
/// 一个厂家自建 API 的「档案」：地址默认值、路由、状态码判读方式、能力开关。
///
/// <para><b>为什么要这层</b>：每个厂家的社区 API 都是独立项目，路由命名、
/// 响应结构、登录流程各写各的。若把这些差异散进 provider 代码，
/// 每加一个厂家就要复制一遍 provider。抽成档案后，provider 只写一份通用实现。</para>
///
/// <para><b>关于「未核实」的字段</b>：部分路由是按项目文档推导的默认值，
/// 已在 <see cref="Notes"/> 中标注。解析侧一律走宽容读取（见 <see cref="MusicJson"/>），
/// 因此即使某条路由与实例版本不符，表现也是「这一项取不到」而非崩溃。</para>
/// </summary>
public sealed class VendorApiProfile
{
    public MusicApiPlatform Platform { get; init; }

    /// <summary>展示名（含「API」字样，用于与 Meting 聚合源区分）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>默认服务地址（通常是 localhost + 该项目的默认端口）。</summary>
    public string DefaultBaseUrl { get; init; } = "";

    /// <summary>建议部署的开源项目名，用于设置页提示用户。</summary>
    public string Project { get; init; } = "";

    /// <summary>路由 / 状态的补充说明（会展示在设置页）。</summary>
    public string Notes { get; init; } = "";

    // ---- 能力 ----

    public bool SupportsSearch { get; init; } = true;
    public bool SupportsPlaylistTracks { get; init; } = true;
    public bool SupportsLyric { get; init; } = true;
    public bool SupportsAccount { get; init; } = true;

    /// <summary>是否支持扫码登录。为 false 时该厂家只能免登录使用（直连 Meting）。</summary>
    public bool SupportsLogin { get; init; } = true;

    // ---- 路由 ----

    /// <summary>搜索歌曲；<c>{kw}</c> 会替换为关键词，<c>{limit}</c> 替换为条数。</summary>
    public string SearchPath { get; init; } = "";

    /// <summary>歌单详情；<c>{id}</c> 替换为歌单 ID。</summary>
    public string PlaylistDetailPath { get; init; } = "";

    /// <summary>取播放直链；<c>{id}</c> 替换为曲目 ID。</summary>
    public string UrlPath { get; init; } = "";

    /// <summary>取歌词；<c>{id}</c> 替换为曲目 ID。</summary>
    public string LyricPath { get; init; } = "";

    /// <summary>取登录二维码标识。</summary>
    public string QrKeyPath { get; init; } = "";

    /// <summary>生成二维码；<c>{key}</c> 替换为上一步的标识。</summary>
    public string QrCreatePath { get; init; } = "";

    /// <summary>轮询扫码状态；<c>{key}</c> 替换为标识。</summary>
    public string QrCheckPath { get; init; } = "";

    /// <summary>取账户信息。</summary>
    public string AccountPath { get; init; } = "";

    /// <summary>取「我的歌单」；<c>{uid}</c> 替换为用户 ID。</summary>
    public string MyPlaylistsPath { get; init; } = "";

    /// <summary>二维码状态判读方式。</summary>
    public VendorQrStyle QrStyle { get; init; } = VendorQrStyle.Netease;

    /// <summary>登录态传递方式。</summary>
    public VendorCookieStyle CookieStyle { get; init; } = VendorCookieStyle.Query;

    /// <summary>替换路径模板里的占位符（未出现的占位符原样保留，不会因此拼出坏 URL）。</summary>
    public static string Expand(string template, params (string Token, string Value)[] pairs)
    {
        var s = template;
        foreach (var (token, value) in pairs)
            s = s.Replace("{" + token + "}", Uri.EscapeDataString(value));
        return s;
    }
}

/// <summary>
/// 各厂家档案的注册表。
///
/// <para><b>覆盖情况（很重要）</b>：只有网易云的社区 API 是被广泛部署、长期维护的；
/// 酷狗与 QQ 音乐有对应项目但需用户自行部署，且搜索 / 歌单等路由随版本变动；
/// 酷我、百度、虾米则没有可靠的自建项目 —— 它们的 <see cref="VendorApiProfile"/> 为 null，
/// 由调用方退回 Meting 聚合（免登录但可用）。</para>
/// </summary>
public static class VendorApiProfiles
{
    private static readonly VendorApiProfile Netease = new()
    {
        Platform = MusicApiPlatform.Netease,
        DisplayName = "网易云 API",
        DefaultBaseUrl = "http://localhost:3000",
        Project = "Binaryify/NeteaseCloudMusicApi",
        Notes = "最成熟的社区 API，扫码登录与我的歌单均可用。",
        SupportsSearch = true,
        SupportsPlaylistTracks = true,
        SupportsLyric = true,
        SupportsAccount = true,
        SupportsLogin = true,
        SearchPath = "/cloudsearch?keywords={kw}&limit={limit}",
        PlaylistDetailPath = "/playlist/detail?id={id}",
        UrlPath = "/song/url/v1?id={id}&level={quality}",
        LyricPath = "/lyric?id={id}",
        QrKeyPath = "/login/qr/key",
        QrCreatePath = "/login/qr/create?key={key}&qrimg=true",
        QrCheckPath = "/login/qr/check?key={key}",
        AccountPath = "/user/account",
        MyPlaylistsPath = "/user/playlist?uid={uid}",
        QrStyle = VendorQrStyle.Netease,
        CookieStyle = VendorCookieStyle.Query
    };

    private static readonly VendorApiProfile Kugou = new()
    {
        Platform = MusicApiPlatform.Kugou,
        DisplayName = "酷狗 API",
        DefaultBaseUrl = "http://localhost:3000",
        Project = "iAJue/KuGouMusicApi",
        Notes = "路由沿用 NeteaseCloudMusicApi 的模块命名约定（login/qr/key|create|check）。"
              + "默认端口与网易云 API 相同，同时部署时请改其中一个端口。",
        SupportsSearch = true,
        SupportsPlaylistTracks = true,
        SupportsLyric = true,
        SupportsAccount = true,
        SupportsLogin = true,
        SearchPath = "/search?keywords={kw}&limit={limit}",
        PlaylistDetailPath = "/playlist/detail?id={id}",
        UrlPath = "/song/url?id={id}",
        LyricPath = "/lyric?id={id}",
        QrKeyPath = "/login/qr/key",
        QrCreatePath = "/login/qr/create?key={key}&qrimg=true",
        QrCheckPath = "/login/qr/check?key={key}",
        AccountPath = "/user/detail",
        MyPlaylistsPath = "/user/playlist",
        QrStyle = VendorQrStyle.Kugou,
        CookieStyle = VendorCookieStyle.Query
    };

    private static readonly VendorApiProfile Tencent = new()
    {
        Platform = MusicApiPlatform.Tencent,
        DisplayName = "QQ音乐 API",
        DefaultBaseUrl = "http://localhost:8080",
        Project = "luren-dc/QQMusicApi（web 层，FastAPI）",
        Notes = "扫码登录与播放地址路由已核实；搜索/歌单为按文档推导，实例版本不同可能取不到。",
        SupportsSearch = true,
        SupportsPlaylistTracks = false,     // 该 web 层未暴露歌单详情路由
        SupportsLyric = false,
        SupportsAccount = false,
        SupportsLogin = true,
        SearchPath = "/search/search_by_type?keyword={kw}&num={limit}",
        PlaylistDetailPath = "",
        UrlPath = "/song/{id}/url",
        LyricPath = "",
        QrKeyPath = "/login/qrcode/qq",
        QrCreatePath = "/login/qrcode/qq",
        QrCheckPath = "/login/qrcode/qq/status",
        AccountPath = "",
        MyPlaylistsPath = "/user/{uid}/created_songlists",
        QrStyle = VendorQrStyle.Tencent,
        CookieStyle = VendorCookieStyle.Header
    };

    /// <summary>取厂家档案；没有可用自建项目时返回 null（调用方应退回 Meting 聚合）。</summary>
    public static VendorApiProfile? For(MusicApiPlatform platform) => platform switch
    {
        MusicApiPlatform.Netease => Netease,
        MusicApiPlatform.Kugou => Kugou,
        MusicApiPlatform.Tencent => Tencent,
        _ => null
    };

    /// <summary>有可用自建项目的厂家（设置页据此提示哪些厂家能登录）。</summary>
    public static IReadOnlyList<MusicApiPlatform> LoginCapable { get; } = new[]
    {
        MusicApiPlatform.Netease,
        MusicApiPlatform.Kugou,
        MusicApiPlatform.Tencent
    };

    /// <summary>该厂家是否有可用自建项目。</summary>
    public static bool HasProfile(MusicApiPlatform platform) => For(platform) is not null;

    /// <summary>某厂家的默认服务地址（没有档案时退回 Meting 公共实例）。</summary>
    public static string DefaultUrlFor(MusicApiPlatform platform) =>
        For(platform)?.DefaultBaseUrl ?? MusicApiPrefs.DefaultMetingUrl;

    /// <summary>该厂家能否扫码登录。</summary>
    public static bool CanLogin(MusicApiPlatform platform) => For(platform)?.SupportsLogin == true;
}
