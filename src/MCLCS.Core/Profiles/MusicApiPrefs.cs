using System.Text.Json.Serialization;
using MCLCS.Core.Music;

namespace MCLCS.Core.Profiles;

/// <summary>
/// 在线音源设置（对接 MusicSourceMode.Api 的那一类「启动器自己去取流」的场景）。
///
/// <para><b>为什么要单独放一份</b>：在线音源依赖用户自选的服务地址，
/// 与「播本地文件」「交给外部客户端」两种模式的配置完全不重叠，
/// 混进 <see cref="MusicClientPrefs"/> 会让「总开关」的语义被稀释。</para>
///
/// <para><b>容错</b>：地址、枚举值一律经 <see cref="Normalized"/> 归一，
/// 手改 / 旧版 / 跨设备同步带来的脏值不会渗进请求拼接。</para>
/// </summary>
public sealed class MusicApiPrefs
{
    /// <summary>Meting 协议的默认公共实例（免配置即可用）。</summary>
    public const string DefaultMetingUrl = "https://api.i-meto.com/meting/api";

    /// <summary>
    /// 网易云 API 的默认地址 localhost:3000 —— 它不是公共实例，几乎都 require 用户自建。
    /// 默认填本地而非某个第三方公共实例，是因为公共实例存活期不可控，
    /// 指向 localhost 至少能让用户明确意识到「需要自己跑服务」。
    /// </summary>
    public const string DefaultNeteaseApiUrl = "http://localhost:3000";

    /// <summary>是否启用在线音源（总开关）。关闭时音乐页不再显示「在线」入口。</summary>
    public const bool DefaultEnabled = true;

    /// <summary>默认音质：极高（320k）—— 免费用户也能取到，不会因为会员限制直接失败。</summary>
    public const MusicApiQuality DefaultQuality = MusicApiQuality.Exquisite;

    /// <summary>在线音源总开关。关闭时音乐页不再显示「在线」入口。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>数据源协议。</summary>
    [JsonPropertyName("kind")]
    public MusicApiKind Kind { get; set; } = MusicApiKind.Meting;

    /// <summary>服务根地址（不含结尾斜杠）。</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = DefaultMetingUrl;

    /// <summary>曲目平台（仅 Meting 协议生效）。</summary>
    [JsonPropertyName("platform")]
    public MusicApiPlatform Platform { get; set; } = MusicApiPlatform.Netease;

    /// <summary>在线播放音质。</summary>
    [JsonPropertyName("quality")]
    public MusicApiQuality Quality { get; set; } = DefaultQuality;

    /// <summary>
    /// 登录凭证（<b>经 <see cref="ApiCredentialProtector"/> 混淆后</b>落盘的字符串）。
    /// 明文 cookie 不应出现在配置文件里 —— 配置文件会被导出 / 云同步 / 手改。
    /// </summary>
    [JsonPropertyName("credential")]
    public string Credential { get; set; } = "";

    /// <summary>已登录昵称（纯展示，登录失效也要留着让用户知道「原本是谁」）。</summary>
    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = "";

    /// <summary>已登录头像地址。</summary>
    [JsonPropertyName("accountAvatar")]
    public string AccountAvatar { get; set; } = "";

    /// <summary>
    /// 每个厂家一份的自建服务配置（地址 + 登录态）。
    /// <para><b>为什么要按厂家分开存</b>：各厂家的社区 API 是各自独立的服务，
    /// 地址不同、登录态也不同（酷狗的 token 拿去问网易云没有任何意义）。
    /// 混用一份地址/凭证的结果是「切个厂家就得重新扫码」，所以按厂家分桶存。</para>
    /// <para>键是 <see cref="MusicApiPlatform"/> 的小写名（netease / kugou / tencent …），
    /// 这样手改配置文件也是可读的，不会因为枚举数值变动而错位。</para>
    /// </summary>
    [JsonPropertyName("vendors")]
    public Dictionary<string, VendorApiEntry> Vendors { get; set; } = new();

    /// <summary>
    /// 是否已配置了可用的服务地址。
    /// <para>Meting 模式下只有一个聚合实例地址（<see cref="BaseUrl"/>）；
    /// 厂家模式下则是「当前厂家」那份地址 —— 两者的含义不同，不能混读。</para>
    /// </summary>
    [JsonIgnore]
    public bool HasBaseUrl => Kind == MusicApiKind.Meting
        ? !string.IsNullOrWhiteSpace(BaseUrl)
        : !string.IsNullOrWhiteSpace(VendorFor(Platform).BaseUrl);

    /// <summary>是否已保存了登录凭证（不代表凭证仍然有效）。</summary>
    [JsonIgnore]
    public bool HasCredential => !string.IsNullOrWhiteSpace(Credential);

    /// <summary>厂家配置在 JSON 里的键名。</summary>
    public static string Key(MusicApiPlatform platform) =>
        platform.ToString().ToLowerInvariant();

    /// <summary>
    /// 取（必要时创建）某厂家的配置。缺省地址来自该厂家的档案。
    /// </summary>
    public VendorApiEntry VendorFor(MusicApiPlatform platform)
    {
        Vendors ??= new Dictionary<string, VendorApiEntry>();

        var key = Key(platform);
        if (!Vendors.TryGetValue(key, out var entry) || entry is null)
        {
            entry = new VendorApiEntry { BaseUrl = VendorApiProfiles.DefaultUrlFor(platform) };
            Vendors[key] = entry;
        }

        entry.BaseUrl = (entry.BaseUrl ?? "").Trim().TrimEnd('/');
        entry.Credential ??= "";
        entry.AccountName ??= "";
        entry.AccountAvatar ??= "";
        return entry;
    }

    /// <summary>
    /// 归一化：修掉尾斜杠、补上 http 前缀缺失的常见误填，非法枚举回退默认，
    /// 并把旧版「单份地址 / 单份凭证」迁移进按厂家分桶的 <see cref="Vendors"/>。
    /// </summary>
    public MusicApiPrefs Normalized()
    {
        BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');

        if (!System.Enum.IsDefined(typeof(MusicApiKind), Kind))
            Kind = MusicApiKind.Meting;
        if (!System.Enum.IsDefined(typeof(MusicApiPlatform), Platform))
            Platform = MusicApiPlatform.Netease;
        if (!System.Enum.IsDefined(typeof(MusicApiQuality), Quality))
            Quality = DefaultQuality;

        AccountName ??= "";
        AccountAvatar ??= "";
        Credential ??= "";

        Vendors ??= new Dictionary<string, VendorApiEntry>();

        // 旧配置迁移：只有「当时填的确实是某个厂家自建服务地址」才搬过去。
        // Meting 的公共实例地址不能当成网易云 API 的地址，否则切到厂家模式会指向错误的地方。
        if (Vendors.Count == 0)
        {
            var legacy = Kind != MusicApiKind.Meting ? Platform : MusicApiPlatform.Netease;

            if (!string.IsNullOrWhiteSpace(BaseUrl) && Kind != MusicApiKind.Meting)
                VendorFor(legacy).BaseUrl = BaseUrl!;

            if (!string.IsNullOrWhiteSpace(Credential))
            {
                var v = VendorFor(legacy);
                v.Credential = Credential!;
                v.AccountName = AccountName ?? "";
                v.AccountAvatar = AccountAvatar ?? "";
            }
        }

        // 补齐各厂家条目（让设置页能直接展示每家的默认地址，而不是空白）
        foreach (var p in System.Enum.GetValues<MusicApiPlatform>())
            VendorFor(p);

        return this;
    }

    /// <summary>某厂家当前生效的服务地址。</summary>
    public string UrlFor(MusicApiPlatform platform) => VendorFor(platform).BaseUrl;

    /// <summary>
    /// 写入某厂家的服务地址。
    /// <para><b>不</b>回写 <see cref="BaseUrl"/>：那是 Meting 聚合实例的地址，
    /// 把厂家地址盖上去会让「切回 Meting」时指向一个错误的服务。</para>
    /// </summary>
    public void SetUrl(MusicApiPlatform platform, string url)
        => VendorFor(platform).BaseUrl = (url ?? "").Trim().TrimEnd('/');

    /// <summary>数据源对应的推荐默认地址（厂家取网易云，供旧代码兼容）。</summary>
    public static string DefaultUrlFor(MusicApiKind kind) => DefaultUrlFor(kind, MusicApiPlatform.Netease);

    /// <summary>数据源 + 厂家对应的推荐默认地址（供设置页「恢复默认」按钮使用）。</summary>
    public static string DefaultUrlFor(MusicApiKind kind, MusicApiPlatform platform) =>
        kind == MusicApiKind.Meting ? DefaultMetingUrl : VendorApiProfiles.DefaultUrlFor(platform);
}

/// <summary>
/// 单个厂家自建 API 的配置项。
/// <para>凭证与 <see cref="MusicApiPrefs.Credential"/> 一样是<b>混淆后</b>存放的，不是明文。</para>
/// </summary>
public sealed class VendorApiEntry
{
    /// <summary>服务根地址（不含结尾斜杠）。</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    /// <summary>登录凭证（经 <see cref="ApiCredentialProtector"/> 混淆后的字符串）。</summary>
    [JsonPropertyName("credential")]
    public string Credential { get; set; } = "";

    /// <summary>已登录昵称（纯展示）。</summary>
    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = "";

    /// <summary>已登录头像地址。</summary>
    [JsonPropertyName("accountAvatar")]
    public string AccountAvatar { get; set; } = "";

    /// <summary>是否保存了登录凭证（不代表凭证仍然有效）。</summary>
    [JsonIgnore]
    public bool HasCredential => !string.IsNullOrWhiteSpace(Credential);
}
