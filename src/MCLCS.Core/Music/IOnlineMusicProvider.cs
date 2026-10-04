namespace MCLCS.Core.Music;

/// <summary>
/// 在线音源访问异常。
/// <para>网络失败、接口改版、Json 解析失败都归到这里；调用方按业务需要决定是
/// 「静默降级」还是「提示用户」，不要让原始 <see cref="HttpRequestException"/>
/// 一路冒到界面层。</para>
/// </summary>
public sealed class MusicApiException : Exception
{
    public MusicApiException(string message, bool isConfigurationError = false, Exception? inner = null)
        : base(message, inner)
    {
        IsConfigurationError = isConfigurationError;
    }

    /// <summary>
    /// 是否属于「配置问题」（没填 API 地址、地址格式不对、选了不支持登录的数据源等）。
    /// <para>这类错误的提示语应当引导用户去设置页修改，而不是让用户重试。</para>
    /// </summary>
    public bool IsConfigurationError { get; }
}

/// <summary>
/// 在线音源需要的最小能力集合（规格：搜索、歌单、取直链、歌词）。
/// <para>抽成接口是为了让「聚合协议」与「网易云官方协议」在同一套 UI 下互换 ——
/// UI 只认 <see cref="OnlineTrack"/> / <see cref="OnlinePlaylist"/> 这两个模型。</para>
/// </summary>
public interface IOnlineMusicProvider
{
    /// <summary>展示名（设置页与搜索结果左上角显示）。</summary>
    string Name { get; }

    /// <summary>是否支持二维码登录 + 我的歌单。</summary>
    bool SupportsLogin { get; }

    /// <summary>是否支持按关键词搜索公开歌单。</summary>
    bool SupportsPlaylistSearch { get; }

    /// <summary>搜索歌曲。</summary>
    /// <param name="keyword">关键词（歌名或「歌名 歌手」）。</param>
    /// <param name="limit">返回条数上限（由实现自行钳制）。</param>
    /// <param name="ct">取消标记。</param>
    Task<IReadOnlyList<OnlineTrack>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default);

    /// <summary>搜索公开歌单（不支持时返回空列表，不抛）。</summary>
    Task<IReadOnlyList<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20, CancellationToken ct = default);

    /// <summary>取歌单内的曲目。</summary>
    Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default);

    /// <summary>
    /// 取可播放的直链。
    /// <para><b>为什么不在搜索时就拿好</b>：直链有有效期且与音质绑定，
    /// 固化到列表里会在过期后整单失效。所以只在真正要点播的那一刻才去换。</para>
    /// </summary>
    /// <param name="track">目标曲目。</param>
    /// <param name="quality">期望音质（服务端通常会自行降级到会员允许的水平）。</param>
    /// <param name="cookie">登录态；未登录时传空。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>直链；取不到返回 null（多为版权/会员限制）。</returns>
    Task<string?> GetPlayableUrlAsync(OnlineTrack track, MusicApiQuality quality,
        string cookie = "", CancellationToken ct = default);

    /// <summary>取歌词 LRC 原文；查不到返回 null（界面应静默隐藏歌词区）。</summary>
    Task<string?> GetLyricAsync(OnlineTrack track, CancellationToken ct = default);
}

/// <summary>
/// 具备登录能力的在线音源（例如自建网易云 API）。
/// <para>单独拆出来是因为不是每种协议都有登录能力 ——
/// Meting 聚合协议天然没有，硬塞一个「不支持」的实现反而会让 UI 分支变复杂。</para>
/// </summary>
public interface IOnlineMusicLoginProvider : IOnlineMusicProvider
{
    /// <summary>生成二维码返回会话（含二维码图像 data URL）。</summary>
    Task<QrLoginSession> CreateQrLoginAsync(CancellationToken ct = default);

    /// <summary>轮询扫码状态（由调用方按 1–2s 节奏反复调用）。</summary>
    Task<QrLoginPollResult> PollQrLoginAsync(string key, CancellationToken ct = default);

    /// <summary>按凭证取账户信息（失败返回 null，不抛）。</summary>
    Task<OnlineAccount?> GetAccountAsync(string cookie, CancellationToken ct = default);

    /// <summary>取「我的歌单」（含用户自建与收藏）。</summary>
    Task<IReadOnlyList<OnlinePlaylist>> GetMyPlaylistsAsync(string cookie, CancellationToken ct = default);
}
