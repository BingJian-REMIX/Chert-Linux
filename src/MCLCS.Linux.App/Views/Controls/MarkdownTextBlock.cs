using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MCLCS.Linux.App.Views.Controls;

/// <summary>
/// 极简 Markdown 渲染控件（对齐 WPF 的 MarkdownTextBlock + MarkdownParser）：
/// 覆盖 AI 回复与更新日志常见子集 —— 标题（#/##）、有序 / 无序列表、加粗、斜体、
/// 行内代码、围栏代码块、普通段落。渲染为原生控件（可在气泡 / 弹窗里随主题换色）。
/// 解析失败时整体退化为纯文本段落，不抛异常。
/// </summary>
public class MarkdownTextBlock : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private readonly StackPanel _host = new() { Spacing = 2 };

    public MarkdownTextBlock()
    {
        Content = _host;
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((c, _) => c.Render());
    }

    private void Render()
    {
        _host.Children.Clear();
        try
        {
            Fill(_host, Markdown ?? "");
        }
        catch
        {
            // 兜底：任何解析异常都退化为一个纯文本块，绝不因为渲染失败让用户看不到内容
            _host.Children.Clear();
            _host.Children.Add(new TextBlock
            {
                Text = Markdown ?? "",
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    // ================= 解析 =================

    private static void Fill(Panel host, string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var listItems = new List<(string Text, bool Ordered)>();
        var codeLines = new List<string>();
        var inCode = false;

        void FlushList()
        {
            if (listItems.Count == 0) return;
            var ordered = listItems[0].Ordered;
            for (var i = 0; i < listItems.Count; i++)
            {
                var (t, _) = listItems[i];
                var marker = ordered ? $"{i + 1}. " : "• ";
                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(18, 0, 0, 2)
                };
                tb.Inlines!.Add(new Run(marker));
                foreach (var il in ParseInline(t)) tb.Inlines.Add(il);
                host.Children.Add(tb);
            }
            listItems.Clear();
        }

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var tb = new SelectableTextBlock
            {
                Text = string.Join("\n", codeLines),
                FontFamily = new FontFamily("Consolas, Menlo, Monaco, DejaVu Sans Mono, monospace"),
                TextWrapping = TextWrapping.Wrap
            };
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(10),
                Child = tb
            };
            TryBind(border, Border.BorderBrushProperty, "ControlBorder");
            TryBind(border, Border.BackgroundProperty, "ControlBackground");
            host.Children.Add(border);
            codeLines.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (inCode)
            {
                if (trimmed == "```") { inCode = false; FlushCode(); }
                else codeLines.Add(line);
                continue;
            }

            if (trimmed.StartsWith("```")) { inCode = true; continue; }

            if (string.IsNullOrWhiteSpace(line)) { FlushList(); continue; }

            if (trimmed.StartsWith("# "))
            {
                FlushList();
                host.Children.Add(Head(trimmed.Substring(2), 17, new Thickness(0, 10, 0, 4)));
                continue;
            }

            if (trimmed.StartsWith("## "))
            {
                FlushList();
                host.Children.Add(Head(trimmed.Substring(3), 15, new Thickness(0, 8, 0, 3)));
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                listItems.Add((trimmed.Substring(2).Trim(), false));
                continue;
            }

            var m = Regex.Match(trimmed, @"^\d+[\.\)]\s+(.*)$");
            if (m.Success)
            {
                listItems.Add((m.Groups[1].Value, true));
                continue;
            }

            FlushList();
            var para = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            foreach (var il in ParseInline(trimmed)) para.Inlines!.Add(il);
            host.Children.Add(para);
        }

        FlushList();
        FlushCode();
    }

    private static TextBlock Head(string raw, double size, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };
        foreach (var il in ParseInline(raw)) tb.Inlines!.Add(il);
        return tb;
    }

    /// <summary>Avalonia 的 Bold / Italic 没有带 Run 的构造，需手动塞 Inlines。</summary>
    private static Inline Wrap(Span span, string text)
    {
        span.Inlines!.Add(new Run(text));
        return span;
    }

    /// <summary>行内解析：反引号代码、加粗 **x**、斜体 *x*，其余按普通文本。</summary>
    private static IEnumerable<Inline> ParseInline(string s)
    {
        var inlines = new List<Inline>();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    var span = new Span()
                    {
                        FontFamily = new FontFamily("Consolas, Menlo, Monaco, DejaVu Sans Mono, monospace")
                    };
                    // 行内代码用强调色区分；取不到资源时退回继承色
                    if (TryFindBrush("AccentBrush") is { } brush) span.Foreground = brush;
                    span.Inlines!.Add(new Run(s.Substring(i + 1, end - i - 1)));
                    inlines.Add(span);
                    i = end + 1;
                    continue;
                }
            }

            if (s[i] == '*')
            {
                if (i + 1 < s.Length && s[i + 1] == '*')            // 加粗 **x**
                {
                    var end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        inlines.Add(Wrap(new Bold(), s.Substring(i + 2, end - i - 2)));
                        i = end + 2;
                        continue;
                    }
                }
                else                                                // 斜体 *x*
                {
                    var end = s.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        inlines.Add(Wrap(new Italic(), s.Substring(i + 1, end - i - 1)));
                        i = end + 1;
                        continue;
                    }
                }
            }

            var next = s.IndexOfAny(new[] { '`', '*' }, i);
            if (next < 0)
            {
                inlines.Add(new Run(s.Substring(i)));
                break;
            }
            if (next > i) inlines.Add(new Run(s.Substring(i, next - i)));
            i = next;
        }
        return inlines;
    }

    // ================= 主题资源（取不到就跳过，跟随继承色）=================

    private static void TryBind(Control target, AvaloniaProperty property, string key)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var res))
            target.SetValue(property, res);
    }

    private static IBrush? TryFindBrush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var res) ? res as IBrush : null;
}
