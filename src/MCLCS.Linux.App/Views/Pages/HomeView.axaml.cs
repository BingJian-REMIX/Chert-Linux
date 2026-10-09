using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MCLCS.Core.UI;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContext = new HomeViewModel();
    }

    /// <summary>「查看全部」：跳到智能推荐结果页（RecommendationView，挂在游戏标签下）。</summary>
    private void OpenRecommendation_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } win)
            win.NavigateTo(MainTabKind.Game, "recommend");
    }

    /// <summary>「进入节日中心」：跳到节日中心页（SeasonalHubView，挂在游戏标签下）。</summary>
    private void OpenSeasonalHub_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } win)
            win.NavigateTo(MainTabKind.Game, "seasonal");
    }

    private void OpenAnnualReport_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } win)
            win.NavigateTo(MainTabKind.Toolbox, "annual");
    }

    /// <summary>「版本列表」入口：跳转到独立的版本列表页（VersionListView，挂在下载标签下）。</summary>
    private void OpenVersionList_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainWindow>() is { } win)
            win.NavigateTo(MainTabKind.Download, "versionlist");
    }
}
