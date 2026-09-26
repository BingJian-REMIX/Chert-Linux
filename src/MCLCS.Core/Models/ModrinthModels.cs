using System.Text.Json.Serialization;

namespace MCLCS.Core.Models;

/// <summary>Modrinth 搜索结果（/v2/search）。</summary>
public class ModrinthSearchResult
{
    [JsonPropertyName("hits")]
    public List<ModrinthHit> Hits { get; set; } = new();

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; set; }
}

public class ModrinthHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = ""; // mod | shader | resourcepack

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("display_categories")]
    public List<string> DisplayCategories { get; set; } = new();

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; } = "";

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = new();

    [JsonPropertyName("follows")]
    public int Follows { get; set; }
}

/// <summary>Modrinth 项目版本（/v2/project/{id}/version）。</summary>
public class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new(); // fabric | forge | quilt

    [JsonPropertyName("files")]
    public List<ModrinthFile> Files { get; set; } = new();
}

public class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hashes")]
    public ModrinthHashes Hashes { get; set; } = new();
}

public class ModrinthHashes
{
    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = "";

    [JsonPropertyName("sha512")]
    public string Sha512 { get; set; } = "";
}

/// <summary>
/// 详情页版本下拉的可选项：版本信息 + 该版本可直接下载的主文件。
/// 此前 mod / 光影 / 资源包点详情直接跳转浏览器，没有页内详情也无法选择版本。
/// </summary>
public class ProjectVersionChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string GameVersionSummary { get; init; } = "";
    public string LoaderSummary { get; init; } = "";
    public string FileUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FileSha1 { get; init; } = "";

    /// <summary>下拉显示文本：版本号 + 名称 + 支持的游戏版本。</summary>
    public string DisplayText =>
        string.IsNullOrEmpty(Name) || Name == VersionNumber
            ? $"{VersionNumber}（{GameVersionSummary}）"
            : $"{VersionNumber} · {Name}（{GameVersionSummary}）";
}

/// <summary>加载器类型枚举（用于筛选）。</summary>
public enum LoaderType
{
    Any,
    Fabric,
    Forge,
    Quilt,
    NeoForge
}

/// <summary>资源类型枚举。</summary>
public enum ModrinthProjectType
{
    Mod,
    Shader,
    ResourcePack,
    Modpack
}

/// <summary>Modrinth 项目详情（/v2/project/{id}）。</summary>
public class ModrinthProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("project_type")]
    public string ProjectType { get; set; } = "";

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = new();

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = new();

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; } = "";

    [JsonPropertyName("gallery")]
    public List<ModrinthGalleryItem> Gallery { get; set; } = new();
}

public class ModrinthGalleryItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("featured")]
    public bool Featured { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

/// <summary>Modrinth 依赖信息（/v2/version/{id} 的 dependencies 字段）。</summary>
public class ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; set; } = ""; // required | optional | incompatible | embedded
}

/// <summary>Modrinth 整合包索引（modrinth.index.json）。</summary>
public class ModrinthPackIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; }

    [JsonPropertyName("game")]
    public string Game { get; set; } = "minecraft";

    [JsonPropertyName("versionId")]
    public string VersionId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    [JsonPropertyName("files")]
    public List<ModrinthPackFile> Files { get; set; } = new();
}

public class ModrinthPackFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("env")]
    public ModrinthPackEnv Env { get; set; } = new();

    [JsonPropertyName("hashes")]
    public ModrinthHashes Hashes { get; set; } = new();

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; set; } = new();

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}

public class ModrinthPackEnv
{
    [JsonPropertyName("client")]
    public string Client { get; set; } = "required"; // required | optional | unsupported

    [JsonPropertyName("server")]
    public string Server { get; set; } = "unsupported";
}
