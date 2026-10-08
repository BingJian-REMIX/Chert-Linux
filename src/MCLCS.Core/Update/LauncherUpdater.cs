using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Update;

/// <summary>更新检查结果。</summary>
public class UpdateCheckResult
{
    public bool Available { get; set; }
    public string? CurrentVersion { get; set; }
    public string? LatestVersion { get; set; }
    /// <summary>新版本更新日志（发布说明），来自 latest.json 的 changelog 字段；为空时弹窗回退为「前往发布页」。</summary>
    public string? Changelog { get; set; }
    /// <summary>下载入口（latest.json 的 downloadUrl，缺省按 CNB Release v{版本} 格式构造）。</summary>
    public string? DownloadUrl { get; set; }
    /// <summary>singlefile 包是否已在 CNB Release 发布（latest.json 的 singleFileAvailable 字段）。</summary>
    public bool SingleFileAvailable { get; set; }
    public bool Mandatory { get; set; }
    /// <summary>更新状态（latest.json 的 status 字段）："false"=冻结/撤回（即便有新版本也强制不提示，坏版本止血用）、"true"=普通可选更新、"emgent"=紧急更新（强制提示并置必更 Mandatory）。缺省/未知值等同 "true"。</summary>
    public string? Status { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 启动器自动更新（全局功能 13）。
/// 更新源为 GitHub Pages 托管的静态 <c>latest.json</c>（<see cref="GameConstants.UpdateInfoUrl"/>，GitHub Pages 走独立 CDN、通常不受 github.com 故障影响）。
/// latest.json 为 WPF / Linux / Android 三端共用的多平台结构（根为 <c>wpf</c>/<c>linux</c>/<c>android</c> 小节），本启动器仅读取与 <see cref="GameConstants.PlatformId"/> 同名的 <c>linux</c> 小节：
/// 普通 HTTPS GET 即可读取，终端用户零 git 依赖、不写临时仓库、无头客户端可达。
/// 网络不可用 / JSON 解析失败时安全返回「无更新」（带 Error），绝不误报。
/// 下载由 UI 层调用内置 <c>HttpDownloader</c> 直接拉取 latest.json 中的 CNB Release 下载直链，不依赖 winget / 浏览器。
/// </summary>
public static class LauncherUpdater
{
    /// <summary>比对两个版本号字符串（如 "0.5.0" 与 "1.0.0"），返回 latest &gt; current 的结果。</summary>
    public static bool IsNewer(string current, string latest)
    {
        var a = Parse(current);
        var b = Parse(latest);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (y > x) return true;
            if (y < x) return false;
        }
        return false;
    }

    private static List<int> Parse(string v)
    {
        var list = new List<int>();
        foreach (var part in v.Split('.'))
            if (int.TryParse(new string(part.Where(char.IsDigit).ToArray()), out var n))
                list.Add(n);
        return list;
    }

    /// <summary>将 latest.json 的下载地址归一到 https 直链：仅当为 http(s) 绝对地址时才采用，否则（ssh://、git@、相对路径等非 https 形式）一律丢弃并改用 fallback 兜底的 CNB Release https 直链。落实「拉更新直接用 http(s) 直链」—永不依赖 ssh/git 协议拉取更新包。</summary>
    private static string? ResolveHttps(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url)) return fallback;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri is not null
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // 一律归一到 https（http 也升 https）
            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
            return builder.Uri.ToString();
        }
        // 非 http(s) 绝对地址：丢弃，回退 CNB Release https 直链
        return fallback;
    }

    /// <summary>归一化 latest.json 的 status 字段为内部取值：false / true / emgent（空或未知值返回空串，按 "true" 处理）。大小写不敏感，emgent/emergency 等价。</summary>
    private static string NormalizeStatus(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "false" => "false",
            "true" => "true",
            "emgent" or "emergency" => "emgent",
            _ => ""
        };
    }

    /// <summary>检查更新；异常 / 解析失败时返回 Available=false（带 Error）。</summary>
    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, HttpClient? client = null)
    {
        var result = new UpdateCheckResult { CurrentVersion = currentVersion };
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var json = await client.GetStringAsync(GameConstants.UpdateInfoUrl);
            UpdateInfo? info;
            try
            {
                using var doc = JsonDocument.Parse(json);
                // MCLCS-upgrade/latest.json 为三端共用的多平台结构：根为 { wpf:{...}, linux:{...}, android:{...} }。
                // 本启动器只读取与 PlatformId 同名的平台小节；若根为旧的单平台对象则整体解析（向后兼容）。
                var root = doc.RootElement;
                var section = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(GameConstants.PlatformId, out var s)
                    && s.ValueKind == JsonValueKind.Object
                        ? s : root;
                info = section.Deserialize<UpdateInfo>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                result.Error = $"更新信息解析失败：{ex.Message}";
                return result;
            }

            if (info is null || string.IsNullOrWhiteSpace(info.Version))
            {
                result.Error = "更新信息为空或缺少 version 字段";
                return result;
            }

            result.LatestVersion = info.Version;
            // 仅当最新版本严格大于当前版本才提示更新（当前==最新不会误报「更新到自身」）。
            result.Available = IsNewer(currentVersion, info.Version);
            result.Status = NormalizeStatus(info.Status);

            // status 字段可覆盖版本比较的结果，表达「更新紧急度 / 是否撤回」：
            //  - "false"：冻结/撤回，即便有新版本也强制不提示（坏版本止血用）。
            //  - "emgent"：紧急更新，强制提示并置为必更（Mandatory=true）。
            //  - "true"/缺省：尊重 IsNewer 的常规可选更新。
            if (result.Status == "false")
                result.Available = false;
            else if (result.Status == "emgent" && result.Available)
                result.Mandatory = true;

            if (result.Available)
            {
                result.Changelog = info.Changelog;
                result.DownloadUrl = ResolveHttps(info.DownloadUrl,
                    $"{GameConstants.CnbReleaseBase}/-/releases/download/v{info.Version}/MCLCS-{info.Version}-Linux-x64.zip");
                result.SingleFileAvailable = info.SingleFileAvailable;
                result.Mandatory = result.Mandatory || info.Mandatory;
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            if (own) client.Dispose();
        }
        return result;
    }

    /// <summary>GitHub Pages 上 latest.json 的字段映射（大小写不敏感）。</summary>
    private sealed class UpdateInfo
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("mandatory")] public bool Mandatory { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("singleFileAvailable")] public bool SingleFileAvailable { get; set; }
        [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    }
}
