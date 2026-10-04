using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MCLCS.Core.Theme;
using MCLCS.Linux.App.Converters;

namespace MCLCS.Linux.App.Controls;

/// <summary>
/// PNG UI 图标控件（Avalonia 版，对齐 WPF <c>Themes/PngIcon.cs</c>）。
///
/// <para>用法：<c>&lt;controls:PngIcon Token="previous" IconSize="16" /&gt;</c></para>
///
/// <para>加载规则复用 <see cref="TokenToBitmapConverter"/>：按
/// <see cref="ThemeManager.Current"/> 挑 <c>Resources/Icons/{light|dark}</c>，
/// <see cref="IconManager.HighDpi"/> 开启时优先 <c>@2x</c> 目录，
/// 主题目录缺失再回退顶层 <c>Resources/Icons/{token}.png</c>（窗口控制等 Linux 独有图标）。
/// token 缺失或加载失败时显示空白，<b>不回退到任何矢量/emoji</b>（与 WPF 一致）。</para>
///
/// <para>亮/暗与高清切换会触发 <see cref="ThemeManager.OnThemeChanged"/> /
/// <see cref="IconManager.HighDpiChanged"/>，此处订阅后自动重载；
/// 又因控件构造期可能早于 <c>ThemeManager.LoadPreference</c>（会读到默认 Light 而挑到黑图标，
/// 在深色界面里隐形），故 <b>入可视树时再重载一次</b>。</para>
/// </summary>
public class PngIcon : Image
{
    /// <summary>图标 token（= 内嵌 PNG 文件名 = WPF PngIcon.Token）。</summary>
    public static readonly StyledProperty<string?> TokenProperty =
        AvaloniaProperty.Register<PngIcon, string?>(nameof(Token), null);

    /// <summary>渲染尺寸（正方形，px）。</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<PngIcon, double>(nameof(IconSize), 16);

    public string? Token
    {
        get => GetValue(TokenProperty);
        set => SetValue(TokenProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public PngIcon()
    {
        Stretch = Stretch.Uniform;

        // AvaloniaProperty.Register 无 changed 回调参数，改用类级处理器统一重载
        TokenProperty.Changed.AddClassHandler<PngIcon>((_, _) => Reload());
        IconSizeProperty.Changed.AddClassHandler<PngIcon>((_, _) => Reload());

        // 主题/高清切换 → 重载
        ThemeManager.OnThemeChanged += OnThemeChanged;
        IconManager.HighDpiChanged += OnHighDpiChanged;

        // 入树时按已确定的主题再重载一次，避免深色界面里图标隐形
        AttachedToVisualTree += (_, _) => Reload();
    }

    /// <summary>脱离可视树时解除订阅（控件会被反复重建，不解除会累积订阅导致重复重载）。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.OnThemeChanged -= OnThemeChanged;
        IconManager.HighDpiChanged -= OnHighDpiChanged;
    }

    private void OnThemeChanged(ThemeType _) => Reload();

    private void OnHighDpiChanged() => Reload();

    private void Reload()
    {
        var size = IconSize;
        if (size <= 0) return;

        // 尺寸需同时设 Width/Height：Stretch=Uniform 只在一边生效时另一边会失真
        Width = size;
        Height = size;

        Source = LoadBitmap(Token);
    }

    /// <summary>按 token 加载图标位图；失败返回 null（显示空白，不回退矢量）。</summary>
    private static Bitmap? LoadBitmap(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        foreach (var rel in TokenToBitmapConverter.ResolveCandidates(token))
        {
            try
            {
                var uri = new Uri($"avares://MCLCS.Linux.App/{rel}");
                using var stream = AssetLoader.Open(uri);
                if (stream is not null)
                    return new Bitmap(stream);
            }
            catch
            {
                // 尝试下一个候选路径
            }
        }
        return null;
    }
}
