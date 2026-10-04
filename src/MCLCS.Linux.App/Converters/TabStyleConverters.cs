using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MCLCS.Core.UI;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Converters;

/// <summary>索引贴宽度：展开 130 / 折叠 56（严格对齐 WPF MainTabs.ExpandedWidth/CollapsedWidth）。
/// 原为 142/60，比 WPF 宽 12/4px，导致同一屏内索引贴整体比 Windows 端宽出一截。</summary>
public class TabWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => value is true ? 130.0 : 56.0;

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}

/// <summary>
/// 索引贴下划线取色：按该项 Kind 返回 brighten(solid,1.45) 的提亮色。
/// 对齐模板 renderTabs 的 <c>const ul = brighten(solid,1.45)</c>。
/// </summary>
public class TabUnderlineBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not MainTabKind kind) return new SolidColorBrush(Colors.White);
        var theme = MainViewModel.Instance?.Theme;
        var hex = theme is not null ? theme.UnderlineColorOf(kind) : "#FFFFFF";
        return HexToBrushConverter.ToBrush(hex);
    }

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}

/// <summary>
/// 索引贴背景（多值）：values[0]=是否选中，values[1]=该项 Kind。
/// 选中取主题色提亮 1.12（ActiveColorOf，对齐 launcher19 的 brightness(1.12)），
/// 未选中取暗化 0.7（DimColorOf，对齐 launcher19 的 brightness(.7)）。
/// 相较旧版 {Binding .} 绑整个 TabItemViewModel，多值绑定会因 IsSelected 的
/// PropertyChanged 精确触发重算，修复切换主标签时背景色不刷新的 Bug-1。
/// </summary>
public class TabBackgroundConverter : IMultiValueConverter
{
    /// <summary>
    /// 底色取值优先级：<b>选中 → 悬浮 → 折叠</b>。
    /// 选中取主题色提亮 1.12（ActiveColorOf），悬浮取提亮 1.2（对齐 WPF <c>Brighten(solid,1.2)</c>），
    /// 折叠取暗化 0.7（DimColorOf）。
    /// hover 参与多值绑定后，底色变化由 <c>BrushTransition</c> 平滑过渡（此前在
    /// <c>Tab_PointerEntered</c> 里直接赋 <c>btn.Background</c>，既无动画又会覆盖绑定，
    /// 导致之后切换选中态时底色不再刷新）。
    /// </summary>
    public object? Convert(IList<object?> values, Type? targetType, object? parameter, CultureInfo? culture)
    {
        if (values.Count < 2 || values[1] is not MainTabKind kind)
            return new SolidColorBrush(Colors.Gray);

        var isSelected = values[0] is bool s && s;
        var isHovered = values.Count >= 3 && values[2] is bool h && h;

        var theme = MainViewModel.Instance?.Theme;
        if (theme is null)
            return new SolidColorBrush(Colors.Gray);

        var hex = isSelected ? theme.ActiveColorOf(kind)
                 : isHovered ? TabThemeConfig.Brighten(theme.ColorOf(kind), 1.2)
                 : theme.DimColorOf(kind);
        return HexToBrushConverter.ToBrush(hex);
    }
}

/// <summary>索引贴外边距：首项无偏移，其余左移形成重叠（对齐 WPF MainTabs.CollapsedOverlap=20）。</summary>
public class TabMarginConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => value is int order && order > 0 ? new Thickness(-20, 0, 0, 0) : new Thickness(0);

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}

/// <summary>索引贴圆角（对齐 WPF TabCornerRadius）：最左贴左圆右直、最右贴右圆左直、中间全直，
/// 使重叠接缝侧切直、外侧圆角，整体呈圆头药丸外观。</summary>
public class TabCornerConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {
        if (value is not TabItemViewModel t) return new CornerRadius(0);
        if (t.Order == 0) return new CornerRadius(8, 8, 0, 0);                 // 最左：仅顶部左圆，下边全直
        if (t.Order == t.TotalTabs - 1) return new CornerRadius(0, 8, 0, 0);  // 最右：仅顶部右圆，下边全直
        return new CornerRadius(0);                                           // 中间：全直
    }

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}
