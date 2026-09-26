using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MCLCS.Core.Tokens;

namespace MCLCS.Linux.App.Converters;

/// <summary>挂机指令类型 → 中文说明（覆盖全部宏动作）。</summary>
public class AfkOpKindToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            AfkOpKind.FunctionKey => "功能键 F",
            AfkOpKind.Delay => "延迟等待",
            AfkOpKind.LongPress => "长按",
            AfkOpKind.KeyCode => "虚拟键码 K",
            AfkOpKind.Click => "连点(左)",
            AfkOpKind.RightClick => "连点(右)",
            AfkOpKind.MouseMove => "鼠标移动",
            AfkOpKind.Scroll => "滚轮",
            AfkOpKind.TypeText => "输入文本",
            AfkOpKind.KeyDown => "按住",
            AfkOpKind.KeyUp => "松开",
            AfkOpKind.RandomDelay => "随机等待",
            AfkOpKind.NamedKey => "按键(名称)",
            AfkOpKind.Repeat => "整体重复",
            _ => value?.ToString() ?? ""
        };

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
