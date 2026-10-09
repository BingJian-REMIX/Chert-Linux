using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MCLCS.Core.Models;

namespace MCLCS.Core.Download;

/// <summary>CurseForge API v1 常量。</summary>
public static class CurseForgeApi
{
    /// <summary>默认 API Root（可被镜像覆盖）。</summary>
    public const string DefaultApiRoot = "https://api.curseforge.com";

    /// <summary>Minecraft 在 CurseForge 的固定 gameId。</summary>
    public const int GameIdMinecraft = 432;

    // ---- classId ----
    public const int ClassMods = 6;
    public const int ClassModpacks = 4471;
    public const int ClassResourcePacks = 12;
    public const int ClassWorlds = 17;
    public const int ClassShaders = 6552;

    // ---- sortField ----
    public const int SortFeatured = 1;
    public const int SortPopularity = 2;
    public const int SortLastUpdated = 3;
    public const int SortTotalDownloads = 6;

    /// <summary>官方站内页面地址（按 classId 拼路径）。</summary>
    public static string WebPageUrl(int classId, string slug, int modId)
    {
        var seg = classId switch
        {
            ClassModpacks => "modpacks",
            ClassResourcePacks => "texture-packs",
            ClassWorlds => "worlds",
            _ => "mc-mods"
        };
        var key = string.IsNullOrWhiteSpace(slug) ? modId.ToString() : slug;
        return $"https://www.curseforge.com/minecraft/{seg}/{key}";
    }
}

/// <summary>CurseForge 的模组加载器枚举（值即为 API 的 modLoaderType 参数）。</summary>
public enum CurseForgeLoaderType
{
    Any = 0,
    Forge = 1,
    Cauldron = 2,
    LiteLoader = 3,
    Fabric = 4,
    Quilt = 5,
    NeoForge = 6
}

/// <summary>
/// CurseForge 运行期配置：解析「三层 Key 策略」与 API Root。
/// <para>Key 优先级：<b>启动参数 &gt; 用户设置 &gt; 内置（构建时注入）</b>。</para>
/// <para>
/// 内置 Key 不写在源码里，而是构建时由仓库根的 <c>Directory.Build.user.props</c>（已 gitignore）
/// 经 <c>AssemblyMetadata</c> 烧进程序集 —— 发布包开箱即用，源码库中不含 Key。
/// </para>
/// </summary>
public static class CurseForgeConfig
{
    private static string? _builtIn;
    private static bool _builtInLoaded;

    /// <summary>启动参数覆盖（CLI <c>--curseforge-key=</c>）。</summary>
    public static string? LaunchArgumentOverride { get; set; }

    /// <summary>用户在「设置 → 下载」里填的 Key。</summary>
    public static string? UserApiKey { get; set; }

    /// <summary>API Root（可切换第三方镜像）；为空表示用官方默认。</summary>
    public static string ApiRoot { get; set; } = "";

    /// <summary>总开关（关闭后 CurseForge 源不可用，但其它源不受影响）。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>构建时注入的内置 Key（读程序集元数据）。读不到返回空串。</summary>
    public static string BuiltInApiKey
    {
        get
        {
            if (_builtInLoaded) return _builtIn ?? "";
            _builtInLoaded = true;
            try
            {
                _builtIn = typeof(CurseForgeConfig).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, "CurseForgeApiKey", StringComparison.Ordinal))?
                    .Value ?? "";
            }
            catch
            {
                _builtIn = "";
            }
            return _builtIn;
        }
    }

    /// <summary>当前生效的 Key（三层优先级）。</summary>
    public static string ResolvedApiKey =>
        !string.IsNullOrWhiteSpace(LaunchArgumentOverride) ? LaunchArgumentOverride!.Trim()
        : !string.IsNullOrWhiteSpace(UserApiKey) ? UserApiKey!.Trim()
        : BuiltInApiKey.Trim();

    /// <summary>当前生效的 API Root（去掉尾部斜杠）。</summary>
    public static string ResolvedApiRoot =>
        string.IsNullOrWhiteSpace(ApiRoot)
            ? CurseForgeApi.DefaultApiRoot
            : ApiRoot.Trim().TrimEnd('/');

    /// <summary>是否已配置可用 Key。</summary>
    public static bool HasKey => !string.IsNullOrWhiteSpace(ResolvedApiKey);

    /// <summary>Key 的来源说明（设置页展示用）。</summary>
    public static string KeySourceText =>
        !string.IsNullOrWhiteSpace(LaunchArgumentOverride) ? "启动参数"
        : !string.IsNullOrWhiteSpace(UserApiKey) ? "用户设置"
        : !string.IsNullOrWhiteSpace(BuiltInApiKey) ? "内置"
        : "未配置";

    /// <summary>从配置对象同步（App 层读 profile 后调用）。</summary>
    public static void Apply(Profiles.CurseForgeSettings? s)
    {
        UserApiKey = s?.ApiKey;
        ApiRoot = s?.ApiRoot ?? "";
        Enabled = s?.Enabled ?? true;
    }
}

/// <summary>
/// CurseForge 限流闸门。
/// <para>
/// 官方在 Key 无效 <b>或</b> 配额超限时都返回 <b>403</b>（而非 429），文案为
/// "Access to https://api.curseforge.com is forbidden or rate-limit has been exceeded"。
/// 命中后按 <c>Retry-After</c>（缺省 60 秒）全局暂停 CurseForge 请求，避免把配额打得更烂。
/// </para>
/// </summary>
public static class CurseForgeThrottle
{
    private static readonly object Gate = new();
    private static DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>当前是否处于限流暂停中。</summary>
    public static bool IsPaused
    {
        get { lock (Gate) return DateTimeOffset.UtcNow < _pausedUntil; }
    }

    /// <summary>距解禁还剩多久（未暂停为 Zero）。</summary>
    public static TimeSpan Remaining
    {
        get
        {
            lock (Gate)
            {
                var left = _pausedUntil - DateTimeOffset.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>暂停到「当前时间 + 时长」（只延长不缩短）。</summary>
    public static void Pause(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(60);
        lock (Gate)
        {
            var until = DateTimeOffset.UtcNow + duration;
            if (until > _pausedUntil) _pausedUntil = until;
        }
    }

    /// <summary>手动解禁（用户在设置里重试时调用）。</summary>
    public static void Resume()
    {
        lock (Gate) _pausedUntil = DateTimeOffset.MinValue;
    }
}

/// <summary>
/// CurseForge 文件指纹（社区标准实现：<b>修改版 MurmurHash2，种子固定为 1</b>）。
/// <para>
/// 规则：哈希前<b>剥离全部空白字节</b>（<c>\t</c> <c>\n</c> <c>\r</c> 与空格），
/// 对剩余字节做 MurmurHash2（m = 0x5bd1e995，r = 24），初值 <c>h = 1 ^ 归一化后长度</c>。
/// </para>
/// <para>
/// 注意：尾部不足 4 字节时必须走<b>标准 MurmurHash2 尾处理</b>（按位移拼 k、<c>h ^= k</c> 后再 <c>h *= m</c>）。
/// 常见错误写法是把尾巴直接写成 <c>h = (h * m) ^ k</c>，对长度非 4 倍数（占绝大多数 jar）会算出错误指纹，
/// 表现为「指纹匹配永远返回空」。
/// </para>
/// </summary>
public static class CurseForgeFingerprint
{
    private const uint M = 0x5bd1e995;   // 1540483477
    private const int R = 24;

    /// <summary>判断是否为需要剥离的空白字节。</summary>
    public static bool IsWhitespace(byte b) => b is (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)' ';

    /// <summary>计算字节数组的 CurseForge 指纹。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        // 1. 归一化：剥离空白，只统计并保留非空白字节
        var length = 0;
        foreach (var b in data)
        {
            if (!IsWhitespace(b)) length++;
        }

        var normalized = new byte[length];
        var w = 0;
        foreach (var b in data)
        {
            if (!IsWhitespace(b)) normalized[w++] = b;
        }

        // 2. 初值：种子 1 与长度异或
        var h = 1u ^ (uint)length;

        // 3. 每 4 字节小端组成 k，做 MurmurHash2 主体
        var blocks = length >> 2;
        for (var i = 0; i < blocks; i++)
        {
            var o = i << 2;
            var k = (uint)(normalized[o]
                           | (normalized[o + 1] << 8)
                           | (normalized[o + 2] << 16)
                           | (normalized[o + 3] << 24));
            k *= M;
            k ^= k >> R;
            k *= M;

            h *= M;
            h ^= k;
        }

        // 4. 尾部不足 4 字节（标准 MurmurHash2 尾处理，不可省）
        var tail = length & 3;
        if (tail > 0)
        {
            var b0 = length & ~3;
            if (tail == 3)
            {
                h ^= (uint)(normalized[b0 + 2] << 16);
                h ^= (uint)(normalized[b0 + 1] << 8);
                h ^= normalized[b0];
            }
            else if (tail == 2)
            {
                h ^= (uint)(normalized[b0 + 1] << 8);
                h ^= normalized[b0];
            }
            else
            {
                h ^= normalized[b0];
            }
            h *= M;
        }

        // 5. 最终混合
        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;

        return h;
    }

    /// <summary>按路径计算指纹；文件不存在或读取失败返回 null。</summary>
    public static async Task<uint?> ComputeFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return null;

            // 超大文件跳过（指纹整文件读入内存，超过 256MB 不处理）
            var info = new FileInfo(path);
            if (info.Length > 256L * 1024 * 1024) return null;

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return Compute(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>批量计算一组文件的指纹（跳过读取失败的）。</summary>
    public static async Task<List<(string Path, uint Fingerprint)>> ComputeFilesAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new List<(string, uint)>();
        foreach (var p in paths)
        {
            var fp = await ComputeFileAsync(p, ct).ConfigureAwait(false);
            if (fp.HasValue) result.Add((p, fp.Value));
        }
        return result;
    }
}

/// <summary>
/// 给所有发往 CurseForge CDN 的请求自动附加 <c>x-api-key</c>。
/// <para>
/// 2026-07-16 起官方新规：<c>edge.forgecdn.net</c> 等 CDN 直链必须携带有效 API Key，否则 401。
/// 由于下载链路（<see cref="HttpDownloader"/> / <see cref="MirrorPolicy"/>）自身不感知业务来源，
/// 用消息处理器统一注入最省事，也不会影响 BMCLAPI / Modrinth 等其它域名。
/// </para>
/// </summary>
public sealed class CurseForgeAuthHandler : DelegatingHandler
{
    /// <summary>需要附加 Key 的 CDN 主机后缀。</summary>
    private static readonly string[] CdnHosts =
    {
        "forgecdn.net",          // edge.forgecdn.net / mediafilez.forgecdn.net
        "curseforge.com",
        "curseforge.gg"
    };

    public CurseForgeAuthHandler() { }

    public CurseForgeAuthHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var host = request.RequestUri?.Host ?? "";
            var needKey = host.EndsWith("edge.forgecdn.net", StringComparison.OrdinalIgnoreCase)
                          || CdnHosts.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));

            if (needKey
                && !request.Headers.Contains("x-api-key")
                && CurseForgeConfig.HasKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", CurseForgeConfig.ResolvedApiKey);
            }
        }
        catch
        {
            // 注入失败不影响请求本身
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// CurseForge API v1 客户端：搜索 / 详情 / 文件列表 / 指纹匹配 / 下载直链解析。
/// <para>
/// 所有方法<b>失败均返回 null / 空集合而不抛异常</b>，失败原因写入 <see cref="LastError"/> 供界面展示，
/// 保证 CurseForge 不可用（无 Key / 限流 / 网络故障）时不会拖垮其它下载源。
/// </para>
/// </summary>
public class CurseForgeClient
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public CurseForgeClient(HttpClient client) => _client = client;

    /// <summary>最近一次失败原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前是否可用（已配置 Key、总开关开启、且未处于限流暂停）。</summary>
    public bool IsAvailable => CurseForgeConfig.Enabled && CurseForgeConfig.HasKey && !CurseForgeThrottle.IsPaused;

    /// <summary>不可用原因（界面提示用）；可用时为 null。</summary>
    public string? UnavailableReason
    {
        get
        {
            if (!CurseForgeConfig.Enabled) return "CurseForge 已在设置中关闭";
            if (!CurseForgeConfig.HasKey) return "未配置 CurseForge API Key（设置 → 下载）";
            if (CurseForgeThrottle.IsPaused)
                return $"CurseForge 触发限流，暂停中（剩 {CurseForgeThrottle.Remaining:mm\\:ss}）";
            return null;
        }
    }

    // ================= 公开接口 =================

    /// <summary>测试连接：拿一条搜索结果验证 Key 与网络是否正常。</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        CurseForgeThrottle.Resume();
        var resp = await GetAsync<CurseForgeListResponse<CurseForgeMod>>(
            $"/v1/mods/search?gameId={CurseForgeApi.GameIdMinecraft}&pageSize=1", ct).ConfigureAwait(false);
        return resp is not null;
    }

    /// <summary>搜索项目（classId 区分 Mod / 整合包 / 资源包 / 地图）。失败返回空列表。</summary>
    public async Task<List<CurseForgeMod>> SearchAsync(
        string? keyword,
        int classId = CurseForgeApi.ClassMods,
        string? gameVersion = null,
        CurseForgeLoaderType loader = CurseForgeLoaderType.Any,
        int sortField = CurseForgeApi.SortPopularity,
        int limit = 24,
        int offset = 0,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder("/v1/mods/search?");
        sb.Append("gameId=").Append(CurseForgeApi.GameIdMinecraft);
        sb.Append("&classId=").Append(classId);
        sb.Append("&sortField=").Append(sortField);
        sb.Append("&sortOrder=desc");
        sb.Append("&pageSize=").Append(Math.Clamp(limit, 1, 50));
        sb.Append("&index=").Append(Math.Max(0, offset));
        if (!string.IsNullOrWhiteSpace(keyword))
            sb.Append("&searchFilter=").Append(Uri.EscapeDataString(keyword.Trim()));
        if (!string.IsNullOrWhiteSpace(gameVersion))
            sb.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion.Trim()));
        if (loader != CurseForgeLoaderType.Any)
            sb.Append("&modLoaderType=").Append((int)loader);

        var resp = await GetAsync<CurseForgeListResponse<CurseForgeMod>>(sb.ToString(), ct).ConfigureAwait(false);
        return resp?.Data ?? new List<CurseForgeMod>();
    }

    /// <summary>取项目详情（含 classes / allowModDistribution）。失败返回 null。</summary>
    public async Task<CurseForgeMod?> GetModAsync(int modId, CancellationToken ct = default)
    {
        var resp = await GetAsync<CurseForgeSingleResponse<CurseForgeMod>>($"/v1/mods/{modId}", ct).ConfigureAwait(false);
        return resp?.Data;
    }

    /// <summary>取项目的文件列表（可按 MC 版本 / 加载器过滤）。失败返回空列表。</summary>
    public async Task<List<CurseForgeFile>> GetFilesAsync(
        int modId,
        string? gameVersion = null,
        CurseForgeLoaderType loader = CurseForgeLoaderType.Any,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder($"/v1/mods/{modId}/files?");
        sb.Append("pageSize=").Append(Math.Clamp(limit, 1, 50));
        sb.Append("&index=").Append(Math.Max(0, offset));
        if (!string.IsNullOrWhiteSpace(gameVersion))
            sb.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion.Trim()));
        if (loader != CurseForgeLoaderType.Any)
            sb.Append("&modLoaderType=").Append((int)loader);

        var resp = await GetAsync<CurseForgeListResponse<CurseForgeFile>>(sb.ToString(), ct).ConfigureAwait(false);
        return resp?.Data ?? new List<CurseForgeFile>();
    }

    /// <summary>取单个文件。失败返回 null。</summary>
    public async Task<CurseForgeFile?> GetFileAsync(int modId, int fileId, CancellationToken ct = default)
    {
        var resp = await GetAsync<CurseForgeSingleResponse<CurseForgeFile>>(
            $"/v1/mods/{modId}/files/{fileId}", ct).ConfigureAwait(false);
        return resp?.Data;
    }

    /// <summary>
    /// 按文件 Id 批量取文件元数据（<c>POST /v1/mods/files</c>，单次上限 1000）。
    /// <para>
    /// 整合包安装时动辄一两百个文件，逐个查会瞬间打满配额，必须走批量接口。
    /// </para>
    /// </summary>
    public async Task<List<CurseForgeFile>> GetFilesByIdsAsync(
        IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        var result = new List<CurseForgeFile>();
        if (fileIds.Count == 0) return result;

        foreach (var chunk in fileIds.Distinct().Chunk(1000))
        {
            var body = JsonSerializer.Serialize(new { fileIds = chunk });
            var resp = await PostAsync<CurseForgeListResponse<CurseForgeFile>>("/v1/mods/files", body, ct)
                .ConfigureAwait(false);
            if (resp?.Data is { Count: > 0 }) result.AddRange(resp.Data);
        }
        return result;
    }

    /// <summary>
    /// 按项目 Id 批量取项目元数据（<c>POST /v1/mods</c>，单次上限 1000）。
    /// 整合包安装时用它一次拿到全部 classId，从而决定文件该落到 mods / resourcepacks / saves。
    /// </summary>
    public async Task<List<CurseForgeMod>> GetModsByIdsAsync(
        IReadOnlyCollection<int> modIds, CancellationToken ct = default)
    {
        var result = new List<CurseForgeMod>();
        if (modIds.Count == 0) return result;

        foreach (var chunk in modIds.Distinct().Chunk(1000))
        {
            var body = JsonSerializer.Serialize(new { modIds = chunk });
            var resp = await PostAsync<CurseForgeListResponse<CurseForgeMod>>("/v1/mods", body, ct)
                .ConfigureAwait(false);
            if (resp?.Data is { Count: > 0 }) result.AddRange(resp.Data);
        }
        return result;
    }

    /// <summary>
    /// 解析文件的可下载直链。
    /// <para>
    /// 优先用文件自带的 <c>downloadUrl</c>；为空（作者关闭第三方分发或 CDN 变更）时走
    /// <c>/v1/mods/{modId}/files/{fileId}/download-url</c> 让官方现算一条。两者都拿不到返回 null。
    /// </para>
    /// </summary>
    public async Task<string?> ResolveDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default)
    {
        var file = await GetFileAsync(modId, fileId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(file?.DownloadUrl)) return file!.DownloadUrl;

        var resp = await GetAsync<CurseForgeSingleResponse<string>>(
            $"/v1/mods/{modId}/files/{fileId}/download-url", ct).ConfigureAwait(false);
        var url = resp?.Data;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>
    /// 批量指纹匹配：把本地文件指纹发给官方，回查它们属于哪个项目 / 文件。
    /// 单次最多 1000 条（官方上限）。失败返回空字典。
    /// <para>
    /// ⚠️ 接口路径是 <c>POST /v1/fingerprints</c>（<b>不是</b> <c>/v1/mods/fingerprint</c>，后者返回 404 —— 实测确认）。
    /// 另外官方指纹缓存首次调用可能返回 <c>isCacheBuilt=false</c> 且不给结果，此处会自动重试一次。
    /// </para>
    /// </summary>
    public async Task<Dictionary<uint, CurseForgeFingerprintMatch>> MatchFingerprintsAsync(
        IReadOnlyCollection<uint> fingerprints, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, CurseForgeFingerprintMatch>();
        if (fingerprints.Count == 0) return result;

        var batch = fingerprints.Distinct().Take(1000).Select(f => (long)f).ToList();
        var body = JsonSerializer.Serialize(new { fingerprints = batch });

        var resp = await PostAsync<CurseForgeFingerprintResponse>("/v1/fingerprints", body, ct).ConfigureAwait(false);

        // 缓存未就绪时官方不返回匹配，等一会儿重试一次
        if (resp?.Data is { IsCacheBuilt: false })
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return result; }

            resp = await PostAsync<CurseForgeFingerprintResponse>("/v1/fingerprints", body, ct).ConfigureAwait(false);
        }

        var data = resp?.Data;
        if (data is null) return result;

        // exactFingerprints 与 exactMatches 按顺序一一对应
        for (var i = 0; i < data.ExactMatches.Count && i < data.ExactFingerprints.Count; i++)
        {
            var fp = (uint)data.ExactFingerprints[i];
            result[fp] = data.ExactMatches[i];
        }
        return result;
    }

    /// <summary>扫描本地 jar 并做指纹匹配（工具箱 / 整合包修复用）。</summary>
    public async Task<Dictionary<string, CurseForgeFingerprintMatch>> MatchLocalFilesAsync(
        IEnumerable<string> jarPaths, CancellationToken ct = default)
    {
        var pairs = await CurseForgeFingerprint.ComputeFilesAsync(jarPaths, ct).ConfigureAwait(false);
        if (pairs.Count == 0) return new Dictionary<string, CurseForgeFingerprintMatch>();

        var matches = await MatchFingerprintsAsync(pairs.Select(p => p.Fingerprint).ToList(), ct).ConfigureAwait(false);

        var result = new Dictionary<string, CurseForgeFingerprintMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, fp) in pairs)
        {
            if (matches.TryGetValue(fp, out var m)) result[path] = m;
        }
        return result;
    }

    // ================= 内部 HTTP =================

    private Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken ct) where T : class
        => SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, CurseForgeConfig.ResolvedApiRoot + pathAndQuery), ct);

    private Task<T?> PostAsync<T>(string pathAndQuery, string jsonBody, CancellationToken ct) where T : class
    {
        var req = new HttpRequestMessage(HttpMethod.Post, CurseForgeConfig.ResolvedApiRoot + pathAndQuery)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        return SendAsync<T>(req, ct);
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct) where T : class
    {
        LastError = null;

        if (!CurseForgeConfig.Enabled)
        {
            LastError = "CurseForge 已在设置中关闭";
            return null;
        }
        if (!CurseForgeConfig.HasKey)
        {
            LastError = "未配置 CurseForge API Key";
            return null;
        }
        if (CurseForgeThrottle.IsPaused)
        {
            LastError = $"CurseForge 触发限流，已暂停 {CurseForgeThrottle.Remaining:mm\\:ss}";
            return null;
        }

        try
        {
            using (request)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", CurseForgeConfig.ResolvedApiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
                }

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.Forbidden:      // 官方对「Key 无效」与「配额超限」都返回 403
                    case (HttpStatusCode)429:
                        CurseForgeThrottle.Pause(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                        LastError = "CurseForge 返回 403/429：API Key 无效或配额超限，已暂停请求";
                        break;

                    case HttpStatusCode.NotFound:
                        LastError = "CurseForge 未找到该资源（404）";
                        break;

                    case HttpStatusCode.Unauthorized:
                        CurseForgeThrottle.Pause(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                        LastError = "CurseForge 拒绝访问（401）：API Key 无效或 CDN 强制认证失败";
                        break;

                    default:
                        LastError = $"CurseForge 请求失败：HTTP {(int)resp.StatusCode}";
                        break;
                }
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"CurseForge 网络错误：{ex.Message}";
            return null;
        }
    }
}
