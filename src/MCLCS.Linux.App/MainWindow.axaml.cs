using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.UI;
using MCLCS.Linux.App.Converters;
using MCLCS.Linux.App.Services;
using MCLCS.Linux.App.ViewModels;
using MCLCS.Linux.App.Views;
using MCLCS.Linux.App.Views.Pages;
using System.Diagnostics;
using System.IO;

namespace MCLCS.Linux.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _isMax = true;

    public MainWindow()
    {
        _vm = new MainViewModel();
        MainViewModel.Instance = _vm;
        DataContext = _vm;
        InitializeComponent();
        // 注入音频解码宿主（BASS），并联动游戏启动 / 退出做 AutoDuck（对齐 WPF 的 MediaElementPlayer 注入）
        var player = new BassPlayer();
        MusicPlayerViewModel.Instance.Host = player;
        MusicPlayerViewModel.Instance.SetVolumeFromHost();
        GameLauncher.GameProcessStarted += OnGameProcessStarted;
        SyncSidebarSelection();
        UpdateMaxIcon();
        // 品牌窗口图标（提取自 WPF 的 MCLCS.ico，保证跨平台品牌一致）
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MCLCS.Linux.App/Resources/mclcs.png"));
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch { /* 图标缺失不致命 */ }
        // 语言切换时重绑侧栏（走 KeyToTextConverter 的项需重绑才能刷新）
        LocaleManager.LocaleChanged += OnLocaleChanged;
        // 上屏且屏幕信息就绪后再铺满（构造函数里 Screens.Primary 尚未可用）
        Opened += (_, _) => FitToScreen();
        // 窗口就绪后应用外观偏好（主题色/字体缩放/背景图）——启动时 MainWindow 尚未创建，此处补全
        Opened += (_, _) => App.ApplyAppearanceFromProfile();
        // 窗口打开后异步扫描 Java，让状态栏显示真实结果（此前为永久占位）
        Opened += (_, _) => _ = _vm.DetectJavaAsync();
        // 启动后自动跑一次文件变更检测（建基线 / 提示新增文件）；同时绑定 Activated，
        // 焦点每次回到启动器时也重新检测（规格 2.3-16 / 用户需求）
        Opened += (_, _) => _ = FileWatchService.Instance.RunScanAsync();
        Activated += (_, _) => _ = FileWatchService.Instance.RunScanAsync();
        // 启动后异步检查启动器更新（有可用版本时弹出 UpdateDialog；失败静默不阻塞）
        Opened += (_, _) => _ = UpdateDialog.CheckAndShowAsync(this);
        // 初始页面路由（默认主页为游戏页，无侧栏）
        ShowPage();

        // 首帧淡入：XAML 初始 Opacity=0，等「布局定稿 + FitToScreen 已铺好尺寸 + 首帧真正渲染完成」
        // 之后再淡入到 1。避免用户看到窗口先空白/尺寸未定 → 内容逐行长出来的启动闪烁。
        // 最后注册，确保排在 FitToScreen 等 Opened 回调之后执行。
        Opened += async (_, _) => await FadeInAfterFirstFrameAsync();
    }

    /// <summary>
    /// 首帧渲染完成后把窗口从透明淡入到不透明。
    /// 用 <see cref="DispatcherPriority.Loaded"/> 确保本轮布局已完成；再让出一帧取定稿尺寸，
    /// 最后以 <c>MotionFx.Slow</c> 统一时长淡入（动画关闭时直接置 1，保证内容一定可见）。
    /// </summary>
    private async Task FadeInAfterFirstFrameAsync()
    {
        // 等 Loaded 优先级任务跑完（本轮布局定稿）。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        // 再让出一帧，确保 FitToScreen 设置的 Width/Height 已反映到可视树。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        MotionFx.FadeIn(this);
    }

    /// <summary>按主屏工作区尺寸铺满窗口（避免固定尺寸在大屏上留黑边）。
    /// 优先用 Screens.Primary（真实桌面 WM 下可靠）；无 WM 的 X11（如 Xvfb）下
    /// Primary 为 null，回退用 xdotool getdisplaygeometry 取显示尺寸。</summary>
    private void FitToScreen()
    {
        var area = GetScreenArea();
        if (area is null) return;
        Position = area.Value.TopLeft;
        Width = area.Value.Width;
        Height = area.Value.Height;
    }

    /// <summary>设置主窗口背景图片（设置 → 外观：背景图）。null/空或文件不存在时清除。</summary>
    public void SetBackgroundImage(string? path)
    {
        if (BgImage is null) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BgImage.Source = null;
            BgImage.IsVisible = false;
            return;
        }
        try
        {
            BgImage.Source = new Bitmap(path);
            BgImage.IsVisible = true;
        }
        catch
        {
            BgImage.Source = null;
            BgImage.IsVisible = false;
        }
    }

    private PixelRect? GetScreenArea()
    {
        var screen = Screens.Primary;
        if (screen is not null) return screen.WorkingArea;
        // 回退：headless / 无 WM 环境下 Screens.Primary 为 null
        try
        {
            var psi = new ProcessStartInfo("xdotool", "getdisplaygeometry")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var parts = outp.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) &&
                w > 0 && h > 0)
                return new PixelRect(0, 0, w, h);
        }
        catch { }
        return null;
    }

    /// <summary>供截屏工程等外部在不经点击的情况下直接路由到指定页面（设置主标签 + 侧栏项）。</summary>
    public void NavigateTo(MainTabKind kind, string sidebarId)
    {
        _vm.SelectedTab = MainTabs.Get(kind);
        _vm.SelectedSidebarId = sidebarId;
        SyncSidebarSelection();
        ShowPage();
    }

    /// <summary>切页展开动画：内容上移淡入（对齐设计稿 @keyframes pageIn：opacity 0→1 + translateY(18px)→0，200ms ease）。
    /// 注意：Opacity 过渡会在导航后偶发卡在半透明（headless / 连续切页时尤甚），导致整页文字被压暗。
    /// 因此这里不再用 Opacity 过渡，始终保证内容 <c>Opacity=1</c>（可读性优先），仅保留位移滑入效果。</summary>
    private void PlayContentEnter()
    {
        if (ContentHost is null) return;
        ContentHost.Opacity = 1;
        ContentHost.RenderTransform = TransformOperations.Parse("translateY(18px)");
        Dispatcher.UIThread.Post(() =>
        {
            if (ContentHost is null) return;
            ContentHost.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);
    }

    // 标题栏下载按钮：打开下载队列（锚定到该按钮中心弹出）
    private void DownloadBtn_Click(object? sender, RoutedEventArgs e) => QueueShow(sender as Control);

    // 下载队列：以标题栏下载按钮为锚点向下弹出（水平中线对齐按钮中心 + 弹出动画）
    private async void QueueShow(Control? anchor = null)
    {
        await DialogService.Instance.ShowAsync(new DialogOptions
        {
            Title = "下载队列",
            Width = 420,
            Anchor = anchor ?? DownloadBtn,
            Content = BuildQueueContent(),
            Buttons = new[] { new DialogButton("开始下载", "start", DialogButtonKind.Primary, isDefault: true) }
        });
    }

    private Control BuildQueueContent()
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(QueueItem("Sodium 0.5.8 (Fabric 1.21.4)", "下载中 42%", 42));
        sp.Children.Add(QueueItem("BSL Shaders v8.3", "等待中", 0));
        return sp;
    }

    private Control QueueItem(string title, string status, double pct)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4)
        };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        grid.Children.Add(new TextBlock
        {
            Text = status,
            FontSize = 12,
            Foreground = (IBrush?)Application.Current.FindResource("SecondaryForeground"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        });
        var bar = new ProgressBar
        {
            Value = pct,
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (IBrush?)Application.Current.FindResource("AccentBrush"),
            Background = (IBrush?)Application.Current.FindResource("ProgressBackground")
        };
        var wrap = new StackPanel { Spacing = 2 };
        wrap.Children.Add(grid);
        wrap.Children.Add(bar);
        return wrap;
    }

    /// <summary>按当前主标签 / 副标签路由到对应功能页；未移植项展示开发中占位页。</summary>
    private void ShowPage()
    {
        if (PageRegion is null) return;
        UserControl page = (_vm.SelectedTab.Kind, _vm.SelectedSidebarId) switch
        {
            // 游戏页无侧边栏，默认展示主页（HomeView）
            (MainTabKind.Game, _) => new HomeView(),
            // 下载中心：六个副标签统一路由到单一 DownloadPageView（对齐 WPF 的单页 + 子标签切换）
            (MainTabKind.Download, "minecraft") => new DownloadPageView(),
            (MainTabKind.Download, "mod") => new DownloadPageView(),
            (MainTabKind.Download, "shader") => new DownloadPageView(),
            (MainTabKind.Download, "resourcepack") => new DownloadPageView(),
            (MainTabKind.Download, "modpack") => new DownloadPageView(),
            (MainTabKind.Download, "map") => new DownloadPageView(),
            (MainTabKind.Download, "shadertoken") => new ShaderTokenView(),
            (MainTabKind.Download, "versionlist") => new VersionListView(),
            (MainTabKind.Download, "center") => new DownloadCenterView(),
            (MainTabKind.Toolbox, "log") => new LogView(),
            (MainTabKind.Toolbox, "clean") => new CleanerView(),
            (MainTabKind.Toolbox, "backup") => new BackupView(),
            (MainTabKind.Toolbox, "screenshot") => new ScreenshotView(),
            (MainTabKind.Toolbox, "crash") => new CrashView(),
            (MainTabKind.Toolbox, "datapack") => new DataPackView(),
            (MainTabKind.Toolbox, "saves") => new SavesView(),
            (MainTabKind.Toolbox, "skin") => new SkinEditorView(),
            (MainTabKind.Toolbox, "network") => new NetworkView(),
            (MainTabKind.Toolbox, "nbt") => new NbtView(),
            (MainTabKind.Toolbox, "shortcut") => new ShortcutView(),
            (MainTabKind.Toolbox, "afk") => new AfkView(),
            (MainTabKind.Toolbox, "aichat") => new AiAssistView(),
            (MainTabKind.Toolbox, "perf") => new PerfView(),
            (MainTabKind.Toolbox, "modpackio") => new ModpackIoView(),
            (MainTabKind.Toolbox, "music") => new MusicView(),
            (MainTabKind.Toolbox, "moddev") => new ModDevView(),
            (MainTabKind.Toolbox, "packmaker") => new PackMakerView(),
            (MainTabKind.Toolbox, "command") => new CommandView(),
            (MainTabKind.Toolbox, "devtools") => new DevToolsView(),
            (MainTabKind.Toolbox, "addserver") => new AddServerView(),
            (MainTabKind.Toolbox, "achievement") => new AchievementView(),
            (MainTabKind.Toolbox, "serverpack") => new ServerPackView(),
            // 年度报告仅在周年日入口可见，保留路由供主页跳转
            (MainTabKind.Toolbox, "annual") => new AnnualReportView(),
            // 设置：单页（左侧分类列表 + 右侧内容区），对应 WPF 的单 SettingsView
            (MainTabKind.Settings, _) => new SettingsView(),
            _ => MakePlaceholder()
        };
        PageRegion.Content = page;
    }

    private PlaceholderPage MakePlaceholder()
    {
        var page = new PlaceholderPage();
        page.Configure(_vm.SelectedSidebarId, _vm.SelectedTab.Kind);
        return page;
    }

    /// <summary>切换主标签：联动侧边栏集合、标题栏色、右面板，并同步 ListBox 选中项。</summary>
    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MainTabKind kind } && DataContext is MainViewModel vm)
        {
            vm.SelectedTab = MainTabs.Get(kind);
            SyncSidebarSelection();
            ShowPage();
            PlayContentEnter();
        }
    }

    /// <summary>索引贴悬停：未选中标签提亮 1.2（对齐模板 renderTabs 的 mouseenter brighten(solid,1.2)）。</summary>
    private void Tab_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button { Tag: MainTabKind kind } btn) return;
        if (btn.DataContext is not TabItemViewModel item || item.IsSelected) return;
        var hex = TabThemeConfig.Brighten(_vm.Theme.ColorOf(kind), 1.2);
        btn.Background = HexToBrushConverter.ToBrush(hex);
    }

    /// <summary>索引贴移出：恢复实色（选中态由背景绑定负责提亮 1.12）。</summary>
    private void Tab_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Button { Tag: MainTabKind kind } btn) return;
        if (btn.DataContext is not TabItemViewModel item) return;
        var hex = item.IsSelected
            ? _vm.Theme.ActiveColorOf(kind)
            : _vm.Theme.ColorOf(kind);
        btn.Background = HexToBrushConverter.ToBrush(hex);
    }

    private void Sidebar_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SidebarItem item } && DataContext is MainViewModel vm)
            vm.SelectedSidebarId = item.Id;
        ShowPage();
        PlayContentEnter();
    }

    /// <summary>把 ListBox 选中项对齐到 VM 当前 SelectedSidebarId（切主标签后调用）。</summary>
    private void SyncSidebarSelection()
    {
        if (SidebarList is null) return;
        SidebarList.SelectedItem = _vm.SidebarItems.FirstOrDefault(i => i.Id == _vm.SelectedSidebarId);
    }

    // ===== 侧边栏折叠 / 展开（对齐 WPF 悬停逻辑，200ms 动画由 XAML DoubleTransition 实现）=====
    private void Sidebar_Enter(object? sender, PointerEventArgs e)
    {
        SidebarRoot.Width = 152;
        _vm.SidebarExpanded = true;
    }

    private void Sidebar_Leave(object? sender, PointerEventArgs e)
    {
        SidebarRoot.Width = 56;
        _vm.SidebarExpanded = false;
    }

    // ===== 窗口控制 =====
    /// <summary>标题栏按下拖拽。但若按下落在交互控件（标签按钮 / 窗口控制 / 搜索框）上，
    /// 则不触发拖拽，交给控件自身的 Click 处理——否则 BeginMoveDrag 会吞掉标签点击，
    /// 导致主标签永远切不动（同 WPF bug #8：索引贴 MouseLeftButtonDown 需截断冒泡）。</summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var el = e.Source as Control;
        while (el is not null)
        {
            if (el is Button or TextBox) return; // 落在交互控件上：不拖拽，让其 Click 正常派发
            el = el.Parent as Control;
        }
        BeginMoveDrag(e);
    }

    private void BtnMin_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object? sender, RoutedEventArgs e)
    {
        if (_isMax)
        {
            // 还原为默认窗口尺寸并居中
            var screen = Screens.Primary;
            Width = 1100;
            Height = 720;
            if (screen is not null)
            {
                var x = screen.WorkingArea.X + (screen.WorkingArea.Width - 1100) / 2;
                var y = screen.WorkingArea.Y + (screen.WorkingArea.Height - 720) / 2;
                Position = new PixelPoint(x, y);
            }
            _isMax = false;
        }
        else
        {
            FitToScreen();
            _isMax = true;
        }
        UpdateMaxIcon();
    }

    /// <summary>根据窗口状态切换最大化/还原图标。</summary>
    private void UpdateMaxIcon()
    {
        if (MaxIconNormal is null || MaxIconRestored is null) return;
        var max = WindowState == WindowState.Maximized;
        MaxIconNormal.IsVisible = !max;
        MaxIconRestored.IsVisible = max;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>游戏进程启动：触发音乐 AutoDuck（降低音量）；进程退出时恢复音量。</summary>
    private void OnGameProcessStarted(System.Diagnostics.Process proc, long _)
    {
        MusicPlayerViewModel.Instance.OnGameLaunch();
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => MusicPlayerViewModel.Instance.OnGameExit();
    }

    /// <summary>语言切换时重绑侧栏列表（走 KeyToTextConverter 的项需重建项才能刷新文本）；
    /// 其余由 {loc:Loc} 绑定自动刷新。</summary>
    private void OnLocaleChanged(string _)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SidebarList is not null)
            {
                SidebarList.ItemsSource = null;
                SidebarList.ItemsSource = _vm.SidebarItems;
            }
        });
    }
}
