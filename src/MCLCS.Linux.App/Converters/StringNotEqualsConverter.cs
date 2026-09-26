using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MCLCS.Linux.App.Converters;

/// <summary>字符串不等于参数 → true（用于按动作类型隐藏某项编辑框，如 T 用明文框时隐藏参数框）。</summary>
public class StringNotEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is string s && parameter is string p && string.Equals(s, p, StringComparison.OrdinalIgnoreCase));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
