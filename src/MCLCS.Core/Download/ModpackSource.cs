using MCLCS.Core.Models;

namespace MCLCS.Core.Download;

/// <summary>整合包搜索结果条目（源无关的统一视图）。</summary>
public class ModpackItem
{
    /// <summary>源标识：modrinth。</summary>
    public string Source { get; init; } = "";

    /// <summary>源内唯一 Id（Modrinth 为 project_id）。</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string? IconUrl { get; init; }

    public int Downloads { get; init; }

    /// <summary>支持的游戏版本（新→旧，最多保留若干个）。</summary>
    public List<string> GameVersions { get; init; } = new();

    /// <summary>加载器标签（fabric / forge / quilt / neoforge）。</summary>
    public List<string> Loaders { get; init; } = new();

    /// <summary>下载量友好文本。</summary>
    public string DownloadsText => Downloads switch
    {
        <= 0 => "",
        < 1000 => $"{Downloads} 次下载",
        < 1000000 => $"{Downloads / 1000.0:0.#}K 次下载",
        _ => $"{Downloads / 1000000.0:0.#}M 次下载"
    };

    /// <summary>最新支持版本（列表首项）。</summary>
    public string LatestGameVersion => GameVersions.Count > 0 ? GameVersions[0] : "";

    /// <summary>加载器摘要（首字母大写，逗号分隔）。</summary>
    public string LoaderSummary => Loaders.Count == 0
        ? "未标注"
        : string.Join(" / ", Loaders.Select(Capitalize));

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>整合包的一个可安装版本。</summary>
public class ModpackVersion
{
    /// <summary>源内版本 Id（Modrinth 为 version id；CurseForge 为 fileId）。</summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// 所属项目在源内的 Id（CurseForge 需用它 + <see cref="Id"/> 才能换到下载直链；
    /// Modrinth 的直链已内嵌在 <see cref="FileUrl"/> 里，此字段可为空）。
    /// </summary>
    public string ProjectId { get; init; } = "";

    /// <summary>展示名（如 "1.20.1-v3.2"）。</summary>
    public string Name { get; init; } = "";

    /// <summary>版本号。</summary>
    public string VersionNumber { get; init; } = "";

    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";

    /// <summary>整合包文件直链（.mrpack / .zip）。CurseForge 源留空，安装时按 ProjectId + Id 现解析。</summary>
    public string FileUrl { get; init; } = "";

    public string FileName { get; init; } = "";
    public string? Sha1 { get; init; }
    public long FileSize { get; init; }

    /// <summary>文件大小友好文本。</summary>
    public string SizeText => FileSize switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{FileSize / 1024.0:0.#} KB",
        _ => $"{FileSize / 1024.0 / 1024.0:0.#} MB"
    };

    /// <summary>下拉框展示文本。</summary>
    public string DisplayText
    {
        get
        {
            var loader = string.IsNullOrEmpty(Loader) ? "" : $" · {Loader}";
            var size = string.IsNullOrEmpty(SizeText) ? "" : $" · {SizeText}";
            var label = string.IsNullOrWhiteSpace(VersionNumber) ? Name : VersionNumber;
            return $"{label}（MC {GameVersion}{loader}{size}）";
        }
    }
}

/// <summary>整合包详情。</summary>
public class ModpackDetail
{
    public string Source { get; init; } = "";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string? IconUrl { get; init; }

    /// <summary>正文纯文本（Markdown 已转换）。</summary>
    public string Description { get; init; } = "";

    public int Downloads { get; init; }

    /// <summary>可安装版本，新→旧。</summary>
    public List<ModpackVersion> Versions { get; init; } = new();

    /// <summary>站内页面地址。</summary>
    public string? PageUrl { get; init; }

    /// <summary>
    /// 作者是否允许第三方分发（CurseForge 的合规底线）。
    /// 为 false 时不得自动下载，界面需提示用户前往官网手动获取；Modrinth 恒为 true。
    /// </summary>
    public bool DistributionAllowed { get; init; } = true;

    public bool HasVersions => Versions.Count > 0;

    public string DownloadsText => Downloads switch
    {
        <= 0 => "",
        < 1000 => $"{Downloads}",
        < 1000000 => $"{Downloads / 1000.0:0.#}K",
        _ => $"{Downloads / 1000000.0:0.#}M"
    };
}

/// <summary>
/// 整合包在线源抽象（规格 2.2 → 整合包：在线浏览，一键安装）。
/// 内置 Modrinth（免 Key 常驻可用）与 CurseForge（需 API Key，未配置时不可用）。
/// </summary>
public interface IModpackSource
{
    /// <summary>源标识：modrinth。</summary>
    string Id { get; }

    /// <summary>界面展示名。</summary>
    string DisplayName { get; }

    /// <summary>当前是否可用（Modrinth 常驻可用）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用原因（界面提示用）；可用时为 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>搜索整合包。失败返回空列表而不抛异常。</summary>
    Task<List<ModpackItem>> SearchAsync(string? keyword, string? gameVersion, string? loader,
        int limit = 24, int offset = 0, CancellationToken ct = default);

    /// <summary>获取详情（含可安装版本列表）。失败返回 null。</summary>
    Task<ModpackDetail?> GetDetailAsync(string id, CancellationToken ct = default);
}
