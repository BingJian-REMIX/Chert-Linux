using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.Transformation;
using System;
using System.Threading.Tasks;

namespace MCLCS.Linux.App.Views;

public partial class ToastCard : UserControl
{
    private ToastModel? _model;

    public ToastCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToastModel m) return;
        _model = m;

        // 危险样式：左侧条与强调色改 DangerBrush
        if (m.Options.Danger && Application.Current?.FindResource("DangerBrush") is ISolidColorBrush brush)
            Bar.Fill = brush;

        // 入场动画：toastIn 200ms（opacity 0→1 + translateY 10px→0）
        Opacity = 0;
        RenderTransform = TransformOperations.Parse("translateY(10px)");
        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;
            RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);

        // 自动消失（淡出后移除）
        var ms = m.Options.DurationMs ?? ToastService.Instance.DefaultDurationMs;
        if (ms > 0)
            _ = DismissAfter(ms);
    }

    private async Task DismissAfter(int ms)
    {
        await Task.Delay(ms);
        Dismiss();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Dismiss();

    private void Action_Click(object? sender, RoutedEventArgs e)
    {
        _model?.Options.Action?.Invoke();
        Dismiss();
    }

    private void Dismiss()
    {
        Opacity = 0; // 触发 0.2s 淡出过渡
        _ = Task.Delay(220).ContinueWith(_ =>
        {
            if (_model is not null)
                ToastService.Instance.Remove(_model);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
