using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLCS.Core.Lan;

/// <summary>
/// 清单 #35 ~ #40：局域网对等联动协议（情人节专项）。
/// <para>设计前提（与早期「远程部署」方案的差异）：MCLCS 启动器是绿色版，不安装、
/// 不写注册表，因此**不做**向远端机器部署启动器这件事。联动模型为对等模式 ——
/// 两端都运行着启动器且开启「允许局域网联动」，谁都可以邀请谁。</para>
/// </summary>
public static class LanPeerProtocol
{
    /// <summary>探测 / 应答使用的 UDP 端口。</summary>
    public const int DiscoveryPort = 47624;

    /// <summary>指令服务使用的 TCP 端口（自实现极简 HTTP）。</summary>
    public const int CommandPort = 47625;

    /// <summary>探测报文魔数，用于过滤网段内无关广播。</summary>
    public const string Magic = "CHERT-DISCOVER-1";

    /// <summary>协议版本，两端不一致时拒绝建立会话。</summary>
    public const int Version = 1;

    /// <summary>配对码有效期。</summary>
    public static readonly TimeSpan PairCodeTtl = TimeSpan.FromMinutes(10);

    /// <summary>超过该时长未再收到应答即认为对等体已离线。</summary>
    public static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>序列化为 JSON（紧凑，便于 UDP 单包承载）。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>反序列化；失败返回 null（网络报文不可信，一律不抛异常）。</summary>
    public static T? Deserialize<T>(string? text) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<T>(text, Json); }
        catch { return null; }
    }
}

/// <summary>局域网内的一台「对等体」（另一台跑着MCLCS 启动器的机器）。</summary>
public sealed class LanPeer
{
    /// <summary>设备名（计算机名，可为空）。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>对方指令服务的 IP 地址（字符串形式）。</summary>
    public string Address { get; set; } = "";

    /// <summary>对方指令服务端口。</summary>
    public int Port { get; set; } = LanPeerProtocol.CommandPort;

    /// <summary>启动器版本。</summary>
    public string LauncherVersion { get; set; } = "";

    /// <summary>协议版本。</summary>
    public int ProtocolVersion { get; set; } = LanPeerProtocol.Version;

    /// <summary>当前是否正在运行 Minecraft。</summary>
    public bool GameRunning { get; set; }

    /// <summary>是否接受局域网联动（对方开关未开时不主动打扰）。</summary>
    public bool LinkEnabled { get; set; }

    /// <summary>已开启的局域网世界地址（host:port），未开启为空。</summary>
    public string LanEndpoint { get; set; } = "";

    /// <summary>最近一次收到应答的时间。</summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>指令服务基址，如 http://192.168.1.5:47625。</summary>
    public string BaseUrl => $"http://{Address}:{Port}";

    /// <summary>列表展示名：优先设备名，回退 IP。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(DeviceName) ? Address : DeviceName;

    /// <summary>端点字符串。</summary>
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>探测包（UDP 广播）。</summary>
public sealed class LanDiscoverQuery
{
    public string Magic { get; set; } = LanPeerProtocol.Magic;
    public int Version { get; set; } = LanPeerProtocol.Version;

    /// <summary>随机串，用于识别自己的广播并丢弃。</summary>
    public string Nonce { get; set; } = "";

    /// <summary>发送方设备名（便于对方日志排查）。</summary>
    public string From { get; set; } = "";
}

/// <summary>探测应答包（UDP 单播回给探测方）。</summary>
public sealed class LanDiscoverReply
{
    public string Magic { get; set; } = LanPeerProtocol.Magic;
    public int Version { get; set; } = LanPeerProtocol.Version;

    /// <summary>回应的探测包 nonce（回声校验）。</summary>
    public string Nonce { get; set; } = "";

    public LanPeer Peer { get; set; } = new();
}

/// <summary>指令种类。</summary>
public enum LanCommandKind
{
    /// <summary>心跳 / 连通性测试。</summary>
    Ping,

    /// <summary>查询对等体状态。</summary>
    Info,

    /// <summary>请求把当前世界对局域网开放。</summary>
    OpenLan,

    /// <summary>邀请对方加入指定局域网世界。</summary>
    JoinLan,

    /// <summary>配对请求（携带配对码）。</summary>
    Pair
}

/// <summary>指令报文（HTTP POST 的 JSON 体）。</summary>
public sealed class LanCommand
{
    public LanCommandKind Kind { get; set; } = LanCommandKind.Ping;

    /// <summary>会话 token。首次调用为配对码，配对成功后由服务端签发 token。</summary>
    public string Token { get; set; } = "";

    /// <summary>发起方设备名。</summary>
    public string From { get; set; } = "";

    /// <summary>JoinLan：目标世界地址 host:port。</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>世界展示名（用于确认弹窗）。</summary>
    public string WorldName { get; set; } = "";

    /// <summary>发起时间戳（Unix 秒），用于拒绝重放。</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>指令响应。</summary>
public sealed class LanCommandResult
{
    public bool Ok { get; set; }

    /// <summary>机器可读的状态码：ok / rejected / unauthorized / busy / error。</summary>
    public string Status { get; set; } = "ok";

    public string Message { get; set; } = "";

    /// <summary>配对成功后签发的会话 token。</summary>
    public string Token { get; set; } = "";

    public LanPeer? Peer { get; set; }

    public static LanCommandResult Fail(string status, string message)
        => new() { Ok = false, Status = status, Message = message };

    public static LanCommandResult Success(string message = "", LanPeer? peer = null)
        => new() { Ok = true, Status = "ok", Message = message, Peer = peer };
}

/// <summary>
/// 会话 / 配对状态机（纯函数，可离线自检）。
/// <para>局域网不可信：所有非 Ping 指令必须携带有效 token，否则视为 unauthorized。</para>
/// </summary>
public sealed class LanSessionStore
{
    private readonly Dictionary<string, DateTime> _authorized = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已签发的会话 token → 过期时间。</summary>
    public IReadOnlyDictionary<string, DateTime> Sessions => _authorized;

    /// <summary>生成一个 6 位数字配对码（仅数字，便于口头传达）。</summary>
    public static string GeneratePairCode()
    {
        var buf = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        var n = (int)(BitConverter.ToUInt32(buf) % 1_000_000);
        return n.ToString("D6");
    }

    /// <summary>校验配对码并签发长期会话 token；失败返回 null。</summary>
    public string? Redeem(string pairCode, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(pairCode)) return null;
        var t = now ?? DateTime.Now;
        Prune(t);
        if (!_authorized.TryGetValue(pairCode.Trim(), out var expiry)) return null;
        if (t > expiry) { _authorized.Remove(pairCode.Trim()); return null; }
        _authorized.Remove(pairCode.Trim());

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _authorized[token] = t.AddDays(7);
        return token;
    }

    /// <summary>登记一个待使用的配对码。</summary>
    public void IssuePairCode(string code, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var t = now ?? DateTime.Now;
        Prune(t);
        _authorized[code.Trim()] = t.Add(LanPeerProtocol.PairCodeTtl);
    }

    /// <summary>token 是否有效。</summary>
    public bool IsAuthorized(string? token, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = now ?? DateTime.Now;
        Prune(t);
        return _authorized.ContainsKey(token.Trim());
    }

    /// <summary>撤销全部会话（关闭联动开关时调用）。</summary>
    public void RevokeAll() => _authorized.Clear();

    /// <summary>清理过期条目，返回清理数量。</summary>
    public int Prune(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var keys = _authorized.Where(kv => t > kv.Value).Select(kv => kv.Key).ToList();
        foreach (var k in keys) _authorized.Remove(k);
        return keys.Count;
    }
}
