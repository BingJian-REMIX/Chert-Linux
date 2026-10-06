using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>下载 → 原版安装页。数据上下文为 InstallViewModel。</summary>
public partial class InstallView : UserControl
{
    public InstallView()
    {
        InitializeComponent();
        DataContext = new InstallViewModel();
        // 版本清单延迟到页面显示时才拉（构造期拉会拖慢切页，且无网络时白等）
        Loaded += async (_, _) =>
        {
            if (DataContext is InstallViewModel vm)
                await vm.EnsureVersionsLoadedAsync();
        };
    }
}
