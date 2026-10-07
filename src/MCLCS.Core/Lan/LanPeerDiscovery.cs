using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MCLCS.Core.Lan;

/// <summary>
/// 清单 #35：局域网对等体发现。
/// <para>探测方向所有本地 IPv4 子网的广播地址发一份 <c>CHERT-DISCOVER-1</c>，
/// 运行中的MCLCS 启动器收到后单播回一份自身信息。广播被禁时可用
/// <see cref="ProbeDirectAsync"/> 手动指定 IP 探测。</para>
/// </summary>
public static class LanPeerDiscovery
{
    /// <summary>
    /// 广播探测。返回去重后的对等体列表；失败或无响应时返回空列表，不抛异常。
    /// </summary>
    public static async Task<List<LanPeer>> ProbeAsync(
        int timeoutMs = 1200,
        string deviceName = "",
        CancellationToken ct = default)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var query = new LanDiscoverQuery { Nonce = nonce, From = deviceName };
        var payload = Encoding.UTF8.GetBytes(LanPeerProtocol.Serialize(query));

        var found = new Dictionary<string, LanPeer>(StringComparer.OrdinalIgnoreCase);
        UdpClient? udp = null;

        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.EnableBroadcast = true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Math.Max(200, timeoutMs));

            // 先发后收：发给所有子网广播地址 + 受限广播
            foreach (var bcast in GetBroadcastAddresses())
            {
                try { await udp.SendAsync(payload, new IPEndPoint(bcast, LanPeerProtocol.DiscoveryPort)).ConfigureAwait(false); }
                catch { /* 单个网卡失败不影响其它 */ }
            }

            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }

                var reply = LanPeerProtocol.Deserialize<LanDiscoverReply>(Encoding.UTF8.GetString(result.Buffer));
                if (reply is null) continue;
                if (!string.Equals(reply.Magic, LanPeerProtocol.Magic, StringComparison.Ordinal)) continue;
                if (!string.Equals(reply.Nonce, nonce, StringComparison.Ordinal)) continue;
                if (reply.Peer is null) continue;

                // 回声校验通过后才信任对方报告的端口；地址一律以实际来源为准
                var peer = reply.Peer;
                peer.Address = result.RemoteEndPoint.Address.ToString();
                if (peer.Port is <= 0 or > 65535) peer.Port = LanPeerProtocol.CommandPort;
                peer.LastSeen = DateTime.Now;

                found[peer.Endpoint] = peer;
            }
        }
        catch
        {
            // 无网卡 / 沙箱 / 权限不足：返回已收到的部分
        }
        finally
        {
            try { udp?.Dispose(); } catch { /* ignore */ }
        }

        return found.Values
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>定向探测：广播不可用时手动指定对方 IP。</summary>
    public static async Task<LanPeer?> ProbeDirectAsync(
        string ip,
        int port = LanPeerProtocol.CommandPort,
        int timeoutMs = 1500)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;

        var nonce = Guid.NewGuid().ToString("N");
        var payload = Encoding.UTF8.GetBytes(
            LanPeerProtocol.Serialize(new LanDiscoverQuery { Nonce = nonce }));

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = Math.Max(300, timeoutMs);
            await udp.SendAsync(payload, new IPEndPoint(addr, LanPeerProtocol.DiscoveryPort)).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeoutMs);
            var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var reply = LanPeerProtocol.Deserialize<LanDiscoverReply>(Encoding.UTF8.GetString(result.Buffer));
            if (reply is null || !string.Equals(reply.Nonce, nonce, StringComparison.Ordinal)) return null;
            if (reply.Peer is null) return null;

            reply.Peer.Address = result.RemoteEndPoint.Address.ToString();
            reply.Peer.Port = port;
            reply.Peer.LastSeen = DateTime.Now;
            return reply.Peer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>枚举本机所有可用 IPv4 子网的广播地址（含受限广播）。</summary>
    public static List<IPAddress> GetBroadcastAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = ni.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;

                    var mask = uni.IPv4Mask;
                    if (mask is null || mask.Equals(IPAddress.Any)) continue;

                    var ipBytes = uni.Address.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (var i = 0; i < 4; i++) bcast[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 0xFF));
                    list.Add(new IPAddress(bcast));
                }
            }
        }
        catch
        {
            // 枚举失败时仍有受限广播兜底
        }

        if (list.Count == 0) list.Add(IPAddress.Broadcast);
        return list;
    }
}

/// <summary>
/// 应答端：常驻监听发现端口，收到探测即回一份自身信息。
/// <para>自身信息由回调 <see cref="Func{LanPeer}"/> 提供，避免 Core 层依赖 App 层状态。</para>
/// </summary>
public sealed class LanPeerBeacon : IDisposable
{
    private readonly Func<LanPeer> _selfProvider;
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>是否成功开始监听（失败通常是端口被占用或防火墙拒绝）。</summary>
    public bool Listening { get; }

    /// <summary>启动失败的原因（用于设置页提示）。</summary>
    public string ErrorMessage { get; } = "";

    public LanPeerBeacon(Func<LanPeer> selfProvider, int port = LanPeerProtocol.DiscoveryPort)
    {
        _selfProvider = selfProvider;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            Listening = true;
            _ = Task.Run(ListenLoopAsync);
        }
        catch (Exception ex)
        {
            Listening = false;
            ErrorMessage = ex.Message;
            try { _udp?.Dispose(); } catch { /* ignore */ }
            _udp = null;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }

            try
            {
                var query = LanPeerProtocol.Deserialize<LanDiscoverQuery>(Encoding.UTF8.GetString(result.Buffer));
                if (query is null) continue;
                if (!string.Equals(query.Magic, LanPeerProtocol.Magic, StringComparison.Ordinal)) continue;

                var self = _selfProvider();
                var reply = new LanDiscoverReply { Nonce = query.Nonce, Peer = self };
                var bytes = Encoding.UTF8.GetBytes(LanPeerProtocol.Serialize(reply));
                await _udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch
            {
                // 单个报文处理失败不影响后续
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _udp?.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
