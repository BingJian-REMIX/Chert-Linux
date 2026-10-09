using System.Text.Json.Serialization;

namespace MCLCS.Core.Models;

/// <summary>
/// CurseForge API v1 数据模型。
/// 所有列表接口统一返回 <c>{ "data": [...] }</c>，详情接口返回 <c>{ "data": {...} }</c>。
/// 字段名与官方 JSON 一一对应，未使用到的字段不声明（反序列化时忽略）。
/// </summary>
public class CurseForgeListResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();

    [JsonPropertyName("pagination")]
    public CurseForgePagination? Pagination { get; set; }
}

/// <summary>单体响应包装（详情 / 下载直链等）。</summary>
public class CurseForgeSingleResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>分页信息。</summary>
public class CurseForgePagination
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("resultCount")] public int ResultCount { get; set; }
    [JsonPropertyName("totalCount")] public long TotalCount { get; set; }
}

/// <summary>CurseForge 项目（Mod / 整合包 / 资源包 / 地图共用）。</summary>
public class CurseForgeMod
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("gameId")] public int GameId { get; set; }
    [JsonPropertyName("classId")] public int? ClassId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
    [JsonPropertyName("dateModified")] public DateTimeOffset? DateModified { get; set; }
    [JsonPropertyName("logo")] public CurseForgeLogo? Logo { get; set; }
    [JsonPropertyName("links")] public CurseForgeLinks? Links { get; set; }
    [JsonPropertyName("authors")] public List<CurseForgeAuthor> Authors { get; set; } = new();
    [JsonPropertyName("categories")] public List<CurseForgeCategory> Categories { get; set; } = new();
    [JsonPropertyName("latestFiles")] public List<CurseForgeFile> LatestFiles { get; set; } = new();
    [JsonPropertyName("latestFilesIndexes")] public List<CurseForgeFileIndex> LatestFilesIndexes { get; set; } = new();

    /// <summary>
    /// 作者是否允许第三方分发（合规底线）。
    /// <c>false</c> 表示作者关闭了「允许第三方分发」，任何第三方客户端都不得再下载其文件，
    /// 必须拒绝自动下载并引导用户前往官网手动获取。null 表示官方未返回该字段（按未知处理）。
    /// </summary>
    [JsonPropertyName("allowModDistribution")]
    public bool? AllowModDistribution { get; set; }
}

/// <summary>项目图标。</summary>
public class CurseForgeLogo
{
    [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>站内链接。</summary>
public class CurseForgeLinks
{
    [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
    [JsonPropertyName("sourceUrl")] public string? SourceUrl { get; set; }
    [JsonPropertyName("issuesUrl")] public string? IssuesUrl { get; set; }
}

/// <summary>作者。</summary>
public class CurseForgeAuthor
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>分类。</summary>
public class CurseForgeCategory
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
}

/// <summary>搜索结果里的「最新文件索引」（不含完整文件对象）。</summary>
public class CurseForgeFileIndex
{
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("fileId")] public int FileId { get; set; }
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("releaseType")] public int ReleaseType { get; set; }
    [JsonPropertyName("gameVersionTypeId")] public int? GameVersionTypeId { get; set; }
    [JsonPropertyName("modLoader")] public int? ModLoader { get; set; }
}

/// <summary>CurseForge 文件。</summary>
public class CurseForgeFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("modId")] public int ModId { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";

    /// <summary>发布类型：1=正式 2=测试 3=抢先。</summary>
    [JsonPropertyName("releaseType")] public int ReleaseType { get; set; }

    /// <summary>
    /// 文件状态：1=正常 2=半成品 3=审核中 4=已删除 5=已隔离 6=已归档。
    /// <para>
    /// ⚠️ 实测官网会把<b>可正常下载</b>的文件也标成 4，因此<b>不要</b>用它判断可用性——
    /// 请改用 <see cref="IsAvailable"/>。
    /// </para>
    /// </summary>
    [JsonPropertyName("fileStatus")] public int FileStatus { get; set; }

    [JsonPropertyName("fileDate")] public DateTimeOffset? FileDate { get; set; }
    [JsonPropertyName("fileLength")] public long FileLength { get; set; }
    [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }

    /// <summary>下载直链。作者关闭第三方分发时为 null，需改走 /download-url 接口。</summary>
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }

    /// <summary>支持的游戏版本 + 加载器标签（如 "1.20.1" / "Forge" / "Fabric"）。</summary>
    [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("dependencies")] public List<CurseForgeFileDependency> Dependencies { get; set; } = new();
    [JsonPropertyName("hashes")] public List<CurseForgeHash> Hashes { get; set; } = new();

    [JsonPropertyName("isAvailable")] public bool IsAvailable { get; set; } = true;
    [JsonPropertyName("isServerPack")] public bool? IsServerPack { get; set; }

    /// <summary>取 SHA-1 校验值（algo=1）；无则返回 null。</summary>
    public string? Sha1 => Hashes.FirstOrDefault(h => h.Algo == 1)?.Value;

    /// <summary>取 MD5 校验值（algo=2）；无则返回 null。</summary>
    public string? Md5 => Hashes.FirstOrDefault(h => h.Algo == 2)?.Value;

    /// <summary>发布类型文案。</summary>
    public string ReleaseTypeText => ReleaseType switch
    {
        1 => "正式版",
        2 => "测试版",
        3 => "抢先版",
        _ => ""
    };
}

/// <summary>文件哈希（algo：1=sha1，2=md5）。</summary>
public class CurseForgeHash
{
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("algo")] public int Algo { get; set; }
}

/// <summary>文件依赖（relationType：1=内嵌库 2=可选 3=必需 4=工具 5=不兼容 6=包含）。</summary>
public class CurseForgeFileDependency
{
    [JsonPropertyName("modId")] public int ModId { get; set; }
    [JsonPropertyName("relationType")] public int RelationType { get; set; }
}

/// <summary>指纹匹配结果（POST /v1/mods/fingerprint）。</summary>
public class CurseForgeFingerprintResponse
{
    [JsonPropertyName("data")] public CurseForgeFingerprintMatches? Data { get; set; }
}

/// <summary>指纹匹配明细。</summary>
public class CurseForgeFingerprintMatches
{
    /// <summary>
    /// 官方指纹缓存是否已构建完成。
    /// <b>首次调用常为 false 且不返回任何匹配</b>，需稍后重试一次才会命中（实测行为）。
    /// </summary>
    [JsonPropertyName("isCacheBuilt")] public bool IsCacheBuilt { get; set; } = true;

    /// <summary>精确匹配到的文件（含所属项目信息）。</summary>
    [JsonPropertyName("exactMatches")] public List<CurseForgeFingerprintMatch> ExactMatches { get; set; } = new();

    /// <summary>已匹配到的指纹值。</summary>
    [JsonPropertyName("exactFingerprints")] public List<long> ExactFingerprints { get; set; } = new();

    /// <summary>模糊匹配（同名不同版本），本次不使用。</summary>
    [JsonPropertyName("fuzzyMatches")] public List<CurseForgeFingerprintMatch> FuzzyMatches { get; set; } = new();

    /// <summary>未匹配到的指纹。</summary>
    [JsonPropertyName("unmatchedFingerprints")] public List<long> UnmatchedFingerprints { get; set; } = new();
}

/// <summary>一条指纹匹配记录。</summary>
public class CurseForgeFingerprintMatch
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("file")] public CurseForgeFile? File { get; set; }
}

/// <summary>
/// CurseForge 整合包 <c>manifest.json</c>（zip 根目录）。
/// 与 Modrinth 的 <c>modrinth.index.json</c> 结构不同：文件以 projectID/fileID 引用，需再查 API 换直链。
/// </summary>
public class CurseForgePackManifest
{
    [JsonPropertyName("minecraft")] public CurseForgePackMinecraft? Minecraft { get; set; }
    [JsonPropertyName("manifestType")] public string ManifestType { get; set; } = "";
    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>覆盖文件目录名（默认 <c>overrides</c>）。</summary>
    [JsonPropertyName("overrides")] public string Overrides { get; set; } = "overrides";

    [JsonPropertyName("files")] public List<CurseForgePackFile> Files { get; set; } = new();
}

/// <summary>整合包声明的 MC 版本与加载器。</summary>
public class CurseForgePackMinecraft
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("modLoaders")] public List<CurseForgePackLoader> ModLoaders { get; set; } = new();
}

/// <summary>整合包声明的加载器（id 形如 <c>forge-47.2.0</c> / <c>fabric-0.15.0</c> / <c>neoforge-20.4.0</c>）。</summary>
public class CurseForgePackLoader
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

/// <summary>整合包内一个待下载文件（只给 ID，直链要再查 API）。</summary>
public class CurseForgePackFile
{
    [JsonPropertyName("projectID")] public int ProjectId { get; set; }
    [JsonPropertyName("fileID")] public int FileId { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}
