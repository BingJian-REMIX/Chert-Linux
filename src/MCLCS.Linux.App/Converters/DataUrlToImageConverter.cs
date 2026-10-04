using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace MCLCS.Linux.App.Converters;

/// <summary>
/// 把 data URL（<c>data:image/png;base64,...</c>）转成 Avalonia 的 <see cref="Bitmap"/>。
///
/// <para><b>为什么需要它</b>：扫码登录接口把二维码作为 data URL 返回 ——
/// Core 层不能依赖任何 UI 框架的图像类型（否则没法双端共用），所以只传字符串上来，
/// 由界面层各自解码。WPF 端是同名转换器 + BitmapImage。</para>
///
/// <para><b>失败策略</b>：任何异常都返回 null —— 二维码解析失败最多是图不显示，
/// 不该把绑定链路拖崩（那样会连累整个设置页）。</para>
/// </summary>
public class DataUrlToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;

        try
        {
            // 目前只支持 data URL；http(s) 图片交给 Image.Source 自己去加载
            if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

            var comma = s.IndexOf(',');
            if (comma <= 0) return null;
            if (!s[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

            var bytes = System.Convert.FromBase64String(s[(comma + 1)..]);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
