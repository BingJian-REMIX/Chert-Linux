using System.Collections.Generic;
using System.Diagnostics;

namespace MCLCS.Linux.App;

/// <summary>外部客户端的播放状态。</summary>
public enum ClientPlaybackState
{
    /// <summary>读不到（探测失败 / 首次采样无基线）。★ 不参与「全部暂停」判定。</summary>
    Unknown = 0,

    /// <summary>正在播放（探测器认定有音频活动）。</summary>
    Playing = 1,

    /// <summary>已暂停 / 未在出声。</summary>
    Paused = 2
}

/// <summary>播放状态探测接口（便于测试替换）。</summary>
public interface IClientPlaybackProbe
{
    /// <summary>批量读取若干进程的播放状态。返回 null 或空字典表示「本轮探测失败」。</summary>
    Dictionary<int, ClientPlaybackState>? GetStates(IReadOnlyList<int> pids);
}

/// <summary>
/// 默认探测器：比较进程的 <b>CPU 时间增量</b>。
///
/// <para><b>为什么不用平台媒体会话</b>：Linux 下没有统一的媒体会话接口
/// （MPRIS 需要 D-Bus 且各客户端实现不一），而本类运行在 Core 无关的 App 层，
/// 引入 D-Bus 客户端绑定代价过大。改用零依赖的 CPU 增量启发式。</para>
///
/// <para><b>启发式原理</b>：音乐客户端在「播放」时会持续解码音频，CPU 时间稳步增长；
/// 暂停时几乎不动。两次采样间 CPU 增量超过阈值即判为播放中。</para>
///
/// <para><b>刻意的保守取向</b>：宁可「误判为播放」也不「误判为暂停」——
/// 后者会导致杀掉用户正在听的客户端。首次采样（没有基线）一律返回
/// <see cref="ClientPlaybackState.Unknown"/>，宁可多等一轮也不误杀。</para>
///
/// <para><b>Linux 注意</b>：<c>TotalProcessorTime</c> 由 /proc/&lt;pid&gt;/stat 换算而来，
/// 在 .NET 上可用；但部分容器/受限环境读不到会抛异常，这里一律按 Unknown 处理。</para>
/// </summary>
public sealed class CpuActivityPlaybackProbe : IClientPlaybackProbe
{
    private readonly Dictionary<int, double> _lastCpuMs = new();

    /// <summary>
    /// 判定为「正在播放」的 CPU 时间增量阈值（毫秒 / 采样周期）。
    /// 默认 40ms：一个 1 秒的采样周期里，暂停的客户端通常 &lt; 5ms，
    /// 播放时即便轻量编解码器也远超此值。
    /// </summary>
    private const double PlayingThresholdMs = 40;

    public Dictionary<int, ClientPlaybackState>? GetStates(IReadOnlyList<int> pids)
    {
        if (pids is null || pids.Count == 0) return null;

        var result = new Dictionary<int, ClientPlaybackState>();
        foreach (var pid in pids)
        {
            if (pid <= 0) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;

                var cpuMs = p.TotalProcessorTime.TotalMilliseconds;

                if (!_lastCpuMs.TryGetValue(pid, out var prev))
                {
                    // 首次采样：建立基线，但状态留 Unknown（不猜）
                    _lastCpuMs[pid] = cpuMs;
                    result[pid] = ClientPlaybackState.Unknown;
                    continue;
                }

                var delta = cpuMs - prev;
                _lastCpuMs[pid] = cpuMs;

                result[pid] = delta >= PlayingThresholdMs
                    ? ClientPlaybackState.Playing
                    : ClientPlaybackState.Paused;
            }
            catch
            {
                // 进程读不到（已退出 / 无权限）——不写进结果，
                // 上层按「不在字典里」处理，等价于不参与判定。
            }
        }

        return result.Count > 0 ? result : null;
    }
}
