using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class PerfView : UserControl
{
    private PerfViewModel? _vm;

    public PerfView()
    {
        InitializeComponent();
        _vm = new PerfViewModel();
        DataContext = _vm;

        // 切页必须收尾：ShowPage() 每次都 new 一个页面，而 DispatcherTimer 的 Tick 委托
        // 反过来引用着 VM —— 不在这里停表的话，切走之后旧计时器照跑，再切回来又多一个，
        // 表现为 CPU 越用越多、监控数值在两套数据之间跳。
        DetachedFromVisualTree += (_, _) =>
        {
            // Dispose 是幂等的，多次触发也不会出错
            _vm?.Dispose();
            _vm = null;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
