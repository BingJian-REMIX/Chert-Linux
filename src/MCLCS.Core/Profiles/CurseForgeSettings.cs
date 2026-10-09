using System.Text.Json.Serialization;

namespace MCLCS.Core.Profiles;

/// <summary>
/// CurseForge 接入配置（设置 → 下载）。
/// <para>
/// 内置 Key 走构建时注入（见 <see cref="MCLCS.Core.Download.CurseForgeConfig"/>），
/// 这里的 <see cref="ApiKey"/> 是<b>用户自定义覆盖</b>，留空则使用内置 Key。
/// </para>
/// </summary>
public class CurseForgeSettings
{
    /// <summary>总开关。关闭后 CurseForge 源在下载中心不可选，其它源不受影响。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>用户自定义 API Key（留空 = 用内置 Key）。</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    /// <summary>API Root 覆盖（留空 = 官方 https://api.curseforge.com；可填第三方镜像）。</summary>
    [JsonPropertyName("apiRoot")]
    public string ApiRoot { get; set; } = "";

    /// <summary>下载 Mod 时是否用指纹匹配已安装文件（预留，暂未启用）。</summary>
    [JsonPropertyName("fingerprintMatch")]
    public bool FingerprintMatch { get; set; } = true;
}
