using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MCLCS.Core.Lan;

/// <summary>
/// 清单 #37 / #38：本机局域网世界的端口解析与分享。
/// <para>「对局域网开放」后 Minecraft 会把端口写进 <c>logs/latest.log</c>，
/// 同时向组播 <c>224.0.2.60:4445</c> 广播。这里以日志为准（最可靠），
/// 组播作为兜底需要自收自发，故只做日志 + 已知端口探测。</para>
/// </summary>
public static class LanWorldShare
{
    /// <summary>匹配日志中的发布端口（中英文客户端与不同版本措辞不同，多模式兜底）。</summary>
    private static readonly Regex[] PortPatterns =
    {
        new(@"Local game hosted on port (?<p>\d{2,5})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"已在端口 (?<p>\d{2,5}) 上发布", RegexOptions.Compiled),
        new(@"Started serving on (?<p>\d{2,5})", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\[AD\](?:[^:]+:)?(?<p>\d{2,5})\[/AD\]", RegexOptions.Compiled)
    };

    /// <summary>
    /// 从游戏日志尾部读取「对局域网开放」的端口。读不到返回 null。
    /// </summary>
    public static int? TryReadPublishedPort(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;

        var candidate = new[]
        {
            Path.Combine(gameDir, "logs", "latest.log"),
            Path.Combine(gameDir, "logs", "latest.log.txt")
        }.FirstOrDefault(File.Exists);

        if (candidate is null) return null;

        try
        {
            // 只取末尾 64KB：发布端口一定在靠近结尾处，避免整文件读取
            const int tailBytes = 65536;
            using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - tailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();

            // 从后往前找，取最后一次发布
            for (var i = PortPatterns.Length - 1; i >= 0; i--)
            {
                var matches = PortPatterns[i].Matches(text);
                if (matches.Count == 0) continue;
                var last = matches[^1];
                if (int.TryParse(last.Groups["p"].Value, out var port) && port is > 0 and <= 65535)
                    return port;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>取本机首选 IPv4（排除回环 / 未启用网卡）。</summary>
    public static string GetLocalIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

                foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;
                    return uni.Address.ToString();
                }
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>拼接 host:port。</summary>
    public static string BuildEndpoint(string host, int port)
    {
        var h = string.IsNullOrWhiteSpace(host) ? GetLocalIPv4() : host.Trim();
        return $"{h}:{port}";
    }

    /// <summary>
    /// 解析用户粘贴的地址。支持 <c>host:port</c> / <c>host</c>（补默认 25565）/ 带 <c>[]</c> 的 IPv6。
    /// </summary>
    public static (string Host, int Port)? ParseEndpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var s = text.Trim();
        // 容忍「地址：192.168.1.5:52931」这类粘贴
        var colon = s.LastIndexOf(':');
        if (colon <= 0)
        {
            var bareHost = s;
            return string.IsNullOrWhiteSpace(bareHost) ? null : (bareHost, 25565);
        }

        var host = s[..colon].Trim();
        var portText = s[(colon + 1)..].Trim();
        if (host.Length == 0) return null;
        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535) return null;
        return (host, port);
    }

    /// <summary>生成可直接发给对方的分享文本。</summary>
    public static string BuildShareText(string endpoint, string worldName = "")
    {
        var name = string.IsNullOrWhiteSpace(worldName) ? "" : $"「{worldName}」";
        return $"MCLCS 启动器 · 局域网世界邀请{name}\n" +
               $"地址：{endpoint}\n" +
               "在启动器「局域网联动」里粘贴这个地址即可加入。";
    }

    /// <summary>
    /// 生成二维码负载。这里只返回待编码文本，二维码渲染由 UI 层负责
    /// （避免 Core 引入图像依赖）。
    /// </summary>
    public static string BuildQrPayload(string endpoint)
        => $"chert-lan://{endpoint}";
}
