using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>游戏主页（快速启动）。数据上下文为 GameHomeViewModel。</summary>
public partial class GameHomeView : UserControl
{
    public GameHomeView()
    {
        InitializeComponent();
        DataContext = new GameHomeViewModel();

        // 横向卡片流滚轮支持（对齐 WPF GameView_PreviewMouseWheel）。
        //   Avalonia 的 ScrollViewer 滚轮默认只作用于垂直方向，横向容器里滚轮完全无效 ——
        //   体感是「必须把鼠标移到最下/最右露出滚动条才能拖」。
        //   在页根统一挂 PointerWheelChanged（隧道阶段能先于子 ItemsControl / 卡片 Button 拿到事件），
        //   命中横向 ScrollViewer 就把垂直 Delta 换算成横向偏移。
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>一格滚轮 ≈ 48px 位移（与 WPF 一致），观感更跟手。</summary>
    private const double WheelStepPx = 48;

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // 命中最近的祖先横向 ScrollViewer；不在卡片流里则不拦截，交给外层正常处理。
        var sv = FindAncestorHorizontalScrollViewer(e.Source as Visual);
        if (sv is null) return;

        // Avalonia 无 WPF 的 ScrollableWidth，可滚动距离 = 内容宽 - 视口宽（>0 才说明确实可滚）。
        var scrollable = sv.Extent.Width - sv.Viewport.Width;
        if (scrollable <= 0) return;

        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;

        var next = sv.Offset.X - Math.Sign(delta) * WheelStepPx;
        var clamped = Math.Max(0, Math.Min(next, scrollable));
        sv.Offset = new Vector(clamped, sv.Offset.Y);

        // 标记已处理：否则事件继续冒泡，页面外层的 ScrollViewer 也会跟着动。
        e.Handled = true;
    }

    /// <summary>从事件源向上找最近的、开了水平滚动且确实可滚的 ScrollViewer。</summary>
    private static ScrollViewer? FindAncestorHorizontalScrollViewer(Visual? node)
    {
        while (node is not null)
        {
            if (node is ScrollViewer sv && sv.Extent.Width - sv.Viewport.Width > 0)
                return sv;
            node = node.GetVisualParent();
        }
        return null;
    }
}
