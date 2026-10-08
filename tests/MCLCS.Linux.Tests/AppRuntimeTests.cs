using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using MCLCS.Core.Launcher;
using MCLCS.Core.Localization;
using MCLCS.Core.Theme;
using MCLCS.Core.Tokens;
using MCLCS.Core.UI;
using MCLCS.Linux.App;
using MCLCS.Linux.App.Converters;
using MCLCS.Linux.App.ViewModels;
using Xunit;
using Avalonia.Headless.XUnit;

namespace MCLCS.Linux.Tests;

/// <summary>
/// 无头运行时验证：不依赖显示，直接验证「视图模型 → Core 数据 → 本地化 → 颜色转换器」整条链路，
/// 用于捕捉编译通过但运行时才暴露的隐式问题。
/// </summary>
/// <remarks>
/// 本类大量创建 Avalonia 线程亲和对象（SolidColorBrush / Color / MainViewModel），须在 Avalonia UI 线程执行，
/// 否则抛 “Call from invalid thread”。改用 Avalonia.Headless.XUnit 的 [AvaloniaTest] 替代 [Fact]，
/// 由框架自动拉起 headless 会话并在 UI 线程运行，从根本消除线程亲和竞争。
/// </remarks>
public class AppRuntimeTests
{
    [AvaloniaFact]
    public void MainViewModel_Tabs_来自_Core_四色标签()
    {
        var vm = new MainViewModel();
        Assert.Equal(4, vm.Tabs.Count);
        Assert.Equal("tab.game", vm.Tabs[0].Title); // VM 仍暴露原始 key（本地化在显示层做）
        Assert.Equal("#4CAF50", vm.Tabs[0].DefaultColor);
        Assert.Equal("#2196F3", vm.Tabs[1].DefaultColor);
        Assert.Equal("#FF9800", vm.Tabs[2].DefaultColor);
        Assert.Equal("#607D8B", vm.Tabs[3].DefaultColor);
    }

    [AvaloniaFact]
    public void KeyToTextConverter_把_l10nKey_翻译为中文()
    {
        var conv = new KeyToTextConverter();
        Assert.Equal("游戏", conv.Convert("tab.game", typeof(string), null, null));
        Assert.Equal("工具箱", conv.Convert("tab.toolbox", typeof(string), null, null));
        Assert.Equal("崩溃分析", conv.Convert("tool.crash", typeof(string), null, null));
        // 未知 key 原样返回，不丢信息
        Assert.Equal("unknown.key", conv.Convert("unknown.key", typeof(string), null, null));
        Assert.Equal("", conv.Convert(null, typeof(string), null, null));
    }

    [AvaloniaFact]
    public void Localization_ToolDescription_覆盖_工具箱20项()
    {
        foreach (var item in Sidebar.Toolbox)
            Assert.NotEmpty(Localization.ToolDescription(item.Id));
        // 未登记项优雅降级
        Assert.Equal("（待接入 Core 能力）", Localization.ToolDescription("nope"));
    }

    [AvaloniaFact]
    public void MainViewModel_SelectedTab_联动_SidebarItems()
    {
        var vm = new MainViewModel();
        // 默认主页为游戏页（用户要求），游戏页无侧边栏
        Assert.Equal(MainTabKind.Game, vm.SelectedTab.Kind);
        Assert.Equal(Sidebar.Game.Count, vm.SidebarItems.Count);
        Assert.Empty(vm.SidebarItems);
        // 切换到 Download（副标签集合与 Core 一致，含新增 versionlist）
        vm.SelectedTab = MainTabs.Get(MainTabKind.Download);
        Assert.Equal(Sidebar.Download.Count, vm.SidebarItems.Count);
        // 切换到 Toolbox（副标签集合与 Core 一致，已移除 toolbox/achievement/annual）
        vm.SelectedTab = MainTabs.Get(MainTabKind.Toolbox);
        Assert.Equal(Sidebar.Toolbox.Count, vm.SidebarItems.Count);
    }

    [AvaloniaFact]
    public void MainViewModel_选中副标签_联动_右面板()
    {
        var vm = new MainViewModel();
        vm.SelectedTab = MainTabs.Get(MainTabKind.Toolbox);
        vm.SelectedSidebarId = "crash";
        Assert.Equal("崩溃分析", vm.PanelTitle);
        Assert.Equal("诊断与排障", vm.PanelGroup);
        Assert.NotEmpty(vm.PanelDescription);
    }

    [AvaloniaFact]
    public void MainViewModel_外观页_展示主题编辑器()
    {
        var vm = new MainViewModel();
        vm.SelectedTab = MainTabs.Get(MainTabKind.Settings);
        vm.SelectedSidebarId = "appearance";
        Assert.True(vm.ShowThemeEditor);
        // Core.TabThemeConfig 真实可用：默认四色与 Core 常量一致
        Assert.Equal(MainTabs.DefaultGameColor, vm.Theme.ColorOf(MainTabKind.Game));
    }

    [AvaloniaFact]
    public async Task MainViewModel_DetectJavaAsync_在Linux真实可用()
    {
        var vm = new MainViewModel();
        await vm.DetectJavaAsync();
        // 本机已探到 Java 20（sdkman 路径），证明 Core.JavaDetector 在 Linux 可用
        Assert.True(vm.JavaList.Count >= 1, "应至少检测到 1 个 Java 安装");
        Assert.Contains(vm.JavaList, j => j.Major >= 8);
        Assert.DoesNotContain("就绪", vm.Status); // Status 已被更新
    }

    [AvaloniaFact]
    public void HexToBrushConverter_解析_Core_四色()
    {
        var conv = new HexToBrushConverter();
        foreach (var hex in new[] { "#4CAF50", "#2196F3", "#FF9800", "#607D8B" })
        {
            var brush = conv.Convert(hex, typeof(SolidColorBrush), null, null);
            Assert.IsType<SolidColorBrush>(brush);
            Assert.NotNull(((SolidColorBrush)brush!).Color);
        }
        // 非法输入优雅降级为 Gray
        var fallback = conv.Convert("not-a-color", typeof(SolidColorBrush), null, null);
        Assert.IsType<SolidColorBrush>(fallback);
    }

    [AvaloniaFact]
    public void KindToBrushConverter_按_Theme_取色_降级Gray()
    {
        var conv = new KindToBrushConverter();
        MainViewModel.Instance = new MainViewModel();
        var brush = conv.Convert(MainTabKind.Game, typeof(SolidColorBrush), null, null);
        Assert.IsType<SolidColorBrush>(brush);
        // 未知类型降级
        var bad = conv.Convert(123, typeof(SolidColorBrush), null, null);
        Assert.IsType<SolidColorBrush>(bad);
    }

    [AvaloniaFact]
    public void LocaleManager_T_随语言切换返回对应文案()
    {
        var before = LocaleManager.CurrentLocale;
        try
        {
            LocaleManager.CurrentLocale = "en_US";
            Assert.Equal("Game", LocaleManager.T("tab.game"));
            Assert.Equal("Download", LocaleManager.T("tab.download"));
            // 未知 key 回退到 key 本身
            Assert.Equal("ghost.key", LocaleManager.T("ghost.key"));
            LocaleManager.CurrentLocale = "zh_CN";
            Assert.Equal("游戏", LocaleManager.T("tab.game"));
        }
        finally
        {
            LocaleManager.CurrentLocale = before;
        }
    }

    [AvaloniaFact]
    public void Localization_Get_委托_LocaleManager_且保留约定()
    {
        // 已知 key 走 Core 多语言框架
        Assert.Equal("游戏", Localization.Get("tab.game"));
        // 未知 key 原样返回，空 key 返回空串（与既有单测约定一致）
        Assert.Equal("未知键", Localization.Get("未知键"));
        Assert.Equal("", Localization.Get(null));
    }

    [AvaloniaFact]
    public void Localization_ToolDescription_覆盖_下载页与设置页_desc_键()
    {
        // 下载页副标签的 desc 键应全部解析（Bug-3 修复）
        foreach (var item in Sidebar.Download)
            Assert.NotEqual("（待接入 Core 能力）", Localization.ToolDescription(item.Id));
        // 设置页副标签的 desc 键应全部解析
        foreach (var item in Sidebar.Settings)
            Assert.NotEqual("（待接入 Core 能力）", Localization.ToolDescription(item.Id));
        // 抽查具体文案非空且非 key 本身
        Assert.NotEmpty(Localization.ToolDescription("mod"));
        Assert.NotEqual("tab.mods.desc", Localization.ToolDescription("mod"));
    }

    [AvaloniaFact]
    public void Localization_ToolDescription_覆盖_工具箱全部项()
    {
        // 工具箱 23 项（含 serverpack / addserver / achievement）都必须有真实描述，
        // 否则侧边栏副标签的位置会显示「（待接入 Core 能力）」占位文案 —— 2026-10-09 修过一轮
        foreach (var item in Sidebar.Toolbox)
            Assert.NotEqual("（待接入 Core 能力）", Localization.ToolDescription(item.Id));
    }

    [AvaloniaFact]
    public void AfkViewModel_生成的_Token_包含_所有_动作()
    {
        var vm = new AfkViewModel();
        Assert.Equal("", vm.TokenText);

        // 新 VM 从 Actions 序列构造 Token（分号分隔）
        vm.AddActionCommand.Execute(null);
        vm.AddActionCommand.Execute(null);
        Assert.Equal(2, vm.Actions.Count);

        if (vm.Actions.Count >= 2)
        {
            vm.Actions[0].ActionType = "F"; vm.Actions[0].Param = "60";
            vm.Actions[1].ActionType = "D"; vm.Actions[1].Param = "8";
        }

        var token = vm.TokenText;
        Assert.Contains("F60", token);
        Assert.Contains("D8", token);
        Assert.Contains(";", token); // 分号分隔
        Assert.Equal(2, token.Split(';').Length);
    }

    [AvaloniaFact]
    public void ThemeManager_切换触发_OnThemeChanged()
    {
        var before = ThemeManager.Current;
        var fired = false;
        void Handler(ThemeType t) => fired = true;
        ThemeManager.OnThemeChanged += Handler;
        try
        {
            ThemeManager.Current = before == ThemeType.Light ? ThemeType.Dark : ThemeType.Light;
            Assert.True(fired, "主题切换应触发 OnThemeChanged，驱动 UI 换肤");
        }
        finally
        {
            ThemeManager.Current = before;
            ThemeManager.OnThemeChanged -= Handler;
        }
    }

    [AvaloniaFact]
    public void TokenToBitmap_按主题解析图标路径()
    {
        // 对齐 WPF IconImage：dark=白系图标 / light=黑系图标，主题目录优先、顶层回退。
        // 注：测试环境无 App 资源系统（avares 不可解析），故验证路径选择逻辑而非位图加载。
        ThemeManager.Current = ThemeType.Dark;
        IconManager.HighDpi = false;
        var dark = TokenToBitmapConverter.ResolveCandidates("ai");
        Assert.Contains("Resources/Icons/dark/ai.png", dark);
        Assert.Contains("Resources/Icons/ai.png", dark); // 顶层回退兜底

        ThemeManager.Current = ThemeType.Light;
        var light = TokenToBitmapConverter.ResolveCandidates("ai");
        Assert.Contains("Resources/Icons/light/ai.png", light);

        IconManager.HighDpi = true;
        var hd = TokenToBitmapConverter.ResolveCandidates("ai");
        Assert.Contains("Resources/Icons/light@2x/ai.png", hd);
        IconManager.HighDpi = false;
    }

    [AvaloniaFact]
    public void IconManager_HighDpi_切换广播事件()
    {
        var fired = 0;
        IconManager.HighDpiChanged += () => fired++;
        IconManager.HighDpi = true;
        IconManager.HighDpi = true; // 同值不重复触发
        IconManager.HighDpi = false;
        Assert.Equal(2, fired);
    }

}

    