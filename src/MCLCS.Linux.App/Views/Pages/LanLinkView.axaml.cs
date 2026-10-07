using System.Threading.Tasks;
using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>清单 #35 ~ #40：局域网联动页（对等模式，不做远端部署）。</summary>
public partial class LanLinkView : UserControl
{
    public LanLinkView()
    {
        InitializeComponent();
        // 单例：服务状态 / 邀请确认都挂在 VM 上，切页重建会重复订阅事件
        DataContext = LanLinkViewModel.Instance;
        // 剪贴板要经 TopLevel 取（Avalonia 无全局 Clipboard 静态类），由 View 注入给 VM
        Loaded += (_, _) =>
        {
            if (DataContext is LanLinkViewModel vm)
                vm.ClipboardWriter = text => WriteClipboardAsync(text);
        };
    }

    private Task WriteClipboardAsync(string text)
        => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    /// <summary>供外部（如全局搜索命中后）主动搜索一次。</summary>
    public void Refresh() => LanLinkViewModel.Instance.RefreshCommand.Execute(null);
}
