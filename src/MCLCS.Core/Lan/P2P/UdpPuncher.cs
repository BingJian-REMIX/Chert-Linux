using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// UDP 打洞器：朝对方的候选地址持续发包，在两边的 NAT 上各戳出一个洞。
/// <para>
/// 为什么能通：NAT 会为「本机某端口发出的第一个包」建立一条 (内网 ip:port) → (外网 ip:port) 的映射，
/// 之后凡是来自**那个目标地址**的包都会被放行。双方同时朝对方发，两头的 NAT 就都有了放行规则——
/// 前提是 NAT 行为允许（所谓「锥形 NAT」）。两边都是对称 NAT 时此路不通，会走到超时。
/// </para>
/// <para>
/// 这个 socket 的生命周期刻意很短：打洞成功 → 立即 <see cref="Stop"/> 释放端口 →
/// 上层用同一个端口发起 QUIC。同一个源端口才能命中刚才戳开的 NAT 映射。
/// </para>
/// </summary>
public sealed class UdpPuncher : IDisposable
{
    /// <summary>打洞包魔数：收到陌生 UDP 包时一眼认出是自己人，而不是把游戏数据当打洞包处理。</summary>
    public const string Magic = "CHP1";

    /// <summary>发一轮包的间隔：太密白送流量，太疏会赶不上 NAT 映射的过期时间。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    private readonly List<Socket> _sockets = new();
    private CancellationTokenSource? _cts;
    private byte[]? _nonce;
    private byte[]? _payload;
    private int _sentCounter;
    private bool _disposed;

    /// <summary>已经观察到对方的地址（首次收到对方打洞包时赋值）。</summary>
    public IPEndPoint? PeerAddress { get; private set; }

    public bool SawPeer => PeerAddress is not null;

    /// <summary>已发出的打洞包数量（UI 里显示「已尝试 N 次」）。</summary>
    public int SentPackets { get; private set; }

    /// <summary>最后收到的对方公钥哈希（6 字节），可用于核对是不是同一个人。</summary>
    public byte[]? PeerKeyHash { get; private set; }

    /// <summary>实际绑定的本地端口（传给上层，QUIC 要复用它）。</summary>
    public int LocalPort { get; private set; }

    /// <summary>第一次看到对方时触发（后续重复包不再触发，但会更新 <see cref="PeerAddress"/>）。</summary>
    public event Action<IPEndPoint>? PeerSeen;

    /// <summary>
    /// 绑定本地端口并开始双向打洞。返回 false 表示端口没绑上（被别的进程占了）。
    /// </summary>
    public bool Start(int localPort, IReadOnlyList<IPEndPoint> remoteCandidates, byte[] nonce, byte[] myPublicKey)
    {
        if (_cts is not null) Stop();
        if (nonce.Length != P2PIdentity.NonceSize) throw new ArgumentException("nonce 必须是 16 字节", nameof(nonce));

        _nonce = nonce;
        _payload = BuildPacket(nonce, myPublicKey);
        PeerAddress = null;
        PeerKeyHash = null;
        SentPackets = 0;

        var list = remoteCandidates.Where(x => x is not null).Distinct().ToList();

        try
        {
            // IPv4 与 IPv6 各一个 socket：v6 设了 IPv6Only 才能和 v4 共用同一个端口号
            if (list.Any(x => x.AddressFamily == AddressFamily.InterNetwork)) Bind(localPort, false);
            if (list.Any(x => x.AddressFamily == AddressFamily.InterNetworkV6)) Bind(localPort, true);
        }
        catch (SocketException)
        {
            CloseSockets();
            return false;
        }

        LocalPort = localPort;
        _cts = new CancellationTokenSource();

        foreach (var socket in _sockets) _ = ReceiveLoop(socket, _cts.Token);
        _ = SendLoop(list, _cts.Token);
        return true;
    }

    /// <summary>停止打洞并释放 UDP 端口（留给 QUIC 复用）。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 取消本身不会失败 */ }
        _cts?.Dispose();
        _cts = null;
        CloseSockets();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ===== 收包格式 =====

    /// <summary>构造打洞包：魔数 + nonce + SHA256(公钥) 前 6 字节。够小（26 字节），NAT 也不会为它单独限流。</summary>
    public static byte[] BuildPacket(byte[] nonce, byte[] publicKey)
    {
        var keyHash = SHA256.HashData(publicKey ?? Array.Empty<byte>());
        var packet = new byte[MagicBytes.Length + nonce.Length + 6];
        MagicBytes.CopyTo(packet, 0);
        nonce.CopyTo(packet, MagicBytes.Length);
        Array.Copy(keyHash, 0, packet, MagicBytes.Length + nonce.Length, 6);
        return packet;
    }

    /// <summary>判断是不是「这一次握手」的打洞包：nonce 不同的就当路人。</summary>
    public static bool TryParse(byte[] buffer, int length, byte[] nonce, out byte[]? keyHash)
    {
        keyHash = null;
        if (length < MagicBytes.Length + nonce.Length + 6) return false;
        for (var i = 0; i < MagicBytes.Length; i++)
            if (buffer[i] != MagicBytes[i]) return false;
        if (!buffer.AsSpan(MagicBytes.Length, nonce.Length).SequenceEqual(nonce)) return false;

        keyHash = new byte[6];
        Array.Copy(buffer, MagicBytes.Length + nonce.Length, keyHash, 0, 6);
        return true;
    }

    // ===== 内部 =====

    private void Bind(int port, bool ipv6)
    {
        var socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                                SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (ipv6) socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            socket.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _sockets.Add(socket);
    }

    private void CloseSockets()
    {
        foreach (var s in _sockets)
        {
            try { s.Dispose(); } catch { /* 释放阶段不该再抛 */ }
        }
        _sockets.Clear();
    }

    private async Task SendLoop(IReadOnlyList<IPEndPoint> candidates, CancellationToken ct)
    {
        var payload = _payload!;
        while (!ct.IsCancellationRequested)
        {
            foreach (var socket in _sockets.ToList())
            {
                foreach (var ep in candidates)
                {
                    // v4 socket 只发 v4，v6 socket 只发 v6：混着发会直接被系统丢掉
                    if (socket.AddressFamily != ep.AddressFamily) continue;
                    try
                    {
                        await socket.SendToAsync(payload, SocketFlags.None, ep).ConfigureAwait(false);
                        SentPackets = Interlocked.Increment(ref _sentCounter);
                    }
                    catch { /* 单个地址不可达不影响其它候选 */ }
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[128];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, any, ct)
                                         .ConfigureAwait(false);
                if (_nonce is null) continue;
                if (!TryParse(buffer, result.ReceivedBytes, _nonce, out var keyHash)) continue;

                PeerKeyHash = keyHash;
                var first = PeerAddress is null;
                PeerAddress = (IPEndPoint)result.RemoteEndPoint;

                // 立刻回一个：对方可能也在这个瞬间看到了我们，双方同时收包才算真正打通
                if (_payload is not null)
                {
                    try { await socket.SendToAsync(_payload, SocketFlags.None, PeerAddress).ConfigureAwait(false); }
                    catch { /* 回包失败无所谓，下一轮还会重发 */ }
                }

                if (first) PeerSeen?.Invoke(PeerAddress);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; }
            catch (ObjectDisposedException) { return; }
        }
    }
}
