using System;
using System.Collections.Generic;
using System.Text;

namespace MCLCS.Core.Toolbox;

/// <summary>一条聊天记录（从游戏日志中提取）。</summary>
public class ChatEntry
{
    /// <summary>原始整行文本（保留时间戳前缀，便于回溯上下文）。</summary>
    public string Raw { get; init; } = "";

    /// <summary>时间戳（形如 12:34:56），无则为空。</summary>
    public string Time { get; init; } = "";

    /// <summary>发言玩家名。</summary>
    public string Player { get; init; } = "";

    /// <summary>聊天正文。</summary>
    public string Message { get; init; } = "";

    /// <summary>该条是否由玩家自己发出（本地单人游戏时把 <c>&lt;me&gt;</c> 之外都算他人）。</summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// 来源类型：<c>player</c> = 玩家聊天（&lt;名字&gt; 内容）；
    /// <c>system</c> = 系统消息（成就 / 调试 / 服务器广播，实测形如
    /// <c>[System] [CHAT] Player取得了进度[...]</c>，**没有尖括号**）。
    /// </summary>
    public string Source { get; init; } = "player";
}

/// <summary>
/// 聊天记录提取器：从 Minecraft 日志中抽取聊天行。
/// <para>需兼容两种历史格式：</para>
/// <list type="bullet">
///   <item>1.7+ 正式版：<c>[12:34:56] [CHAT] &lt;玩家&gt; 内容</c>（部分版本有 <c>[Render thread/CHAT]</c>）</item>
///   <item>1.6 及更早：<c>[12:34:56] &lt;玩家&gt; 内容</c>（无 CHAT 标记）</item>
/// </list>
/// </summary>
public static class ChatExtractor
{
    /// <summary>从整份日志文本中提取聊天行（按出现顺序）。</summary>
    /// <param name="logText">日志全文（可含 \r\n / \n 混合）。</param>
    /// <param name="maxCount">最多返回多少条，0 或负数表示不限。</param>
    public static List<ChatEntry> Extract(string? logText, int maxCount = 0)
    {
        var result = new List<ChatEntry>();
        if (string.IsNullOrEmpty(logText)) return result;

        foreach (var raw in logText.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryParseLine(raw, out var entry)) result.Add(entry!);
            if (maxCount > 0 && result.Count >= maxCount) break;
        }
        return result;
    }

    /// <summary>尝试把一行解析为聊天记录；不是聊天行返回 false。</summary>
    public static bool TryParseLine(string? line, out ChatEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var rest = line!.Trim();

        // 剥掉开头的时间戳 [12:34:56]
        var time = "";
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close > 1)
            {
                var head = rest[1..close];
                // 形如 12:34:56 / 1:44:49 / 12:34:56.789 —— 注意小时只有 1 位也可能（截图里的 1:44:49）
                if (head.Contains(':') && !head.Contains(' ') && head.Length <= 12)
                {
                    time = head;
                    rest = rest[(close + 1)..].TrimStart();
                }
            }
        }
        if (rest.Length == 0) return false;

        // 循环剥掉**所有**前缀方括号标记，直到不再以 '[' 开头。
        // 实测（1.19+ 单人游戏）真实前缀有**三层**：
        //   [1:44:49] [Render thread/INFO]: [System] [CHAT] Player取得了进度[...]
        //   ^ 时间戳   ^ 线程/级别(带冒号)      ^ 频道    ^ 频道
        // 旧版只有一层（[CHAT] 或 [Render thread/CHAT]），故必须循环而非只剥一次。
        // 标记后可能紧跟冒号（如 `[Render thread/INFO]:`），一并去掉。
        var strippedTags = 0;
        var sawChatTag = false;
        while (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close <= 0) break;
            var tag = rest[1..close];
            if (tag.Contains("CHAT", StringComparison.OrdinalIgnoreCase)) sawChatTag = true;
            rest = rest[(close + 1)..].TrimStart();
            if (rest.StartsWith(':'))
            {
                rest = rest[1..].TrimStart();
                // [Render thread/INFO]: 后紧跟下一个标记，继续循环
            }
            strippedTags++;
        }
        if (rest.Length == 0) return false;

        // 情况 A：<玩家> 内容 —— 真正的玩家聊天
        if (rest.Length >= 3 && rest[0] == '<')
        {
            var gt = rest.IndexOf('>');
            if (gt > 1)
            {
                var player = rest[1..gt];
                var nameOk = player.Length > 0 && !player.Contains('\n');
                // 玩家名不应含空白（Minecraft 名字不允许），据此排除误判
                if (nameOk)
                    foreach (var c in player)
                        if (char.IsWhiteSpace(c)) { nameOk = false; break; }

                if (nameOk)
                {
                    entry = new ChatEntry
                    {
                        Raw = line!,
                        Time = time,
                        Player = player,
                        Message = rest[(gt + 1)..].Trim(),
                        IsLocal = player.Equals("me", StringComparison.OrdinalIgnoreCase),
                        Source = "player"
                    };
                    return true;
                }
            }
        }

        // 情况 B：无尖括号 —— 剥完前缀后剩下的就是正文本身，归为**系统消息**。
        // 实测 1.19+ 单人游戏：achievement / 调试回显 / 服务器广播都走这种格式，
        // 例如 `[System] [CHAT] Player取得了进度[这不是铁镐么]`。
        // ★ 必须**见过 [CHAT] 频道标记**才收 —— 否则 `Saving chunks for level ...`、
        //   `Stopping!` 这类同为 `[Xxx thread/INFO]:` 前缀的普通日志会被大量误收。
        if (sawChatTag && strippedTags > 0 && rest.Length > 0)
        {
            entry = new ChatEntry
            {
                Raw = line!,
                Time = time,
                Player = "",
                Message = rest,
                Source = "system"
            };
            return true;
        }

        return false;
    }

    /// <summary>导出为纯文本（每行 "[时间] 玩家: 内容"）。</summary>
    public static string ToPlainText(IEnumerable<ChatEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (e.Time.Length > 0) sb.Append('[').Append(e.Time).Append("] ");
            sb.Append(e.Player).Append(": ").AppendLine(e.Message);
        }
        return sb.ToString();
    }

    /// <summary>导出为 Markdown（表格，GFM 风格）。</summary>
    public static string ToMarkdown(IEnumerable<ChatEntry> entries)
    {
        var list = new List<ChatEntry>(entries);
        var sb = new StringBuilder();
        sb.AppendLine("# 聊天记录");
        sb.AppendLine();
        sb.AppendLine($"共 {list.Count} 条");
        sb.AppendLine();
        sb.AppendLine("| 时间 | 玩家 | 内容 |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var e in list)
        {
            sb.Append('|').Append(Escape(e.Time))
              .Append('|').Append(Escape(e.Player))
              .Append('|').Append(Escape(e.Message))
              .AppendLine("|");
        }
        return sb.ToString();

        static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }
}
