using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MCLCS.Core.Lan;

/// <summary>
/// 清单 #36 ~ #39：局域网指令服务（自实现极简 HTTP/1.1 over <see cref="TcpListener"/>）。
/// <para>
/// 为什么不用 <c>HttpListener</c>：它监听非 loopback 前缀（<c>http://+:47625/</c>）
/// 必须先执行 <c>netsh http add urlacl</c>，需要管理员且是永久注册。
/// 自实现 HTTP 只需 Windows 防火墙弹一次「允许访问网络」——用户点一下即可，无需提权。
/// </para>
/// <para>安全模型：局域网不可信，除 Ping / Pair 外的所有指令必须携带有效会话 token。</para>
/// </summary>
public sealed class LanCommandServer : IDisposable
{
    private readonly Func<LanCommand, LanCommandResult> _handler;
    private readonly LanSessionStore _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private bool _disposed;

    /// <summary>是否已成功监听。</summary>
    public bool Listening { get; private set; }

    /// <summary>启动失败原因（端口占用 / 防火墙拒绝等）。</summary>
    public string ErrorMessage { get; private set; } = "";

    /// <summary>实际监听端口。</summary>
    public int Port { get; }

    /// <summary>会话 / 配对状态机（供 UI 生成配对码）。</summary>
    public LanSessionStore Sessions => _sessions;

    public LanCommandServer(Func<LanCommand, LanCommandResult> handler, int port = LanPeerProtocol.CommandPort)
    {
        _handler = handler;
        Port = port;
    }

    /// <summary>开始监听。失败时置 <see cref="ErrorMessage"/> 并返回 false。</summary>
    public bool Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            Listening = true;
            _ = Task.Run(AcceptLoopAsync);
            return true;
        }
        catch (Exception ex)
        {
            Listening = false;
            ErrorMessage = ex.Message;
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener is not null)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
            var result = Dispatch(request);
            await WriteHttpResponseAsync(stream, result).ConfigureAwait(false);
        }
        catch
        {
            // 单个连接失败不影响服务
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>解析一个完整的 HTTP 请求（请求行 + 头 + 可选 body）。</summary>
    internal static async Task<HttpRequestText> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var total = new List<byte>(4096);
        var headerEnd = -1;

        while (headerEnd < 0 && total.Count < 65536)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read <= 0) break;
            total.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = IndexOf(total, "\r\n\r\n");
        }

        if (headerEnd < 0) return new HttpRequestText("", "", "");

        var headerText = Encoding.UTF8.GetString(total.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0] : "";
        var parts = requestLine.Split(' ');
        var path = parts.Length > 1 ? parts[1] : "";

        var contentLength = 0;
        foreach (var line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out var v)) contentLength = v;
        }

        var bodyStart = headerEnd + 4;
        var body = "";
        if (contentLength > 0)
        {
            while (total.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) break;
                total.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            var available = Math.Min(contentLength, total.Count - bodyStart);
            if (available > 0) body = Encoding.UTF8.GetString(total.ToArray(), bodyStart, available);
        }

        return new HttpRequestText(requestLine, path, body);
    }

    private static int IndexOf(List<byte> data, string pattern)
    {
        var pat = Encoding.UTF8.GetBytes(pattern);
        for (var i = 0; i + pat.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pat.Length; j++)
            {
                if (data[i + j] != pat[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>按路径分发。所有路由统一为 POST /chert/&lt;action&gt;。</summary>
    internal LanCommandResult Dispatch(HttpRequestText request)
    {
        var path = request.Path.Split('?')[0];
        if (path.Equals("/chert/ping", StringComparison.OrdinalIgnoreCase))
            return LanCommandResult.Success("pong");

        var cmd = LanPeerProtocol.Deserialize<LanCommand>(request.Body);
        if (cmd is null)
            return LanCommandResult.Fail("error", "bad request body");

        if (path.EndsWith("/pair", StringComparison.OrdinalIgnoreCase))
        {
            var token = _sessions.Redeem(cmd.Token);
            return token is null
                ? LanCommandResult.Fail("unauthorized", "pair code invalid or expired")
                : new LanCommandResult { Ok = true, Status = "ok", Message = "paired", Token = token };
        }

        if (!_sessions.IsAuthorized(cmd.Token))
            return LanCommandResult.Fail("unauthorized", "session token required");

        return _handler(cmd);
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, LanCommandResult result)
    {
        var json = LanPeerProtocol.Serialize(result);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Listening = false;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

/// <summary>极简 HTTP 请求文本（仅解析到够用为止）。</summary>
internal sealed record HttpRequestText(string RequestLine, string Path, string Body);

/// <summary>指令客户端：向对等体发一条指令。</summary>
public static class LanCommandClient
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>连通性测试（无需授权）。</summary>
    public static async Task<bool> PingAsync(string baseUrl)
    {
        try
        {
            var res = await Http.GetAsync($"{baseUrl}/chert/ping").ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用配对码换取会话 token。</summary>
    public static async Task<string?> PairAsync(string baseUrl, string pairCode)
        => await PostAsync(baseUrl, "pair", new LanCommand { Kind = LanCommandKind.Pair, Token = pairCode })
            .ContinueWith(t => t.Result is { Ok: true } r ? r.Token : null).ConfigureAwait(false);

    /// <summary>发送一条指令，返回响应；网络异常时返回 null。</summary>
    public static async Task<LanCommandResult?> SendAsync(string baseUrl, LanCommand command)
        => await PostAsync(baseUrl, KindToPath(command.Kind), command).ConfigureAwait(false);

    private static string KindToPath(LanCommandKind kind) => kind switch
    {
        LanCommandKind.Info => "info",
        LanCommandKind.OpenLan => "lan/open",
        LanCommandKind.JoinLan => "lan/join",
        _ => "ping"
    };

    private static async Task<LanCommandResult?> PostAsync(string baseUrl, string path, LanCommand command)
    {
        try
        {
            var json = LanPeerProtocol.Serialize(command);
            using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
            var res = await Http.PostAsync($"{baseUrl}/chert/{path}", content).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return LanPeerProtocol.Deserialize<LanCommandResult>(text);
        }
        catch
        {
            return null;
        }
    }
}
