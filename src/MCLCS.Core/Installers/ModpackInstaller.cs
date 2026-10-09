using System.IO.Compression;
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

/// <summary>整合包归档格式。</summary>
public enum ModpackFormat
{
    /// <summary>无法识别（既没有 modrinth.index.json 也没有 manifest.json）。</summary>
    Unknown,
    /// <summary>Modrinth <c>.mrpack</c>（特征文件 modrinth.index.json）。</summary>
    Modrinth,
    /// <summary>CurseForge 整合包 zip（特征文件 manifest.json）。</summary>
    CurseForge
}

/// <summary>
/// 整合包安装器（Modrinth <c>.mrpack</c> 与 CurseForge zip 两种格式）。
/// <para>
/// Modrinth：解压 → 读 modrinth.index.json → 安装 MC + loader → 并行下载文件 → 复制 overrides。
/// </para>
/// <para>
/// CurseForge：解压 → 读 manifest.json → 安装 MC + loader → 按 projectID/fileID 批量换直链后下载 →
/// 复制 manifest 声明的覆盖目录。作者关闭第三方分发（<c>allowModDistribution=false</c>）的文件会被跳过并记录。
/// </para>
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
            CopyOverrides(tempDir, targetDir, "overrides", "client-overrides");

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

    /// <summary>Modrinth 整合包：装原版 + 按 dependencies 装 loader。</summary>
    private Task<string> InstallGameAndLoader(ModrinthPackIndex index,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
        => InstallBaseAsync(index.VersionId, index.Dependencies.Keys, progress, ct);

    /// <summary>
    /// 统一安装「原版 + 加载器」，返回最终可启动的叶子版本 Id（有加载器时为加载器版本 Id）。
    /// Modrinth 与 CurseForge 共用。加载器名兼容 Modrinth 的 <c>fabric-loader</c>/<c>forge</c>/<c>quilt-loader</c>/<c>neoforge</c>
    /// 与 CurseForge 的 <c>forge-47.2.0</c> 形式。
    /// </summary>
    private async Task<string> InstallBaseAsync(string mcVersion, IEnumerable<string> loaderIds,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        _logger?.Log($"安装原版 Minecraft {mcVersion} ...");
        var vanilla = new VanillaInstaller(_gameRoot, _client, _downloader, _logger);
        await vanilla.InstallAsync(mcVersion, progress, ct);

        var leafId = mcVersion;

        foreach (var raw in loaderIds)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                continue;

            var loader = SplitLoaderId(raw, out var loaderVersion);
            _logger?.Log(string.IsNullOrEmpty(loaderVersion)
                ? $"安装 loader: {loader}"
                : $"安装 loader: {loader} {loaderVersion}");

            switch (loader)
            {
                case "fabric":
                    await new FabricInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(mcVersion, progress, ct);
                    leafId = LatestVersionIdContaining(mcVersion, "fabric") ?? leafId;
                    break;
                case "forge":
                    await new ForgeInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(mcVersion, progress, ct);
                    leafId = LatestVersionIdContaining(mcVersion, "forge") ?? leafId;
                    break;
                case "neoforge":
                    await new NeoForgeInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(mcVersion, progress, ct);
                    leafId = LatestVersionIdContaining(mcVersion, "neoforge") ?? leafId;
                    break;
                case "quilt":
                    await new QuiltInstaller(_gameRoot, _client, _downloader, _logger)
                        .InstallAsync(mcVersion, progress, ct);
                    leafId = LatestVersionIdContaining(mcVersion, "quilt") ?? leafId;
                    break;
                default:
                    _logger?.Log($"未知 loader: {raw}，跳过");
                    break;
            }
        }

        return leafId;
    }

    /// <summary>
    /// 把加载器标识拆成「名称 + 版本」。
    /// <c>fabric-loader</c> → (fabric, "")；<c>forge-47.2.0</c> → (forge, 47.2.0)；<c>neoforge-20.4.0</c> → (neoforge, 20.4.0)。
    /// </summary>
    public static string SplitLoaderId(string loaderId, out string version)
    {
        var s = (loaderId ?? "").Trim().ToLowerInvariant().Replace("-loader", "");
        version = "";

        var dash = s.LastIndexOf('-');
        var name = s;
        if (dash > 0)
        {
            name = s[..dash];
            version = s[(dash + 1)..];
        }

        if (name.Contains("neoforge")) return "neoforge";
        if (name.Contains("fabric")) return "fabric";
        if (name.Contains("quilt")) return "quilt";
        if (name.Contains("forge")) return "forge";
        return name;
    }

    /// <summary>按 CurseForge classId 决定文件落地子目录。</summary>
    public static string FolderForClassId(int? classId) => classId switch
    {
        CurseForgeApi.ClassResourcePacks => "resourcepacks",
        CurseForgeApi.ClassWorlds => "saves",
        _ => "mods"
    };

    /// <summary>把 CurseForge 返回的文件名清洗成安全的单层文件名。</summary>
    private static string SanitizeFileName(string name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? $"mod_{Guid.NewGuid():N}.jar" : n;
    }

    /// <summary>嗅探整合包格式（读 zip 中央目录里的特征文件，不解压）。</summary>
    public static ModpackFormat DetectFormat(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            if (names.Any(n => n.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)))
                return ModpackFormat.Modrinth;
            if (names.Any(n => n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                return ModpackFormat.CurseForge;
            return ModpackFormat.Unknown;
        }
        catch
        {
            return ModpackFormat.Unknown;
        }
    }

    /// <summary>
    /// 按内容自动分派的安装入口（「从 Zip 导入」走这里）：
    /// 自动识别 Modrinth <c>.mrpack</c> 与 CurseForge 整合包 zip，识别不了时给出明确原因。
    /// </summary>
    public async Task<ModpackInstallResult> InstallAnyAsync(string archivePath,
        bool isolated = false,
        string? preferredName = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        return DetectFormat(archivePath) switch
        {
            ModpackFormat.CurseForge => await InstallCurseForgeAsync(archivePath, isolated, preferredName, progress, ct),
            ModpackFormat.Modrinth => await InstallAsync(archivePath, isolated, preferredName, progress, ct),
            _ => throw new InvalidDataException(
                "无法识别的整合包：需要 Modrinth 的 modrinth.index.json 或 CurseForge 的 manifest.json")
        };
    }

    /// <summary>
    /// 安装 CurseForge 整合包 zip（<c>manifest.json</c> + <c>overrides/</c>）。
    /// <para>
    /// 合规：作者关闭第三方分发（<c>allowModDistribution=false</c>）的文件无法自动下载，
    /// 会被跳过并在日志中逐条记录，安装结束后汇总提示。
    /// </para>
    /// </summary>
    public async Task<ModpackInstallResult> InstallCurseForgeAsync(string packPath,
        bool isolated = false,
        string? preferredName = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(packPath))
            throw new FileNotFoundException("整合包文件不存在", packPath);
        if (!CurseForgeConfig.HasKey)
            throw new InvalidOperationException(
                "安装 CurseForge 整合包需要 API Key，请先在「设置 → 下载」中配置。");

        var tempDir = Path.Combine(Path.GetTempPath(), "chert_cfpack_" + Guid.NewGuid().ToString("N"));
        try
        {
            _logger?.Log("解压 CurseForge 整合包 ...");
            Unzip.ExtractToDirectory(packPath, tempDir);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("整合包缺少 manifest.json");

            var manifest = JsonSerializer.Deserialize<CurseForgePackManifest>(
                               await File.ReadAllTextAsync(manifestPath, ct))
                           ?? throw new InvalidDataException("无法解析 manifest.json");

            var mcVersion = manifest.Minecraft?.Version ?? "";
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new InvalidDataException("manifest.json 未声明 Minecraft 版本");

            _logger?.Log($"整合包: {manifest.Name} (MC {mcVersion})");

            // 1. 原版 + 加载器（取 primary，没有 primary 则取第一项）
            var loaders = manifest.Minecraft?.ModLoaders ?? new List<CurseForgePackLoader>();
            var primary = loaders.FirstOrDefault(l => l.Primary) ?? loaders.FirstOrDefault();
            var loaderIds = primary is null ? Array.Empty<string>() : new[] { primary.Id };

            var leafId = await InstallBaseAsync(mcVersion, loaderIds, progress, ct);

            // 2. 决定内容落地目录
            var targetDir = _gameRoot;
            var versionId = leafId;
            var renamed = false;

            if (isolated)
            {
                var baseId = VersionIsolation.SafeVersionId(preferredName ?? manifest.Name, mcVersion);
                if (string.Equals(baseId, leafId, StringComparison.OrdinalIgnoreCase))
                    baseId = $"{baseId}-整合包";

                versionId = VersionIsolation.ResolveIdConflict(_gameRoot, baseId, out renamed);
                targetDir = CreateIsolatedVersion(versionId, leafId, manifest.Name);
                _logger?.Log($"隔离安装到 versions/{versionId}（继承 {leafId}）");
            }

            // 3. 按 projectID/fileID 批量换直链后下载
            var (modCount, skipped) = await DownloadCurseForgeFilesAsync(manifest, targetDir, progress, ct);

            // 4. 复制 manifest 声明的覆盖目录
            var overridesName = string.IsNullOrWhiteSpace(manifest.Overrides) ? "overrides" : manifest.Overrides;
            CopyOverrides(tempDir, targetDir, overridesName);

            if (skipped > 0)
                _logger?.Log($"注意：{skipped} 个文件因作者未开放第三方分发而跳过，请前往 CurseForge 官网手动下载。");

            _logger?.Log($"整合包 {manifest.Name} 安装完成（{modCount} 个文件）");

            return new ModpackInstallResult
            {
                Name = manifest.Name,
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
    /// 下载 CurseForge 整合包声明的文件，返回 (成功数, 因分发限制跳过数)。
    /// 先用批量接口一次拿全元数据（含 classId 决定落地目录），避免逐个请求打满配额。
    /// </summary>
    private async Task<(int Done, int Skipped)> DownloadCurseForgeFilesAsync(
        CurseForgePackManifest manifest, string targetDir,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var required = manifest.Files.Where(f => f.Required && f.FileId > 0).ToList();
        if (required.Count == 0) return (0, 0);

        var cf = new CurseForgeClient(_client);

        // 文件元数据（一次 1000 条）
        var files = await cf.GetFilesByIdsAsync(required.Select(f => f.FileId).ToList(), ct);
        var fileById = files.ToDictionary(f => f.Id);

        // 项目元数据（拿 classId，决定落到 mods / resourcepacks / saves）
        var mods = await cf.GetModsByIdsAsync(required.Select(f => f.ProjectId).Distinct().ToList(), ct);
        var classById = mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First().ClassId);

        var items = new List<DownloadItem>();
        var skipped = 0;

        foreach (var rf in required)
        {
            if (!fileById.TryGetValue(rf.FileId, out var meta) || !meta.IsAvailable)
            {
                skipped++;
                _logger?.Log($"跳过不可用文件 {rf.ProjectId}/{rf.FileId}");
                continue;
            }

            var url = meta.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                // 作者关闭第三方分发时直链为空，需服务端现算；仍拿不到即视为禁止分发
                url = await cf.ResolveDownloadUrlAsync(rf.ProjectId, rf.FileId, ct);
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                skipped++;
                _logger?.Log($"该文件未开放第三方分发，已跳过：{meta.FileName}");
                continue;
            }

            var folder = FolderForClassId(classById.TryGetValue(rf.ProjectId, out var cid) ? cid : null);
            var dest = Path.Combine(targetDir, folder, SanitizeFileName(meta.FileName));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            items.Add(new DownloadItem(new[] { url }, dest, meta.Sha1, meta.FileLength));
        }

        if (items.Count > 0)
        {
            _logger?.Log($"下载 {items.Count} 个整合包文件 ...");
            await _downloader.DownloadBatchAsync(items, progress, ct);
        }

        return (items.Count, skipped);
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

    /// <summary>复制覆盖目录（Modrinth：overrides → client-overrides 后者优先；CurseForge：manifest 声明的目录）。</summary>
    private void CopyOverrides(string tempDir, string targetDir, params string[] names)
    {
        foreach (var name in names)
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
