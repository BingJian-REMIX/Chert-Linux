namespace MCLCS.Core.Theme;

/// <summary>主题类型。</summary>
public enum ThemeType
{
    Light,
    Dark
}

/// <summary>
/// 主题管理器：存储/读取当前主题偏好，通知 App 层切换 ResourceDictionary。
/// Core 层只负责状态持久化，实际 UI 切换由 App 层处理。
/// </summary>
public static class ThemeManager
{
    private static ThemeType _current = ThemeType.Light;

    /// <summary>当前主题。</summary>
    public static ThemeType Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            OnThemeChanged?.Invoke(value);
        }
    }

    /// <summary>主题变更事件（App 层订阅以切换 ResourceDictionary）。</summary>
    public static event Action<ThemeType>? OnThemeChanged;

    /// <summary>
    /// 清单 #18：是否跟随操作系统主题自动切换亮 / 暗。
    /// 开启后由界面层监听系统主题变化并写入 <see cref="Current"/>；关闭时保持手动选择的主题。
    /// Linux 下由 Avalonia 的 RequestedThemeVariant=Default 提供（X11 / 桌面环境支持时生效）。
    /// </summary>
    public static bool FollowSystem { get; set; }

    /// <summary>
    /// 清单 #12 / #15 / #16 / #17：界面风格 Id（standard / android / glass / dynamic）。
    /// 与 <see cref="Current"/>（亮 / 暗）正交，由 App 层叠加对应风格资源字典。
    /// 目前 Linux 端尚未提供各风格的资源字典，字段仅用于与 WPF 端配置互通。
    /// </summary>
    public static string UiStyle { get; private set; } = "standard";

    /// <summary>界面风格变更事件（App 层订阅以叠加 / 移除风格资源字典与沉底导航）。</summary>
    public static event Action<string>? OnUiStyleChanged;

    /// <summary>设置界面风格并通知界面层（值未变化时不重复触发）。</summary>
    public static void SetUiStyle(string id)
    {
        var v = (id ?? "standard").Trim().ToLowerInvariant();
        if (v != "android" && v != "glass" && v != "dynamic") v = "standard";
        if (UiStyle == v) return;
        UiStyle = v;
        OnUiStyleChanged?.Invoke(v);
    }

    /// <summary>从配置文件加载主题偏好。</summary>
    public static void LoadPreference(string gameRoot)
    {
        var path = System.IO.Path.Combine(gameRoot, "mclcs_theme.json");
        if (!System.IO.File.Exists(path)) return;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            var pref = System.Text.Json.JsonSerializer.Deserialize<ThemePreference>(json);
            if (pref is not null)
            {
                FollowSystem = pref.FollowSystem;
                UiStyle = string.IsNullOrWhiteSpace(pref.UiStyle) ? "standard" : pref.UiStyle.Trim().ToLowerInvariant();
                var changed = false;
                if (Enum.TryParse<ThemeType>(pref.Theme, true, out var t) && _current != t)
                {
                    _current = t;
                    changed = true;
                }
                // 上面直接写 _current 绕过了 setter，OnThemeChanged 不会触发。
                // PngIcon 靠该事件按亮/暗重新挑图标（light=纯黑 / dark=纯白），
                // 不广播就会让它们停在默认 Light 的黑图标上——深色界面里等于隐形。
                if (changed) OnThemeChanged?.Invoke(_current);
            }
        }
        catch { }
    }

    /// <summary>保存主题偏好到文件。</summary>
    public static void SavePreference(string gameRoot)
    {
        var path = System.IO.Path.Combine(gameRoot, "mclcs_theme.json");
        var pref = new ThemePreference { Theme = _current.ToString(), FollowSystem = FollowSystem, UiStyle = UiStyle };
        System.IO.Directory.CreateDirectory(gameRoot);
        System.IO.File.WriteAllText(path,
            System.Text.Json.JsonSerializer.Serialize(pref));
    }

    private class ThemePreference
    {
        public string Theme { get; set; } = "Light";

        /// <summary>清单 #18：跟随系统主题。</summary>
        public bool FollowSystem { get; set; }

        /// <summary>清单 #12：界面风格 Id（standard / android / glass / dynamic）。</summary>
        public string UiStyle { get; set; } = "standard";
    }
}
