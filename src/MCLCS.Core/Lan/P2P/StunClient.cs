using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// 一次 Binding 查询的结果（拿到的是 NAT 分配给本机的「公网 ip:port」，也叫 server-reflexive candidate）。
/// </summary>
public sealed class StunResult
{
    public bool Ok { get; init; }

    /// <summary>NAT 映射后的公网端点；失败为 null。</summary>
    public IPEndPoint? Mapped { get; init; }

    /// <summary>来源倾向的 cause 地址（基本无用，留着排查）。</summary>
    public string Server { get; init; } = "";

    public string Error { get; init; } = "";

    public static StunResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// STUN 客户端（RFC 5389 / 8489 的最小子集：只做 Binding Request）。
/// <para>
/// 为什么非有不可：NAT 穿透的第一步是**知道自己在公网上长什么样**。
/// 没有它，握手码里只能塞本机内网地址（192.168.x.x），对方照着发包必然石沉大海 ——
/// 唯一还能通的情况是两边路由器都成功做了 UPnP 映射，而这在国内宽带上远不是常态。
/// </para>
/// <para>
/// 关键约束：**查询必须从实际要打洞的那个 UDP 端口发出**。
/// NAT 的映射是按 (内网 ip:port) 记账的，换个端口问出来的地址就没有意义。
/// 所以 API 收一个已绑定的 socket，而不是自己随手开一个。
/// </para>
/// <para>
/// 为什么解析同步阻塞放在 Task.Run 里、而不是用 ReceiveFromAsync + CancellationToken：
/// 在 Windows 上取消一个进行中的 IOCP 接收会污染 socket 的后续操作，
/// 而这里同一个 socket 还要连着做三四次查询（判 NAT 行为用的就是它）。
/// 用古老的 <see cref="Socket.ReceiveTimeout"/>（SO_RCVTIMEO）配合同步 ReceiveFrom 更稳。
/// </para>
/// </summary>
public static class StunClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 默认服务器列表。刻意多放几个：公共 STUN 在国内的可达性差异很大，
    /// 逐个试到第一个回包的为止（顺序先放境外常见的，国内有镜像的排后面）。
    /// 用户可以在设置里指定自己的 —— 公司内网 / 特殊宽带下常常只有自建的才通。
    /// </summary>
    public static readonly string[] DefaultServers =
    {
        "stun.l.google.com:19302",
        "stun.miwifi.com:3478",
        "stun.cloudflare.com:3478",
        "stun.antisip.com:3478",
        "stun.l.google.com:3478",
        "global.stun.twilio.com:3478"
    };

    private const ushort MsgBindingRequest = 0x0001;
    private const ushort MsgBindingSuccess = 0x0101;
    private const ushort MsgBindingError = 0x0111;
    private const uint MagicCookie = 0x2112A442;

    private const ushort AttrMappedAddress = 0x0001;      // 老式，没做异或混淆
    private const ushort AttrXorMappedAddress = 0x0020;   // RFC 5389 起的标准写法

    private const int HeaderSize = 20;
    private const int TransactionIdSize = 12;

    /// <summary>
    /// 用给定 socket 向指定服务器查询一次。socket 必须已经绑定且在能收到回包的地址族上。
    /// </summary>
    public static Task<StunResult> QueryAsync(Socket socket, EndPoint server,
                                              TimeSpan? timeout = null, CancellationToken ct = default)
        => Task.Run(() => QueryCore(socket, server, timeout ?? DefaultTimeout, ct), ct);

    /// <summary>
    /// 「端口-末端能否连上」「拿得到的公网映射是什么」—— 用临时 socket 在给定本地端口上问一次。
    /// 供 UI 在后台预热 / 排查用；真正的打洞请复用同一个 socket。
    /// </summary>
    public static async Task<IPEndPoint?> ProbeAsync(int localPort, string serverText,
                                                     TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!TryResolve(serverText, out var ep, out _)) return null;

        using var socket = new Socket(ep!.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { socket.Bind(new IPEndPoint(ep.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, localPort)); }
        catch (SocketException) { return null; }

        var result = await QueryAsync(socket, ep, timeout, ct).ConfigureAwait(false);
        return result.Mapped;
    }

    /// <summary>
    /// 把 <c>host:port</c>（可带 udp:// 前缀）解析成一到多个端点；域名可能解出多个 IP，全部返回。
    /// 这是同步解析（DNS），调用方负责丢到后台线程去。
    /// </summary>
    public static IReadOnlyList<IPEndPoint> ResolveEntries(string serverText)
    {
        var s = (serverText ?? "").Trim();
        if (s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)) s = s["udp://".Length..];

        var port = 3478;
        var host = s;

        if (s.StartsWith('['))                                  // [2001:db8::1]:3478
        {
            var close = s.IndexOf(']');
            if (close < 0) return Array.Empty<IPEndPoint>();
            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.StartsWith(':')) rest = rest[1..];
            if (rest.Length > 0 && int.TryParse(rest, out var p6)) port = p6;
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon > 0)
            {
                host = s[..colon];
                if (int.TryParse(s[(colon + 1)..], out var p)) port = p;
            }
        }

        if (port is < 1 or > 65535) return Array.Empty<IPEndPoint>();
        if (host.Length == 0) return Array.Empty<IPEndPoint>();

        if (IPAddress.TryParse(host, out var literal)) return new[] { new IPEndPoint(literal, port) };

        try
        {
            return Dns.GetHostAddresses(host)
                      .Where(x => x.AddressFamily == AddressFamily.InterNetwork ||
                                  x.AddressFamily == AddressFamily.InterNetworkV6)
                      .Select(x => new IPEndPoint(x, port))
                      .ToArray();
        }
        catch
        {
            // DNS 失败是常态（域名不存在 / 网络断开 / DNS 被污染），当成「这个服务器不可用」
            return Array.Empty<IPEndPoint>();
        }
    }

    /// <summary>解析 <c>host:port</c> 取第一个可用端点；失败返回 false。</summary>
    public static bool TryResolve(string serverText, out EndPoint? endPoint, out string error)
    {
        var all = ResolveEntries(serverText);
        if (all.Count == 0)
        {
            endPoint = null;
            error = $"解析不了的地址：{serverText}";
            return false;
        }
        endPoint = all[0];
        error = "";
        return true;
    }

    // ===== 报文 =====

    /// <summary>构造 Binding Request：20 字节头，没有 body。事务 id 必须随机且每次不同。</summary>
    public static byte[] BuildRequest(byte[]? transactionId = null)
    {
        var id = transactionId ?? RandomNumberGenerator.GetBytes(TransactionIdSize);
        if (id.Length != TransactionIdSize) throw new ArgumentException("事务 id 必须是 12 字节", nameof(transactionId));

        var message = new byte[HeaderSize];
        WriteUInt16(message, 0, MsgBindingRequest);
        WriteUInt16(message, 2, 0);                       // body 长度
        WriteUInt32(message, 4, MagicCookie);
        id.CopyTo(message, 8);
        return message;
    }

    /// <summary>
    /// 解析 Binding 响应。会校验魔数与事务 id —— 同一个 socket 上可能有别的包，
    /// 没有事务 id 这道校验就会把别人的响应当成自己的公网地址。
    /// </summary>
    public static bool TryParse(byte[] buffer, int length, byte[] transactionId, out IPEndPoint? mapped, out string error)
    {
        mapped = null;
        error = "";

        if (length < HeaderSize) { error = "响应太短"; return false; }
        if (ReadUInt32(buffer, 4) != MagicCookie) { error = "不是 STUN 包（魔数不符）"; return false; }
        if (!buffer.AsSpan(8, TransactionIdSize).SequenceEqual(transactionId)) { error = "事务 id 不符"; return false; }

        var type = ReadUInt16(buffer, 0);
        var bodyLength = ReadUInt16(buffer, 2);

        if (type == MsgBindingError) { error = "服务器返回错误"; return false; }
        if (type != MsgBindingSuccess) { error = $"不是 Binding 响应（type 0x{type:X4}）"; return false; }
        if (length < HeaderSize + bodyLength) { error = "响应被截断"; return false; }

        IPEndPoint? plain = null;
        var pos = HeaderSize;
        var end = HeaderSize + bodyLength;

        while (pos + 4 <= end)
        {
            var attrType = ReadUInt16(buffer, pos);
            var attrLength = ReadUInt16(buffer, pos + 2);
            pos += 4;

            if (attrLength > end - pos) break;                 // 属性越界：这包有问题，别再往下读
            var value = new ReadOnlySpan<byte>(buffer, pos, attrLength);

            if (attrType == AttrXorMappedAddress)
            {
                if (TryReadAddress(value, transactionId, xor: true, out var xorMapped)) { mapped = xorMapped; return true; }
            }
            else if (attrType == AttrMappedAddress)
            {
                if (TryReadAddress(value, transactionId, xor: false, out var fallback)) plain = fallback;
            }

            pos += attrLength + (attrLength % 4 == 0 ? 0 : 4 - attrLength % 4);   // 属性按 4 字节对齐
        }

        if (plain is not null) { mapped = plain; return true; }     // 老服务器只有 MAPPED-ADDRESS
        error = "响应里没有映射地址";
        return false;
    }

    // ===== 内部 =====

    private static StunResult QueryCore(Socket socket, EndPoint server, TimeSpan timeout, CancellationToken ct)
    {
        // 别等到发包抛异常才发现：bind 的是 IPv4 socket 却给了 IPv6 服务器，
        // 这种组合会直接 AddressFamilyNotSupported（在 IPv6 优先的系统上很容易撞到 ——
        // stun.l.google.com 解析出来的第一条就是 IPv6 记录）
        if (server.AddressFamily != socket.AddressFamily)
            return StunResult.Fail($"socket 是 {socket.AddressFamily}，服务器却是 {server.AddressFamily}");

        var transactionId = RandomNumberGenerator.GetBytes(TransactionIdSize);
        var request = BuildRequest(transactionId);
        var buffer = new byte[512];

        // 收两次机会：UDP 丢第一个包在现实网络里很常见，重发一次的成本远低于让用户等十秒
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (ct.IsCancellationRequested) return StunResult.Fail("已取消");

            try { socket.SendTo(request, server); }
            catch (SocketException ex) { return StunResult.Fail($"发包失败：{ex.SocketErrorCode}"); }
            catch (ObjectDisposedException) { return StunResult.Fail("socket 已释放"); }

            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (ct.IsCancellationRequested) return StunResult.Fail("已取消");

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                try { socket.ReceiveTimeout = (int)Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue); }
                catch (SocketException) { break; }

                EndPoint from = server;
                int received;
                try { received = socket.ReceiveFrom(buffer, ref from); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { return StunResult.Fail("socket 已释放"); }

                // 事务 id 不符说明是别人 / 上一次的包：继续等，不要当失败
                if (TryParse(buffer, received, transactionId, out var mapped, out _))
                    return new StunResult { Ok = true, Mapped = mapped, Server = from.ToString() ?? "" };
            }
        }

        return StunResult.Fail("没有收到服务器回应");
    }

    private static bool TryReadAddress(ReadOnlySpan<byte> value, byte[] transactionId, bool xor, out IPEndPoint? address)
    {
        address = null;
        if (value.Length < 4) return false;

        var family = value[1];
        var portValue = ReadUInt16(value, 2);
        if (xor) portValue ^= (ushort)(MagicCookie >> 16);

        if (family == 0x01 && value.Length >= 8)            // IPv4
        {
            var bytes = new byte[4];
            value.Slice(4, 4).CopyTo(bytes);
            if (xor) Xor(bytes, MagicCookieBytes(), transactionId, ipv6: false);
            address = new IPEndPoint(new IPAddress(bytes), portValue);
            return true;
        }

        if (family == 0x02 && value.Length >= 20)           // IPv6
        {
            var bytes = new byte[16];
            value.Slice(4, 16).CopyTo(bytes);
            if (xor) Xor(bytes, MagicCookieBytes(), transactionId, ipv6: true);
            address = new IPEndPoint(new IPAddress(bytes), portValue);
            return true;
        }

        return false;
    }

    /// <summary>
    /// XOR-MAPPED-ADDRESS 的混淆密钥：IPv4 只有 4 字节魔数，IPv6 是「魔数 + 事务 id」共 16 字节。
    /// 混淆本身没有安全意义，只是为了让中间盒子不认出这是地址（避免 ALG 改写）。
    /// </summary>
    private static void Xor(byte[] address, byte[] cookie, byte[] transactionId, bool ipv6)
    {
        if (!ipv6)
        {
            for (var i = 0; i < 4; i++) address[i] ^= cookie[i];
            return;
        }
        for (var i = 0; i < 4; i++) address[i] ^= cookie[i];
        for (var i = 0; i < TransactionIdSize; i++) address[4 + i] ^= transactionId[i];
    }

    private static byte[] MagicCookieBytes() =>
        new[]
        {
            (byte)((MagicCookie >> 24) & 0xFF),
            (byte)((MagicCookie >> 16) & 0xFF),
            (byte)((MagicCookie >> 8) & 0xFF),
            (byte)(MagicCookie & 0xFF)
        };

    private static void WriteUInt16(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
        => (ushort)((source[offset] << 8) | source[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => ((uint)source[offset] << 24) | ((uint)source[offset + 1] << 16) |
           ((uint)source[offset + 2] << 8) | source[offset + 3];
}
