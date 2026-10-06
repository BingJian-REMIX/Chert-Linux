using System.Text.Json;
using MCLCS.Core.Download;
using MCLCS.Core.Models;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Installers;

/// <summary>整合包安装结果。</summary>
public sealed class ModpackInstallResult
{
    /// <summary>整合包名。</summary>
    public string Name { get; init; } = "";

    /// <summary>实际落地的版本 Id（隔离安装时为整合包名，否则为 MC 版本号）。</summary>
    public string VersionId { get; init; } = "";

    /// <summary>游戏工作目录（隔离时为 versions/&lt;id&gt;）。</summary>
    public string GameDir { get; init; } = "";

    /// <summary>是否隔离安装。</summary>
    public bool Isolated { get; init; }

    /// <summary>因重名而自动改了 Id。</summary>
    public bool Renamed { get; init; }

    /// <summary>安装的 Mod 数量。</summary>
    public int ModCount { get; init; }
}

/// <summary>
/// Modrinth 整合包安装器（.mrpack 格式）。
/// 流程：解压 → 读 modrinth.index.json → 安装 MC + loader → 并行下载文件 → 复制 overrides。
/// <para>
/// 支持<b>隔离安装</b>：整合包的 mods / config / resourcepacks 落到 <c>versions/&lt;整合包名&gt;/</c>，
/// 与其它版本互不干扰（规格 3.13 多实例）。libraries / assets 仍走共享目录，避免重复下载几个 G。
/// </para>
/// </summary>
public class ModpackInstaller
{
    private readonly string _gameRoot;
    private readonly HttpClient _client;
    private readonly IDownloader _downloader;
    private readonly ILogger? _logger;

    public ModpackInstaller(string gameRoot, HttpClient client, IDownloader downloader, ILogger? logger = null)
    {
        _gameRoot = gameRoot;
        _client = client;
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>安装 .mrpack 整合包（兼容旧签名：共享目录安装）。</summary>
    public async Task InstallAsync(string mrpackPath,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
        => await InstallAsync(mrpackPath, isolated: false, preferredName: null, progress, ct);

    /// <summary>
    /// 安装 .mrpack 整合包。
    /// </summary>
    /// <param name="isolated">true 时装进 <c>versions/&lt;整合包名&gt;/</c> 独立目录。</param>
    /// <param name="preferredName">隔离目录名，留空则用整合包自带的名字。</param>
    public async Task<ModpackInstallResult> InstallAsync(string mrpackPath,
        bool isolated,
        string? preferredName = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath))
            throw new FileNotFoundException("整合包文件不存在", mrpackPath);

        var tempDir = Path.Combine(Path.GetTempPath(), "mclcs_mrpack_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. 解压 .mrpack（本质是 ZIP）
            _logger?.Log("解压整合包 ...");
            Unzip.ExtractToDirectory(mrpackPath, tempDir);
            var indexJson = Path.Combine(tempDir, "modrinth.index.json");
            if (!File.Exists(indexJson))
                throw new InvalidDataException("整合包缺少 modrinth.index.json");

            var index = JsonSerializer.Deserialize<ModrinthPackIndex>(await File.ReadAllTextAsync(indexJson, ct))
                        ?? throw new InvalidDataException("无法解析 modrinth.index.json");

            _logger?.Log($"整合包: {index.Name} (MC {index.VersionId}, format {index.FormatVersion})");

            // 2. 安装原版 + loader（libraries / assets 始终走共享目录）
            var leafId = await InstallGameAndLoader(index, progress, ct);

            // 3. 决定内容落地目录
            var targetDir = _gameRoot;
            var versionId = leafId;
            var renamed = false;

            if (isolated)
            {
                var baseId = VersionIsolation.SafeVersionId(preferredName ?? index.Name, index.VersionId);

                // 与刚装好的原版/loader 版本重名时换一个，避免污染基础版本目录
                if (string.Equals(baseId, leafId, StringComparison.OrdinalIgnoreCase))
                    baseId = $"{baseId}-整合包";

                versionId = VersionIsolation.ResolveIdConflict(_gameRoot, baseId, out renamed);
                targetDir = CreateIsolatedVersion(versionId, leafId, index.Name);
                _logger?.Log($"隔离安装到 versions/{versionId}（继承 {leafId}）");
            }

            // 4. 下载 files（Mod）
            var modCount = await DownloadPackFiles(index, targetDir, progress, ct);

            // 5. 复制 overrides
            CopyOverrides(tempDir, targetDir);

            _logger?.Log($"整合包 {index.Name} 安装完成（{modCount} 个 Mod）");

            return new ModpackInstallResult
            {
                Name = index.Name,
                VersionId = versionId,
                GameDir = targetDir,
                Isolated = isolated,
                Renamed = renamed,
                ModCount = modCount
            };
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// 建一个继承自 <paramref name="parentId"/> 的隔离版本：写 <c>&lt;id&gt;.json</c>（inheritsFrom）
    /// 与隔离标记，并预建 mods / config 等子目录。
    /// </summary>
    private string CreateIsolatedVersion(string versionId, string parentId, string packName)
    {
        var dir = VersionIsolation.Enable(_gameRoot, versionId, $"整合包 {packName}");

        var jsonPath = PathEx.VersionJsonPath(_gameRoot, versionId);
        if (!File.Exists(jsonPath))
        {
            var stub = new
            {
                id = versionId,
                inheritsFrom = parentId,
                type = "release",
                releaseTime = DateTimeOffset.Now.ToString("O"),
                time = DateTimeOffset.Now.ToString("O"),
                libraries = Array.Empty<object>()
            };
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(stub, new JsonSerializerOptions { WriteIndented = true }));
        }

        VersionIsolation.EnsureFolders(dir);
        return dir;
    }

    /// <summary>安装原版与 loader，返回最终可启动的叶子版本 Id。</summary>
    private async Task<string> InstallGameAndLoader(ModrinthPackIndex index,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var leafId = await InstallGameAndLoaderCore(index, progress, ct);
        return leafId;
    }

    /// <summary>装原版与 loader，返回最终叶子版本 Id（有 loader 时为 loader 版本 Id）。</summary>
    private async Task<string> InstallGameAndLoaderCore(ModrinthPackIndex index,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        // 安装原版
        _logger?.Log($"安装原版 Minecraft {index.VersionId} ...");
        var vanilla = new VanillaInstaller(_gameRoot, _client, _downloader, _logger);
        await vanilla.InstallAsync(index.VersionId, progress, ct);

        var leafId = index.VersionId;

        // 按 dependencies 安装 loader
        foreach (var (loader, version) in index.Dependencies)
        {
            if (loader == "minecraft") continue;

            _logger?.Log($"安装 loader: {loader} {version}");
            switch (loader)
            {
                case "fabric-loader":
                    await new FabricInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(index.VersionId, progress, ct);
                    leafId = LatestVersionIdContaining(index.VersionId, "fabric") ?? leafId;
                    break;
                case "forge":
                    await new ForgeInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(index.VersionId, progress, ct);
                    leafId = LatestVersionIdContaining(index.VersionId, "forge") ?? leafId;
                    break;
                case "quilt-loader":
                    _logger?.Log("Quilt loader 暂未实现，跳过");
                    break;
                case "neoforge":
                    _logger?.Log("NeoForge 暂未实现，跳过");
                    break;
                default:
                    _logger?.Log($"未知 loader: {loader}，跳过");
                    break;
            }
        }

        return leafId;
    }

    /// <summary>在 versions 目录里找出最新的、同时包含 MC 版本号与 loader 关键字的版本 Id。</summary>
    private string? LatestVersionIdContaining(string mcVersion, string loaderKeyword)
    {
        var versionsDir = PathEx.VersionsDir(_gameRoot);
        if (!Directory.Exists(versionsDir)) return null;

        return Directory.GetDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id))
            .Where(id => id!.Contains(mcVersion, StringComparison.OrdinalIgnoreCase)
                         && id.Contains(loaderKeyword, StringComparison.OrdinalIgnoreCase))
            .Where(id => File.Exists(PathEx.VersionJsonPath(_gameRoot, id!)))
            .OrderByDescending(id => File.GetLastWriteTimeUtc(PathEx.VersionJsonPath(_gameRoot, id!)))
            .FirstOrDefault();
    }

    /// <summary>下载整合包声明的 Mod 到目标目录的 mods 下，返回下载数量。</summary>
    private async Task<int> DownloadPackFiles(ModrinthPackIndex index,
        string targetDir,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var clientFiles = index.Files
            .Where(f => f.Env.Client is "required" or "optional")
            .ToList();

        if (clientFiles.Count == 0) return 0;

        var items = clientFiles.Select(f =>
        {
            // f.Path 形如 "mods/xxx.jar"、"config/yyy.toml"，按声明的相对路径落地
            var relative = f.Path.Replace('\\', '/').TrimStart('/');
            var dest = SafeCombine(targetDir, relative)
                       ?? Path.Combine(targetDir, "mods", Path.GetFileName(relative));

            var destDir = Path.GetDirectoryName(dest);
            if (destDir is not null) Directory.CreateDirectory(destDir);

            return new DownloadItem(f.Downloads, dest, f.Hashes.Sha1, f.FileSize);
        }).ToList();

        _logger?.Log($"下载 {items.Count} 个整合包文件 ...");
        await _downloader.DownloadBatchAsync(items, progress, ct);
        return items.Count;
    }

    private void CopyOverrides(string tempDir, string targetDir)
    {
        // client-overrides 优先级高于 overrides（Modrinth 规范）
        foreach (var name in new[] { "overrides", "client-overrides" })
        {
            var overridesDir = Path.Combine(tempDir, name);
            if (!Directory.Exists(overridesDir)) continue;

            _logger?.Log($"复制 {name} ...");
            foreach (var file in Directory.EnumerateFiles(overridesDir, "*", SearchOption.AllDirectories))
            {
                var relative = file[(overridesDir.Length + 1)..];
                var dest = SafeCombine(targetDir, relative);
                if (dest is null) continue;   // 防路径穿越

                var destDir = Path.GetDirectoryName(dest);
                if (destDir is not null) Directory.CreateDirectory(destDir);
                // 【修复】整合包自带的 config / options 会静默覆盖用户改过的设置。
                // 覆盖前把原文件留一份 .mclcs-bak，并在日志里点名，用户至少能找回。
                if (File.Exists(dest))
                {
                    try
                    {
                        File.Copy(dest, dest + ".mclcs-bak", overwrite: true);
                        _logger?.Log($"已备份被覆盖的文件：{Path.GetFileName(dest)}（原文件另存为 .mclcs-bak）");
                    }
                    catch { /* 备份失败不阻断安装 */ }
                }
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    /// <summary>拼路径并确保结果仍在根目录内；越界返回 null。</summary>
    private static string? SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        return combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
