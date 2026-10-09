using Avalonia.Controls;
using Avalonia.Interactivity;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class SeasonalHubView : UserControl
{
    public SeasonalHubView()
    {
        InitializeComponent();
        DataContext = new SeasonalHubViewModel();
    }

    /// <summary>复制服务器地址：Avalonia 的剪贴板挂在 TopLevel 上，VM 层拿不到，故在视图里处理。</summary>
    private async void CopyAddress_Click(object? sender, RoutedEventArgs e)
    {
        var address = (sender as Control)?.Tag as string;
        if (string.IsNullOrWhiteSpace(address)) return;

        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not null)
                await top.Clipboard.SetTextAsync(address);
            if (DataContext is SeasonalHubViewModel vm)
                vm.StatusMessage = "服务器地址已复制";
        }
        catch
        {
            if (DataContext is SeasonalHubViewModel vm2)
                vm2.StatusMessage = "复制失败，请手动选中地址复制";
        }
    }
}
