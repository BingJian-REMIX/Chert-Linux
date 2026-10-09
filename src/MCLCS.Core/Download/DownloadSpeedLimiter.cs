using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCLCS.Core.Download;

/// <summary>
/// 全局下载限速（清单 #67）：按 1 秒窗口做令牌桶限流。
/// 下载循环每读完一批字节调用 <see cref="ThrottleAsync"/>，超出配额时睡眠补齐，
/// 配额为 0 表示不限速。所有 <see cref="MirrorPolicy"/> 走的网络读取共享同一配额。
/// </summary>
public static class DownloadSpeedLimiter
{
    private static readonly object Sync = new();
    private static long _bytesPerSecond;
    private static long _windowBytes;
    private static long _windowStartTicks = Environment.TickCount64;

    /// <summary>每秒字节数上限；0 表示不限速。</summary>
    public static long BytesPerSecond
    {
        get { lock (Sync) return _bytesPerSecond; }
        set { lock (Sync) _bytesPerSecond = Math.Max(0, value); }
    }

    /// <summary>按 KB/s 设置限速（0 表示不限速）。</summary>
    public static void SetKilobytesPerSecond(long kbps)
        => BytesPerSecond = kbps <= 0 ? 0 : kbps * 1024;

    /// <summary>当前限速（KB/s，0 表示不限速）。</summary>
    public static long KilobytesPerSecond
    {
        get
        {
            var v = BytesPerSecond;
            return v <= 0 ? 0 : v / 1024;
        }
    }

    /// <summary>
    /// 记账并按需等待。bytes 为本次读取到的字节数；未超限立即返回。
    /// 取消时静默返回（不抛异常，避免打断下载流程之外的处理）。
    /// </summary>
    public static async Task ThrottleAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0) return;

        long waitMs;
        lock (Sync)
        {
            if (_bytesPerSecond <= 0) return;

            var now = Environment.TickCount64;
            if (now - _windowStartTicks >= 1000)
            {
                _windowStartTicks = now;
                _windowBytes = 0;
            }

            _windowBytes += bytes;
            if (_windowBytes <= _bytesPerSecond) return;

            var over = _windowBytes - _bytesPerSecond;
            waitMs = (long)Math.Ceiling(over * 1000.0 / _bytesPerSecond);
            waitMs = Math.Clamp(waitMs, 5, 1000);

            // 等待期间不再重复累计：把窗口用量压回上限，等窗口滚动后继续
            _windowBytes = _bytesPerSecond;
        }

        try { await Task.Delay((int)waitMs, ct); }
        catch (OperationCanceledException) { /* 取消即放弃等待 */ }
        catch { /* 其他异常不阻断下载 */ }
    }
}
