using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MCLCS.Core.Download;
using MCLCS.Core.Installers;
using MCLCS.Core.Launcher;
using MCLCS.Core.Models;
using MCLCS.Core.Profiles;
using MCLCS.Core.Resources;
using MCLCS.Core.Save;
using MCLCS.Core.Utils;
using System.Text.RegularExpressions;

namespace MCLCS.Linux.App.Services;

/// <summary>
/// 启动器后端服务（对齐 MCLCS-WPF 的 LauncherService）：整合下载页所需的全部能力，
/// 向下委托 Core 层的下载原语（ModrinthClient / PixelmapClient / ModpackInstaller / 版本安装器 / MapInstaller / ExtraResourceInstaller）。
/// 下载页 ViewModel 只与本服务打交道，不直接依赖具体 Core 实现，便于与 WPF 保持一致。
/// </summary>
public class LauncherService : ILogger
{
    /// <summary>单例实例。</summary>
    public static LauncherService Instance { get; } = new(GameConstants.DefaultGameRoot);

    /// <summary>游戏根目录（来自 GameConstants，供安装器落地）。</summary>
    public string GameRoot { get; }

    /// <summary>共享 HttpClient（含 User-Agent 与下载器复用）。</summary>
    public HttpClient ApiClient { get; }

    private readonly IDownloader _downloader;

    /// <summary>像素茶艺（PixelMap）地图站客户端（下载页 → 地图）。</summary>
    public PixelmapClient Pixelmap { get; }

    /// <summary>当前可用的整合包在线源（Modrinth 免 Key 常驻；CurseForge 需 API Key）。</summary>
    public IReadOnlyList<IModpackSource> ModpackSources { get; }

    /// <summary>CurseForge API 客户端（搜索 / 详情 / 指纹匹配 / 直链解析）。未配置 Key 时不可用。</summary>
    public CurseForgeClient CurseForge { get; }

    public LauncherService(string gameRoot)
    {
        GameRoot = gameRoot;
        // 挂 CurseForgeAuthHandler：发往 forgecdn 的请求自动附加 x-api-key（2026-07 起 CDN 强制认证）
        ApiClient = new HttpClient(new CurseForgeAuthHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        // 规格 2.2：地图站要求 User-Agent 为 MCLCS/版本号
        ApiClient.DefaultRequestHeaders.UserAgent.TryParseAdd(
            $"MCLCS/{GameConstants.LauncherVersion} (Linux; +{GameConstants.CnbRepoUrl})");

        _downloader = new HttpDownloader(ApiClient, 8, this);
        Pixelmap = new PixelmapClient(ApiClient);

        // 整合包在线源：Modrinth 免 Key 常驻可用；CurseForge 需 API Key（未配置时
        // IsAvailable=false，界面显示原因且不影响 Modrinth）。
        var cfSource = new CurseForgeModpackSource(ApiClient);
        CurseForge = cfSource.Client;
        ModpackSources = new IModpackSource[] { new ModrinthModpackSource(ApiClient), cfSource };

        // 把 profile 里的 CurseForge 设置与下载偏好（限速 / 并发）同步进 Core
        ApplyCurseForgeSettings();
        ApplyDownloadPreferences();
    }

    /// <summary>
    /// 重新读取 profile 并同步 CurseForge 配置（设置页保存 Key / API Root / 开关后调用）。
    /// </summary>
    public void ApplyCurseForgeSettings()
    {
        try
        {
            var profile = ProfileStore.Load(GameRoot);
            CurseForgeConfig.Apply(profile.CurseForge);
        }
        catch
        {
            // 读配置失败不影响启动，按未配置处理
        }
    }

    /// <summary>
    /// 把 profile 里的下载偏好同步进 Core：全局限速 / 单任务重试次数。
    /// 此前这两项「存了不用」——限速只在不可达的下载中心页里被设置。
    /// </summary>
    public void ApplyDownloadPreferences()
    {
        try
        {
            var profile = ProfileStore.Load(GameRoot);
            DownloadSpeedLimiter.SetKilobytesPerSecond(profile.DownloadSpeedLimitKbps);
            // DownloadAutoRetryCount 目前与 WPF 端一致：配置已持久化，重试逻辑尚未接线（待办）
        }
        catch
        {
            // 同上：配置不可读时保持默认（不限速 / 不重试）
        }
    }

    /// <summary>按 Id 取得整合包源（未知 Id 回退到 Modrinth）。</summary>
    private IModpackSource GetModpackSource(string? id) =>
        ModpackSources.FirstOrDefault(s => s.Id == (id ?? "")) ?? ModpackSources[0];

    // ---- 版本列表 ----

    public async Task<List<string>> GetVanillaVersionsAsync()
    {
        try
        {
            var json = await MirrorPolicy.GetStringWithFallback(MirrorPolicy.VersionManifestUrls(), ApiClient);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<VersionManifest>(json);
            return manifest?.Versions.Select(v => v.Id).ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>返回完整版本清单条目（含类型 / 发布时间），供下载页 Minecraft 子页列举与分类。</summary>
    public async Task<List<VersionEntry>> GetVanillaVersionsDetailedAsync()
    {
        try
        {
            var json = await MirrorPolicy.GetStringWithFallback(MirrorPolicy.VersionManifestUrls(), ApiClient);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<VersionManifest>(json);
            return manifest?.Versions ?? new List<VersionEntry>();
        }
        catch
        {
            return new List<VersionEntry>();
        }
    }

    // ---- Modrinth 搜索 / 下载 ----

    public async Task<List<ModrinthHit>> SearchModsAsync(string query, string? gameVersion, LoaderType loader, ModrinthProjectType type)
    {
        var client = new ModrinthClient(ApiClient);
        var r = await client.SearchAsync(query, gameVersion, loader, type);
        return r.Hits;
    }

    public async Task<bool> DownloadModAsync(string projectId, string targetDir, string? gameVersion, LoaderType loader,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var client = new ModrinthClient(ApiClient);
        var versions = await client.GetVersionsAsync(projectId, ct);
        ModrinthFile? file = null;
        foreach (var v in versions)
        {
            var f = client.SelectBestFile(v, gameVersion, loader);
            if (f is not null) { file = f; break; }
        }
        if (file is null) return false;

        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, file.FileName);
        var item = new DownloadItem(new[] { file.Url }, dest, file.Hashes.Sha1, file.Size);
        await _downloader.DownloadAsync(item, progress, ct);
        return true;
    }

    // ---- 整合包 ----

    public async Task<List<ModpackItem>> SearchModpacksAsync(string? keyword, string? gameVersion, string? loader, string? sourceId, CancellationToken ct)
    {
        var source = GetModpackSource(sourceId);
        if (!source.IsAvailable) return new List<ModpackItem>();
        return await source.SearchAsync(keyword, gameVersion, loader, 24, 0, ct);
    }

    public async Task<ModpackDetail?> GetModpackDetailAsync(string? sourceId, string id, CancellationToken ct)
    {
        var source = GetModpackSource(sourceId);
        if (!source.IsAvailable) return null;
        return await source.GetDetailAsync(id, ct);
    }

    public async Task<ModpackInstallResult?> InstallModpackVersionAsync(
        string? sourceId, ModpackVersion version, bool isolated, string? preferredName,
        IProgress<double>? progress, CancellationToken ct)
    {
        var isCurseForge = string.Equals(sourceId, "curseforge", StringComparison.OrdinalIgnoreCase);

        // CurseForge 的版本条目不带直链（作者可关闭第三方分发），此处按 modId + fileId 现解析
        var url = version.FileUrl;
        if (string.IsNullOrWhiteSpace(url) && isCurseForge)
        {
            if (int.TryParse(version.ProjectId, out var modId) && int.TryParse(version.Id, out var fileId))
                url = await CurseForge.ResolveDownloadUrlAsync(modId, fileId, ct);

            if (string.IsNullOrWhiteSpace(url))
            {
                Log("该整合包未开放第三方分发，请前往 CurseForge 官网手动下载后「从 Zip 导入」。");
                return null;
            }
        }
        if (string.IsNullOrWhiteSpace(url)) return null;

        var tmp = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + (isCurseForge ? ".zip" : ".mrpack"));
        try
        {
            await _downloader.DownloadAsync(new DownloadItem(new[] { url }, tmp, version.Sha1), progress, ct);
            var installer = new ModpackInstaller(GameRoot, ApiClient, _downloader, this);
            return isCurseForge
                ? await installer.InstallCurseForgeAsync(tmp, isolated, preferredName, null, ct)
                : await installer.InstallAsync(tmp, isolated, preferredName, null, ct);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
        }
    }

    public async Task<ModpackInstallResult?> InstallModpackAsync(
        string? sourceId, string projectId, string? gameVersion, string? preferredName,
        IProgress<double>? progress, CancellationToken ct)
    {
        var detail = await GetModpackDetailAsync(sourceId, projectId, ct);
        if (detail is null || detail.Versions.Count == 0) return null;
        var version = detail.Versions.FirstOrDefault(v =>
                          string.IsNullOrEmpty(gameVersion) || string.Equals(v.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase))
                      ?? detail.Versions[0];
        return await InstallModpackVersionAsync(sourceId, version, isolated: true, preferredName, progress, ct);
    }

    // ---- 版本安装（下载页 → Minecraft 下载）----

    public async Task<string?> InstallVersionAsync(string mcVersion, string loader,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // 进度适配：各安装器上报 (已完成, 总数)，这里折算成 0-1 的 double。
        // 此前一律传 null —— 进度参数声明了却从不下发，安装页进度条永远不动。
        IProgress<(int Done, int Total)>? stageProgress = progress is null
            ? null
            : new Progress<(int Done, int Total)>(p =>
                progress.Report(p.Total <= 0 ? 0 : Math.Clamp((double)p.Done / p.Total, 0, 1)));

        switch (loader.ToLowerInvariant())
        {
            case "fabric":
                return await new FabricInstaller(GameRoot, ApiClient, _downloader, this).InstallAsync(mcVersion, stageProgress, ct);
            case "forge":
                return await new ForgeInstaller(GameRoot, ApiClient, _downloader, this).InstallAsync(mcVersion, stageProgress, ct);
            case "neoforge":
                return await new NeoForgeInstaller(GameRoot, ApiClient, _downloader, this).InstallAsync(mcVersion, stageProgress, ct);
            case "quilt":
                return await new QuiltInstaller(GameRoot, ApiClient, _downloader, this).InstallAsync(mcVersion, stageProgress, ct);
            default:
                await new VanillaInstaller(GameRoot, ApiClient, _downloader, this).InstallAsync(mcVersion, stageProgress, ct);
                return mcVersion;
        }
    }

    // ---- 地图 ----

    public async Task<bool> DownloadMapAsync(string slug, IProgress<double>? progress, CancellationToken ct)
    {
        var detail = await Pixelmap.GetDetailAsync(slug, ct);
        if (detail is null || !detail.CanDownload) return false;
        var item = PixelmapClient.ToDownloadItem(detail, GameRoot);
        if (item is null) return false;

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_.zip");
        try
        {
            await _downloader.DownloadAsync(item, progress, ct);
            var result = MapInstaller.Install(tmp, GameRoot, slug);
            return result.Ok;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
        }
    }

    public async Task<ExtraResourceInstallResult?> DownloadMapExtraAsync(PixelMapDetail detail, IProgress<double>? progress, CancellationToken ct)
    {
        var item = PixelmapClient.ToExtraDownloadItem(detail, GameRoot);
        if (item is null) return null;

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_.zip");
        try
        {
            await _downloader.DownloadAsync(item, progress, ct);
            return ExtraResourceInstaller.Install(tmp, GameRoot, detail.Title);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
        }
    }

    // ---- 崩溃自动修复（对齐 MCLCS-WPF LauncherService.ApplyRepairAsync）----

    /// <summary>
    /// 执行一次崩溃自动修复。所有修复均为非破坏性：
    /// 调大内存仅改启动器配置、切换 Java 仅影响外部 Java、重下库仅重写依赖缓存、
    /// 禁用冲突 Mod 仅重命名为 .disabled（可还原）、安装缺失前置仅向 mods 目录新增文件、
    /// 降级联动恢复仅复制备份不删原档。返回修复是否成功执行。
    /// </summary>
    public async Task<bool> ApplyRepairAsync(CrashRepairPlan plan, CancellationToken ct = default)
    {
        var profile = ProfileStore.Load(GameRoot);

        switch (plan.Strategy)
        {
            case RepairStrategy.IncreaseMemory:
                if (plan.TargetMemoryMb is not null)
                {
                    profile.MaxMemoryMb = plan.TargetMemoryMb.Value;
                    ProfileStore.Save(profile);
                    Log($"自动修复：内存调整至 {plan.TargetMemoryMb.Value}MB");
                    return true;
                }
                return false;

            case RepairStrategy.SwitchJava:
            {
                var required = plan.RequiredJavaMajor ?? GameConstants.MinimumJavaMajorVersion;
                var java = await JavaDetector.FindBestAsync(required);
                if (java is null)
                {
                    Log($"未找到 Java {required}+，尝试下载安装（{profile.PreferredJavaVendor}）…");
                    java = await JavaInstaller.EnsureJavaAsync(required, GameRoot, _downloader, profile.PreferredJavaVendor, this, ct);
                }
                if (java is null)
                {
                    Log($"自动修复失败：无法获取 Java {required}+");
                    return false;
                }
                profile.JavaPath = java.JavaExe;
                ProfileStore.Save(profile);
                Log($"自动修复：切换 Java 至 {java}");
                return true;
            }

            case RepairStrategy.RedownloadLibraries:
                if (string.IsNullOrEmpty(plan.VersionId)) return false;
                var repair = await LibraryRepair.RepairAsync(GameRoot, plan.VersionId, ApiClient, _downloader, this, ct);
                return repair.Success || repair.AllHealthy;

            case RepairStrategy.DisableConflictingMods:
                return ApplyDisableConflictingMods(plan);

            case RepairStrategy.InstallMissingModDependency:
                return await ApplyInstallMissingModsAsync(plan, ct);

            case RepairStrategy.ResetResourcePacks:
            {
                var r = ResourcePackRepairer.ResetToVanilla(GameRoot);
                foreach (var a in r.Actions) Log(a);
                if (!r.Success) Log($"资源包/光影回滚失败：{r.Error}");
                return r.Success;
            }

            // §四.2 降级联动
            case RepairStrategy.RevertDowngradeBackup:
            case RepairStrategy.RetryDowngradeOtherMethod:
            case RepairStrategy.InstallOriginalVersion:
                return await ApplyDowngradeRecoveryAsync(plan, ct);

            case RepairStrategy.None:
            default:
                return false;
        }
    }

    /// <summary>禁用冲突 Mod：保留用户选定的一个，其余重命名为 .disabled（不删除）。</summary>
    private bool ApplyDisableConflictingMods(CrashRepairPlan plan)
    {
        bool any = false;
        foreach (var mod in plan.ConflictingMods)
        {
            if (string.IsNullOrEmpty(mod.FilePath)) continue;
            if (string.Equals(mod.FilePath, plan.KeepModFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(mod.FilePath)) continue;
            if (mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;

            var disabled = mod.FilePath + ".disabled";
            try
            {
                if (File.Exists(disabled))
                {
                    // 已存在 .disabled 副本，直接移除启用的那份
                    File.Delete(mod.FilePath);
                }
                else
                {
                    File.Move(mod.FilePath, disabled);
                }
                Log($"禁用冲突 Mod：{Path.GetFileName(mod.FilePath)} → {Path.GetFileName(disabled)}");
                any = true;
            }
            catch (Exception ex)
            {
                Log($"禁用 Mod 失败 {mod.FilePath}：{ex.Message}");
            }
        }
        return any || plan.ConflictingMods.Count > 0;
    }

    /// <summary>自动安装缺失的 Mod 前置依赖（从 Modrinth 下载到 mods 目录）。</summary>
    private async Task<bool> ApplyInstallMissingModsAsync(CrashRepairPlan plan, CancellationToken ct)
    {
        if (plan.MissingModDependencies.Count == 0) return false;

        var loader = DetectLoader(GameRoot, plan.VersionId);
        var gameVersion = ExtractGameVersion(GameRoot, plan.VersionId);
        var client = new ModrinthClient(ApiClient);

        var allOk = true;
        foreach (var id in plan.MissingModDependencies)
        {
            try
            {
                var ok = await InstallModDependencyAsync(client, id, loader, gameVersion, ct);
                if (ok) Log($"已安装缺失前置：{id}");
                else { Log($"未找到可安装的缺失前置：{id}"); allOk = false; }
            }
            catch (Exception ex)
            {
                Log($"安装缺失前置失败 {id}：{ex.Message}");
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>§四.2 降级联动恢复：回滚备份 / 改用其他方式重试 / 安装存档原版本。</summary>
    private async Task<bool> ApplyDowngradeRecoveryAsync(CrashRepairPlan plan, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(plan.SavePath))
        {
            Log("降级联动恢复失败：未指定存档路径。");
            return false;
        }

        switch (plan.Strategy)
        {
            case RepairStrategy.RevertDowngradeBackup:
            {
                if (string.IsNullOrEmpty(plan.BackupPath) || !Directory.Exists(plan.BackupPath))
                {
                    Log("回滚失败：找不到降级备份。");
                    return false;
                }
                var replaced = SaveDowngrader.RestoreBackupAsync(plan.BackupPath, plan.SavePath);
                Log($"已回滚到降级前备份（当前存档另存于 {replaced}）。");
                return true;
            }

            case RepairStrategy.RetryDowngradeOtherMethod:
            {
                if (string.IsNullOrEmpty(plan.BackupPath) || !Directory.Exists(plan.BackupPath))
                {
                    Log("改用其他方式失败：找不到降级备份。");
                    return false;
                }
                var targetDv = SaveDowngrader.GetSaveDataVersion(plan.SavePath);
                var targetVer = DataVersionMap.ToGameVersion(targetDv);
                if (targetVer is null)
                {
                    Log($"改用其他方式失败：目标 DataVersion {targetDv} 不在对照表中。");
                    return false;
                }
                SaveDowngrader.RestoreBackupAsync(plan.BackupPath, plan.SavePath);
                var dp = await SaveDowngrader.DowngradeAsync(plan.SavePath, targetVer, DowngradeMethod.Amulet);
                if (dp.Success) Log($"已用 Amulet 重新降级到 {targetVer}。");
                else Log($"改用 Amulet 降级失败：{dp.ErrorMessage}");
                return dp.Success;
            }

            case RepairStrategy.InstallOriginalVersion:
            {
                if (string.IsNullOrEmpty(plan.VersionId))
                {
                    Log("安装原版本失败：未记录原版本号。");
                    return false;
                }
                try
                {
                    var (loader, mcVersion) = SplitVersionId(plan.VersionId);
                    Log($"正在安装存档原版本 {plan.VersionId}（{loader}）…");
                    await InstallVersionAsync(mcVersion, loader, ct: ct);
                    var p = ProfileStore.Load(GameRoot);
                    p.LastVersionId = plan.VersionId;
                    ProfileStore.Save(p);
                    Log($"已安装原版本 {plan.VersionId}，将用该版本打开存档。");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"安装原版本失败：{ex.Message}（可手动在「安装新版本」中安装 {plan.VersionId}）");
                    return false;
                }
            }

            default:
                return false;
        }
    }

    /// <summary>自动安装单个缺失的 Mod 前置依赖（从 Modrinth 下载到 mods 目录）。</summary>
    private async Task<bool> InstallModDependencyAsync(ModrinthClient client, string modId,
        LoaderType loader, string? gameVersion, CancellationToken ct)
    {
        var search = await client.SearchAsync(modId, gameVersion, loader, ModrinthProjectType.Mod, limit: 5, ct: ct);
        var hit = search.Hits.FirstOrDefault(h => string.Equals(h.Slug, modId, StringComparison.OrdinalIgnoreCase))
                  ?? search.Hits.FirstOrDefault();
        if (hit is null) return false;

        var versions = await client.GetVersionsAsync(hit.ProjectId, ct);
        var ver = versions.FirstOrDefault(v =>
                        (gameVersion is null || v.GameVersions.Contains(gameVersion))
                        && (loader == LoaderType.Any || v.Loaders.Contains(ModrinthClient.LoaderString(loader), StringComparer.OrdinalIgnoreCase)))
                  ?? versions.FirstOrDefault();
        if (ver is null) return false;

        var file = client.SelectBestFile(ver, gameVersion, loader);
        if (file is null) return false;

        var modsDir = PathEx.ModsDir(GameRoot);
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, file.FileName);
        await _downloader.DownloadAsync(new DownloadItem(new[] { file.Url }, dest, file.Hashes.Sha1), null, ct);
        return true;
    }

    /// <summary>由版本 id 推断（loader, 游戏版本号）二元组，供自动安装原版本使用。</summary>
    private static (string Loader, string McVersion) SplitVersionId(string versionId)
    {
        var v = versionId.ToLowerInvariant();
        foreach (var l in new[] { "neoforge", "forge", "fabric", "quilt" })
            if (v.StartsWith(l))
                return (l, versionId.Substring(l.Length).TrimStart('-'));
        return ("vanilla", versionId);
    }

    /// <summary>从版本合并结果推断加载器类型。</summary>
    private static LoaderType DetectLoader(string gameRoot, string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return LoaderType.Any;
        try
        {
            var merged = VersionMerger.Merge(gameRoot, versionId);
            var mc = merged.MainClass ?? "";
            if (mc.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return LoaderType.Fabric;
            if (mc.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return LoaderType.NeoForge;
            if (mc.Contains("forge", StringComparison.OrdinalIgnoreCase)) return LoaderType.Forge;
            if (mc.Contains("quilt", StringComparison.OrdinalIgnoreCase)) return LoaderType.Quilt;
        }
        catch { /* 忽略 */ }
        return LoaderType.Any;
    }

    /// <summary>从版本 id（如 fabric-1.20.1）中提取 Minecraft 游戏版本号。</summary>
    private static string? ExtractGameVersion(string gameRoot, string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return null;
        var m = Regex.Match(versionId, @"\d+\.\d+(?:\.\d+)?");
        return m.Success ? m.Value : null;
    }

    /// <summary>实现 ILogger：安装器日志当前仅丢弃（进度由 IProgress 回调驱动）。</summary>
    public void Log(string message) { }
}
