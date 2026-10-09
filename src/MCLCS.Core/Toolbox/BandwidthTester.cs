using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MCLCS.Core.Toolbox;

/// <summary>本地带宽测速结果。</summary>
public class BandwidthResult
{
    /// <summary>单线程连续下载的平均速率（字节/秒）。</summary>
    public double SingleThreadBytesPerSec { get; init; }

    /// <summary>多线程并发的峰值速率（字节/秒）。</summary>
    public double MultiThreadBytesPerSec { get; init; }

    /// <summary>实际下载字节数。</summary>
    public long BytesDownloaded { get; init; }

    /// <summary>实际耗时（秒）。</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>测速实际使用的源名称。</summary>
    public string SourceName { get; init; } = "";

    /// <summary>是否成功完成（任一模式有结果即算成功）。</summary>
    public bool Ok => SingleThreadBytesPerSec > 0 || MultiThreadBytesPerSec > 0;

    /// <summary>平均速率友好文本。</summary>
    public string SingleText => FormatRate(SingleThreadBytesPerSec);

    /// <summary>峰值带宽友好文本。</summary>
    public string MultiText => FormatRate(MultiThreadBytesPerSec);

    /// <summary>速率格式化：B/s → KB/s → MB/s → GB/s。</summary>
    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "—";
        const double K = 1024.0, M = K * 1024.0, G = M * 1024.0;
        if (bytesPerSec >= G) return (bytesPerSec / G).ToString("0.##") + " GB/s";
        if (bytesPerSec >= M) return (bytesPerSec / M).ToString("0.##") + " MB/s";
        if (bytesPerSec >= K) return (bytesPerSec / K).ToString("0.#") + " KB/s";
        return bytesPerSec.ToString("0") + " B/s";
    }
}

/// <summary>
/// 本地带宽测速（P05）：真实下载 Minecraft 客户端 jar 并计时。
/// <para>两种模式同时跑、分开呈现：</para>
/// <list type="bullet">
///   <item><b>单线程连续下载</b> → 平均速率，贴近「单个文件下载」的真实体验；</item>
///   <item><b>多线程并发下载</b> → 峰值带宽，贴近启动器实际的多线程下载行为。</para>
/// <para>★ 测速源是**动态解析出的真实 client.jar 直链**（约 40MB，走 piston-data）：
/// 版本清单 → 最新正式版元数据 → client.jar 的 url 与大小。
/// 早前版本用 <c>resources.download.minecraft.net/</c>（实测 **404**）或
/// <c>version_manifest_v2.json</c>（仅几 KB，秒传完算出的速率接近 0），故均已废弃。</para>
/// </summary>
public static class BandwidthTester
{
    // ★ 本类**不产生任何临时文件**：测速用 HttpCompletionOption.ResponseHeadersRead 直接读
    //   网络流到内存缓冲区（byte[]），用完即弃；响应体与流由 using / await using 保证释放。
    //   因此不存在「测速残留文件需清理」的问题 —— 也刻意避开了「先落盘再删」的做法
    //   （中途崩溃/被杀会留下垃圾文件，且大文件白占磁盘）。

    /// <summary>版本清单地址（用于解析出真实 jar 直链）。</summary>
    private const string VersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>单线程模式下载上限（字节）。</summary>
    public const long SingleThreadMaxBytes = 32L * 1024 * 1024;

    /// <summary>多线程模式每线程下载上限（字节）。</summary>
    public const long PerThreadMaxBytes = 8L * 1024 * 1024;

    /// <summary>解析真实可下载的测速目标。</summary>
    /// <returns>源名称、jar 直链、jar 大小（字节，失败为 0）。</returns>
    public static async Task<(string Name, string Url, long Size)> ResolveTargetAsync(
        HttpClient client, CancellationToken ct = default)
    {
        var manifest = await client.GetStringAsync(VersionManifestUrl, ct).ConfigureAwait(false);
        using var mfDoc = JsonDocument.Parse(manifest);
        var mfRoot = mfDoc.RootElement;
        var versionId = mfRoot.GetProperty("latest").GetProperty("release").GetString() ?? "";

        string metaUrl = "";
        foreach (var v in mfRoot.GetProperty("versions").EnumerateArray())
        {
            if (v.TryGetProperty("id", out var id) && id.GetString() == versionId)
            {
                metaUrl = v.GetProperty("url").GetString() ?? "";
                break;
            }
        }
        if (string.IsNullOrEmpty(metaUrl))
            throw new InvalidOperationException("无法从版本清单解析出最新版本地址。");

        var meta = await client.GetStringAsync(metaUrl, ct).ConfigureAwait(false);
        using var metaDoc = JsonDocument.Parse(meta);
        var clientJar = metaDoc.RootElement.GetProperty("downloads").GetProperty("client");
        var jarUrl = clientJar.GetProperty("url").GetString() ?? "";
        var jarSize = clientJar.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0L;
        if (string.IsNullOrEmpty(jarUrl))
            throw new InvalidOperationException("无法从版本元数据解析出 client.jar 地址。");

        return ($"Mojang client.jar {versionId}", jarUrl, jarSize);
    }

    /// <summary>执行测速。</summary>
    /// <param name="seconds">测速时长上限（秒）。用户已定：5 / 10 / 15 / 30，默认 10。</param>
    /// <param name="multiThreadCount">多线程并发数。</param>
    public static async Task<BandwidthResult> RunAsync(
        int seconds = 10, int multiThreadCount = 4, CancellationToken ct = default)
    {
        if (seconds <= 0) seconds = 10;
        if (multiThreadCount <= 0) multiThreadCount = 4;

        using var client = CreateClient();
        var (name, url, size) = await ResolveTargetAsync(client, ct).ConfigureAwait(false);

        var single = await MeasureAsync(client, url, seconds, 1, SingleThreadMaxBytes, ct)
            .ConfigureAwait(false);
        var multi = await MeasureAsync(client, url, seconds, multiThreadCount, PerThreadMaxBytes, ct)
            .ConfigureAwait(false);

        return new BandwidthResult
        {
            SourceName = name,
            SingleThreadBytesPerSec = single.bytesPerSec,
            MultiThreadBytesPerSec = multi.bytesPerSec,
            BytesDownloaded = single.bytes + multi.bytes,
            ElapsedSeconds = Math.Max(single.elapsed, multi.elapsed)
        };
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// 核心下载计时。速率用**实际耗时**而非配置的 seconds 计算 ——
    /// 否则小文件秒传完会被算成接近 0 的速率。
    /// </summary>
    private static async Task<(long bytes, double elapsed, double bytesPerSec)> MeasureAsync(
        HttpClient client, string url, int seconds, int concurrency, long maxBytesPerTask, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var buffer = new byte[64 * 1024];
        var tasks = new Task<(long Bytes, double Elapsed)>[concurrency];
        for (var i = 0; i < concurrency; i++)
            tasks[i] = DownloadOneAsync();

        long total = 0;
        var maxElapsed = 0.001;
        try
        {
            var all = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var t in all)
            {
                total += t.Bytes;
                if (t.Elapsed > maxElapsed) maxElapsed = t.Elapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // 到时取消是正常路径：把已产出的部分计入
            foreach (var t in tasks)
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    total += t.Result.Bytes;
                    if (t.Result.Elapsed > maxElapsed) maxElapsed = t.Result.Elapsed;
                }
            }
        }
        catch (HttpRequestException)
        {
            total = 0;
        }

        return (total, maxElapsed, total / maxElapsed);

        async Task<(long, double)> DownloadOneAsync()
        {
            long got = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                while (got < maxBytesPerTask)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token)
                        .ConfigureAwait(false);
                    if (n <= 0) break;      // 服务器没数据了
                    got += n;
                }
            }
            catch (OperationCanceledException) { /* 正常超时 */ }
            catch (HttpRequestException) { /* 网络问题，忽略该任务 */ }
            catch (IOException) { /* 连接中断 */ }
            finally
            {
                sw.Stop();
            }
            return (got, sw.Elapsed.TotalSeconds);
        }
    }
}
