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

    /// <summary>是否已配置了可用的服务地址。</summary>
    [JsonIgnore]
    public bool HasBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>是否已保存了登录凭证（不代表凭证仍然有效）。</summary>
    [JsonIgnore]
    public bool HasCredential => !string.IsNullOrWhiteSpace(Credential);

    /// <summary>
    /// 归一化：修掉尾斜杠、补上 http 前缀缺失的常见误填，非法枚举回退默认。
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

        return this;
    }

    /// <summary>某协议的推荐默认地址（供设置页「恢复默认」按钮使用）。</summary>
    public static string DefaultUrlFor(MusicApiKind kind) =>
        kind == MusicApiKind.NeteaseApi ? DefaultNeteaseApiUrl : DefaultMetingUrl;
}
