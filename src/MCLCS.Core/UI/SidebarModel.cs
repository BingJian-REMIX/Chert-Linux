using System.Text.Json.Serialization;

namespace MCLCS.Core.UI;

/// <summary>侧边栏副标签项。</summary>
public sealed class SidebarItem
{
    public SidebarItem(string id, string title, string icon, int order, string? group = null, bool bottom = false)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Order = order;
        Group = group;
        Bottom = bottom;
    }

    public string Id { get; }
    public string Title { get; }
    public string Icon { get; }
    public int Order { get; }

    /// <summary>分组名（null 表示不分组，工具箱用它做四组分区）。</summary>
    public string? Group { get; }

    /// <summary>是否固定在侧边栏底部。</summary>
    public bool Bottom { get; }

    /// <summary>徽标数字，0 表示不显示；-1 表示仅显示红点。</summary>
    public int Badge { get; set; }

    public bool HasBadge => Badge != 0;
    public string BadgeText => Badge < 0 ? "" : (Badge > 99 ? "99+" : Badge.ToString());
}

/// <summary>
/// 全局侧边栏状态机：默认折叠（仅图标），鼠标悬停展开、移出折叠（与 UI 模板 launcher19.html 一致）。
/// 点击副标签仅切换选中项，不改变展开/收起——不再「保持展开」（那是计划案里与模板冲突的设定，已按模板移除）。
/// 纯逻辑，界面层只负责把鼠标事件与计时器转成 <see cref="HoverEnter"/> / <see cref="HoverLeave"/> 调用。
/// </summary>
public class SidebarState
{
    /// <summary>折叠宽度（px）。规格 1.4：48-56。</summary>
    public const double CollapsedWidth = 56;

    /// <summary>展开宽度（px）。规格 1.4：140-160，模板取 152。</summary>
    public const double ExpandedWidth = 152;

    /// <summary>悬停多久后展开（毫秒）。</summary>
    public const int HoverExpandDelayMs = 150;

    /// <summary>移出多久后收起（毫秒）。规格 1.4 明确 300ms。</summary>
    public const int HoverCollapseDelayMs = 300;

    /// <summary>展开 / 收起动画时长（毫秒）。规格 1.4：200ms 缓动。</summary>
    public const int TransitionMs = 200;

    /// <summary>选中指示竖线宽度（px）。</summary>
    public const double IndicatorWidth = 3;

    /// <summary>当前是否展开。</summary>
    public bool Expanded { get; private set; }

    /// <summary>鼠标是否在侧边栏范围内。</summary>
    public bool Hovering { get; private set; }

    /// <summary>当前应用的宽度（仅由悬停展开状态决定）。</summary>
    public double Width => Expanded ? ExpandedWidth : CollapsedWidth;

    /// <summary>当前选中项 Id。</summary>
    public string? SelectedId { get; private set; }

    /// <summary>当前所属主标签（决定副标签集合）。</summary>
    public MainTabKind Owner { get; private set; } = MainTabKind.Download;

    /// <summary>鼠标进入：延迟由界面层负责，回调到此方法时即展开。</summary>
    public void HoverEnter()
    {
        Hovering = true;
        Expanded = true;
    }

    /// <summary>鼠标离开：收起（与模板一致，不钉住）。</summary>
    public void HoverLeave()
    {
        Hovering = false;
        Expanded = false;
    }

    /// <summary>
    /// 点击副标签：仅切换选中项，不改变展开/收起状态（展开由悬停决定，与 launcher19.html 模板一致）。
    /// </summary>
    public void Select(string? id)
    {
        SelectedId = id;
    }

    /// <summary>
    /// 切换主标签：重置副标签集合，选中该页第一项。
    /// 游戏页无侧边栏，调用后 <see cref="SelectedId"/> 为 null。
    /// </summary>
    public void SwitchOwner(MainTabKind owner)
    {
        Owner = owner;
        var items = Sidebar.For(owner);
        SelectedId = items.Count > 0 ? items[0].Id : null;
    }

    /// <summary>从配置恢复（仅恢复上次选中项；展开状态始终由悬停决定）。</summary>
    public void Restore(SidebarConfig cfg)
    {
        var items = Sidebar.For(Owner);
        SelectedId = !string.IsNullOrWhiteSpace(cfg.LastSelectedId) && items.Any(i => i.Id == cfg.LastSelectedId)
            ? cfg.LastSelectedId
            : items.FirstOrDefault()?.Id;
    }

    /// <summary>写回配置。</summary>
    public SidebarConfig Capture() => new() { LastSelectedId = SelectedId };
}

/// <summary>侧边栏持久化配置。</summary>
public class SidebarConfig
{
    [JsonPropertyName("lastSelectedId")]
    public string? LastSelectedId { get; set; }

    /// <summary>是否启用悬停展开（关闭后侧边栏保持折叠，仅手动展开）。</summary>
    [JsonPropertyName("hoverExpand")]
    public bool HoverExpand { get; set; } = true;
}

/// <summary>
/// 副标签注册表：按主标签分组。
/// 游戏页无侧边栏（规格 2.1）；下载 5 项（2.2）；工具箱 18 项（2.3，开发工具拆 4 子项）；设置 8 项（2.4）。
/// </summary>
public static class Sidebar
{
    /// <summary>下载页副标签（规格 2.2 + Minecraft 版本下载）。</summary>
    public static IReadOnlyList<SidebarItem> Download { get; } = new List<SidebarItem>
    {
        new("minecraft",    "tab.minecraft",    "download", 0),
        new("mod",          "tab.mods",         "mod",      1),
        new("shader",       "tab.shader",       "shader",   2),
        new("resourcepack", "tab.resourcepack", "tex",      3),
        new("modpack",      "lbl.modpack",      "pack",     4),
        new("map",          "tab.map",          "map",      5),
        new("shadertoken",  "tool.shadertoken", "token",    6),
        new("center",       "tool.dlcenter",    "download", 7)
    };

    /// <summary>工具箱页副标签（规格 2.3，种子库已移除并并入存档管理器）。</summary>
    public static IReadOnlyList<SidebarItem> Toolbox { get; } = new List<SidebarItem>
    {
        // 诊断与排障
        new("log",        "tool.log",        "log",       0, "tool.group.diag"),
        new("crash",      "tool.crash",      "bug",       1, "tool.group.diag"),
        new("perf",       "tool.perf",       "perf",      2, "tool.group.diag"),
        new("network",    "tool.network",    "net",       3, "tool.group.diag"),
        new("datapack",   "tool.datapack",   "dp",        4, "tool.group.diag"),

        // 资源与内容
        new("saves",      "tool.saves",      "save",      6, "tool.group.resource"),
        new("backup",     "tool.backup",     "backup",    7, "tool.group.resource"),
        new("screenshot", "tool.screenshot", "shot",      8, "tool.group.resource"),
        new("clean",      "tool.clean",      "clean",     9, "tool.group.resource"),
        new("modpackio",  "tool.modpackio",  "modpack",  10, "tool.group.resource"),
        new("music",      "tool.music",      "music",    11, "tool.group.resource"),
        new("serverpack", "tool.serverpack", "tex",      12, "tool.group.resource"),

        // 开发工具
        new("moddev",     "tool.moddev",     "dev",      12, "tool.group.dev"),
        new("packmaker",  "tool.packmaker",  "dev",      13, "tool.group.dev"),
        new("nbt",        "tool.nbt",        "dev",      14, "tool.group.dev"),
        new("command",    "tool.command",    "dev",      15, "tool.group.dev"),
        new("skin",       "tool.skin",       "skin",     16, "tool.group.dev"),
        new("shortcut",   "tool.shortcut",   "shortcut", 17, "tool.group.dev"),

        // 其他
        new("afk",        "tool.afk",        "flowchart",18, "tool.group.other"),
        new("aichat",     "tool.aichat",     "ai",       19, "tool.group.other"),
        new("addserver",  "tool.addserver",  "server",   21, "tool.group.other"),
        new("achievement", "tool.achievement", "save",    22, "tool.group.other"),

        // 开发工具聚合入口（移除 toolbox/achievement/annual 常驻项）
        new("devtools",    "tool.devtools",    "dev",      20, "tool.group.dev")
    };

    /// <summary>
    /// 设置页副标签：置空。设置不再以侧边栏子页呈现，而是收敛为单 SettingsView
    /// （左侧分类列表 + 右侧内容区，对应 WPF 的单 SettingsView），分类切换在页内完成。
    /// </summary>
    public static IReadOnlyList<SidebarItem> Settings { get; } = new List<SidebarItem>();

    /// <summary>游戏页无侧边栏（规格 2.1）：默认展示主页（HomeView）全宽内容。</summary>
    public static IReadOnlyList<SidebarItem> Game { get; } = new List<SidebarItem>();

    /// <summary>取某个主标签下的副标签集合。</summary>
    public static IReadOnlyList<SidebarItem> For(MainTabKind kind) => kind switch
    {
        MainTabKind.Game => Game,
        MainTabKind.Download => Download,
        MainTabKind.Toolbox => Toolbox,
        MainTabKind.Settings => Settings,
        _ => Array.Empty<SidebarItem>()
    };

    /// <summary>某个主标签是否有侧边栏。</summary>
    public static bool Has(MainTabKind kind) => For(kind).Count > 0;

    /// <summary>在指定主标签下按 Id 查找副标签。</summary>
    public static SidebarItem? ById(MainTabKind kind, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : For(kind).FirstOrDefault(i => i.Id == id);

    /// <summary>顶部项（按 Order 升序）。</summary>
    public static IEnumerable<SidebarItem> Top(MainTabKind kind) =>
        For(kind).Where(i => !i.Bottom).OrderBy(i => i.Order);

    /// <summary>底部固定项（按 Order 升序）。</summary>
    public static IEnumerable<SidebarItem> Bottom(MainTabKind kind) =>
        For(kind).Where(i => i.Bottom).OrderBy(i => i.Order);

    /// <summary>按分组归并（保持声明顺序，无分组项归入 null 键）。</summary>
    public static IEnumerable<IGrouping<string?, SidebarItem>> Grouped(MainTabKind kind) =>
        For(kind).OrderBy(i => i.Order).GroupBy(i => i.Group);
}
