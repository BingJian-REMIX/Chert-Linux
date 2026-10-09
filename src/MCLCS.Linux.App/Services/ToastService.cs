using System.Collections.ObjectModel;

namespace MCLCS.Linux.App;

/// <summary>Toast 通知服务（单例）。管理右下角堆叠的通知列表，供 <see cref="Views.ToastHost"/> 绑定。</summary>
public sealed class ToastService
{
    public static readonly ToastService Instance = new();

    public ObservableCollection<ToastModel> Toasts { get; } = new();

    /// <summary>
    /// Toast 停留时长（秒），来自「设置 → 通用」。0 表示不自动消失、需手动关闭；默认 5 秒。
    /// 仅在单条通知未显式指定 <see cref="ToastOptions.DurationMs"/> 时生效。
    /// </summary>
    public int DurationSeconds { get; set; } = 5;

    /// <summary>由 <see cref="DurationSeconds"/> 折算的默认毫秒数（0 = 不自动消失）。</summary>
    public int DefaultDurationMs => DurationSeconds <= 0 ? 0 : DurationSeconds * 1000;

    public void Show(ToastOptions options) => Toasts.Add(new ToastModel(options));

    public void Remove(ToastModel model) => Toasts.Remove(model);
}
