using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 单个存档的成就统计：从 <c>saves/&lt;存档&gt;/advancements/*.json</c> 统计达成数、总数与紫色挑战数。
/// 用于版本设置中的「成就」展示（按当前版本的工作目录隔离扫描）。
/// </summary>
public class AchievementStats
{
    public string SaveName { get; init; } = "";
    /// <summary>已完成成就数（advancements 中 done=true）。</summary>
    public int Completed { get; init; }
    /// <summary>总成就数（advancements 文件数）。</summary>
    public int Total { get; init; }
    /// <summary>紫色（challenge）成就完成数。</summary>
    public int Purple { get; init; }

    public string Summary => Total == 0
        ? "无成就数据"
        : $"达成 {Completed}/{Total}" + (Purple > 0 ? $"（{Purple} 紫色挑战）" : "");

    /// <summary>紫色挑战数的人类可读文本（供成就展示页直接绑定）。</summary>
    public string PurpleText => $"紫色 {Purple}";
}

/// <summary>
/// 成就扫描：按游戏工作目录（考虑版本隔离）读取各存档的 advancements，产出每存档统计。
/// 纯文件读取，不依赖运行中的游戏进程。
/// </summary>
public static class AchievementScanner
{
    /// <summary>
    /// 扫描 <paramref name="gameDir"/> 下所有存档的成就。
    /// 传入的 <paramref name="gameDir"/> 应通过 <see cref="VersionIsolation.GameDirFor"/>
    /// 得到（隔离版本 → versions/&lt;id&gt;，否则 → 共享根）。
    /// </summary>
    public static IReadOnlyList<AchievementStats> Scan(string gameDir)
    {
        var result = new List<AchievementStats>();
        var savesDir = Path.Combine(gameDir, "saves");
        if (!Directory.Exists(savesDir)) return result;

        foreach (var saveDir in Directory.GetDirectories(savesDir))
        {
            var advDir = Path.Combine(saveDir, "advancements");
            if (!Directory.Exists(advDir)) continue;

            var files = Directory.GetFiles(advDir, "*.json", SearchOption.AllDirectories);
            int completed = 0, purple = 0;
            foreach (var jsonFile in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
                    if (!doc.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True) continue;
                    completed++;
                    if (doc.RootElement.TryGetProperty("display", out var disp)
                        && disp.TryGetProperty("frame", out var frame)
                        && frame.ValueKind == JsonValueKind.String
                        && string.Equals(frame.GetString(), "challenge", StringComparison.OrdinalIgnoreCase))
                    {
                        purple++;
                    }
                }
                catch { /* 忽略单个文件解析错误 */ }
            }

            result.Add(new AchievementStats
            {
                SaveName = Path.GetFileName(saveDir),
                Completed = completed,
                Total = files.Length,
                Purple = purple,
            });
        }

        return result.OrderBy(r => r.SaveName).ToList();
    }
}
