using MCLCS.Core.Profiles;

namespace MCLCS.Core.Music;

/// <summary>
/// 在线音源服务门面：按当前设置挑一个数据源协议，向上提供统一能力。
///
/// <para><b>为什么 UI 不应直接持有 provider</b>：用户在设置里切一次协议（或改一次服务地址），
/// 就得重建 provider 实例并重新登录。把「选谁 + 登录态在哪」收在这里，
/// UI 就只需 <see cref="Reload"/> 一下，不必关心实例是否需要重建。</para>
///
/// <para><b>登录态的生命周期</b>：凭证（cookie）以混淆形式存在 <see cref="MusicApiPrefs.Credential"/>，
/// 由外部持久化。<see cref="Reload"/> 时自动尝试还原到内存；
/// 账户昵称/头像则可能要联网才能拿到，故单独提供 <see cref="RefreshAccountAsync"/> ——
/// 「已登录」这个状态本身不需要联网，先让用户看到「已登录」，头像晚点到也无所谓。</para>
///
/// <para><b>失败策略</b>：所有方法都不向 UI 抛 <see cref="MusicApiException"/> 之外的东西；
/// 网络类失败包成 MusicApiException，装饰性数据（歌词/头像）失败一律返回 null。</para>
/// </summary>
public sealed class OnlineMusicService
{
    private readonly HttpClient _http;
    private readonly Dictionary<MusicApiPlatform, IOnlineMusicProvider> _perPlatform = new();
    private MusicApiPrefs _prefs;
    private IOnlineMusicProvider _provider;
    private string _cookie = "";
    private OnlineAccount? _account;

    public OnlineMusicService(MusicApiPrefs? prefs = null, HttpClient? http = null)
    {
        _http = http ?? CreateDefaultClient();
        _prefs = (prefs ?? new MusicApiPrefs()).Normalized();
        _provider = BuildProvider(_prefs);

        // ★ 构造时就必须还原凭证：上层通常是「new 一个服务然后直接用」，
        //   若只在 Reload 里还原，每次启动都得让用户重新扫一次码。
        ApplyCredential();
    }

    /// <summary>
    /// 统一的 HTTP 客户端。
    /// <para>UA 用桌面浏览器串并带上 Referer：部分音乐接口会对空 UA / 命令行 UA 返回空 body，
    /// 表现为「搜索永远零结果」，比直接报错难查得多。</para>
    /// </summary>
    private static HttpClient CreateDefaultClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        return c;
    }

    /// <summary>
    /// 当前「对话对象」的厂家。
    /// <para>「全部平台」是搜索范围而非厂家 —— 登录、我的歌单这类动作必须落在具体厂家上，
    /// 这里统一退回网易云（自建项目最成熟的一家）。</para>
    /// </summary>
    private MusicApiPlatform CurrentPlatform =>
        _prefs.Platform == MusicApiPlatform.All ? MusicApiPlatform.Netease : _prefs.Platform;

    private IOnlineMusicProvider BuildProvider(MusicApiPrefs prefs)
    {
        if (prefs.Kind == MusicApiKind.Meting)
            return new MetingMusicProvider(_http, prefs.BaseUrl, prefs.Platform);

        var platform = CurrentPlatform;
        var url = prefs.UrlFor(platform);
        var profile = VendorApiProfiles.For(platform);

        // ★ 没有可用自建项目的厂家（酷我 / 百度 / 虾米）：退回 Meting 聚合。
        //   宁可「免登录但能搜能播」，也不要给用户一个永远连不上的输入框。
        if (profile is null)
            return new MetingMusicProvider(_http, prefs.BaseUrl, platform);

        // 网易云走已验证的专用实现，其余厂家走通用实现
        return platform == MusicApiPlatform.Netease
            ? new NeteaseCloudMusicApiProvider(_http, url)
            : new CommunityApiProvider(_http, profile, url);
    }

    /// <summary>
    /// 按曲目所属厂家取数据源。
    /// <para><b>为什么不能一律用当前 provider</b>：聚合搜索出来的结果混着多家曲目，
    /// 而曲目 ID 只在自己厂家域内有效。拿网易云的 provider 去换酷狗曲目的直链，
    /// 必然换不出来 —— 用户看到的就是「搜索有结果，点播没声音」。</para>
    /// </summary>
    private IOnlineMusicProvider ProviderFor(OnlineTrack? track)
    {
        if (_prefs.Kind != MusicApiKind.Meting || track is null) return _provider;
        if (track.Platform == _prefs.Platform || track.Platform == MusicApiPlatform.All) return _provider;

        if (_perPlatform.TryGetValue(track.Platform, out var cached)) return cached;

        var created = new MetingMusicProvider(_http, _prefs.BaseUrl, track.Platform);
        _perPlatform[track.Platform] = created;
        return created;
    }

    // ---- 设置 / 数据源 ----

    /// <summary>当前生效的设置。</summary>
    public MusicApiPrefs Prefs => _prefs;

    /// <summary>当前数据源实现。</summary>
    public IOnlineMusicProvider Provider => _provider;

    /// <summary>数据源展示名（界面上标注「当前歌词来自 X」这类信息时用）。</summary>
    public string SourceName => _provider.Name;

    /// <summary>在线音源是否可用（开关打开且填了地址）。</summary>
    public bool IsAvailable => _prefs.Enabled && _prefs.HasBaseUrl;

    /// <summary>数据源是否支持登录。</summary>
    public bool CanLogin => _provider.SupportsLogin;

    /// <summary>当前厂家（Meting 模式下就是设置里那个搜索范围）。</summary>
    public MusicApiPlatform Platform => _prefs.Platform;

    /// <summary>当前厂家的自建 API 档案（Meting 模式或该厂家无自建项目时为 null）。</summary>
    public VendorApiProfile? VendorProfile =>
        _prefs.Kind == MusicApiKind.Meting ? null : VendorApiProfiles.For(CurrentPlatform);

    /// <summary>
    /// 厂家模式下是否真的能登录。
    /// <para>与 <see cref="CanLogin"/> 的区别：有些厂家（酷我 / 百度 / 虾米）没有可用的自建项目，
    /// 会退回到 Meting，此时「不能登录」是厂家本身决定的，而不是服务没配好 ——
    /// 界面要据此给出不同提示。</para>
    /// </summary>
    public bool VendorLoginSupported => VendorApiProfiles.CanLogin(CurrentPlatform);

    /// <summary>是否支持按关键词搜索公开歌单。</summary>
    public bool CanSearchPlaylists => _provider.SupportsPlaylistSearch;

    /// <summary>已登录（有凭证）。不代表凭证一定还有效 —— 失效会在请求时被识别。</summary>
    public bool IsLoggedIn => !string.IsNullOrEmpty(_cookie);

    /// <summary>登录账户信息（可能尚未拉取成功）。</summary>
    public OnlineAccount? Account => _account;

    /// <summary>登录态变化（供界面刷新「我的歌单」入口）。</summary>
    public event Action? LoginStateChanged;

    /// <summary>
    /// 用新设置重建数据源。
    /// <para>凭证会被<b>保留</b>：仅当协议换成不支持登录的（如切到 Meting）时丢弃 ——
    /// 切换协议不该让用户重新扫码。</para>
    /// </summary>
    public void Reload(MusicApiPrefs prefs)
    {
        _prefs = (prefs ?? new MusicApiPrefs()).Normalized();
        _perPlatform.Clear();       // 服务地址可能已变，按厂家缓存的 provider 必须一起作废
        _provider = BuildProvider(_prefs);
        ApplyCredential();

        LoginStateChanged?.Invoke();
    }

    /// <summary>
    /// 按「当前 provider 是否支持登录」决定凭证的去留。
    /// <para>支持登录：尝试还原（由于 AES 密钥取自本机，换机器会解不开 → 按未登录处理）。
    /// 不支持登录：把凭证与账户信息一并清掉 —— 留在内存里既用不上，还会让 UI 显示「已登录」这种误导状态。</para>
    /// </summary>
    private void ApplyCredential()
    {
        if (!_provider.SupportsLogin)
        {
            _cookie = "";
            _account = null;
            return;
        }

        // ★ 凭证按厂家分开保存：切厂家时读的是那一家自己的凭证，
        //   不会把酷狗的 token 当成网易云的登录态。
        var entry = _prefs.VendorFor(CurrentPlatform);
        _cookie = ApiCredentialProtector.TryUnprotect(entry.Credential) ?? "";

        // 昵称/头像先用落盘的展示值顶上：让界面立刻显示「已登录」，
        // 真实信息由 RefreshAccountAsync 联网补全（拉不到也只是显示退化）。
        _account = string.IsNullOrWhiteSpace(entry.AccountName) && string.IsNullOrWhiteSpace(entry.AccountAvatar)
            ? null
            : new OnlineAccount { Nickname = entry.AccountName, AvatarUrl = entry.AccountAvatar };
    }

    /// <summary>取合规质量（当前设置里的音质）。</summary>
    public MusicApiQuality Quality => _prefs.Quality;

    // ---- 曲目 / 歌单 ----

    public Task<IReadOnlyList<OnlineTrack>> SearchSongsAsync(string keyword, int limit = 30, CancellationToken ct = default)
        => _provider.SearchSongsAsync(keyword, limit, ct);

    public Task<IReadOnlyList<OnlinePlaylist>> SearchPlaylistsAsync(string keyword, int limit = 20, CancellationToken ct = default)
        => _provider.SupportsPlaylistSearch
            ? _provider.SearchPlaylistsAsync(keyword, limit, ct)
            : Task.FromResult<IReadOnlyList<OnlinePlaylist>>(Array.Empty<OnlinePlaylist>());

    public Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
        => _provider.GetPlaylistTracksAsync(playlistId, ct);

    /// <summary>
    /// 取直链。已登录时自动带上凭证（部分曲目只有登录才能取到完整长度）。
    /// <para>会按 <see cref="OnlineTrack.Platform"/> 回到对应厂家取，跨厂家不会串。</para>
    /// </summary>
    public Task<string?> GetPlayableUrlAsync(OnlineTrack track, CancellationToken ct = default)
        => ProviderFor(track).GetPlayableUrlAsync(track, _prefs.Quality, _cookie, ct);

    public Task<string?> GetLyricAsync(OnlineTrack track, CancellationToken ct = default)
        => ProviderFor(track).GetLyricAsync(track, ct);

    /// <summary>
    /// 取「我的歌单」。数据源不支持登录或尚未登录时返回空列表（不抛）。
    /// </summary>
    public Task<IReadOnlyList<OnlinePlaylist>> GetMyPlaylistsAsync(CancellationToken ct = default)
    {
        if (_provider is not IOnlineMusicLoginProvider login || !IsLoggedIn)
            return Task.FromResult<IReadOnlyList<OnlinePlaylist>>(Array.Empty<OnlinePlaylist>());
        return login.GetMyPlaylistsAsync(_cookie, ct);
    }

    // ---- 登录 ----

    /// <summary>生成二维码（数据源不支持登录时抛配置类异常）。</summary>
    public Task<QrLoginSession> CreateQrLoginAsync(CancellationToken ct = default)
    {
        if (_provider is not IOnlineMusicLoginProvider login)
            throw new MusicApiException("当前数据源不支持扫码登录，请在设置中切换为网易云 API",
                isConfigurationError: true);
        return login.CreateQrLoginAsync(ct);
    }

    /// <summary>轮询扫码状态。</summary>
    public Task<QrLoginPollResult> PollQrLoginAsync(string key, CancellationToken ct = default)
    {
        if (_provider is not IOnlineMusicLoginProvider login)
            throw new MusicApiException("当前数据源不支持扫码登录", isConfigurationError: true);
        return login.PollQrLoginAsync(key, ct);
    }

    /// <summary>
    /// 完成登录：保存凭证并尝试拉取账户信息。
    /// <para>账户信息拉不到也算登录成功 —— 凭证本身有效是最重要的，
    /// 头像/昵称失败只是显示退化。</para>
    /// </summary>
    /// <returns>成功返回 true。</returns>
    public async Task<bool> CompleteLoginAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return false;

        _cookie = cookie.Trim();

        if (_provider is IOnlineMusicLoginProvider login)
            _account = await login.GetAccountAsync(_cookie, ct).ConfigureAwait(false);

        LoginStateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 联网刷新账户信息（启动后用于补全昵称/头像，或验证凭证是否仍然有效）。
    /// <para>凭证已失效时返回 false，并把账户信息清空 —— 让界面回到未登录状态，
    /// 而不是一直显示一个已经失效的昵称。</para>
    /// </summary>
    public async Task<bool> RefreshAccountAsync(CancellationToken ct = default)
    {
        if (_provider is not IOnlineMusicLoginProvider login || !IsLoggedIn) return false;

        try
        {
            var acc = await login.GetAccountAsync(_cookie, ct).ConfigureAwait(false);
            if (acc is null)
            {
                // 凭证失效：清空但**不**清理磁盘上的字符串 —— 用户可以选择换回有效凭证或重新扫码
                _account = null;
                LoginStateChanged?.Invoke();
                return false;
            }
            _account = acc;
            LoginStateChanged?.Invoke();
            return true;
        }
        catch (MusicApiException) { return false; }
    }

    /// <summary>退出登录（清内存凭证与当前厂家落盘的凭证）。</summary>
    public void Logout()
    {
        _cookie = "";
        _account = null;

        var entry = _prefs.VendorFor(CurrentPlatform);
        entry.Credential = "";
        entry.AccountName = "";
        entry.AccountAvatar = "";

        LoginStateChanged?.Invoke();
    }

    /// <summary>
    /// 导出当前厂家登录态以便持久化。
    /// <para>返回的 <c>Credential</c> 已混淆；未登录时返回 null。</para>
    /// </summary>
    public StoredCredential? ExportCredential()
    {
        if (!IsLoggedIn) return null;

        var name = _account?.Nickname ?? _prefs.AccountName ?? "";
        var avatar = _account?.AvatarUrl ?? _prefs.AccountAvatar ?? "";

        var entry = _prefs.VendorFor(CurrentPlatform);
        entry.Credential = ApiCredentialProtector.Protect(_cookie);
        entry.AccountName = name;
        entry.AccountAvatar = avatar;

        return new StoredCredential(entry.Credential, name, avatar);
    }

    /// <summary>待持久化的登录态。</summary>
    public sealed record StoredCredential(string Credential, string Nickname, string AvatarUrl);
}
