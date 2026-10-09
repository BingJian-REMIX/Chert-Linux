using System.Text.RegularExpressions;
using MCLCS.Core.Models;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Download;

/// <summary>
/// CurseForge 整合包源。
/// <para>
/// 与 Modrinth 的差别：搜索 / 详情走 <c>/v1/mods/search</c> 与 <c>/v1/mods/{id}/files</c>，
/// 版本条目<b>不带直链</b>（作者可关闭第三方分发），安装时才用 modId + fileId 现解析，
/// 因此 <see cref="ModpackVersion.FileUrl"/> 留空、改由 <see cref="ModpackVersion.ProjectId"/> 承载项目 Id。
/// </para>
/// <para>需要 API Key；未配置 / 被限流时 <see cref="IsAvailable"/> 为 false，界面会显示原因且不影响 Modrinth 源。</para>
/// </summary>
public class CurseForgeModpackSource : IModpackSource
{
    private readonly CurseForgeClient _client;

    public CurseForgeModpackSource(HttpClient http) => _client = new CurseForgeClient(http);

    /// <summary>供 App 层直接取用底层客户端（安装 / Mod 下载 / 指纹匹配）。</summary>
    public CurseForgeClient Client => _client;

    public string Id => "curseforge";
    public string DisplayName => "CurseForge";
    public bool IsAvailable => _client.IsAvailable;
    public string? UnavailableReason => _client.UnavailableReason;

    /// <summary>CurseForge 加载器展示名（用于从 gameVersions 里筛出加载器标签）。</summary>
    private static readonly string[] LoaderNames =
        { "Forge", "Fabric", "NeoForge", "Quilt", "LiteLoader", "Cauldron" };

    /// <summary>看起来像 Minecraft 版本号（1.20.1 / 1.21 / 24w14a 之类只取前两者）。</summary>
    private static readonly Regex McVersionPattern = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    public async Task<List<ModpackItem>> SearchAsync(string? keyword, string? gameVersion, string? loader,
        int limit = 24, int offset = 0, CancellationToken ct = default)
    {
        if (!IsAvailable) return new List<ModpackItem>();

        var mods = await _client.SearchAsync(
            keyword,
            CurseForgeApi.ClassModpacks,
            gameVersion,
            ParseLoader(loader),
            CurseForgeApi.SortPopularity,
            limit, offset, ct).ConfigureAwait(false);

        return mods.Select(MapItem).ToList();
    }

    public async Task<ModpackDetail?> GetDetailAsync(string id, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        if (!int.TryParse(id, out var modId)) return null;

        var mod = await _client.GetModAsync(modId, ct).ConfigureAwait(false);
        if (mod is null) return null;

        var files = await _client.GetFilesAsync(modId, limit: 50, ct: ct).ConfigureAwait(false);

        // 合规底线：作者关闭「允许第三方分发」时，第三方客户端不得再下载其文件
        var allowed = mod.AllowModDistribution ?? true;

        var detail = new ModpackDetail
        {
            Source = Id,
            Id = mod.Id.ToString(),
            Title = mod.Name,
            Summary = mod.Summary,
            Author = mod.Authors.FirstOrDefault()?.Name ?? "",
            IconUrl = FirstIcon(mod),
            Description = allowed
                ? "（CurseForge 未提供正文摘要，可在站内页面查看完整介绍与变更日志。）"
                : "该整合包作者已关闭「允许第三方分发」，启动器无法自动下载，请前往 CurseForge 官网手动获取。",
            Downloads = (int)Math.Min(mod.DownloadCount, int.MaxValue),
            DistributionAllowed = allowed,
            Versions = MapVersions(modId, files),
            PageUrl = CurseForgeApi.WebPageUrl(CurseForgeApi.ClassModpacks, mod.Slug, modId)
        };
        return detail;
    }

    /// <summary>搜索结果 → 统一条目。</summary>
    public static ModpackItem MapItem(CurseForgeMod mod)
    {
        var gameVersions = new List<string>();
        var loaders = new List<string>();

        foreach (var idx in mod.LatestFilesIndexes)
        {
            if (!string.IsNullOrWhiteSpace(idx.GameVersion))
            {
                if (McVersionPattern.IsMatch(idx.GameVersion)) gameVersions.Add(idx.GameVersion);
                else if (LoaderNames.Contains(idx.GameVersion, StringComparer.OrdinalIgnoreCase)
                         && !loaders.Contains(idx.GameVersion, StringComparer.OrdinalIgnoreCase))
                    loaders.Add(idx.GameVersion);
            }
        }

        return new ModpackItem
        {
            Source = "curseforge",
            Id = mod.Id.ToString(),
            Title = mod.Name,
            Summary = mod.Summary,
            Author = mod.Authors.FirstOrDefault()?.Name ?? "",
            IconUrl = FirstIcon(mod),
            Downloads = (int)Math.Min(mod.DownloadCount, int.MaxValue),
            GameVersions = ModrinthModpackSource.SortVersionsDescending(gameVersions),
            Loaders = loaders.Select(l => l.ToLowerInvariant()).ToList()
        };
    }

    /// <summary>文件列表 → 统一版本视图。</summary>
    public static List<ModpackVersion> MapVersions(int modId, List<CurseForgeFile> files)
    {
        var result = new List<ModpackVersion>();
        foreach (var f in files)
        {
            // 只按 isAvailable 过滤 —— 不要用 fileStatus：实测官方会把可正常下载的文件也标成 4（已删除）之类，
            // 按 fileStatus 过滤会把真实可用的新版本整个隐藏掉。
            if (!f.IsAvailable) continue;

            var mc = f.GameVersions.FirstOrDefault(v => McVersionPattern.IsMatch(v)) ?? "";
            var loader = f.GameVersions.FirstOrDefault(v =>
                LoaderNames.Contains(v, StringComparer.OrdinalIgnoreCase)) ?? "";

            result.Add(new ModpackVersion
            {
                Id = f.Id.ToString(),
                ProjectId = modId.ToString(),
                Name = f.DisplayName,
                VersionNumber = string.IsNullOrWhiteSpace(f.DisplayName) ? f.FileName : f.DisplayName,
                GameVersion = mc,
                Loader = loader.ToLowerInvariant(),
                FileUrl = f.DownloadUrl ?? "",      // 可能为空：安装时经 /download-url 现解析
                FileName = f.FileName,
                Sha1 = f.Sha1,
                FileSize = f.FileLength
            });
        }

        // 新版本在前（fileId 单调递增，可直接当版本序）
        return result.OrderByDescending(v => long.TryParse(v.Id, out var n) ? n : 0).ToList();
    }

    /// <summary>加载器字符串 → API 枚举。</summary>
    public static CurseForgeLoaderType ParseLoader(string? loader) => (loader ?? "").ToLowerInvariant() switch
    {
        "forge" => CurseForgeLoaderType.Forge,
        "fabric" => CurseForgeLoaderType.Fabric,
        "quilt" => CurseForgeLoaderType.Quilt,
        "neoforge" => CurseForgeLoaderType.NeoForge,
        "liteloader" => CurseForgeLoaderType.LiteLoader,
        _ => CurseForgeLoaderType.Any
    };

    private static string? FirstIcon(CurseForgeMod mod)
    {
        var url = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }
}
