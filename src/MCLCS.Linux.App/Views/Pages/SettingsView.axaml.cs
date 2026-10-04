using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MCLCS.Core.Localization;
using MCLCS.Linux.App;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>
/// 设置总页（对应 WPF 的单 SettingsView）：左侧分类列表 + 右侧内容区。
/// 八个分区（general/launch/download/recommend/account/ai/appearance/about）不再是独立侧边栏子页，
/// 而是承载在本页内的子 UserControl，与 WPF「单页 + ListBox 分类切换」一致。
/// </summary>
public partial class SettingsView : UserControl
{
    private sealed record Cat(string Id, string Title);

    private static readonly (string Id, string Key)[] Defs =
    {
        ("general",    "settings.general"),
        ("launch",     "settings.launch"),
        ("download",   "settings.download"),
        ("music",      "settings.music"),
        ("recommend",  "settings.recommend"),
        ("account",    "settings.account"),
        ("ai",         "settings.ai"),
        ("appearance", "settings.appearance"),
        ("about",      "settings.about")
    };

    public SettingsView()
    {
        InitializeComponent();
        CategoryList.ItemsSource = Defs.Select(d => new Cat(d.Id, LocaleManager.T(d.Key))).ToList();
        // 首次选定会触发 SelectionChanged；此处置 _suppressAnimation 让首屏直接显示、不播入场动画。
        _suppressAnimation = true;
        CategoryList.SelectedIndex = 0;
        PageHost.Content = Build(Defs[0].Id);
        _suppressAnimation = false;
    }

    /// <summary>首屏初始化期间抑制动画，避免设置页一打开就播一次滚动淡入。</summary>
    private bool _suppressAnimation;

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not Cat c) return;

        var next = Build(c.Id);
        if (_suppressAnimation)
        {
            PageHost.Content = next;
            return;
        }

        // 左右滚动交叉过渡：旧内容向左滚出淡出、新内容从右滚入淡入（对齐 WPF MotionFX.SlideSwap）。
        MotionFx.SwapContent(PageHost, next, PageHost.Content);
    }

    private static UserControl Build(string id) => id switch
    {
        "general"    => new GeneralSettingsView(),
        "launch"     => new LaunchSettingsView(),
        "download"   => new DownloadSettingsView(),
        "music"      => new MusicSettingsView(),
        "recommend"  => new RecommendSettingsView(),
        "account"    => new AccountsView(),
        "ai"         => new AiSettingsView(),
        "appearance" => new AppearanceView(),
        "about"      => new AboutView(),
        _            => new GeneralSettingsView()
    };
}
