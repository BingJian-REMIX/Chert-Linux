using System.Diagnostics;

namespace MCLCS.Core.MultiInstance;

/// <summary>
/// 跨进程发现正在运行的游戏实例（补 <see cref="InstanceTracker"/> 的盲区）。
///
/// <para><b>为什么需要</b>：<see cref="InstanceTracker"/> 是**进程内静态字典**，只能记录
/// 「本次启动器进程亲自拉起」的游戏。以下场景会漏统计，而用户看到的就是「运行 0 个实例」：
/// ① 启动器重启过（游戏仍在后台跑，字典已随进程清空）；
/// ② 游戏由外部 / 旧版本启动器拉起；
/// ③ 启动器崩溃重启后接手。</para>
///
/// <para><b>判定依据</b>：进程映像为 <c>java</c>，且其**命令行包含本启动器当前游戏目录**。
/// 后者是关键 —— 同机可能跑着别的启动器的 MC，只看进程名会误统计。</para>
///
/// <para><b>平台差异</b>：WPF 侧靠 <c>NtQueryInformationProcess</c> 读 PEB 取命令行；
/// Linux 直接读 <c>/proc/&lt;pid&gt;/cmdline</c>（参数以 '\0' 分隔）。</para>
/// </summary>
public static class InstanceScanner
{
    private static readonly string[] JavaImageNames = { "java", "javaw" };

    /// <summary>
    /// 扫描当前由本启动器游戏目录启动、且仍存活的游戏进程。
    /// <paramref name="gameRoot"/> 为当前生效的游戏目录（不区分大小写匹配）。
    /// </summary>
    public static List<RunningInstance> Scan(string? gameRoot)
    {
        var result = new List<RunningInstance>();
        if (string.IsNullOrWhiteSpace(gameRoot)) return result;

        var root = gameRoot!.Trim().TrimEnd('\\', '/');
        if (root.Length == 0) return result;
        if (!Directory.Exists("/proc")) return result;   // 非 Linux（如被移植到别的平台）时静默退回

        try
        {
            foreach (var p in EnumerateJavaProcesses())
            {
                var cmd = ReadCommandLine(p.Id);
                if (cmd is null) continue;

                // 命令行必须明确指向本启动器的游戏目录，避免统计到别的启动器的 MC
                if (!MentionsGameRoot(cmd, root)) continue;

                result.Add(new RunningInstance
                {
                    Pid = p.Id,
                    VersionId = ExtractVersionId(cmd),
                    StartedUtc = SafeGetStartTime(p.Id),
                    IsAlive = true
                });
            }
        }
        catch
        {
            // /proc 不可读（容器限制 / 权限不足）时静默返回空列表：
            // 界面显示 0 个实例，但不影响启动、性能采样等其它功能。
        }

        return result;
    }

    private static List<Process> EnumerateJavaProcesses()
    {
        var list = new List<Process>();
        foreach (var name in JavaImageNames)
        {
            try
            {
                list.AddRange(Process.GetProcessesByName(name));
            }
            catch
            {
                // 枚举某个名字失败不影响另一个
            }
        }
        return list;
    }

    /// <summary>读取进程命令行（Linux：<c>/proc/&lt;pid&gt;/cmdline</c>，参数以 '\0' 分隔）。</summary>
    private static string? ReadCommandLine(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/cmdline";
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path);
            if (raw.Length == 0) return null;
            return raw.Replace('\0', ' ').Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>命令行里是否提到了该游戏目录（不区分大小写，含路径分隔符容错）。</summary>
    private static bool MentionsGameRoot(string cmd, string root)
    {
        if (cmd.Contains(root, StringComparison.OrdinalIgnoreCase)) return true;

        // 命令行里常见的是不带引号且分隔符可能不同（\ 与 /），做一次宽松匹配：
        // 取目录名的最后一段，判断是否作为独立片段出现。
        var leaf = root.Split('\\', '/').LastOrDefault();
        if (string.IsNullOrEmpty(leaf)) return false;
        return cmd.Contains(leaf, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从命令行里抠出版本 id（取 <c>--version</c> 后的值，或 jar 路径的父目录名）。</summary>
    private static string ExtractVersionId(string cmd)
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("--version", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1].Trim('"');
        }

        // 退而求其次：找形如 .../versions/<id>/<id>.jar 的路径
        foreach (var p in parts)
        {
            var s = p.Trim('"');
            if (!s.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(s);
            if (string.IsNullOrEmpty(dir)) continue;
            var leaf = new DirectoryInfo(dir).Name;
            // 命中 versions/<id> 或隔离目录里的 <id> 目录
            if (leaf.Length > 0 && !leaf.Equals("versions", StringComparison.OrdinalIgnoreCase))
                return leaf;
        }
        return "未知版本";
    }

    private static DateTime SafeGetStartTime(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }
}
