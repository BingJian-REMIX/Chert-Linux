namespace MCLCS.Core.Badges;

/// <summary>
/// 勋章服务（清单 #49 · 接口预留）。
/// 当前只落地「本地数据结构 + 本地持久化」这一层：
/// 目录是静态的、进度存本地 JSON、UserId 字段预留待账号系统接入。
/// 后续接入远端下发时，只需把 <see cref="BadgeProfile.UserId"/> 填上并在
/// <see cref="BadgeUnlocked"/> 处上报即可，无需改动数据结构。
/// </summary>
public static class BadgeService
{
    private static readonly object Sync = new();

    /// <summary>勋章解锁时触发（参数为解锁的勋章定义）。UI 层可订阅做提示。</summary>
    public static event Action<BadgeDefinition>? BadgeUnlocked;

    /// <summary>内置勋章目录（随发版增长；Id 一经发布不可更改）。</summary>
    public static IReadOnlyList<BadgeDefinition> All { get; } = new List<BadgeDefinition>
    {
        new BadgeDefinition
        {
            Id = BadgeIds.LauncherFirstStart,
            Title = "初次点亮",
            Description = "第一次启动燧石启动器",
            Category = BadgeCategory.Launcher,
            Icon = "✦"
        },
        new BadgeDefinition
        {
            Id = BadgeIds.LauncherLaunchCount,
            Title = "老朋友",
            Description = "累计启动启动器 10 次",
            Category = BadgeCategory.Launcher,
            Icon = "🔁",
            Target = 10
        },
        new BadgeDefinition
        {
            Id = BadgeIds.GamePlayCount,
            Title = "世界旅人",
            Description = "累计启动游戏 20 次",
            Category = BadgeCategory.Game,
            Icon = "🎮",
            Target = 20
        },
        new BadgeDefinition
        {
            Id = BadgeIds.GameModInstall,
            Title = "模组爱好者",
            Description = "累计安装 10 个 Mod",
            Category = BadgeCategory.Game,
            Icon = "🧩",
            Target = 10
        },
        new BadgeDefinition
        {
            Id = BadgeIds.SeasonalFirstEvent,
            Title = "节日同好",
            Description = "参与首个节日活动",
            Category = BadgeCategory.Seasonal,
            Icon = "🎉"
        },
        new BadgeDefinition
        {
            Id = BadgeIds.HiddenEasterEgg,
            Title = "？？？",
            Description = "你发现了什么",
            Category = BadgeCategory.Hidden,
            Icon = "🥚"
        },
    };

    public static BadgeDefinition? Find(string id)
        => All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    /// <summary>界面展示用：彩蛋类勋章在未解锁前不列出。</summary>
    public static IReadOnlyList<BadgeDefinition> Visible()
        => All.Where(b => !b.IsHidden || IsUnlocked(b.Id)).ToList();

    /// <summary>本机设备标识（首次调用即生成并持久化）。</summary>
    public static string DeviceId => BadgeStore.DeviceId;

    /// <summary>
    /// 账号系统接入后回填的用户标识；当前恒为空（预留字段）。
    /// 写入后会立即落盘。
    /// </summary>
    public static string? UserId
    {
        get => BadgeStore.Current.UserId;
        set
        {
            lock (Sync)
            {
                BadgeStore.Current.UserId = value;
                BadgeStore.Flush();
            }
        }
    }

    /// <summary>指定勋章是否已解锁（未知 Id 一律视为未解锁）。</summary>
    public static bool IsUnlocked(string id)
    {
        lock (Sync)
        {
            var rec = FindRecord(id, create: false);
            return rec is not null && rec.Unlocked;
        }
    }

    /// <summary>指定勋章的当前进度（未知 Id 返回 0）。</summary>
    public static int ProgressOf(string id)
    {
        lock (Sync)
        {
            return FindRecord(id, create: false)?.Progress ?? 0;
        }
    }

    /// <summary>直接解锁（一次性勋章走这里；重复调用不会产生重复事件）。</summary>
    public static void Unlock(string id)
    {
        BadgeDefinition? def;
        lock (Sync)
        {
            def = Find(id);
            if (def is null) return;
            var rec = FindRecord(id, create: true)!;
            if (rec.Unlocked) return;
            rec.Unlocked = true;
            rec.EarnedAt = DateTime.Now;
            if (def.Target > 0) rec.Progress = Math.Max(rec.Progress, def.Target);
            BadgeStore.Flush();
        }

        BadgeUnlocked?.Invoke(def);
    }

    /// <summary>
    /// 设置进度；达到目标（Target&gt;0）时自动解锁。
    /// 未知 Id、或已解锁的勋章不会重复触发事件。
    /// </summary>
    public static void SetProgress(string id, int value)
    {
        BadgeDefinition? def = null;
        lock (Sync)
        {
            def = Find(id);
            if (def is null) return;
            var rec = FindRecord(id, create: true)!;
            if (rec.Unlocked) return;

            rec.Progress = Math.Clamp(value, 0, def.Target <= 0 ? int.MaxValue : def.Target);
            if (def.Target > 0 && rec.Progress >= def.Target)
            {
                rec.Unlocked = true;
                rec.EarnedAt = DateTime.Now;
            }
            else
            {
                BadgeStore.Flush();
                return;
            }

            BadgeStore.Flush();
        }

        BadgeUnlocked?.Invoke(def);
    }

    /// <summary>进度累加（默认 +1）。</summary>
    public static void Increment(string id, int delta = 1)
    {
        lock (Sync)
        {
            var def = Find(id);
            if (def is null) return;
            var rec = FindRecord(id, create: true)!;
            if (rec.Unlocked) return;
            SetProgress(id, rec.Progress + delta);
        }
    }

    private static BadgeRecord? FindRecord(string id, bool create)
    {
        var profile = BadgeStore.Current;
        var rec = profile.Badges.FirstOrDefault(r => string.Equals(r.BadgeId, id, StringComparison.Ordinal));
        if (rec is null && create)
        {
            rec = new BadgeRecord { BadgeId = id };
            profile.Badges.Add(rec);
        }

        return rec;
    }
}
