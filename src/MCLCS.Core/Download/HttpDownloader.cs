using MCLCS.Core.Utils;

namespace MCLCS.Core.Download;

/// <summary>
/// 基于 HttpClient 的并行下载器：镜像回退、SHA-1/大小校验、进度回调、断点续传友好。
/// </summary>
public class HttpDownloader : IDownloader
{
    private readonly HttpClient _client;
    private readonly ILogger? _logger;
    private int _maxConcurrency;

    public HttpDownloader(HttpClient client, int maxConcurrency = 8, ILogger? logger = null)
    {
        _client = client;
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _logger = logger;
    }

    /// <summary>
    /// 并发上限。可运行时调整，用于跟随设置里的「最大并发下载数」
    /// （此前只能在构造时定死，设置项改了也不生效）。
    /// </summary>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => _maxConcurrency = Math.Max(1, value);
    }

    public async Task DownloadAsync(DownloadItem item, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (item.Urls.Count == 0)
            throw new ArgumentException("DownloadItem 必须包含至少一个 URL", nameof(item));

        // 已存在且校验通过则跳过
        if (File.Exists(item.Destination)
            && HashUtil.VerifySize(item.Destination, item.ExpectedSize)
            && HashUtil.VerifySha1(item.Destination, item.ExpectedSha1))
        {
            progress?.Report(1.0);
            return;
        }

        var dir = Path.GetDirectoryName(item.Destination);
        if (dir is not null) Directory.CreateDirectory(dir);
        var tmp = item.Destination + ".part";

        // 流式写入 .part 并支持断点续传：中途取消 / 断网后重下可接着上次的进度
        await MirrorPolicy.DownloadToFileWithFallback(item.Urls, _client, tmp, item.ExpectedSize ?? 0, progress, ct);

        // 校验
        if (!HashUtil.VerifySize(tmp, item.ExpectedSize) || !HashUtil.VerifySha1(tmp, item.ExpectedSha1))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"校验失败：{item.Destination}");
        }

        if (File.Exists(item.Destination)) File.Delete(item.Destination);
        File.Move(tmp, item.Destination);
    }

    public async Task DownloadBatchAsync(IEnumerable<DownloadItem> items,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        var total = list.Count;
        var done = 0;
        progress?.Report((0, total));

        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        var tasks = list.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await DownloadAsync(item, null, ct);
            }
            catch (Exception ex)
            {
                _logger?.Log($"下载失败 {item.Destination}: {ex.Message}");
                throw;
            }
            finally
            {
                semaphore.Release();
                var d = Interlocked.Increment(ref done);
                progress?.Report((d, total));
            }
        });

        await Task.WhenAll(tasks);
    }
}

/// <summary>轻量日志接口（UI 层可注入以显示进度）。</summary>
public interface ILogger
{
    void Log(string message);
}
