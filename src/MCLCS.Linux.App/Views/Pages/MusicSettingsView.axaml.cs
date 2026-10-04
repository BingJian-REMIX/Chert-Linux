using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>设置 → 音乐子页。数据上下文为 <see cref="MusicSettingsViewModel"/>。</summary>
public partial class MusicSettingsView : UserControl
{
    private MusicSettingsViewModel? _vm;

    public MusicSettingsView()
    {
        InitializeComponent();
        _vm = new MusicSettingsViewModel();
        // 文件选择需 UI 线程的 StorageProvider，由 VM 抛事件、此处代为弹窗。
        // VM 与本控件同生命周期（设置页每次切换新建本页），无需在 Unloaded 摘除订阅。
        _vm.BrowseClientRequested += async (_, _) => await PickClientExeAsync();
        DataContext = _vm;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>弹出文件选择器让用户挑选音乐客户端程序，并写回 profile。</summary>
    private async Task PickClientExeAsync()
    {
        if (_vm is null) return;
        var picked = await UIService.PickFileAsync(
            title: "选择音乐客户端程序",
            filterPattern: "*.exe;*.AppImage;*.sh").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked)) return;

        try
        {
            var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
            profile.MusicClientExePath = picked!;
            ProfileStore.Save(profile);
            _vm.SetStatus("已保存客户端程序路径");
            // 让播放器单例重新读取并通知音乐页刷新显示
            MusicPlayerViewModel.Instance.OnClientPrefsChanged();
        }
        catch (Exception ex)
        {
            _vm.SetStatus($"保存失败：{ex.Message}");
        }
    }
}
