using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Theme;
using MCLCS.Core.UI;
using MCLCS.Core.Utils;
using MCLCS.Linux.App;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>单个 Java 安装的展示模型（桥接 Core 的 JavaInfo）。</summary>
public class JavaEntry
{
    public string Exe { get; set; } = "";
    public int Major { get; set; }
    public string Raw { get; set; } = "";
    public override string ToString() => $"Java {Major}  {Exe}  ({Raw})";
}

/// <summary>
/// 主窗口视图模型：直接复用 Core.UI 的平台无关视图模型
/// （MainTabs / Sidebar / ObservableObject / TabThemeConfig），实现与 MCLCS.App 一致的四色标签 + 侧边栏布局，
/// 并在此之上接入本地化层与主题自定义（均来自 Core，非伪造）。
/// </summary>
public class MainViewModel : ObservableObject
{
    /// <summary>单例引用，供 KindToBrushConverter 在 XAML 模板加载时取主题色。</summary>
    public static MainViewModel? Instance { get; set; }

    /// <summary>持久化的启动器配置（来自 Core；外观设置真正链接到这里，而非空壳）。</summary>
    private readonly LauncherProfile _profile = ProfileStore.Load(GameConstants.DefaultGameRoot);

    /// <summary>四色主标签（来自 Core.UI.MainTabs.All），保留以兼容既有绑定。</summary>
    public IReadOnlyList<MainTabDefinition> Tabs => MainTabs.All;

    /// <summary>四色主标签的可绑定包装集合，驱动索引贴的展开 / 选中动画与重叠 Z 序。</summary>
    public ObservableCollection<TabItemViewModel> TabItems { get; }

    /// <summary>四色标签配色（Core.TabThemeConfig，直接复用 profile.TabTheme，编辑即持久化）。</summary>
    public TabThemeConfig Theme => _profile.TabTheme;

    // ===== 语言 / 主题（对齐 WPF SettingsView：语言在「通用」，主题在「外观」）=====

    /// <summary>当前语言（zh_CN / en_US）。切换时即时写入 LocaleManager 并持久化到 profile。</summary>
    public string SelectedLanguage
    {
        get => LocaleManager.CurrentLocale;
        set
        {
            var norm = LocaleManager.NormalizeLocaleCode(value);
            if (!string.Equals(LocaleManager.CurrentLocale, norm, StringComparison.OrdinalIgnoreCase))
            {
                LocaleManager.CurrentLocale = norm;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>当前主题（Dark / Light）。切换时即时写入 ThemeManager 并持久化。</summary>
    public string SelectedTheme
    {
        get => ThemeManager.Current.ToString();
        set
        {
            if (Enum.TryParse<ThemeType>(value, true, out var t) && ThemeManager.Current != t)
            {
                ThemeManager.Current = t;
                ThemeManager.SavePreference(AppConfig.DataRoot);
                OnPropertyChanged();
            }
        }
    }

    // ===== 外观（对齐 WPF 设置 → 外观：主题色 / 背景图 / 字体缩放，实时生效并持久化到 profile）=====

    private string _themeColor = "#3a7b4f";
    /// <summary>全局强调色（hex）。修改即时覆盖 Accent 系列资源并写入 profile。</summary>
    public string ThemeColor
    {
        get => _themeColor;
        set
        {
            var hex = (value ?? "").Trim();
            if (!TabThemeConfig.IsValidColor(hex)) return;
            if (SetField(ref _themeColor, hex))
            {
                App.ApplyAccentColor(hex);
                _profile.ThemeColor = hex;
                PersistProfile();
            }
        }
    }

    private string _backgroundImagePath = "";
    /// <summary>主窗口背景图片路径。修改即时预览并持久化。</summary>
    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set
        {
            var path = (value ?? "").Trim();
            if (SetField(ref _backgroundImagePath, path))
            {
                App.ApplyBackgroundImage(string.IsNullOrWhiteSpace(path) ? null : path);
                _profile.BackgroundImagePath = string.IsNullOrWhiteSpace(path) ? null : path;
                PersistProfile();
            }
        }
    }

    private double _fontScale = 1.0;
    /// <summary>字体缩放系数（0.8–1.5）。修改即时缩放主窗口字号并持久化。</summary>
    public double FontScale
    {
        get => _fontScale;
        set
        {
            var s = value <= 0 ? 1.0 : value;
            if (SetField(ref _fontScale, s))
            {
                App.ApplyFontScale(s);
                _profile.FontScale = s;
                PersistProfile();
            }
        }
    }

    /// <summary>保存外观设置到 profile（四色标签色一并写入，因 Theme 复用 profile.TabTheme）。</summary>
    public void PersistProfile() => ProfileStore.Save(_profile);

    private MainTabDefinition _selectedTab = MainTabs.Get(MainTabKind.Game);

    public MainViewModel()
    {
        var all = MainTabs.All;
        var items = new ObservableCollection<TabItemViewModel>();
        for (var i = 0; i < all.Count; i++)
            items.Add(new TabItemViewModel(all[i], i, all.Count));
        TabItems = items;
        Instance = this;
        _selectedTab = MainTabs.Get(MainTabKind.Game);
        _selectedSidebarId = Sidebar.For(_selectedTab.Kind).FirstOrDefault()?.Id ?? "";
        SyncTabSelection();
        // 外观：从 profile 载入主题色 / 背景图 / 字体缩放（链接 core，消除空壳）
        _themeColor = _profile.ThemeColor;
        _backgroundImagePath = _profile.BackgroundImagePath ?? "";
        _fontScale = _profile.FontScale > 0 ? _profile.FontScale : 1.0;
        // 语言切换时刷新所有数据驱动文本与索引贴显示名
        LocaleManager.LocaleChanged += OnLocaleChanged;
    }

    /// <summary>语言切换回调：刷新索引贴显示名与所有本地化文本属性。</summary>
    private void OnLocaleChanged(string _)
    {
        foreach (var t in TabItems)
            t.RaiseDisplayNameChanged();
        OnPropertyChanged(nameof(PanelTitle));
        OnPropertyChanged(nameof(PanelGroup));
        OnPropertyChanged(nameof(PanelDescription));
        OnPropertyChanged(nameof(InstalledCountText));
        OnPropertyChanged(nameof(RunningInstancesText));
        OnPropertyChanged(nameof(NetworkStatusText));
        OnPropertyChanged(nameof(JavaVersionText));
        OnPropertyChanged(nameof(Status));
        // 界面文案（标题栏 / 游戏主页 / 主题编辑器 / Java 区 / 下拉项）
        OnPropertyChanged(nameof(SearchWatermark));
        OnPropertyChanged(nameof(GameHomeTitle));
        OnPropertyChanged(nameof(GameHomeDesc));
        OnPropertyChanged(nameof(ThemeEditorTitle));
        OnPropertyChanged(nameof(ThemeEditorHint));
        OnPropertyChanged(nameof(LabelGame));
        OnPropertyChanged(nameof(LabelDownload));
        OnPropertyChanged(nameof(LabelToolbox));
        OnPropertyChanged(nameof(LabelSettings));
        OnPropertyChanged(nameof(JavaSectionTitle));
        OnPropertyChanged(nameof(JavaDetectButton));
        OnPropertyChanged(nameof(LangChinese));
        OnPropertyChanged(nameof(LangEnglish));
        OnPropertyChanged(nameof(ThemeDarkLabel));
        OnPropertyChanged(nameof(ThemeLightLabel));
    }

    /// <summary>把 TabItems 的 IsSelected 对齐到当前 SelectedTab，驱动索引贴展开 / Z 序动画。</summary>
    private void SyncTabSelection()
    {
        foreach (var t in TabItems)
            t.IsSelected = t.Kind == _selectedTab.Kind;
    }

    /// <summary>当前选中的主标签；切换时联动侧边栏集合与右面板标题。</summary>
    public MainTabDefinition SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetField(ref _selectedTab, value))
            {
                SelectedSidebarId = Sidebar.For(value.Kind).FirstOrDefault()?.Id ?? "";
                OnPropertyChanged(nameof(SidebarItems));
                OnPropertyChanged(nameof(PanelTitle));
                OnPropertyChanged(nameof(PanelGroup));
                OnPropertyChanged(nameof(PanelDescription));
                OnPropertyChanged(nameof(ShowThemeEditor));
                OnPropertyChanged(nameof(TitleBarColor));
                OnPropertyChanged(nameof(HasSidebar));
                OnPropertyChanged(nameof(IsToolboxTab));
                OnPropertyChanged(nameof(SidebarItems));
                SyncTabSelection();
            }
        }
    }

    /// <summary>当前主标签下的副标签集合（来自 Core.UI.Sidebar.For）。</summary>
    public IReadOnlyList<SidebarItem> SidebarItems => Sidebar.For(_selectedTab.Kind);

    private string _selectedSidebarId = "";

    /// <summary>当前选中的副标签 Id；变化时联动右面板。</summary>
    public string SelectedSidebarId
    {
        get => _selectedSidebarId;
        set
        {
            if (SetField(ref _selectedSidebarId, value))
            {
                OnPropertyChanged(nameof(PanelTitle));
                OnPropertyChanged(nameof(PanelGroup));
                OnPropertyChanged(nameof(PanelDescription));
            }
        }
    }

    private SidebarItem? SelectedSidebarItem =>
        Sidebar.ById(_selectedTab.Kind, _selectedSidebarId);

    /// <summary>右面板标题（副标签本地化名，无副标签时取主标签名）。</summary>
    public string PanelTitle => Localization.Get(SelectedSidebarItem?.Title ?? _selectedTab.Title);

    /// <summary>右面板分组（副标签所在组，无则为空）。</summary>
    public string PanelGroup => SelectedSidebarItem?.Group is { } g ? Localization.Get(g) : "";

    /// <summary>右面板一句话说明（来自本地化描述表）。</summary>
    public string PanelDescription =>
        SelectedSidebarItem is { } s ? Localization.ToolDescription(s.Id) : "";

    /// <summary>是否在右面板展示主题编辑器（设置页 → 外观）。</summary>
    public bool ShowThemeEditor =>
        _selectedTab.Kind == MainTabKind.Settings && _selectedSidebarId == "appearance";

    /// <summary>主题色被改后，强制刷新主标签的 ItemsSource 以重算颜色，
    /// 并通知 SelectedTab 让标题栏底色 / 内容渐隐带 / 侧栏指示线一并按新色重算。</summary>
    public void RefreshTabs()
    {
        OnPropertyChanged(nameof(Tabs));
        OnPropertyChanged(nameof(TitleBarColor));
        OnPropertyChanged(nameof(SelectedTab));
    }

    /// <summary>标题栏背景色：跟随当前主标签（对齐 WPF 的 TitleBarBrush）。</summary>
    public string TitleBarColor => Theme.ColorOf(_selectedTab.Kind);

    private bool _sidebarExpanded;
    /// <summary>侧边栏是否展开（折叠时仅图标，悬停展开显示文字）。由界面层悬停事件驱动。</summary>
    public bool SidebarExpanded
    {
        get => _sidebarExpanded;
        set => SetField(ref _sidebarExpanded, value);
    }

    /// <summary>当前主标签是否带侧边栏（游戏页无侧边栏，对齐 WPF 规格 2.1）。</summary>
    public bool HasSidebar => Sidebar.For(_selectedTab.Kind).Count > 0;

    /// <summary>当前是否处于工具箱主标签（用于隐藏页头的「分组」与介绍段落）。</summary>
    public bool IsToolboxTab => _selectedTab.Kind == MainTabKind.Toolbox;

    // ===== 状态栏（对齐 WPF 底部 StatusBar）=====
    private string? _javaVersionText;
    /// <summary>状态栏：Java 版本（检测后填入最高大版本；未检测时本地化占位）。</summary>
    public string JavaVersionText
    {
        get => _javaVersionText ?? LocaleManager.T("status.no_java");
        private set => SetField(ref _javaVersionText, value);
    }

    /// <summary>状态栏：已安装实例数（占位，待接入 Core 实例管理）。</summary>
    public string InstalledCountText => LocaleManager.Tf("status.installed", 0);

    /// <summary>状态栏：运行中的实例数（占位）。</summary>
    public string RunningInstancesText => LocaleManager.Tf("status.running", 0);

    /// <summary>状态栏：下载进度文本（由下载页 ViewModel 回写）。</summary>
    private string _downloadText = "下载 0%";
    public string DownloadText
    {
        get => _downloadText;
        set => SetField(ref _downloadText, value);
    }

    /// <summary>状态栏：下载进度（0-100，由下载页 ViewModel 回写）。</summary>
    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => SetField(ref _downloadProgress, value);
    }

    /// <summary>状态栏：网络是否正常（占位，默认正常）。</summary>
    public bool IsNetworkOk => true;

    /// <summary>状态栏：网络状态文本（本地化）。</summary>
    public string NetworkStatusText =>
        IsNetworkOk ? LocaleManager.T("status.network_ok") : LocaleManager.T("status.network_offline");

    // ===== 本地化展示属性：标题栏 / 侧栏 / 状态栏之外的界面文案，随语言切换刷新 =====
    /// <summary>搜索框占位（lbl.search_mods）。</summary>
    public string SearchWatermark => LocaleManager.T("lbl.search_mods");
    /// <summary>游戏主页标题（tab.game）。</summary>
    public string GameHomeTitle => LocaleManager.T("tab.game");
    /// <summary>游戏主页描述（home.game.desc）。</summary>
    public string GameHomeDesc => LocaleManager.T("home.game.desc");
    /// <summary>四色主题编辑器标题（theme.editor.title）。</summary>
    public string ThemeEditorTitle => LocaleManager.T("theme.editor.title");
    /// <summary>四色主题编辑器提示（theme.editor.hint）。</summary>
    public string ThemeEditorHint => LocaleManager.T("theme.editor.hint");
    /// <summary>主题编辑器四色标签：游戏。</summary>
    public string LabelGame => LocaleManager.T("tab.game");
    /// <summary>主题编辑器四色标签：下载。</summary>
    public string LabelDownload => LocaleManager.T("tab.download");
    /// <summary>主题编辑器四色标签：工具箱。</summary>
    public string LabelToolbox => LocaleManager.T("tab.toolbox");
    /// <summary>主题编辑器四色标签：设置。</summary>
    public string LabelSettings => LocaleManager.T("tab.settings");
    /// <summary>Java 环境检测区标题（java.title）。</summary>
    public string JavaSectionTitle => LocaleManager.T("java.title");
    /// <summary>Java 检测按钮文案（java.detect）。</summary>
    public string JavaDetectButton => LocaleManager.T("java.detect");
    /// <summary>语言下拉：简体中文（lbl.chinese）。</summary>
    public string LangChinese => LocaleManager.T("lbl.chinese");
    /// <summary>语言下拉：English（lbl.english）。</summary>
    public string LangEnglish => LocaleManager.T("lbl.english");
    /// <summary>主题下拉：暗色（lbl.dark）。</summary>
    public string ThemeDarkLabel => LocaleManager.T("lbl.dark");
    /// <summary>主题下拉：亮色（lbl.light）。</summary>
    public string ThemeLightLabel => LocaleManager.T("lbl.light");

    /// <summary>已检测到的 Java 列表。</summary>
    public ObservableCollection<JavaEntry> JavaList { get; } = new();

    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    /// <summary>调用 Core 的 JavaDetector 在 Linux 上扫描 Java。</summary>
    public async Task DetectJavaAsync()
    {
        Status = LocaleManager.T("java.scanning");
        var list = await JavaDetector.DetectAsync();
        JavaList.Clear();
        foreach (var j in list.OrderByDescending(j => j.MajorVersion))
            JavaList.Add(new JavaEntry { Exe = j.JavaExe, Major = j.MajorVersion, Raw = j.RawVersion });
        Status = LocaleManager.Tf("java.detected", JavaList.Count);
        JavaVersionText = JavaList.Count > 0 ? $"Java {JavaList[0].Major}" : LocaleManager.T("status.no_java");
    }
}

/// <summary>
/// 四色索引贴的可绑定包装：驱动选中展开 / 折叠动画与重叠 Z 序。
/// 几何对齐 WPF Core.UI.MainTabDefinition 的 MainTabs 规则：
/// 选中页展开显示文字（含游戏页），其余折叠为色条；左压右叠放。
/// </summary>
public class TabItemViewModel : ObservableObject
{
    public MainTabDefinition Def { get; }
    public MainTabKind Kind => Def.Kind;
    /// <summary>本地化后的显示名（Def.Title 是 l10n key）。</summary>
    public string DisplayName => Localization.Get(Def.Title);
    /// <summary>是否常驻展开：由 Core.MainTabDefinition 决定（游戏页为 true）。</summary>
    public bool AlwaysExpanded => Def.AlwaysExpanded;

    /// <summary>在四色序列中的次序（0=游戏）。决定重叠方向与默认 Z 序。</summary>
    public int Order { get; }
    public int TotalTabs { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(TextOpacity));
                OnPropertyChanged(nameof(UnderlineOpacity));
                OnPropertyChanged(nameof(ZIndex));
                OnPropertyChanged(nameof(Margin));
                // 右邻项的 Margin 依赖本项 IsExpanded，必须联动刷新
                NotifyNeighbour();
            }
        }
    }

    private bool _isHovered;

    /// <summary>
    /// 鼠标悬浮（对齐 WPF <c>OnTabHover</c>）：未选中的贴悬浮时也会展开显示文字。
    /// 驱动 <see cref="IsExpanded"/>（宽度/文字）、<see cref="ZIndex"/>（抬到邻贴之上）
    /// 与 <see cref="Margin"/>（左邻展开时重叠从 20 收到 10）。
    /// </summary>
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (!SetField(ref _isHovered, value)) return;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(TextOpacity));
            OnPropertyChanged(nameof(ZIndex));
            OnPropertyChanged(nameof(Margin));
            NotifyNeighbour();
        }
    }

    /// <summary>展开态：选中 / 常驻展开（游戏页）/ 悬浮，三者任一成立即展开。</summary>
    public bool IsExpanded => _isSelected || Def.AlwaysExpanded || _isHovered;

    /// <summary>
    /// 文字不透明度：跟随 <see cref="IsExpanded"/> 的 0↔1（配合 XAML DoubleTransition 实现淡入淡出，
    /// 对齐 WPF <c>RevealTitle</c>）。单独暴露属性而非直接绑 <c>IsExpanded</c>，
    /// 是为了让过渡有「起止值」可插值——布尔值本身无法参与插值。
    /// </summary>
    public double TextOpacity => IsExpanded ? 1d : 0d;

    /// <summary>下划线不透明度：跟随选中态 0↔1（对齐 WPF 下划线 DoubleAnimation 淡入）。</summary>
    public double UnderlineOpacity => _isSelected ? 1d : 0d;

    /// <summary>语言切换时由 VM 调用，强制刷新显示名（DisplayName 依赖当前语言）。</summary>
    public void RaiseDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));

    /// <summary>
    /// Z 序：默认严格按「左压右」由 Order 决定（Order 越小越高）。
    /// 悬浮或选中时抬到 20（对齐 WPF <c>Panel.SetZIndex(..., 20)</c>），
    /// 否则展开后会被左侧未选中贴的色块/圆角遮住文字。
    /// </summary>
    public int ZIndex => (_isSelected || _isHovered) ? 20 : Def.ZIndex;

    /// <summary>与左侧邻居的重叠外边距：左邻展开时 10px，否则 20px（对齐 WPF NeighborExpanded）。</summary>
    public Thickness Margin
    {
        get
        {
            if (Order == 0) return new Thickness(0);
            var prev = MainViewModel.Instance?.TabItems.FirstOrDefault(t => t.Order == Order - 1);
            var overlap = prev?.IsExpanded == true ? 10.0 : 20.0;
            return new Thickness(-overlap, 0, 0, 0);
        }
    }

    /// <summary>通知左右邻居刷新 Margin（它们的 Margin 依赖本项 IsExpanded）。</summary>
    private void NotifyNeighbour()
    {
        var items = MainViewModel.Instance?.TabItems;
        if (items is null) return;
        foreach (var t in items)
        {
            if (t.Order == Order + 1 || t.Order == Order - 1)
                t.OnPropertyChanged(nameof(Margin));
        }
    }

    public TabItemViewModel(MainTabDefinition def, int order, int total)
    {
        Def = def;
        Order = order;
        TotalTabs = total;
    }
}
