using System.Text.Json.Serialization;
using MCLCS.Core.Ai;
using MCLCS.Core.Hud;
using MCLCS.Core.Lan;
using MCLCS.Core.Launcher;
using MCLCS.Core.Recommend;
using MCLCS.Core.Resources;
using MCLCS.Core.Toolbox;
using MCLCS.Core.UI;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Profiles;

/// <summary>下载源偏好（设置 → 下载）。</summary>
public enum DownloadSourcePreference
{
    /// <summary>镜像优先（BMCLAPI 等），失败回退官方。</summary>
    MirrorFirst,
    /// <summary>官方优先，失败回退镜像。</summary>
    OfficialFirst
}

/// <summary>启动器持久化配置（默认用户名、内存、Java 路径等）。</summary>
public class LauncherProfile
{
    [JsonPropertyName("defaultUsername")]
    public string DefaultUsername { get; set; } = "Player";

    [JsonPropertyName("maxMemoryMb")]
    public int MaxMemoryMb { get; set; } = 2048;

    [JsonPropertyName("minMemoryMb")]
    public int MinMemoryMb { get; set; } = 512;

    [JsonPropertyName("javaPath")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("gameRoot")]
    public string GameRoot { get; set; } = GameConstants.DefaultGameRoot;

    [JsonPropertyName("lastVersionId")]
    public string? LastVersionId { get; set; }

    [JsonPropertyName("resolutionWidth")]
    public int? ResolutionWidth { get; set; }

    [JsonPropertyName("resolutionHeight")]
    public int? ResolutionHeight { get; set; }

    /// <summary>额外的 JVM 参数（如 -XX:+UseZGC）。</summary>
    [JsonPropertyName("extraJvmArgs")]
    public List<string> ExtraJvmArgs { get; set; } = new();

    /// <summary>当前活跃账号 ID（AccountEntry.Id）。</summary>
    [JsonPropertyName("lastAccountId")]
    public string? LastAccountId { get; set; }

    /// <summary>崩溃自动修复策略：始终开启 / 每次询问 / 始终拒绝（默认每次询问）。</summary>
    [JsonPropertyName("repairPolicy")]
    public CrashRepairPolicy RepairPolicy { get; set; } = CrashRepairPolicy.Ask;

    /// <summary>自动安装 Java 时首选的发行商：Auto / Temurin / Oracle（默认 Auto）。</summary>
    [JsonPropertyName("preferredJavaVendor")]
    public JavaVendor PreferredJavaVendor { get; set; } = JavaVendor.Auto;

    /// <summary>启动时自动安装缺失的 Mod 前置依赖：开启 / 询问 / 关闭（默认询问）。</summary>
    [JsonPropertyName("autoInstallMissingMods")]
    public AutoInstallPolicy AutoInstallMissingMods { get; set; } = AutoInstallPolicy.Ask;

    /// <summary>智能推荐总开关：启用推荐 / 仅本地 / 禁用（默认启用）。</summary>
    [JsonPropertyName("intelliRecommend")]
    public IntelliRecommendMode IntelliRecommend { get; set; } = IntelliRecommendMode.Enabled;

    /// <summary>用户感兴趣的玩法分区（默认全选）。用于优先展示对应类别的推荐。</summary>
    [JsonPropertyName("preferredCategories")]
    public List<GameplayCategory> PreferredCategories { get; set; } = new()
    {
        GameplayCategory.Tech, GameplayCategory.Building, GameplayCategory.Adventure,
        GameplayCategory.Magic, GameplayCategory.Optimization, GameplayCategory.Utility
    };

    // ---- 通用（设置 → 通用）----

    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh_CN";

    [JsonPropertyName("autoStartLauncher")]
    public bool AutoStartLauncher { get; set; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>文件变更检测（规格 2.3-16 / 3.13）：启动或焦点回归时检测手动丢入 mods/resourcepacks/shaderpacks 的新文件。</summary>
    [JsonPropertyName("fileWatchEnabled")]
    public bool FileWatchEnabled { get; set; } = true;

    // ---- 下载（设置 → 下载）----

    [JsonPropertyName("downloadSource")]
    public DownloadSourcePreference DownloadSource { get; set; } = DownloadSourcePreference.MirrorFirst;

    [JsonPropertyName("maxConcurrentDownloads")]
    public int MaxConcurrentDownloads { get; set; } = 8;

    // ---- 外观（设置 → 外观）----

    [JsonPropertyName("themeColor")]
    public string ThemeColor { get; set; } = "#3a7b4f";

    [JsonPropertyName("backgroundImagePath")]
    public string? BackgroundImagePath { get; set; }

    [JsonPropertyName("fontScale")]
    public double FontScale { get; set; } = 1.0;

    /// <summary>适配高分辨率屏幕：启用后 UI 图标加载 2x 高清资源（设置 → 外观，默认关闭）。</summary>
    [JsonPropertyName("highDpiIcons")]
    public bool HighDpiIcons { get; set; }

    /// <summary>显示控件边框：开启后按钮/输入框/卡片/面板等描边可见；关闭则透明隐藏（设置 → 外观，默认开启）。</summary>
    [JsonPropertyName("showControlBorders")]
    public bool ShowControlBorders { get; set; } = true;

    // ---- 关于 / 更新 ----

    [JsonPropertyName("autoUpdateCheck")]
    public bool AutoUpdateCheck { get; set; } = true;

    // ---- AI 助手（设置 → AI 助手）----

    [JsonPropertyName("ai")]
    public AiConfig Ai { get; set; } = new();

    // ---- 账号（设置 → 账号）----

    /// <summary>Microsoft OAuth 应用的 client_id（可选）。留空时使用内置默认 client_id；设备代码流无需配置任何回跳地址。</summary>
    [JsonPropertyName("microsoftOAuthClientId")]
    public string MicrosoftOAuthClientId { get; set; } = "";

    // ---- v2.0 新增 ----

    /// <summary>index 四色主标签的配色（可自定义）。</summary>
    [JsonPropertyName("tabTheme")]
    public TabThemeConfig TabTheme { get; set; } = new();

    /// <summary>全局侧边栏（悬停展开 / 钉住）。</summary>
    [JsonPropertyName("sidebar")]
    public SidebarConfig Sidebar { get; set; } = new();

    /// <summary>游戏内 HUD 悬浮窗（默认关闭）。</summary>
    [JsonPropertyName("hud")]
    public HudConfig Hud { get; set; } = new();

    /// <summary>启动预热。</summary>
    [JsonPropertyName("prewarm")]
    public PrewarmConfig Prewarm { get; set; } = new();

    /// <summary>启动前存档兼容性检测（规格 2.4 — 启动）。</summary>
    [JsonPropertyName("launchCompatCheckEnabled")]
    public bool LaunchCompatCheckEnabled { get; set; } = true;

    /// <summary>备份保留策略。</summary>
    [JsonPropertyName("backup")]
    public BackupPolicy Backup { get; set; } = new();

    /// <summary>服务器资源包缓存容量上限（MB）。</summary>
    [JsonPropertyName("serverPackCacheMb")]
    public int ServerPackCacheMb { get; set; } = ServerResourcePackCache.DefaultCapacityMb;

    /// <summary>进服时自动检测并修复资源包问题。</summary>
    [JsonPropertyName("autoRepairResourcePacks")]
    public bool AutoRepairResourcePacks { get; set; } = true;

    /// <summary>服务器资源包缓存总开关（规格 2.4 — 下载）。关闭时进服不缓存。</summary>
    [JsonPropertyName("serverPackCacheEnabled")]
    public bool ServerPackCacheEnabled { get; set; } = true;

    /// <summary>已保存的挂机工作流 Token（名称 → Token）。</summary>
    [JsonPropertyName("afkWorkflows")]
    public Dictionary<string, string> AfkWorkflows { get; set; } = new();

    /// <summary>已收藏的光影配置 Token（名称 → Token）。</summary>
    [JsonPropertyName("shaderTokens")]
    public Dictionary<string, string> ShaderTokens { get; set; } = new();

    /// <summary>音乐播放器：游戏启动时自动暂停 / 降音量（规格 2.3）。</summary>
    [JsonPropertyName("musicAutoDuck")]
    public bool MusicAutoDuck { get; set; } = true;

    /// <summary>音乐播放器音量（0-100）。</summary>
    [JsonPropertyName("musicVolume")]
    public int MusicVolume { get; set; } = 60;

    /// <summary>
    /// 本地客户端模式设置（总开关 / 宽限期 / 歌词固定方式 / 客户端模式歌词开关）。
    /// 读出时由 <see cref="ProfileStore.Load"/> 调 <see cref="MusicClientPrefs.Normalized"/> 容错。
    /// </summary>
    [JsonPropertyName("musicClient")]
    public MusicClientPrefs MusicClient { get; set; } = new();

    /// <summary>启动时自动断点续播：恢复上次停下的曲目与位置。</summary>
    [JsonPropertyName("musicResumeOnLaunch")]
    public bool MusicResumeOnLaunch { get; set; }

    /// <summary>本地客户端模式下用户选择的音乐客户端可执行文件路径（当遥控器用）。</summary>
    [JsonPropertyName("musicClientExePath")]
    public string MusicClientExePath { get; set; } = "";

    /// <summary>断点续播：上次播放的本地曲目路径（空表示无）。</summary>
    [JsonPropertyName("musicLastTrack")]
    public string MusicLastTrack { get; set; } = "";

    /// <summary>断点续播：上次停下的位置（秒）。</summary>
    [JsonPropertyName("musicLastPosition")]
    public double MusicLastPosition { get; set; }

    /// <summary>音乐播放器：上次打开的本地音乐文件夹（空表示无，下次从默认位置打开）。</summary>
    [JsonPropertyName("musicLastFolder")]
    public string MusicLastFolder { get; set; } = "";

    /// <summary>
    /// 在线音源设置（数据源协议 / 服务地址 / 音质 / 登录凭证）。
    /// 读出时由 <see cref="ProfileStore.Load"/> 调 <see cref="MusicApiPrefs.Normalized"/> 容错。
    /// </summary>
    [JsonPropertyName("musicApi")]
    public MusicApiPrefs MusicApi { get; set; } = new();

    /// <summary>新建版本的默认隔离模式（通用设置「版本隔离」默认项，bug2.txt #9）。</summary>
    [JsonPropertyName("defaultVersionIsolation")]
    public IsolationMode DefaultVersionIsolation { get; set; } = IsolationMode.Auto;

    /// <summary>清单 #35：局域网联动（默认关闭，需用户显式开启）。</summary>
    [JsonPropertyName("lanLink")]
    public LanLinkConfig LanLink { get; set; } = LanLinkConfig.CreateDefault();
}

/// <summary>缺失 Mod 前置依赖的自动安装策略。</summary>
public enum AutoInstallPolicy
{
    /// <summary>始终自动安装缺失的前置依赖。</summary>
    Always,

    /// <summary>每次询问用户是否安装。</summary>
    Ask,

    /// <summary>从不自动安装（仅提示）。</summary>
    Never
}
