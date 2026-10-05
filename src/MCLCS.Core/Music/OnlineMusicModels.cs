namespace MCLCS.Core.Music;

/// <summary>
/// 在线音源的数据协议类型（决定了 URL 拼法与登录能力）。
/// </summary>
public enum MusicApiKind
{
    /// <summary>
    /// Meting 协议：搜索 / 歌单 / 直链三个能力<b>免签名免登录</b>即可使用。
    /// 公共实例较多，适合开箱即用；代价是没有「我的歌单」。
    /// </summary>
    Meting = 0,

    /// <summary>
    /// NeteaseCloudMusicApi（Node 版）协议：能力最全，**支持二维码登录与我的歌单**，
    /// 但几乎必须自建实例（公共实例大多已失效），即 software 侧要用户自己部署。
    /// <para>已并入 <see cref="VendorApi"/> 体系：本值等价于「厂家自建 API + 网易云」，
    /// 只为兼容旧配置保留，新写入一律用 <see cref="VendorApi"/>。</para>
    /// </summary>
    NeteaseApi = 1,

    /// <summary>
    /// 厂家自建 API：按 <see cref="MusicApiPlatform"/> 选择厂家，每个厂家一份
    /// 服务地址与登录态，可同时登录多个厂家。
    /// <para>各厂家的社区 API 项目互不相同（网易云 NeteaseCloudMusicApi、
    /// 酷狗 KuGouMusicApi、QQ 音乐的 QQMusicApi 等），路由与响应结构也不一样，
    /// 因此由 <see cref="VendorApiProfile"/> 描述差异，代码只写一份通用实现。</para>
    /// </summary>
    VendorApi = 2
}

/// <summary>
/// 曲库厂家。Meting 协议下决定请求的 <c>server</c> 参数；
/// 厂家自建 API 下决定「跟哪个厂家自建的服务对话、用哪份登录态」。
/// </summary>
public enum MusicApiPlatform
{
    /// <summary>网易云音乐。</summary>
    Netease = 0,

    /// <summary>QQ 音乐。</summary>
    Tencent = 1,

    /// <summary>酷狗音乐。</summary>
    Kugou = 2,

    /// <summary>酷我音乐。</summary>
    Kuwo = 3,

    /// <summary>百度音乐（千千音乐）。</summary>
    Baidu = 4,

    /// <summary>虾米音乐（已停服，仅保留以兼容仍留有缓存的老实例）。</summary>
    Xiami = 5,

    /// <summary>
    /// 全部平台：并发搜索所有厂家再合并结果（仅 Meting 协议生效）。
    /// <para>它是「搜索范围」而不是「某个厂家」—— 因此曲目自身不会带这个值，
    /// 每条结果都会落到真实的某一个厂家上（见 <see cref="OnlineTrack.Platform"/>）。</para>
    /// </summary>
    All = 6
}

/// <summary>
/// 厂家的展示名与能力标注。
/// <para>放在这里而不是各 provider 内部，是为了让 UI 能把「全部平台」也列进同一个下拉框，
/// 不必为聚合选项单独写一套分支。</para>
/// </summary>
public static class MusicApiPlatformInfo
{
    /// <summary>厂家展示名（中文）。</summary>
    public static string DisplayName(MusicApiPlatform p) => p switch
    {
        MusicApiPlatform.Tencent => "QQ音乐",
        MusicApiPlatform.Kugou => "酷狗音乐",
        MusicApiPlatform.Kuwo => "酷我音乐",
        MusicApiPlatform.Baidu => "百度音乐",
        MusicApiPlatform.Xiami => "虾米音乐",
        MusicApiPlatform.All => "全部平台",
        _ => "网易云音乐"
    };

    /// <summary>可参与聚合搜索的厂家（不含 <see cref="MusicApiPlatform.All"/> 自身）。</summary>
    public static IReadOnlyList<MusicApiPlatform> Searchable { get; } = new[]
    {
        MusicApiPlatform.Netease,
        MusicApiPlatform.Tencent,
        MusicApiPlatform.Kugou,
        MusicApiPlatform.Kuwo,
        MusicApiPlatform.Baidu,
        MusicApiPlatform.Xiami
    };
}

/// <summary>
/// 在线播放音质。各协议内部自行映射到自己的参数（Meting 是码率 br，网易云是 level）。
/// </summary>
public enum MusicApiQuality
{
    /// <summary>标准（128k 左右）。</summary>
    Standard = 0,

    /// <summary>较高（192k 左右）。</summary>
    Higher = 1,

    /// <summary>极高（320k，最常用）。</summary>
    Exquisite = 2,

    /// <summary>无损（需要会员，取不到时由服务端自行降级）。</summary>
    Lossless = 3
}

/// <summary>
/// 在线曲目（搜索结果与歌单曲目统一用这个模型）。
/// <para>「曲目标识」与「播放地址」是两件事：搜索拿到的是 <see cref="Id"/>，
/// 真正播放前必须再取一次直链 —— 因为直链有有效期、且与所选音质绑定，
/// 提前固化到列表里会在过期后整单失效。</para>
/// </summary>
public sealed class OnlineTrack
{
    /// <summary>音源内的曲目 ID（Meting 与网易云不同域，不可混用）。</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 这首曲目来自哪个厂家。
    /// <para><b>为什么必须挂在曲目上</b>：曲目 ID 只在自己的厂家域内有效，
    /// 用网易云的 provider 去换酷狗曲目的直链必然失败。聚合搜索下结果来自多个厂家，
    /// 取直链 / 取歌词时必须按这个值回到对应厂家，而不是「当前设置里的那个」。</para>
    /// </summary>
    public MusicApiPlatform Platform { get; set; } = MusicApiPlatform.Netease;

    /// <summary>曲名。</summary>
    public string Title { get; set; } = "";

    /// <summary>歌手名（多个用「 / 」分隔）。</summary>
    public string Artist { get; set; } = "";

    /// <summary>专辑名，取不到为空。</summary>
    public string Album { get; set; } = "";

    /// <summary>时长（秒），未知为 0。</summary>
    public double DurationSec { get; set; }

    /// <summary>封面地址，可能为 relative HTTP 地址或空。</summary>
    public string CoverUrl { get; set; } = "";

    /// <summary>
    /// 原始播放地址（部分协议在列表里就直接给了 url）。
    /// <b>不可直接播</b>：可能为 null，也可能是另一平台的临时地址 ——
    /// 统一由 <see cref="IOnlineMusicProvider.GetPlayableUrlAsync"/> 复核后再用。
    /// </summary>
    public string? PreviewUrl { get; set; }

    /// <summary>列表展示用的一行元数据（歌手 · 专辑）。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
            if (!string.IsNullOrWhiteSpace(Album)) parts.Add(Album);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>来源厂家名（聚合搜索时用于在结果上标注出处）。</summary>
    public string PlatformText => MusicApiPlatformInfo.DisplayName(Platform);

    public string DurationText => DurationSec <= 0
        ? "--:--"
        : TimeSpan.FromSeconds(DurationSec).ToString(DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>拿到直链后转成播放列表用的 <see cref="Toolbox.Track"/>。</summary>
    public Toolbox.Track ToTrack(string playUrl) => new()
    {
        Path = playUrl,
        Title = Title,
        Artist = Artist,
        Album = Album,
        DurationSec = DurationSec
    };

    /// <summary>用于歌词查找的展示名。</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

/// <summary>
/// 在线歌单摘要（搜索出的公开歌单与「我的歌单」共用）。
/// </summary>
public sealed class OnlinePlaylist
{
    /// <summary>歌单 ID。</summary>
    public string Id { get; set; } = "";

    /// <summary>歌单名。</summary>
    public string Name { get; set; } = "";

    /// <summary>创建者昵称，取不到为空。</summary>
    public string Creator { get; set; } = "";

    /// <summary>曲目总数（部分协议给 0）。</summary>
    public int TrackCount { get; set; }

    /// <summary>封面地址。</summary>
    public string CoverUrl { get; set; } = "";

    /// <summary>是否来自「我的歌单」（登录后才可能为 true）。</summary>
    public bool IsMine { get; set; }

    /// <summary>歌单所属厂家（跨厂家聚合「我的歌单」时用于标注出处）。</summary>
    public MusicApiPlatform Platform { get; set; } = MusicApiPlatform.Netease;

    /// <summary>来源厂家名。</summary>
    public string PlatformText => MusicApiPlatformInfo.DisplayName(Platform);

    /// <summary>列表副标题。</summary>
    public string MetaText =>
        TrackCount > 0
            ? string.IsNullOrWhiteSpace(Creator) ? $"{TrackCount} 首" : $"{Creator} · {TrackCount} 首"
            : Creator;
}

/// <summary>
/// 登录后的在线账户信息。
/// </summary>
public sealed class OnlineAccount
{
    /// <summary>平台侧用户 ID。</summary>
    public string UserId { get; set; } = "";

    /// <summary>昵称。</summary>
    public string Nickname { get; set; } = "";

    /// <summary>头像地址。</summary>
    public string AvatarUrl { get; set; } = "";
}

/// <summary>
/// 二维码扫码状态。
/// </summary>
public enum QrLoginState
{
    /// <summary>二维码尚未过期，等待扫描。</summary>
    Pending = 0,

    /// <summary>已扫描，等待用户在手机上确认。</summary>
    Scanned = 1,

    /// <summary>已确认，登录成功。</summary>
    Confirmed = 2,

    /// <summary>二维码已过期，需要重新生成。</summary>
    Expired = 3,

    /// <summary>本次轮询出错（网络/接口异常），调用方可重试。</summary>
    Failed = 4
}

/// <summary>
/// 一次二维码登录会话。
/// </summary>
public sealed class QrLoginSession
{
    /// <summary>会话标识（轮询与 finalize 都要带回它）。</summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// 二维码图片的 data URL（<c>data:image/png;base64,...</c>）。
    /// <para>这里刻意不直接给 ImageSource —— Core 层不能依赖 WPF/Avalonia 的图像类型，
    /// 界面层自己解码成各自的图像对象。</para>
    /// </summary>
    public string QrDataUrl { get; set; } = "";

    /// <summary>过期时刻（取不到时由服务端自己判通过期码）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// 二维码轮询结果。
/// </summary>
public sealed class QrLoginPollResult
{
    /// <summary>当前状态。</summary>
    public QrLoginState State { get; set; } = QrLoginState.Pending;

    /// <summary>
    /// 登录凭证（仅 <see cref="QrLoginState.Confirmed"/> 时非空）。
    /// 形如网易云的 <c>MUSIC_U=xxx; ...</c>，后续请求需原样带回。
    /// </summary>
    public string Cookie { get; set; } = "";

    /// <summary>供界面显示的状态说明（如「等待扫码」「已过期」）。</summary>
    public string Message { get; set; } = "";
}
