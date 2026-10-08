using System.Diagnostics;

namespace MCLCS.Core.MultiInstance;

/// <summary>一个正在运行的游戏实例。</summary>
public class RunningInstance
{
    public int Pid { get; set; }
    public string VersionId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public bool IsAlive { get; set; }
}

/// <summary>
/// 多开实例跟踪（全局功能 11）：记录由本启动器拉起的游戏进程，便于在界面展示
/// 正在运行的实例并支持同时启动多个不同版本。本启动器不持有互斥锁，天然允许多开。
/// </summary>
public static class InstanceTracker
{
    private static readonly Dictionary<int, (string VersionId, DateTime Started)> _instances = new();

    public static void Register(int pid, string versionId)
    {
        lock (_instances)
            _instances[pid] = (versionId, DateTime.UtcNow);
    }

    public static void Unregister(int pid)
    {
        lock (_instances)
            _instances.Remove(pid);
    }

    /// <summary>返回当前仍存活的实例快照（自动剔除已退出进程）。</summary>
    public static List<RunningInstance> ListActive()
    {
        var result = new List<RunningInstance>();
        List<int> stale = new();
        lock (_instances)
        {
            foreach (var kv in _instances)
            {
                var alive = false;
                try { alive = !Process.GetProcessById(kv.Key).HasExited; }
                catch { alive = false; }
                if (alive)
                    result.Add(new RunningInstance
                    {
                        Pid = kv.Key,
                        VersionId = kv.Value.VersionId,
                        StartedUtc = kv.Value.Started,
                        IsAlive = true
                    });
                else
                    stale.Add(kv.Key);
            }
            foreach (var s in stale) _instances.Remove(s);
        }
        return result;
    }

    public static int ActiveCount() => ListActive().Count;

    /// <summary>
    /// 返回存活实例：**内存登记 + 跨进程扫描**的合并结果。
    /// <para>
    /// 只靠 <see cref="ListActive"/> 会漏掉「启动器重启过、但游戏还在跑」的实例 ——
    /// 静态字典随启动器进程一起没了，用户看到的就是「运行 0 个实例」。
    /// 这里补上 <see cref="InstanceScanner"/> 的进程扫描，按 pid 去重（登记过的优先，版本 id 更准）。
    /// </para>
    /// </summary>
    public static List<RunningInstance> ListActiveIncludingExternal(string? gameRoot)
    {
        var result = ListActive();
        var seen = new HashSet<int>(result.Select(r => r.Pid));

        foreach (var r in InstanceScanner.Scan(gameRoot))
            if (seen.Add(r.Pid))
                result.Add(r);

        return result;
    }

    /// <summary>存活实例数（含跨进程扫描）。</summary>
    public static int ActiveCountIncludingExternal(string? gameRoot)
        => ListActiveIncludingExternal(gameRoot).Count;
}
