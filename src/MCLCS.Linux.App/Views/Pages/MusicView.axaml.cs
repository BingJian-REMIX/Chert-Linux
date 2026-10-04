using System;
using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

/// <summary>音乐播放器面板（工具箱）。播放状态与命令来自 <see cref="MusicPlayerViewModel"/> 单例；
/// 实际解码由主窗口注入的 BASS 宿主完成，本面板只负责展示与交互。
/// 因 VM 为单例，切到其它页再回来时播放状态保持。</summary>
public partial class MusicView : UserControl
{
    /// <summary>歌词换行动画时长（对齐 WPF LyricFadeMs=260）。</summary>
    private const int LyricFadeMs = 260;

    /// <summary>歌词换行时的上移距离（px，对齐 WPF LyricRisePx=7）。</summary>
    private const double LyricRisePx = 7;

    public MusicView()
    {
        InitializeComponent();
        DataContext = MusicPlayerViewModel.Instance;
        // 歌词逐行切换时淡出→上移淡入（对齐 WPF OnLyricTextChanged/PlayLyricTransition）。
        // VM 为单例而本控件可能被重建，故在 Unloaded 摘除订阅，避免重复订阅叠加多次动画。
        MusicPlayerViewModel.Instance.PropertyChanged += OnLyricTextChanged;
        Unloaded += (_, _) => MusicPlayerViewModel.Instance.PropertyChanged -= OnLyricTextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private string _lastLyricText = "";

    /// <summary>按下进度条：暂停心跳回写，避免刚拖到的位置被采样值拽回（对齐 WPF wasSeeking）。</summary>
    private void Seek_PointerPressed(object? sender, RoutedEventArgs e)
        => MusicPlayerViewModel.Instance.IsSeeking = true;

    /// <summary>松开进度条：按当前值真正跳转，并恢复心跳回写。</summary>
    private void Seek_PointerReleased(object? sender, RoutedEventArgs e)
    {
        var vm = MusicPlayerViewModel.Instance;
        if (sender is Slider slider) vm.SeekTo(slider.Value, commit: true);
        vm.IsSeeking = false;
    }

    private void OnLyricTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MusicPlayerViewModel.LyricText)) return;
        Dispatcher.UIThread.Post(() => PlayLyricTransition(), DispatcherPriority.Render);
    }

    /// <summary>歌词行变化时播一段「淡出 + 上移 → 淡入」过渡。</summary>
    private void PlayLyricTransition()
    {
        if (LyricBox is null) return;
        var vm = MusicPlayerViewModel.Instance;
        if (string.Equals(vm.LyricText, _lastLyricText, StringComparison.Ordinal)) return;
        _lastLyricText = vm.LyricText;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(LyricFadeMs),
            Easing = new CubicEaseOut(),
        };
        anim.Children.Add(new KeyFrame
        {
            Setters =
            {
                new Setter { Property = Avalonia.Visual.OpacityProperty, Value = 0.15d },
                new Setter { Property = TranslateTransform.YProperty, Value = -LyricRisePx },
            },
        });
        anim.Children.Add(new KeyFrame
        {
            Setters =
            {
                new Setter { Property = Avalonia.Visual.OpacityProperty, Value = 0.92d },
                new Setter { Property = TranslateTransform.YProperty, Value = 0d },
            },
        });

        var tf = LyricBox.RenderTransform as TranslateTransform;
        if (tf is null)
        {
            tf = new TranslateTransform(0, 0);
            LyricBox.RenderTransform = tf;
        }
        tf.Y = 0;
        _ = anim.RunAsync(LyricBox);
    }
}
