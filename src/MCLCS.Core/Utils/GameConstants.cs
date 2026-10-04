namespace MCLCS.Core.Utils;

/// <summary>
/// 全局常量：目录约定、镜像源基址、启动器标识。
/// 镜像策略统一封装在 Download/MirrorPolicy.cs，此处仅存放最底层常量。
/// </summary>
public static class GameConstants
{
    /// <summary>系统默认的 .minecraft 根目录（%APPDATA%/.minecraft），用户未自定义时使用。</summary>
    public static string SystemGameRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>用户在「设置 → 启动」自定义的游戏目录；null 表示沿用系统默认。</summary>
    private static string? _gameRootOverride;

    /// <summary>
    /// 当前生效的 .minecraft 根目录。
    /// 优先取用户自定义值（bug #26），否则回落到 <see cref="SystemGameRoot"/>。
    /// </summary>
    public static string DefaultGameRoot => _gameRootOverride ?? SystemGameRoot;

    /// <summary>是否已自定义游戏目录。</summary>
    public static bool IsGameRootCustomized => _gameRootOverride is not null;

    /// <summary>
    /// 记录自定义游戏目录的启动器级配置文件。
    /// 不能存进 profile：profile 本身就位于游戏目录内，会形成"要先知道目录才能读目录"的循环依赖。
    /// </summary>
    private static string GameRootConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LauncherName, "gameroot.txt");

    /// <summary>设置并持久化自定义游戏目录；传 null/空则恢复系统默认。</summary>
    public static void SetGameRoot(string? root)
    {
        root = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root.Trim());

        // 与系统默认一致时视为"未自定义"，避免写入多余配置
        if (root is not null && string.Equals(root, SystemGameRoot, StringComparison.OrdinalIgnoreCase))
            root = null;

        _gameRootOverride = root;

        try
        {
            var cfg = GameRootConfigPath;
            var dir = Path.GetDirectoryName(cfg);
            if (root is null)
            {
                if (File.Exists(cfg)) File.Delete(cfg);
            }
            else
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Directory.CreateDirectory(root);
                File.WriteAllText(cfg, root);
            }
        }
        catch
        {
            // 落盘失败不影响本次会话内生效
        }
    }

    /// <summary>启动时读取已持久化的自定义游戏目录（目录不存在则忽略）。</summary>
    public static void LoadGameRootOverride()
    {
        try
        {
            var cfg = GameRootConfigPath;
            if (!File.Exists(cfg)) return;
            var root = File.ReadAllText(cfg).Trim();
            if (string.IsNullOrWhiteSpace(root)) return;
            if (!Directory.Exists(root)) return;   // 目录被删除/移动 → 回落系统默认
            _gameRootOverride = Path.GetFullPath(root);
        }
        catch
        {
            _gameRootOverride = null;
        }
    }

    /// <summary>启动器名称（写入 ${launcher_name}）。</summary>
    public const string LauncherName = "MCLCS";

    /// <summary>启动器版本（写入 ${launcher_version}）。</summary>
    public const string LauncherVersion = "2.6.0";

    /// <summary>离线账号类型（写入 ${user_type}）。</summary>
    public const string OfflineUserType = "mojang";

    /// <summary>要求的最低 Java 主版本号。</summary>
    public const int MinimumJavaMajorVersion = 21;

    /// <summary>自动修复调大内存时的上限（MB），超过此值不再自动调大。</summary>
    public const int MaxRepairMemoryMb = 12288;

    /// <summary>自动修复（始终开启策略）单次启动的最大尝试次数，避免无限循环。</summary>
    public const int MaxRepairAttempts = 5;

    /// <summary>Fabric 官方的 Fabric API 项目 slug（用于自动安装）。</summary>
    public const string FabricApiProjectId = "fabric-api";

    /// <summary>Fabric Loader maven 坐标前缀。</summary>
    public const string FabricLoaderGroup = "net.fabricmc";

    // ---- 镜像源：BMCLAPI 优先，官方回退 ----

    public const string BmclapiBase = "https://bmclapi2.bangbang93.com";
    // Piston 是 Mojang 当前的官方元数据服务（launchermeta.mojang.com 已废弃）；核心文件清单与版本 JSON 均托管于此。
    // 版本清单使用 v2（version_manifest_v2.json），在 v1 基础上为每条版本增加了 sha1 / complianceLevel 字段。
    public const string OfficialMetaBase = "https://piston-meta.mojang.com";
    public const string OfficialLibrariesBase = "https://libraries.minecraft.net";
    public const string OfficialAssetsBase = "https://resources.download.minecraft.net";
    public const string BmclapiVersionManifest = BmclapiBase + "/mc/game/version_manifest_v2.json";
    public const string OfficialVersionManifest = OfficialMetaBase + "/mc/game/version_manifest_v2.json";

    public const string FabricMetaBase = "https://meta.fabricmc.net/v2";
    public const string FabricMavenBase = "https://maven.fabricmc.net";

    public const string BmclapiForgeList = BmclapiBase + "/forge/minecraft";
    public const string OfficialForgeBase = "https://files.minecraftforge.net";

    public const string ModrinthApiBase = "https://api.modrinth.com/v2";

    public const string AdoptiumApiBase = "https://api.adoptium.net/v3";

    /// <summary>本项目在 CNB 的仓库地址（关于页链接、地图站 User-Agent 均指向此处）。</summary>
    public const string CnbRepoUrl = "https://cnb.cool/RLRS-Studio/MCLCS-WPF";

    /// <summary>CNB 仓库 git 地址（发布页链接、singlefile 包按 v{版本} 格式发布）。</summary>
    public const string CnbRepoGitUrl = CnbRepoUrl + ".git";

    /// <summary>本项目在 GitHub 的镜像仓库地址（cnb.cool/RLRS-Studio/MCLCS-WPF 的代码镜像，仅作源码备份，不参与更新下载）。</summary>
    public const string GitHubRepoUrl = "https://github.com/BingJian-REMIX/MCLCS-WPF";

    /// <summary>更新包（single-file zip）托管在 CNB Release；latest.json 的 downloadUrl 优先，
    /// 仅当 downloadUrl 缺失时由 LauncherUpdater 兜底构造此处地址。最新版本信息（latest.json）本身托管在 GitHub Pages，不再依赖 CNB。</summary>
    public const string CnbReleaseBase = "https://cnb.cool/RLRS-Studio/MCLCS-Linux";

    /// <summary>
    /// 更新信息（latest.json）静态地址：GitHub Pages 托管的 <c>latest.json</c>
    /// （<c>remix-laser-raising-studio.github.io/MCLCS-upgrade</c>，GitHub Pages 走独立 CDN，通常不受 github.com 故障影响）。
    /// 普通 HTTPS GET 即可读取，终端用户零 git 依赖；网络超时/失败即视为「已是最新」，绝不误报。
    /// 字段与解析逻辑见 <see cref="MCLCS.Core.Update.LauncherUpdater"/>。
    /// </summary>
    public const string UpdateInfoUrl = "https://remix-laser-raising-studio.github.io/MCLCS-upgrade/latest.json";

    /// <summary>
    /// 本启动器平台标识，对应 MCLCS-upgrade/latest.json 中各自的平台小节键名（wpf / linux / android）。
    /// 升级站点为 WPF / Linux / Android 三端共用，latest.json 根对象包含 <c>wpf</c>/<c>linux</c>/<c>android</c> 三个小节，
    /// 本启动器仅读取与 PlatformId 同名的 <c>linux</c> 小节；若根对象即单平台结构（旧格式）则整体解析。</summary>
    public const string PlatformId = "linux";
}
