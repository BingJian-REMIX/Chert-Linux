using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCLCS.Core.Profiles;

namespace MCLCS.Core.Music;

/// <summary>一行歌词及其时间戳（秒）。</summary>
public sealed class LyricLine
{
    public required double TimeSec { get; init; }
    public required string Text { get; init; }

    public override string ToString() => $"[{TimeSec:F2}] {Text}";
}

/// <summary>
/// 歌词引擎（规格 · 实现项 3）。
///
/// <para><b>输入</b>：API 模式 / 本地客户端模式下播放器的实时进度（秒），
/// 或本地文件夹模式下解析出的 LRC 文件。</para>
///
/// <para><b>查找</b>：按时间戳排序后<b>二分查找</b>当前行 —— 歌词常有数百行，
/// 每帧线性扫描会浪费 CPU；二分是 O(log n)。</para>
///
/// <para><b>偏移</b>：<see cref="OffsetMs"/> 为全局偏移（毫秒），正值表示歌词提前。
/// 本期<b>不提供 UI 入口</b>（规格已确认），但字段与逻辑已就位，留待后续接 UI。</para>
///
/// <para><b>线程安全</b>：<see cref="Load"/> 可从后台线程调用（解析 LRC 较慢）；
/// 读取歌词行用 <see cref="Volatile"/> 换整份数组引用，避免与写入竞争。</para>
///
/// <para><b>复用</b>：<see cref="LyricEngine"/> 实例由上层（迷你播放条 / 叠加层）共享，
/// 避免重复解析同一份歌词 —— 规格实现项 4 要求「共用同一个歌词引擎实例」。</para>
/// </summary>
public sealed class LyricEngine
{
    private LyricLine[] _lines = Array.Empty<LyricLine>();
    private string _source = "";

    /// <summary>已加载的歌词行（按时间升序）。空数组表示无歌词。</summary>
    public IReadOnlyList<LyricLine> Lines => Volatile.Read(ref _lines);

    /// <summary>当前歌词来源标识（文件路径或 API 返回的键），供 UI 判断「有没有歌词」。</summary>
    public string Source => _source;

    /// <summary>是否已加载到歌词。</summary>
    public bool HasLyric => Lines.Count > 0;

    /// <summary>
    /// 全局偏移量（毫秒）。<b>本期不提供 UI 入口</b>，字段与逻辑已就位。
    /// <para>语义：<b>正值 = 歌词提前显示</b>（判定时把进度<b>加上</b>偏移，
    /// 等价于歌词时间轴整体前移）；<b>负值 = 歌词延后</b>。</para>
    /// </summary>
    public int OffsetMs { get; set; }

    /// <summary>
    /// 载入歌词。同一来源重复调用会直接返回，避免重复解析（规格：避免重复解析）。
    /// </summary>
    public void Load(string? source, IEnumerable<LyricLine> lines)
    {
        if (string.Equals(_source, source, StringComparison.Ordinal) && HasLyric) return;

        var arr = lines.OrderBy(l => l.TimeSec).ToArray();
        Volatile.Write(ref _lines, arr);
        _source = source ?? "";
    }

    /// <summary>清空歌词。</summary>
    public void Clear()
    {
        Volatile.Write(ref _lines, Array.Empty<LyricLine>());
        _source = "";
    }

    /// <summary>
    /// 解析标准 LRC 文本。<b>支持一行多时间戳</b>（<c>[00:12.00][01:30.50]歌词</c>）。
    /// </summary>
    public static List<LyricLine> ParseLrc(string? text)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 收集该行所有 [mm:ss.xx] 时间戳
            var times = new List<double>();
            int idx = 0;
            while (idx < line.Length && line[idx] == '[')
            {
                var close = line.IndexOf(']', idx);
                if (close < 0) break;
                var tag = line.Substring(idx + 1, close - idx - 1);
                if (TryParseTimestamp(tag, out var sec)) times.Add(sec);
                idx = close + 1;
            }
            if (times.Count == 0) continue;   // 无有效时间戳（metadata 行等）→ 跳过

            // 剩余部分是歌词正文；完全空白的视为「间奏占位」，也保留（渲染成空白行）
            var text2 = line[idx..].Trim();

            foreach (var t in times)
                result.Add(new LyricLine { TimeSec = t, Text = text2 });
        }

        result.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
        return result;
    }

    /// <summary>解析 <c>mm:ss.xx</c> / <c>mm:ss</c> / <c>hh:mm:ss.xx</c> 形式的时间戳。</summary>
    private static bool TryParseTimestamp(string tag, out double seconds)
    {
        seconds = 0;
        // 去掉可能存在的 x100 毫秒后缀（如 [00:12.34] 已是秒；[01:02:03] 是 时:分:秒）
        var parts = tag.Split(':');
        if (parts.Length is < 2 or > 3) return false;

        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var last)) return false;
        if (!int.TryParse(parts[^2], out var min)) return false;
        // 时段可选：只有 hh:mm:ss.xx 才有第三段
        var hh = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], out hh)) return false;

        if (min < 0 || min >= 60) return false;
        if (hh < 0) return false;

        seconds = hh * 3600 + min * 60 + last;
        return true;
    }

    /// <summary>
    /// 从本地文件载入歌词。优先找同名 <c>.lrc</c>（大小写不敏感），找不到返回 false。
    /// </summary>
    public bool TryLoadFromFile(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return false;

        string? lrc = null;
        try
        {
            // 先取扩展名前的部分，再拼 .lrc（处理 .mp3 / .flac / .ogg 等）
            var dir = Path.GetDirectoryName(mediaPath);
            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            if (string.IsNullOrEmpty(stem)) return false;

            var candidate = Path.Combine(dir ?? "", stem + ".lrc");
            if (File.Exists(candidate))
            {
                lrc = candidate;
            }
            else
            {
                // 目录里可能大小写不同（.LRC），做一次不区分大小写的枚举
                foreach (var f in Directory.EnumerateFiles(dir ?? "", "*.*"))
                {
                    if (!string.Equals(Path.GetFileNameWithoutExtension(f), stem,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(Path.GetExtension(f), ".lrc", StringComparison.OrdinalIgnoreCase)) continue;
                    lrc = f;
                    break;
                }
            }
        }
        catch
        {
            return false;
        }

        if (lrc is null) { Clear(); return false; }

        try
        {
            var parsed = ParseLrc(File.ReadAllText(lrc));
            Load(lrc, parsed);
            return HasLyric;
        }
        catch
        {
            // 读文件失败（占用 / 编码异常）→ 视为无歌词，静默
            Clear();
            return false;
        }
    }

    /// <summary>
    /// 按当前播放进度<b>二分查找</b>当前行索引。没有歌词返回 -1。
    /// </summary>
    /// <param name="positionSec">播放进度（秒）。</param>
    /// <returns>当前行索引；无歌词或进度在第一行之前返回 -1。</returns>
    public int FindIndexAt(double positionSec)
    {
        var lines = Lines;
        if (lines.Count == 0) return -1;

        var t = positionSec + OffsetMs / 1000.0;
        if (t < lines[0].TimeSec) return -1;

        // 二分：找最后一个 TimeSec <= t 的行
        int lo = 0, hi = lines.Count - 1, ans = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].TimeSec <= t) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>
    /// 按 <see cref="LyricPinMode"/> 取当前应显示的歌词行。
    /// 供叠加层与迷你播放条<b>共用同一实例</b>消费（规格实现项 4）。
    /// </summary>
    public IReadOnlyList<LyricLine> GetDisplayLines(double positionSec)
    {
        var idx = FindIndexAt(positionSec);
        if (idx < 0) return Array.Empty<LyricLine>();

        var lines = Lines;
        var mode = PinMode;

        if (mode == LyricPinMode.CurrentOnly)
            return new[] { lines[idx] };

        if (mode == LyricPinMode.CurrentAndNext)
        {
            if (idx + 1 < lines.Count) return new[] { lines[idx], lines[idx + 1] };
            return new[] { lines[idx] };
        }

        // PrevCurrentNext
        if (idx > 0 && idx + 1 < lines.Count)
            return new[] { lines[idx - 1], lines[idx], lines[idx + 1] };
        if (idx > 0) return new[] { lines[idx - 1], lines[idx] };
        if (idx + 1 < lines.Count) return new[] { lines[idx], lines[idx + 1] };
        return new[] { lines[idx] };
    }

    /// <summary>当前应显示的行数（由 <see cref="LyricPinMode"/> 与实际可用行数共同决定）。</summary>
    public int DisplayLineCount(double positionSec) => GetDisplayLines(positionSec).Count;

    /// <summary>歌词固定方式（由设置项驱动）。</summary>
    public LyricPinMode PinMode { get; set; } = LyricPinMode.CurrentOnly;
}
