namespace MCLCS.Core.Badges;

/// <summary>勋章分类。</summary>
public enum BadgeCategory
{
    /// <summary>启动器使用（首次启动、累计启动次数等）。</summary>
    Launcher,

    /// <summary>游戏内行为（累计启动游戏、进入世界次数等）。</summary>
    Game,

    /// <summary>节日 / 限时活动。</summary>
    Seasonal,

    /// <summary>社区 / 联动。</summary>
    Community,

    /// <summary>彩蛋：解锁前不对外展示。</summary>
    Hidden
}

/// <summary>
/// 勋章定义（静态数据，随启动器发版，不进存档）。
/// 清单 #49 为「接口预留」阶段：只落地本地数据结构与持久化，
/// <see cref="BadgeProfile.UserId"/> 作为账号系统接入后的回填字段。
/// </summary>
public class BadgeDefinition
{
    /// <summary>稳定标识（小写 + 点分，跨版本不可更改）。</summary>
    public string Id { get; set; } = "";

    /// <summary>展示名（中文；后续如需多语再改为本地化 key）。</summary>
    public string Title { get; set; } = "";

    /// <summary>解锁条件说明。</summary>
    public string Description { get; set; } = "";

    public BadgeCategory Category { get; set; } = BadgeCategory.Launcher;

    /// <summary>展示用图标字符（emoji 或单字），UI 层可直接显示。</summary>
    public string Icon { get; set; } = "";

    /// <summary>进度目标；0 表示一次性解锁（不显示进度条）。</summary>
    public int Target { get; set; }

    /// <summary>彩蛋类勋章在解锁前不对外展示。</summary>
    public bool IsHidden => Category == BadgeCategory.Hidden;
}

/// <summary>单枚勋章的本地进度记录。</summary>
public class BadgeRecord
{
    public string BadgeId { get; set; } = "";

    /// <summary>当前进度（0 ~ Definition.Target）。</summary>
    public int Progress { get; set; }

    /// <summary>是否已解锁。</summary>
    public bool Unlocked { get; set; }

    /// <summary>解锁时间（未解锁为空）。</summary>
    public DateTime? EarnedAt { get; set; }
}

/// <summary>勋章存档（<c>mclcs_badges.json</c>）。</summary>
public class BadgeProfile
{
    /// <summary>存档格式版本，便于后续迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 本机设备标识：首次读取时生成并持久化，之后固定不变。
    /// 用于后续「勋章下发 / 核对」时标识设备，与账号无关。
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 账号系统接入后回填的用户标识。当前账号体系尚未打通，恒为空；
    /// 字段与 JSON key（<c>userId</c>）先固定下来，避免届时改数据结构。
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>已记录进度的勋章；未出现的勋章视为 0 进度。</summary>
    public List<BadgeRecord> Badges { get; set; } = new();
}

/// <summary>内置勋章标识常量（与 <see cref="BadgeService"/> 的目录一一对应）。</summary>
public static class BadgeIds
{
    /// <summary>首次启动启动器（一次性）。</summary>
    public const string LauncherFirstStart = "launcher.first_start";

    /// <summary>累计启动启动器 10 次。</summary>
    public const string LauncherLaunchCount = "launcher.launch_count";

    /// <summary>累计启动游戏 20 次。</summary>
    public const string GamePlayCount = "game.play_count";

    /// <summary>累计安装 10 个 Mod。</summary>
    public const string GameModInstall = "game.mod_install";

    /// <summary>参与首个节日活动（一次性）。</summary>
    public const string SeasonalFirstEvent = "seasonal.first_event";

    /// <summary>彩蛋（解锁前隐藏）。</summary>
    public const string HiddenEasterEgg = "hidden.easter_egg";
}
