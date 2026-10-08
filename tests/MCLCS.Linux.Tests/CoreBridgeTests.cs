using MCLCS.Core.Launcher;
using MCLCS.Core.Mvvm;
using MCLCS.Core.UI;
using Xunit;

namespace MCLCS.Linux.Tests;

/// <summary>
/// Linux 桥接测试：验证 vendored MCLCS.Core 的平台无关部分
/// （UI 视图模型 / MVVM / 工具逻辑）在 Linux 上可正常工作。
/// </summary>
public class CoreBridgeTests
{
    [Fact]
    public void MainTabs_HasFourTabs_InOrder()
    {
        var tabs = MainTabs.All;
        Assert.Equal(4, tabs.Count);
        Assert.Equal(MainTabKind.Game, tabs[0].Kind);
        Assert.Equal(MainTabKind.Download, tabs[1].Kind);
        Assert.Equal(MainTabKind.Toolbox, tabs[2].Kind);
        Assert.Equal(MainTabKind.Settings, tabs[3].Kind);
        // 游戏页未选中时折叠（与选中页一致，不冗余显示「游戏」），且无侧边栏
        Assert.False(tabs[0].AlwaysExpanded);
        Assert.False(tabs[0].HasSidebar);
        // 其余三页均有侧边栏
        Assert.True(tabs[1].HasSidebar);
        Assert.True(tabs[2].HasSidebar);
        Assert.True(tabs[3].HasSidebar);
    }

    [Fact]
    public void MainTabs_DefaultColors_MatchSpec()
    {
        Assert.Equal("#4CAF50", MainTabs.DefaultGameColor);
        Assert.Equal("#2196F3", MainTabs.DefaultDownloadColor);
        Assert.Equal("#FF9800", MainTabs.DefaultToolboxColor);
        Assert.Equal("#607D8B", MainTabs.DefaultSettingsColor);
    }

    [Fact]
    public void Sidebar_Toolbox_HasItems_WithGroups()
    {
        var items = Sidebar.For(MainTabKind.Toolbox);
        // 21 原工具 + addserver + achievement = 23（后两者都有真实页面与路由）；
        // toolbox（聚合入口已拆）/ annual（仅周年日从主页跳转）不在侧边栏常驻
        Assert.Equal(23, items.Count);
        Assert.Contains(items, i => i.Id == "log");
        Assert.Contains(items, i => i.Id == "aichat");
        Assert.Contains(items, i => i.Id == "devtools");
        Assert.Contains(items, i => i.Id == "achievement");
        Assert.Contains(items, i => i.Id == "addserver");
        Assert.DoesNotContain(items, i => i.Id == "toolbox");
        Assert.DoesNotContain(items, i => i.Id == "annual");
        // 全部带分组（四组分区）
        Assert.All(items, i => Assert.NotNull(i.Group));
    }

    [Fact]
    public void Sidebar_Game_HasNoSidebar()
    {
        var items = Sidebar.For(MainTabKind.Game);
        Assert.Empty(items);
        Assert.False(Sidebar.Has(MainTabKind.Game));
    }

    [Fact]
    public void SidebarState_SwitchOwner_SelectsFirstItem()
    {
        var state = new SidebarState();
        state.SwitchOwner(MainTabKind.Download);
        Assert.Equal("minecraft", state.SelectedId);
        Assert.Equal(MainTabKind.Download, state.Owner);

        state.SwitchOwner(MainTabKind.Game);
        Assert.Null(state.SelectedId);
    }

    [Fact]
    public void TabThemeConfig_Brighten_ClampsChannels()
    {
        Assert.Equal("#FFFFFF", TabThemeConfig.Brighten("#FFFFFF", 1.45));
        Assert.Equal("#000000", TabThemeConfig.Brighten("#000000", 0.7));
        // 4CAF50 提亮 1.12 倍
        var b = TabThemeConfig.Brighten("#4CAF50", 1.12);
        Assert.Matches("^#[0-9A-F]{6}$", b);
    }

    [Fact]
    public void TabThemeConfig_InvalidColor_Rejected()
    {
        Assert.False(TabThemeConfig.IsValidColor("not-a-color"));
        Assert.False(TabThemeConfig.IsValidColor(null));
        Assert.True(TabThemeConfig.IsValidColor("#4CAF50"));
        Assert.True(TabThemeConfig.IsValidColor("#804CAF50"));
    }

    [Fact]
    public void JavaDetector_ParsesLegacyAndModernVersions()
    {
        Assert.Equal(8, JavaDetector.MajorFromVersionString("1.8.0_301"));
        Assert.Equal(21, JavaDetector.MajorFromVersionString("21.0.3"));
        Assert.Equal(17, JavaDetector.MajorFromVersionString("17"));
        Assert.Equal(0, JavaDetector.MajorFromVersionString(""));
    }

    [Fact]
    public void ObservableObject_SetField_RaisesPropertyChanged_OnlyOnChange()
    {
        var obj = new TestObservable();
        Assert.True(obj.Set(1));           // 0→1 变化触发
        Assert.False(obj.Set(1));          // 相同值不触发
        Assert.True(obj.Set(2));           // 1→2 变化触发
        Assert.Equal(2, obj.ChangedCount);
        Assert.Equal(2, obj.Value);
    }

    [Fact]
    public void JavaDetector_DetectAsync_RunsOnLinux_WithoutRegistry()
    {
        // Linux 上注册表分支被 IsWindows() 守卫跳过，
        // 此处验证调用本身不抛 PlatformNotSupportedException 即可。
        var list = JavaDetector.DetectAsync().GetAwaiter().GetResult();
        Assert.NotNull(list);
    }

    private class TestObservable : ObservableObject
    {
        private int _value;
        public int ChangedCount { get; private set; }
        public int Value => _value;
        public bool Set(int v)
        {
            var changed = SetField(ref _value, v);
            if (changed) ChangedCount++;
            return changed;
        }
    }
}
