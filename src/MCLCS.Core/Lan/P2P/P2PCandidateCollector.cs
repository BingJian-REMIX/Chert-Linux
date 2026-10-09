using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// 候选地址：把自己「可能被对方连到的方式」列出来，全部塞进握手码。
/// <para>
/// 打洞的本质是双方同时朝对方的地址发包，在 NAT 上各留一个洞。
/// 所以地址要尽量全：本机内网 IPv4、IPv6（有 IPv6 时通常根本不用打洞）、
/// UPnP 映射出来的公网地址（对称 NAT 的唯一解），以及无处一时用户手填的公网 IP。
/// </para>
/// </summary>
public sealed class P2PCandidate
{
    public IPEndPoint EndPoint { get; init; } = new(IPAddress.Any, 0);

    /// <summary>来源：<c>lan</c>（内网）/ <c>ipv6</c> / <c>upnp</c>（UPnP 映射）/ <c>manual</c>（手填）。</summary>
    public string Kind { get; init; } = "lan";

    /// <summary><c>udp://1.2.3.4:47810</c>（IPv6 带方括号）。</summary>
    public string Uri => FormatUri(EndPoint);

    public string Label => Kind switch
    {
        "stun" => "STUN 公网",
        "ipv6" => "IPv6",
        "upnp" => "UPnP 公网",
        "manual" => "手填公网",
        _ => "内网"
    };

    /// <summary>把地址写成 <c>udp://host:port</c>；IPv6 必须包方括号，否则 URI 解析会把冒号吃掉。</summary>
    public static string FormatUri(IPEndPoint ep)
        => ep.AddressFamily == AddressFamily.InterNetworkV6
            ? $"udp://[{ep.Address}]:{ep.Port}"
            : $"udp://{ep.Address}:{ep.Port}";

    /// <summary>解析 <c>udp://host:port</c>；失败返回 null（不要抛，儿候选里混进脏数据会拖垮整轮打洞）。</summary>
    public static IPEndPoint? TryParse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)) s = s["udp://".Length..];
        if (s.Length == 0) return null;

        var port = P2POffer.DefaultUdpPort;
        string host = s;

        if (s[0] == '[')                       // [2001:db8::1]:47810
        {
            var close = s.IndexOf(']');
            if (close < 0) return null;
            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.StartsWith(':')) rest = rest[1..];
            if (rest.Length > 0 && !int.TryParse(rest, out port)) return null;
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon > 0)
            {
                host = s[..colon];
                if (!int.TryParse(s[(colon + 1)..], out port)) return null;
            }
        }

        if (port is < 1 or > 65535) return null;
        if (!IPAddress.TryParse(host, out var ip)) return null;
        return new IPEndPoint(ip, port);
    }
}

/// <summary>一次收集的全部产出：候选地址 + 顺带摸出来的 NAT 行为。</summary>
public sealed class P2PCandidateSet
{
    public IReadOnlyList<P2PCandidate> Candidates { get; init; } = Array.Empty<P2PCandidate>();

    /// <summary>STUN 探测结论（没做 STUN 时为 null）。它决定这套地址到底有没有用。</summary>
    public NatProbeResult? Nat { get; init; }
}

/// <summary>本机候选地址的收集器。</summary>
public static class P2PCandidateCollector
{
    /// <summary>
    /// 收集候选地址。
    /// <para>
    /// 顺序有讲究：STUN 排在最前，因为它要占用 <paramref name="localPort"/> 这个 UDP 端口
    /// ——NAT 的映射按 (内网 ip:port) 记账，只有从真正打洞的那个端口发查询，问出来的公网地址才有意义。
    /// 探完立刻释放端口，后面的 UPnP 与打洞才不会撞车。
    /// </para>
    /// <para>「UPnP 要不要默认开」：这是唯一会在路由器上留动作的分支——它会临时开一个 UDP 口。
    /// 只在用户明确发起广域网直连时才调用（不是开启动器就偷偷开），且失败完全不影响其它候选。</para>
    /// </summary>
    /// <param name="localPort">本机打洞用的 UDP 端口。</param>
    /// <param name="externalIp">用户手填的公网 IP（可空）。</param>
    /// <param name="tryUpnp">是否尝试 UPnP 端口映射。</param>
    /// <param name="upnpTimeout">UPnP 超时（宽带环境下 1.5 秒足够，太短会错过慢的路由器）。</param>
    /// <param name="tryStun">是否 STUN 探测。关掉的话跨网段基本连不上——那份邀请码里只剩下内网地址。</param>
    /// <param name="stunServers">自定义 STUN 服务器；不传就用内置的公开列表。</param>
    public static async Task<P2PCandidateSet> CollectAsync(
        int localPort,
        string? externalIp = null,
        bool tryUpnp = true,
        TimeSpan? upnpTimeout = null,
        bool tryStun = true,
        IReadOnlyList<string>? stunServers = null,
        CancellationToken ct = default)
    {
        // 公网可达的排前面：跨网段时只有它们有用，而 UPnP 映射出来的口是真能收包的，给它最高优先级
        var wide = new List<P2PCandidate>();
        var local = new List<P2PCandidate>();

        NatProbeResult? nat = null;
        if (tryStun)
        {
            nat = await NatProbe.ProbeAsync(localPort, stunServers, null, ct).ConfigureAwait(false);
            if (nat.Ok && nat.ServerReflexive is { } reflexive)
                wide.Add(new P2PCandidate { EndPoint = reflexive, Kind = "stun" });
        }

        if (tryUpnp)
        {
            var upnp = await UpnpPortMapper.TryMapAsync(localPort, upnpTimeout ?? TimeSpan.FromSeconds(1.5), ct)
                                           .ConfigureAwait(false);
            if (upnp is not null) wide.Insert(0, upnp);
        }

        foreach (var ip in LocalAddresses())
        {
            var candidate = new P2PCandidate
            {
                EndPoint = new IPEndPoint(ip, localPort),
                Kind = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "lan"
            };
            // IPv6 通常没有 NAT，和公网地址同等地位；内网 IPv4 只在同一个路由器下有用
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) wide.Add(candidate);
            else local.Add(candidate);
        }

        var all = new List<P2PCandidate>(wide);
        all.AddRange(local);

        if (P2PCandidate.TryParse(externalIp) is { } manual)
            all.Add(new P2PCandidate { EndPoint = manual, Kind = "manual" });

        return new P2PCandidateSet { Candidates = all, Nat = nat };
    }

    /// <summary>
    /// 本机可用地址：IPv4 排前面（多数家庭网络还得靠它），有公网 IPv6 也一并带上
    /// —— IPv6 通常没有 NAT，能直接用就不必打洞。排除回环 / 链路本地 / 站点本地。
    /// </summary>
    public static List<IPAddress> LocalAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) continue;  // 跨网段没用
                    }
                    result.Add(ip);
                }
            }
        }
        catch
        {
            // 取网卡失败时调用方会退回「手填」
        }

        // IPv4 优先：绝大多数家庭网络还是靠它
        return result.OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }

    /// <summary>
    /// 对方的地址里有没有一个和本机在同一个局域网网段。
    /// <para>
    /// 打洞失败时这条判断值千金：同一个网段根本不需要打洞，直接走局域网联动就行。
    /// 虚拟局域网（ZeroTier / Tailscale 之类）建的网卡也算在内 —— 它们的地址同样会出现在网卡列表里。
    /// </para>
    /// </summary>
    public static bool SharesLanWith(IEnumerable<IPEndPoint> peerEndPoints)
    {
        var peers = peerEndPoints.Where(x => x.AddressFamily == AddressFamily.InterNetwork).ToList();
        if (peers.Count == 0) return false;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask is null || IPAddress.IsLoopback(ua.Address)) continue;

                    var mine = NetworkOf(ua.Address, mask);
                    foreach (var peer in peers)
                        if (NetworkOf(peer.Address, mask).Equals(mine)) return true;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>本机有没有非链路本地的 IPv6（有它通常就不需要打洞）。</summary>
    public static bool HasUsableIpv6()
        => LocalAddresses().Any(x => x.AddressFamily == AddressFamily.InterNetworkV6);

    private static IPAddress NetworkOf(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var network = new byte[4];
        for (var i = 0; i < 4; i++) network[i] = (byte)(a[i] & m[i]);
        return new IPAddress(network);
    }
}

/// <summary>
/// 极简 UPnP 端口映射（IGD v1 / WANIPConnection）。
/// <para>
/// 为什么不用 NuGet 的 Open.NAT：这里只需要「发一个 M-SEARCH + 三个 SOAP 请求」，
/// 引入一个包为的是 200 行 HTTP/XML。而且这是**尽力而为**的功能 —— 失败、路由器不支持、
/// 或者用户在路由器里禁了 UPnP，都只是少一个候选地址，不应该让整条直连流程崩掉。
/// </para>
/// </summary>
public static class UpnpPortMapper
{
    /// <summary>默认的描述写进路由器，方便用户在路由器后台里认出是谁开的口。</summary>
    private const string Description = "Chert Launcher P2P";

    private const string StIgd = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
    private const string StWanIp = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private const string StWanPpp = "urn:schemas-upnp-org:service:WANPPPConnection:1";

    /// <summary>
    /// 尝试在路由器上把本机 UDP 端口映射出去，返回外部访问地址；失败返回 null。
    /// 整个过程超时由 <paramref name="timeout"/> 控制 —— 别让用户盯着「正在连接」等半天。
    /// </summary>
    public static async Task<P2PCandidate?> TryMapAsync(int localPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(1.5);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);

            var location = await DiscoverAsync(cts.Token).ConfigureAwait(false);
            if (location is null) return null;

            var control = await FindControlUrlAsync(location, cts.Token).ConfigureAwait(false);
            if (control is null) return null;

            using var http = new HttpClient { Timeout = budget };
            var externalIp = await GetExternalIpAsync(control, http, cts.Token).ConfigureAwait(false);
            if (externalIp is null) return null;

            var ok = await AddPortMappingAsync(control, http, localPort, cts.Token).ConfigureAwait(false);
            if (!ok) return null;

            return new P2PCandidate
            {
                EndPoint = new IPEndPoint(externalIp, localPort),
                Kind = "upnp"
            };
        }
        catch
        {
            // UPnP 失败是家常便饭（多网卡、虚拟网卡、路由器关了 SSDP、Windows 防火墙拦了发现包）
            return null;
        }
    }

    /// <summary>
    /// 撤掉刚才加的那条映射。不做的话路由器上会一直留着一个 UDP 口，
    /// 用户换一台机器、或者下次换了个端口再连，都会撞上这条陈年老映射。
    /// 失败无所谓 —— 映射本身带租约，到期会自动没。
    /// </summary>
    public static async Task<bool> TryDeleteAsync(int localPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(1.5);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);

            var location = await DiscoverAsync(cts.Token).ConfigureAwait(false);
            if (location is null) return false;

            var control = await FindControlUrlAsync(location, cts.Token).ConfigureAwait(false);
            if (control is null) return false;

            using var http = new HttpClient { Timeout = budget };
            return await DeletePortMappingAsync(control, http, localPort, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    // ===== SSDP 发现 =====

    private static async Task<string?> DiscoverAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 1200;
        try { udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }

        var request = System.Text.Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: " + StIgd + "\r\n\r\n");
        await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900))
                 .ConfigureAwait(false);

        // 多网卡会收到多份：取第一个能用即可
        while (!ct.IsCancellationRequested)
        {
            var receive = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receive, Task.Delay(1000, ct)).ConfigureAwait(false);
            if (completed != receive) return null;

            var text = System.Text.Encoding.ASCII.GetString(receive.Result.Buffer);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                if (!string.Equals(parts[0].Trim(), "location", StringComparison.OrdinalIgnoreCase)) continue;
                var url = parts[1].Trim();
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            }
        }
        return null;
    }

    // ===== 设备描述 → 控制 URL =====

    private static async Task<Uri?> FindControlUrlAsync(string location, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        var xml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
        var root = XDocument.Parse(xml);
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";

        foreach (var service in root.Descendants(ns + "service"))
        {
            var type = service.Element(ns + "serviceType")?.Value ?? "";
            if (!string.Equals(type, StWanIp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, StWanPpp, StringComparison.OrdinalIgnoreCase)) continue;

            var controlPath = service.Element(ns + "controlURL")?.Value;
            if (string.IsNullOrWhiteSpace(controlPath)) continue;

            if (Uri.TryCreate(new Uri(location), controlPath, out var absolute)) return absolute;
        }
        return null;
    }

    // ===== SOAP =====

    private static async Task<string> PostSoapAsync(Uri control, HttpClient http, string serviceType, string action, string body, CancellationToken ct)
    {
        var soap = "<?xml version=\"1.0\"?>\r\n" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                   "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   "<s:Body>" + body + "</s:Body></s:Envelope>";

        using var content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = content };
        request.Headers.Add("SOAPACTION", $"\"{serviceType}#{action}\"");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IPAddress?> GetExternalIpAsync(Uri control, HttpClient http, CancellationToken ct)
    {
        const string body = "<u:GetExternalIPAddress xmlns:u=\"" + StWanIp + "\"/>";
        var text = await PostSoapAsync(control, http, StWanIp, "GetExternalIPAddress", body, ct).ConfigureAwait(false);

        var doc = XDocument.Parse(text);
        var value = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "NewExternalIPAddress")?.Value;
        return IPAddress.TryParse((value ?? "").Trim(), out var ip) ? ip : null;
    }

    private static async Task<bool> AddPortMappingAsync(Uri control, HttpClient http, int localPort, CancellationToken ct)
    {
        var address = P2PCandidateCollector.LocalAddresses()
                          .FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? IPAddress.Loopback;

        var ttl = (int)Math.Clamp(P2POffer.Ttl.TotalSeconds, 60, 7200);
        var body =
            "<u:AddPortMapping xmlns:u=\"" + StWanIp + "\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{localPort}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>" +
            $"<NewInternalPort>{localPort}</NewInternalPort>" +
            $"<NewInternalClient>{address}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{Description}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{ttl}</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        var text = await PostSoapAsync(control, http, StWanIp, "AddPortMapping", body, ct).ConfigureAwait(false);
        // 没有 Fault 就是成功：部分路由器的 SOAP 回包不带 Body，只看 HTTP 200
        return !text.Contains("Fault", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> DeletePortMappingAsync(Uri control, HttpClient http, int localPort, CancellationToken ct)
    {
        var body =
            "<u:DeletePortMapping xmlns:u=\"" + StWanIp + "\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{localPort}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>" +
            "</u:DeletePortMapping>";

        var text = await PostSoapAsync(control, http, StWanIp, "DeletePortMapping", body, ct).ConfigureAwait(false);
        return !text.Contains("Fault", StringComparison.OrdinalIgnoreCase);
    }
}
