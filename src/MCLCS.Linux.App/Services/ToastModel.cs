using System;
using MCLCS.Core.Theme;

namespace MCLCS.Linux.App;

/// <summary>Toast 运行时模型（含唯一 Id，供服务移除）。</summary>
public sealed class ToastModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public ToastOptions Options { get; }

    public ToastModel(ToastOptions options) => Options = options;

    /// <summary>是否为「安卓布局」风格：仅 <see cref="ThemeManager.UiStyle"/> 为 android 时为真，改用安卓通知卡片头部。</summary>
    public bool IsAndroid => ThemeManager.UiStyle == "android";

    /// <summary>时间戳的人类可读文本：1 分钟内显示「刚刚」，否则显示 HH:mm。</summary>
    public string TimeText => (DateTime.Now - Options.Timestamp).TotalMinutes < 1 ? "刚刚" : Options.Timestamp.ToString("HH:mm");
}
