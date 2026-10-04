using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MCLCS.Linux.App;

/// <summary>
/// 页面 / 面板切换动画（对齐 WPF <c>MotionFX.SlideSwap</c> 的观感）。
///
/// 模式：<b>左右滚动交叉过渡</b>——旧内容向左滚出并淡出、新内容从右滚入并淡入，两者同时进行。
/// 此前设置页分类切换是「内容瞬换 + 无动画」，观感为硬跳。
///
/// 实现说明（Avalonia 与 WPF 的差异）：
/// WPF 用 <c>RenderTargetBitmap.Render</c> 同步拍一张旧内容位图当残影叠上去。
/// Avalonia 的位图渲染依赖渲染后端的 DPI，跨后端（Win/X11/软件）行为不一致；
/// 因此这里改为<b>把旧内容控件实例临时搬到覆盖层</b>做残影——渲染真实、无位图 API 依赖、跨后端稳定，
/// 视觉与 WPF 等价。
/// </summary>
public static class MotionFx
{
    /// <summary>滚动距离（px）。</summary>
    public const double SlideOffsetX = 28;

    /// <summary>滚动时长，与 WPF <c>SlideMs</c> 对齐。</summary>
    public static readonly TimeSpan SlideMs = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan FadeMs = TimeSpan.FromMilliseconds(160);

    /// <summary>是否允许动画（对齐 WPF <c>MainWindow.AnimationsEnabled</c>）。</summary>
    public static bool AnimationsEnabled { get; set; } = true;

    private static readonly Easing Ease = new CubicEaseOut();

    /// <summary>
    /// 交叉过渡换内容：旧内容（<paramref name="oldContent"/>，若为可视控件）向左滚出淡出，
    /// <paramref name="host"/> 换入新内容后从右滚入淡入。
    /// 宿主不可见 / 尺寸为 0 / 动画关闭时退化为直接替换，不产生残影。
    /// </summary>
    public static async void SwapContent(ContentControl host, object? newContent, object? oldContent)
    {
        if (host is null) return;

        if (!CanAnimate(host))
        {
            host.Content = newContent;
            return;
        }

        var old = oldContent as Control;
        var layer = FindLayer(host);
        Border? ghost = null;

        if (old is not null && layer is not null)
        {
            // 先把旧内容从宿主摘下（Avalonia 要求控件仅一个父级），再挂到覆盖层。
            host.Content = null;
            ghost = BuildGhost(old, host, layer);
            if (ghost is not null)
                layer.Children.Add(ghost);   // 后加入者绘制在上层 → 残影盖住新内容，形成交叉
        }

        host.Content = newContent;

        // 旧残影：向左滚出 + 淡出；新内容：从右滚入 + 淡入。两者同时进行。
        var ghostTask = ghost is not null ? AnimateAsync(ghost, 0, -SlideOffsetX, 1, 0, SlideMs) : Task.CompletedTask;
        var hostTask = AnimateAsync(host, SlideOffsetX, 0, 0, 1, FadeMs);

        try { await Task.WhenAll(ghostTask, hostTask).ConfigureAwait(true); }
        catch (Exception) { /* 动画被中断/取消时静默收尾 */ }

        if (ghost is not null && layer is not null)
        {
            layer.Children.Remove(ghost);
            ghost.Child = null;
        }
    }

    /// <summary>从右滚入 + 淡入（无旧内容时的单纯入场）。</summary>
    public static void SlideInFromRight(Control? host)
    {
        if (host is null || !CanAnimate(host)) return;
        _ = AnimateAsync(host, SlideOffsetX, 0, 0, 1, FadeMs);
    }

    /// <summary>宿主是否可参与动画：动画开关开 + 在可视树中 + 尺寸有效。</summary>
    private static bool CanAnimate(Control host) =>
        AnimationsEnabled
        && host.IsAttachedToVisualTree()
        && host.Bounds.Width >= 1
        && host.Bounds.Height >= 1;

    /// <summary>构造覆盖旧内容的残影层（尺寸/位置对齐宿主，透明且不拦命中测试）。</summary>
    private static Border? BuildGhost(Control old, Control host, Panel layer)
    {
        try
        {
            var origin = host.TranslatePoint(new Point(0, 0), layer) ?? new Point(0, 0);
            return new Border
            {
                Width = host.Bounds.Width,
                Height = host.Bounds.Height,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(origin.X, origin.Y, 0, 0),
                RenderTransform = new TranslateTransform(0, 0),
                Child = old,
            };
        }
        catch
        {
            return null;   // 构建失败 → 退化为仅新内容滑入
        }
    }

    /// <summary>向上找到最近的 <see cref="Panel"/> 祖先，用于插放残影覆盖层。</summary>
    private static Panel? FindLayer(Visual visual)
    {
        Visual? cur = visual;
        while (cur is not null)
        {
            if (cur is Panel p) return p;
            cur = cur.GetVisualParent();
        }
        return null;
    }

    /// <summary>
    /// 统一动画驱动：把控件的 X 位移与不透明度从起始值过渡到终值。
    /// 终值取自然值（X=0、Opacity=1），故 <see cref="FillMode.Forward"/> 收尾后不残留偏移。
    /// </summary>
    private static Task AnimateAsync(Control control, double fromX, double toX,
                                     double fromOpacity, double toOpacity, TimeSpan duration)
    {
        if (control.RenderTransform is not TranslateTransform tf)
        {
            tf = new TranslateTransform(fromX, 0);
            control.RenderTransform = tf;
        }

        control.Opacity = fromOpacity;
        tf.X = fromX;

        var anim = new Animation
        {
            Duration = duration,
            FillMode = FillMode.Forward,
            Easing = Ease,
        };
        anim.Children.Add(new KeyFrame
        {
            Setters = { new Setter { Property = TranslateTransform.XProperty, Value = toX } },
        });
        anim.Children.Add(new KeyFrame
        {
            Setters = { new Setter { Property = Visual.OpacityProperty, Value = toOpacity } },
        });

        return anim.RunAsync(control, CancellationToken.None);
    }
}
