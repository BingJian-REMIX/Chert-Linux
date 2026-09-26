using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MCLCS.Core.Localization;

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
        CategoryList.SelectedIndex = 0;
        PageHost.Content = Build(Defs[0].Id);
    }

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is Cat c)
            PageHost.Content = Build(c.Id);
    }

    private static UserControl Build(string id) => id switch
    {
        "general"    => new GeneralSettingsView(),
        "launch"     => new LaunchSettingsView(),
        "download"   => new DownloadSettingsView(),
        "recommend"  => new RecommendSettingsView(),
        "account"    => new AccountsView(),
        "ai"         => new AiSettingsView(),
        "appearance" => new AppearanceView(),
        "about"      => new AboutView(),
        _            => new GeneralSettingsView()
    };
}
