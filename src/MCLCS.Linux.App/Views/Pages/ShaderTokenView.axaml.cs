using Avalonia.Controls;
using Avalonia.Interactivity;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class ShaderTokenView : UserControl
{
    public ShaderTokenView()
    {
        InitializeComponent();
        DataContext = new ShaderTokenViewModel();
    }

    // 复制完整 Token 到系统剪贴板（对齐 WPF 行为；headless 下静默跳过）。
    private async void CopyFull_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShaderTokenViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.FullToken)) { vm.StatusMessage = "没有可复制的内容"; return; }
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not null)
            {
                await top.Clipboard.SetTextAsync(vm.FullToken);
                vm.StatusMessage = "已复制";
            }
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"复制失败: {ex.Message}";
        }
    }
}
