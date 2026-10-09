using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCLCS.Linux.App.Themes;

/// <summary>
/// 清单 #31 / #42 / #43 / #44：节日内容中心的数据服务。
/// 把 <see cref="HolidayConfig"/> 里的远程条目换算成界面可直接绑定的卡片，
/// 全程<b>纯函数 + 静默失败</b>：任何解析异常都退化为「不展示该条」。
/// </summary>
public static class SeasonalContentService
{
    /// <summary>按当前节日筛选：条目未指定 season 或 season 与当前节日一致才保留。</summary>
    private static bool MatchesSeason(string? season, string activeKey)
        => string.IsNullOrWhiteSpace(season)
           || string.Equals(season, activeKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把限时活动条目换算成倒计时卡片。已结束的活动排在最后，未开始的按剩余天数升序。
    /// </summary>
    public static List<SeasonalEventCard> BuildEventCards(HolidayConfig config, string activeKey, DateTimeOffset now)
    {
        var cards = new List<SeasonalEventCard>();

        foreach (var e in config.Events ?? new List<SeasonalEventEntry>())
        {
            if (string.IsNullOrWhiteSpace(e.Title)) continue;
            if (!MatchesSeason(e.Season, activeKey)) continue;

            var start = ResolveDate(e.Start, now, endOfDay: false);
            var end = ResolveDate(e.End, now, endOfDay: true);
            if (start is null || end is null) continue;

            var daysToStart = (start.Value.Date - now.Date).Days;
            var daysToEnd = (end.Value.Date - now.Date).Days;

            var started = daysToStart <= 0;
            var ended = daysToEnd < 0;

            var countdown = ended
                ? "已结束"
                : started
                    ? (daysToEnd <= 0 ? "进行中 · 今日截止" : $"进行中 · 剩 {daysToEnd} 天")
                    : $"还有 {daysToStart} 天开始";

            cards.Add(new SeasonalEventCard
            {
                Title = e.Title,
                Note = e.Note ?? "",
                Url = string.IsNullOrWhiteSpace(e.Url) ? null : e.Url,
                DaysUntilStart = daysToStart,
                DaysUntilEnd = daysToEnd,
                Started = started,
                Ended = ended,
                CountdownText = countdown
            });
        }

        return cards
            .OrderBy(c => c.Ended)
            .ThenBy(c => c.Started ? 0 : 1)
            .ThenBy(c => c.DaysUntilStart)
            .ToList();
    }

    /// <summary>取当前节日下的服务器推荐。</summary>
    public static List<SeasonalServerEntry> BuildServers(HolidayConfig config, string activeKey)
        => (config.Servers ?? new List<SeasonalServerEntry>())
           .Where(s => !string.IsNullOrWhiteSpace(s.Name) && MatchesSeason(s.Season, activeKey))
           .ToList();

    /// <summary>取当前节日下的内容推荐。</summary>
    public static List<SeasonalPickEntry> BuildPicks(HolidayConfig config, string activeKey)
        => (config.Recommended ?? new List<SeasonalPickEntry>())
           .Where(p => !string.IsNullOrWhiteSpace(p.Title) && MatchesSeason(p.Season, activeKey))
           .ToList();

    /// <summary>取当前节日下的公告；无公告返回 null。</summary>
    public static SeasonalAnnouncement? BuildAnnouncement(HolidayConfig config, string activeKey)
    {
        var a = config.Announcement;
        if (a is null || a.IsEmpty) return null;
        return MatchesSeason(a.Season, activeKey) ? a : null;
    }

    /// <summary>
    /// 解析日期：支持 <c>MM-dd</c>（不带年份，逐年复用，已过则顺延到明年）与 <c>yyyy-MM-dd</c>。
    /// <paramref name="endOfDay"/> 为真时表示这是区间结束日（含当日）。
    /// </summary>
    private static DateTime? ResolveDate(string value, DateTimeOffset now, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();

        try
        {
            DateTime date;
            var parts = s.Split('-');

            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return null;
                if (month is < 1 or > 12 || day is < 1 or > 31) return null;

                // 先按今年构造；若已过（结束日按 < 今天判定）则顺延到明年
                var thisYear = SafeDate(now.Year, month, day);
                if (thisYear is null) return null;
                date = thisYear.Value;
                if (date.Date < now.Date) date = date.AddYears(1);
            }
            else if (parts.Length == 3)
            {
                if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed)) return null;
                date = parsed;
            }
            else
            {
                return null;
            }

            // 区间结束日含当日，统一以「当天 0 点」参与天数计算
            return date.Date;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? SafeDate(int year, int month, int day)
    {
        try
        {
            var dim = DateTime.DaysInMonth(year, month);
            return new DateTime(year, month, Math.Min(day, dim));
        }
        catch
        {
            return null;
        }
    }
}
