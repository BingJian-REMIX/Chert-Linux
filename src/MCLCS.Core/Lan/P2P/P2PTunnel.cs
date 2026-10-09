using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// 广域网直连的隧道：把 Minecraft 的 TCP 连接塞进一条 QUIC 连接里。
/// <code>
/// Minecraft 客户端 ──TCP──&gt; 127.0.0.1:25565（本地代理）
///                                 │
///                          每条 TCP 连接 = 一条 QUIC 双向流
///                                 │
///                         对方启动器 ──TCP──&gt; 127.0.0.1:对方的 MC 端口
/// </code>
/// 为什么要走 QUIC 而不是裸 TCP：QUIC 自带 TLS 加密与丢包重传，
/// 而我们拿到的只是一个 NAT 洞，链路质量没保证；另外 QUIC 的多流正好映射多个 TCP 连接
/// （Minecraft 会开好几条），自己造 TCP-over-UDP 会重蹈 TCP-in-TCP 的性能陷阱。
/// </summary>
public sealed class P2PTunnel : IDisposable
{
    /// <summary>应用层协议名（双方必须对上，否则握手立刻被拒）。</summary>
    public const string Alpn = "chert-p2p";

    /// <summary>Minecraft 默认端口：本地代理优先占它，被占用了再往后找。</summary>
    public const int PreferredProxyPort = 25565;

    /// <summary>找本地端口时最多往后试几个（试太多会让用户搞不清该连哪个）。</summary>
    private const int ProxyPortTries = 12;

    private const byte RoleClient = 1;
    private const byte RoleAccept = 1;
    private const byte RoleReject = 0;

    /// <summary>每条数据流的第一个字节：这一条是转发 Minecraft 数据的。</summary>
    private const byte ChanData = 1;

    /// <summary>每条数据流的第一个字节：这一条只是保活心跳，不转发任何数据。</summary>
    private const byte ChanPing = 2;

    /// <summary>心跳的应答字节（内容无所谓，收到就算活着）。</summary>
    private const byte Pong = 1;

    /// <summary>
    /// 心跳间隔。选 15 秒是被两件事夹出来的：NAT 的 UDP 映射常见 30～60 秒不通信就回收，
    /// 而 Minecraft 在加载地形、切维度时空窗可以到十几秒 —— 再密就是白送流量了。
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>单次心跳等多久算没回音。</summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>连续几次心跳没回音就判定链路断了（15×3+5 ≈ 50 秒，足够扛过一次 Wi-Fi 抖动）。</summary>
    public const int MissedBeatsBeforeLost = 3;

    /// <summary>房主侧：多久没有一点动静就认为对方掉线了（心跳 15 秒一次，给到 60 秒容错）。</summary>
    public static readonly TimeSpan HostSilenceLimit = TimeSpan.FromSeconds(60);

    private readonly byte[] _sessionSecret = Array.Empty<byte>();
    private CancellationTokenSource? _cts;
    private QuicListener? _listener;
    private QuicConnection? _connection;
    private TcpListener? _proxy;

    private DateTime _lastActivity = DateTime.UtcNow;
    private int _linkLostRaised;
    private bool _disposed;

    /// <summary>判定链路已断（参数是人话描述）。UI 应当提示可以重连，而不是继续显示「已连接」。</summary>
    public event Action<string>? LinkLost;

    /// <summary>最后一次在这条链路上看到动静的时间（UTC）。用于判断「还活着吗」。</summary>
    public DateTime LastActivityUtc => _lastActivity;

    /// <summary>当前平台能否跑 QUIC（Windows 需要 MsQuic，随 .NET 运行时分发）。</summary>
    public static bool IsSupported
    {
        get
        {
            try { return QuicListener.IsSupported; }
            catch { return false; }
        }
    }

    /// <summary>不支持时的说明。</summary>
    public static string UnsupportedReason =>
        "当前系统的 .NET QUIC（MsQuic）不可用。需要 Windows 11 / Windows Server 2022 及以上，" +
        "或安装最新的 .NET 运行时。";

    public bool Busy => _listener is not null || _connection is not null;

    public P2PTunnel(byte[] sessionSecret)
    {
        ArgumentNullException.ThrowIfNull(sessionSecret);
        if (sessionSecret.Length == 0) throw new ArgumentException("会话密钥不能为空", nameof(sessionSecret));
        _sessionSecret = sessionSecret;
    }

    // ===== 房主：监听 QUIC，把入站流转到本机 MC =====

    /// <summary>
    /// 房主端启动 QUIC 监听。必须在**打洞之后**调用，且用与打洞相同的本地端口
    /// （同一个源端口才能命中刚才在 NAT 上戳开的映射）。
    /// </summary>
    /// <param name="localUdpPort">本地 UDP 端口（与握手码里通告的一致）。</param>
    /// <param name="targetMcPort">本机 Minecraft 已开放的端口（隧道另一端要连的目标）。</param>
    public async Task StartHostAsync(int localUdpPort, int targetMcPort, CancellationToken ct = default)
    {
        EnsureFresh();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, localUdpPort),
            ApplicationProtocols = new List<SslApplicationProtocol> { new(Alpn) },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                IdleTimeout = TimeSpan.FromMinutes(5),
                MaxInboundBidirectionalStreams = 64,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { new(Alpn) },
                    ServerCertificate = CreateEphemeralCertificate(),
                    ClientCertificateRequired = false
                }
            })
        };

        _listener = await QuicListener.ListenAsync(options, _cts.Token).ConfigureAwait(false);
        _ = Task.Run(() => AcceptLoopAsync(targetMcPort, _cts.Token), _cts.Token);

        // 房主不主动心跳（它是被连的一方），改由看门狗盯「多久没动静」：
        // 对端一旦拔网线或笔记本合盖，QUIC 不会立刻报错，只有静默时间能说明问题。
        _ = Task.Run(() => HostWatchdogAsync(_cts.Token), _cts.Token);
    }

    // ===== 加入者：连对方，并在本机开 Minecraft 要连的端口 =====

    /// <summary>
    /// 加入者端连接对方，成功后在本机监听 TCP；返回实际监听的端口（可能不是 25565）。
    /// </summary>
    public async Task<int> ConnectAsync(int localUdpPort, IPEndPoint peerEndPoint, int preferredProxyPort, CancellationToken ct = default)
    {
        EnsureFresh();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var localAddress = peerEndPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = peerEndPoint,
            // 关键：从打洞时用过的那个端口出去。换端口等于换一条 NAT 映射，刚才的洞就白戳了。
            LocalEndPoint = new IPEndPoint(localAddress, localUdpPort),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            IdleTimeout = TimeSpan.FromMinutes(5),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new(Alpn) },
                // 证书是每次连接临时生成的自签证书，本来就没有可信颁发方 —— 校验链毫无意义。
                // 真正的身份验证紧接着在应用层做（用 X25519 派生的会话密钥挑战—应答）。
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        _connection = await QuicConnection.ConnectAsync(options, _cts.Token).ConfigureAwait(false);

        // 第一条流是控制流：互相出题，证明自己持有对方公钥对应的私钥
        await using var control = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _cts.Token)
                                                    .ConfigureAwait(false);
        if (!await HandshakeAsClientAsync(control, _cts.Token).ConfigureAwait(false))
            throw new InvalidOperationException("对方没通过身份验证（会话密钥不一致）");

        _proxy = BindProxy(preferredProxyPort, out var port);
        _ = Task.Run(() => ProxyLoopAsync(_proxy, _connection, _cts.Token), _cts.Token);

        // 心跳由加入者发：它持有那条 QUIC 连接，也只有它在持续观察对端。
        // 不是为了「显得在线」——NAT 的映射一段时间没流量就被回收，洞没了隧道也就断了。
        _ = Task.Run(() => KeepAliveLoopAsync(_connection, _cts.Token), _cts.Token);
        return port;
    }

    /// <summary>挑一个空闲的本地端口。被占用时不要硬抢 —— 用户本机可能正开着服务端。</summary>
    public static int PickFreePort(int preferred)
    {
        for (var i = 0; i < ProxyPortTries; i++)
        {
            var port = preferred + i;
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { /* 被占，试下一个 */ }
        }
        return 0;
    }

    /// <summary>
    /// 挑一个空闲的 UDP 端口（打洞用的那个）。
    /// 默认端口写死迟早会撞：别的程序占了、或者上一次没退干净，症状都是「打洞超时」，
    /// 看起来跟路由器不支持直连一模一样 —— 所以宁可往后找一个能用的。
    /// </summary>
    public static int PickFreeUdpPort(int preferred)
    {
        for (var i = 0; i < ProxyPortTries; i++)
        {
            var port = preferred + i;
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                probe.Bind(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException) { /* 被占，试下一个 */ }
        }
        return 0;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 取消本身不会失败 */ }
        try { _proxy?.Stop(); } catch { }
        try { _listener?.DisposeAsync().AsTask().Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        try { _connection?.CloseAsync(0).AsTask().Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        _listener = null;
        _connection = null;
        _proxy = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ===== 房主端循环 =====

    private async Task AcceptLoopAsync(int targetMcPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            QuicConnection connection;
            try { connection = await _listener.AcceptConnectionAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { if (ct.IsCancellationRequested) return; continue; }

            _ = Task.Run(async () =>
            {
                try { await ServeHostConnectionAsync(connection, targetMcPort, ct).ConfigureAwait(false); }
                catch { /* 单个连接出问题不影响监听本身 */ }
            }, ct);
        }
    }

    private async Task ServeHostConnectionAsync(QuicConnection connection, int targetMcPort, CancellationToken ct)
    {
        await using (connection)
        {
            await using var control = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
            if (!await HandshakeAsHostAsync(control, ct).ConfigureAwait(false))
            {
                try { await control.WriteAsync(new[] { RoleReject }, ct).ConfigureAwait(false); } catch { }
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                QuicStream stream;
                try { stream = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using (stream)
                        {
                            // 每条流的第一个字节说明这条流是干嘛的：心跳就地答一句就关，
                            // 其余的才去连本机 MC（这个字节不转发，是隧道自己的开销）。
                            var kind = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
                            if (kind.Length == 1 && kind[0] == ChanPing)
                            {
                                NoteActivity();
                                await stream.WriteAsync(new[] { Pong }, ct).ConfigureAwait(false);
                                await stream.FlushAsync(ct).ConfigureAwait(false);
                                return;
                            }

                            using var tcp = new TcpClient();
                            await tcp.ConnectAsync(IPAddress.Loopback, targetMcPort, ct).ConfigureAwait(false);
                            NoteActivity();
                            await PumpAsync(stream, tcp.GetStream(), ct).ConfigureAwait(false);
                        }
                    }
                    catch { /* 对端断开、本机 MC 没开，都是正常的结束方式 */ }
                }, ct);
            }
        }
    }

    // ===== 加入者端循环 =====

    private async Task ProxyLoopAsync(TcpListener proxy, QuicConnection connection, CancellationToken ct)
    {
        proxy.Start();
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await proxy.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct)
                                                             .ConfigureAwait(false);
                    await stream.WriteAsync(new[] { ChanData }, ct).ConfigureAwait(false);
                    using (tcp)
                        await PumpAsync(stream, tcp.GetStream(), ct).ConfigureAwait(false);
                }
                catch { /* Minecraft 断开连接时会直接重置，属正常收尾 */ }
            }, ct);
        }
    }

    // ===== 保活 =====

    /// <summary>
    /// 加入者侧心跳：定期开一条流问一句，收不到回音就累计失败次数。
    /// 连续几次都收不到才算断 —— 单次丢包在 UDP 上太常见了，一次失败就报警会误报个不停。
    /// </summary>
    private async Task KeepAliveLoopAsync(QuicConnection connection, CancellationToken ct)
    {
        var misses = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                await using (var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct)
                                                          .ConfigureAwait(false))
                {
                    await stream.WriteAsync(new[] { ChanPing }, ct).ConfigureAwait(false);

                    using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    beat.CancelAfter(KeepAliveTimeout);

                    var reply = await ReadExactlyAsync(stream, 1, beat.Token).ConfigureAwait(false);
                    if (reply.Length == 1 && reply[0] == Pong)
                    {
                        misses = 0;
                        NoteActivity();
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时：算一次没回音，下面统一累计
            }
            catch
            {
                // 连接已经废了：同样按没回音处理
            }

            if (++misses < MissedBeatsBeforeLost) continue;

            ReportLinkLost($"已经 {misses} 次心跳没有回音（约 {(int)(KeepAliveInterval.TotalSeconds * misses)} 秒），链路断了");
            return;
        }
    }

    /// <summary>房主侧看门狗：只看「多久没动静」，不动手发包。Minecraft 没在传数据时尤其需要它。</summary>
    private async Task HostWatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var silence = DateTime.UtcNow - _lastActivity;
            if (silence < HostSilenceLimit) continue;

            ReportLinkLost($"超过 {(int)silence.TotalSeconds} 秒没收到对方任何数据，链路断了");
            return;
        }
    }

    private void NoteActivity() => _lastActivity = DateTime.UtcNow;

    private void ReportLinkLost(string reason)
    {
        // 同一条链路只报一次：心跳超时和看门狗可能同时到，弹两次提示只会让人困惑
        if (Interlocked.Exchange(ref _linkLostRaised, 1) != 0) return;
        LinkLost?.Invoke(reason);
    }

    // ===== 转发 =====

    private static async Task PumpAsync(Stream a, Stream b, CancellationToken ct)
    {
        var ab = a.CopyToAsync(b, 81920, ct);
        var ba = b.CopyToAsync(a, 81920, ct);
        await Task.WhenAll(ab, ba).ConfigureAwait(false);
    }

    private TcpListener BindProxy(int preferred, out int port)
    {
        port = PickFreePort(preferred);
        if (port == 0) throw new InvalidOperationException($"本机 {preferred} 附近的端口都被占用了");
        return new TcpListener(IPAddress.Loopback, port);
    }

    // ===== 应用层身份认证 =====

    /// <summary>
    /// 服务端侧：先答对方的题，再出自己的题。任一步失败就断开。
    /// 这一步是必需的 —— 单单 QUIC 加密只保证「链路上没被偷看」，
    /// 不保证连上的是刚才交换过握手码的那个人（NAT 洞也可能被别人蹭到）。
    /// </summary>
    private async Task<bool> HandshakeAsHostAsync(Stream stream, CancellationToken ct)
    {
        var first = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
        if (first.Length == 0 || first[0] != RoleClient) return false;

        var challenge = await ReadExactlyAsync(stream, 16, ct).ConfigureAwait(false);

        await stream.WriteAsync(Answer(challenge), ct).ConfigureAwait(false);          // 32 字节
        var mine = RandomNumberGenerator.GetBytes(16);
        await stream.WriteAsync(mine, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var theirAnswer = await ReadExactlyAsync(stream, 32, ct).ConfigureAwait(false);
        if (!Verify(Answer(mine), theirAnswer)) return false;

        await stream.WriteAsync(new[] { RoleAccept }, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandshakeAsClientAsync(Stream stream, CancellationToken ct)
    {
        var mine = RandomNumberGenerator.GetBytes(16);
        await stream.WriteAsync(new[] { RoleClient }, ct).ConfigureAwait(false);
        await stream.WriteAsync(mine, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var theirAnswer = await ReadExactlyAsync(stream, 32, ct).ConfigureAwait(false);
        if (!Verify(Answer(mine), theirAnswer)) return false;

        var theirChallenge = await ReadExactlyAsync(stream, 16, ct).ConfigureAwait(false);
        await stream.WriteAsync(Answer(theirChallenge), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var verdict = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
        return verdict.Length == 1 && verdict[0] == RoleAccept;
    }

    // 复用 Core 的实现：会话密钥的派生与挑战—应答必须两边完全一致
    private byte[] Answer(byte[] challenge) => P2PIdentity.Answer(_sessionSecret, challenge);

    private static bool Verify(byte[] expected, byte[] actual)
        => P2PIdentity.Verify(expected, actual);

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) break;      // 对端单方面断开
            read += n;
        }
        return read == count ? buffer : buffer[..read];
    }

    // ===== 证书 =====

    /// <summary>
    /// 每次隧道都新造一张自签证书：QUIC 强制要求 TLS，而我们不要固定有效期长的证书落在磁盘上。
    /// 身份不由它证明（应用层认证负责），它只是「让 QUIC 肯握手」的入场券。
    /// </summary>
    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=chert-p2p", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(6));
        // 过一遍 PFX：QUIC 需要「带私钥且可导出」的证书实例，CreateSelfSigned 的返回值常常拿不到 ECDsa 私钥句柄
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private void EnsureFresh()
    {
        Stop();
        _lastActivity = DateTime.UtcNow;
        _linkLostRaised = 0;
    }
}
