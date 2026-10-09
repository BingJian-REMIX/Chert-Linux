namespace MCLCS.Core.UI;

/// <summary>
/// 清单 #12 / #15 / #16：界面风格。
/// 与 <see cref="MCLCS.Core.Theme.ThemeType"/>（亮 / 暗）正交——每种风格都提供亮 / 暗两版资源字典。
/// </summary>
public enum UiStyleKind
{
    /// <summary>标准风格（四色索引贴，当前默认）。</summary>
    Standard,
    /// <summary>清单 #15：简约安卓式（Material 配色 + 沉底导航）。</summary>
    Android,
    /// <summary>清单 #16：简约毛玻璃（半透明面板，强调层次与通透感）。</summary>
    Glass,
    /// <summary>清单 #17：灵动（透明标题栏 + 整页圆角彩色卡片 + 卡片横向滑动）。</summary>
    Dynamic
}

/// <summary>界面风格注册表与工具方法。</summary>
public static class UiStyles
{
    /// <summary>字符串 → 枚举（大小写不敏感，未知值回退 Standard）。</summary>
    public static UiStyleKind Parse(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "android" => UiStyleKind.Android,
        "glass" => UiStyleKind.Glass,
        "dynamic" => UiStyleKind.Dynamic,
        _ => UiStyleKind.Standard
    };

    /// <summary>枚举 → 字符串 Id。</summary>
    public static string ToId(UiStyleKind kind) => kind switch
    {
        UiStyleKind.Android => "android",
        UiStyleKind.Glass => "glass",
        UiStyleKind.Dynamic => "dynamic",
        _ => "standard"
    };

    /// <summary>清单 #15：该风格是否使用沉底导航（安卓式特性）。</summary>
    public static bool UsesBottomNav(UiStyleKind kind) => kind == UiStyleKind.Android;

    /// <summary>所有可选风格（按展示顺序）。</summary>
    public static UiStyleKind[] All { get; } =
    {
        UiStyleKind.Standard,
        UiStyleKind.Android,
        UiStyleKind.Glass,
        UiStyleKind.Dynamic
    };
}
