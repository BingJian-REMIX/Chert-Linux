using System.IO;
using System.IO.Compression;
using System.Text;

namespace MCLCS.Core.Toolbox;

/// <summary>日志行级别，用于高亮与过滤。</summary>
public enum LogSeverity { Info, Warn, Error, Debug }

/// <summary>一条日志记录（已分级）。</summary>
public class LogLine
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public LogSeverity Severity { get; set; }
    public bool IsError => Severity == LogSeverity.Error;
}

/// <summary>一个日志/崩溃报告文件的元信息。</summary>
public class LogFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Kind { get; set; } = "log"; // log | crash
}

/// <summary>
/// 日志管理器（工具箱功能 1）：列出、读取、搜索、过滤、高亮、导出游戏日志与崩溃报告。
/// 仅做文件级操作，不产生副作用（导出为复制）。
/// </summary>
public static class LogManager
{
    public static string LogsDir(string gameRoot) => Path.Combine(gameRoot, "logs");
    public static string CrashReportsDir(string gameRoot) => Path.Combine(gameRoot, "crash-reports");

    /// <summary>
    /// 追加一行到启动器自身日志（logs/mclcs_launcher.log），可在工具箱「日志」页查看。
    /// 写入失败静默：日志永远不该阻塞主流程。
    /// </summary>
    public static void Append(string gameRoot, string message)
    {
        try
        {
            var dir = LogsDir(gameRoot);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "mclcs_launcher.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* 忽略 */ }
    }

    /// <summary>列出全部日志与崩溃报告文件（按修改时间倒序）。</summary>
    public static List<LogFileInfo> ListLogs(string gameRoot)
    {
        var list = new List<LogFileInfo>();
        AddDir(list, LogsDir(gameRoot), "log");
        AddDir(list, CrashReportsDir(gameRoot), "crash");
        list.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
        return list;
    }

    private static void AddDir(List<LogFileInfo> list, string dir, string kind)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".log" or ".gz" or ".txt")
                list.Add(new LogFileInfo
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    SizeBytes = new FileInfo(file).Length,
                    LastWriteUtc = File.GetLastWriteTimeUtc(file),
                    Kind = kind
                });
        }
    }

    /// <summary>读取日志文本（自动解压 .gz）。</summary>
    public static string ReadLog(string path)
    {
        if (!File.Exists(path)) return "";
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>把日志文本切分为带级别的行。</summary>
    public static List<LogLine> ParseLines(string text)
    {
        var lines = new List<LogLine>();
        if (string.IsNullOrEmpty(text)) return lines;
        var i = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines.Add(new LogLine { Index = i++, Text = raw, Severity = Classify(raw) });
        }
        return lines;
    }

    private static LogSeverity Classify(string line)
    {
        var u = line.ToUpperInvariant();
        if (u.Contains("FATAL") || u.Contains("ERROR") || u.Contains("EXCEPTION") || u.Contains("CRASH"))
            return LogSeverity.Error;
        if (u.Contains("WARN") || u.Contains("WARNING")) return LogSeverity.Warn;
        if (u.Contains("DEBUG") || u.Contains("[DEBUG]")) return LogSeverity.Debug;
        return LogSeverity.Info;
    }

    /// <summary>
    /// 按关键字（不区分大小写、可空）与级别过滤；onlyErrors 时仅返回错误行。
    /// <paramref name="maxLines"/> &gt; 0 时只保留**最后** maxLines 条（日志最新内容通常在末尾），
    /// 避免把几万行一次性绑给界面导致卡顿。
    /// </summary>
    public static List<LogLine> Filter(
        IEnumerable<LogLine> lines, string? keyword = null, bool onlyErrors = false, int maxLines = 0)
        => Filter(lines, keyword, onlyErrors, maxLines, out _);

    /// <summary>带匹配总数的重载（供界面显示「匹配 N / 共 M 行」）。</summary>
    public static List<LogLine> Filter(
        IEnumerable<LogLine> lines, string? keyword, bool onlyErrors, int maxLines, out int totalMatches)
    {
        var kw = string.IsNullOrWhiteSpace(keyword) ? null : keyword!.ToLowerInvariant();
        var matched = new List<LogLine>();
        foreach (var l in lines)
        {
            if (onlyErrors && l.Severity != LogSeverity.Error) continue;
            if (kw is not null && l.Text.ToLowerInvariant().Contains(kw)) matched.Add(l);
            else if (kw is null) matched.Add(l);
        }
        totalMatches = matched.Count;
        if (maxLines > 0 && matched.Count > maxLines)
            return matched.GetRange(matched.Count - maxLines, maxLines);
        return matched;
    }

    /// <summary>导出（复制）日志到目标路径，返回是否成功。</summary>
    public static bool Export(string sourcePath, string destPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
            File.Copy(sourcePath, destPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
