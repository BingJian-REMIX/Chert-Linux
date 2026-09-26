using System.Text.Json;

namespace MCLCS.Core.Statistics;

/// <summary>启动器统计信息（主页统计区 / 设置展示）。</summary>
public class PlayStats
{
    public long TotalPlayMinutes { get; set; }
    public long WeeklyPlayMinutes { get; set; }
    public string? WeekStartIso { get; set; }
    public int LaunchCount { get; set; }
    public int CrashCount { get; set; }
    public string? RecentVersion { get; set; }
    public string? LastPlayedUtc { get; set; }

    /// <summary>首次通过启动器启动游戏的 UTC 时间（用于年度报告周年日入口）。</summary>
    public string? FirstLaunchUtc { get; set; }
}

/// <summary>
/// 游玩统计（主页统计区 / 全局功能）：记录启动次数、累计与本周游玩时长、崩溃次数、
/// 最近游玩版本，持久化到 <c>mclcs_stats.json</c>。
/// </summary>
public static class PlaytimeTracker
{
    private static string Path(string gameRoot) => System.IO.Path.Combine(gameRoot, "mclcs_stats.json");

    public static PlayStats Load(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return new PlayStats();
        try
        {
            var p = Path(gameRoot);
            if (!File.Exists(p)) return new PlayStats();
            return JsonSerializer.Deserialize<PlayStats>(File.ReadAllText(p)) ?? new();
        }
        catch { return new PlayStats(); }
    }

    private static void Save(string gameRoot, PlayStats s)
    {
        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(Path(gameRoot),
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>一次启动开始时调用：记录最近版本、累计启动次数。</summary>
    public static PlayStats RecordLaunch(string gameRoot, string versionId)
    {
        var s = Load(gameRoot);
        s.LaunchCount++;
        if (string.IsNullOrWhiteSpace(s.FirstLaunchUtc))
            s.FirstLaunchUtc = DateTime.UtcNow.ToString("o");
        s.RecentVersion = versionId;
        s.LastPlayedUtc = DateTime.UtcNow.ToString("o");
        EnsureWeek(ref s);
        Save(gameRoot, s);
        return s;
    }

    /// <summary>一次游玩结束时调用：累加时长（分钟）。</summary>
    public static PlayStats RecordPlayMinutes(string gameRoot, long minutes)
    {
        var s = Load(gameRoot);
        EnsureWeek(ref s);
        s.TotalPlayMinutes += minutes;
        s.WeeklyPlayMinutes += minutes;
        Save(gameRoot, s);
        return s;
    }

    /// <summary>记录一次崩溃。</summary>
    public static PlayStats RecordCrash(string gameRoot)
    {
        var s = Load(gameRoot);
        s.CrashCount++;
        Save(gameRoot, s);
        return s;
    }

    private static void EnsureWeek(ref PlayStats s)
    {
        var thisMonday = MondayOf(DateTime.UtcNow);
        var iso = thisMonday.ToString("o");
        if (s.WeekStartIso != iso)
        {
            s.WeekStartIso = iso;
            s.WeeklyPlayMinutes = 0;
        }
    }

    private static DateTime MondayOf(DateTime utc)
    {
        var diff = (int)utc.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        return utc.Date.AddDays(-diff);
    }
}
