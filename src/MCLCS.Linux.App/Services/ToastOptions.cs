namespace MCLCS.Linux.App;

/// <summary>一条 Toast 通知的配置（对齐设计稿 .toast）。</summary>
public sealed class ToastOptions
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>危险样式（左侧条与强调色用 DangerBrush）。</summary>
    public bool Danger { get; init; }
    /// <summary>可选操作按钮文字；为 null 不显示。</summary>
    public string? ActionText { get; init; }
    /// <summary>操作按钮点击回调。</summary>
    public System.Action? Action { get; init; }
    /// <summary>
    /// 自动消失时长（毫秒）；≤0 表示不自动消失、需手动关闭。
    /// 为 null 时跟随 <see cref="ToastService.DurationSeconds"/>（设置 → 通用 的 Toast 停留时长）。
    /// </summary>
    public int? DurationMs { get; init; }
}
