using Avalonia.Controls;
using Avalonia.Input;
using MCLCS.Core.Profiles;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>设置 → 账户页。数据上下文为 AccountsViewModel。</summary>
public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
        DataContext = new AccountsViewModel();
    }

    /// <summary>
    /// 双击账号条目 = 设为当前账号（对齐 WPF AccountList_DoubleClick）。
    /// 多角色时切账号是最高频动作，给个双击直达。
    /// </summary>
    private void AccountList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // 取双击命中的条目；未命中（点在行间空白）时退回当前选中项。
        var item = (e.Source as Control)?.DataContext as AccountEntry
                   ?? (sender as ListBox)?.SelectedItem as AccountEntry;

        if (item is null) return;
        if (DataContext is AccountsViewModel vm)
        {
            vm.SelectedAccount = item;
            vm.SetActiveAccountCommand.Execute(null);
        }
    }
}
