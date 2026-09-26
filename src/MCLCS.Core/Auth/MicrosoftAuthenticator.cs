using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MCLCS.Core.Auth;

/// <summary>
/// Microsoft 账号认证：OAuth2 设备代码流（device code flow）→ Xbox Live → XSTS → Minecraft 令牌。
/// 设备代码流无需任何 redirect_uri（回跳地址），因此不涉及浏览器回跳捕获或手动粘贴，兼容性最好。
/// client_id 可由用户在设置中覆盖；留空时使用内置官方启动器 client_id（已加入 XboxLive 白名单）。
/// </summary>
public class MicrosoftAuthenticator : IAuthenticator
{
    // 设备代码流使用 Microsoft 身份平台 v2.0 端点（consumers = 个人账户租户）。
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    // Minecraft 所需的 Microsoft 作用域。
    private const string Scope = "XboxLive.signin offline_access";

    // 内置默认 client_id（微软官方启动器，已在 XboxLive 白名单中）。用户可在设置中替换为自己的 Azure 应用。
    private const string DefaultClientId = "00000000402b5328";

    private readonly HttpClient _client;
    private readonly string _clientId;
    private readonly Action<string>? _onUserCode;

    /// <param name="client">HttpClient 实例</param>
    /// <param name="clientId">Azure 应用的 OAuth client_id（可空，留空使用内置默认）</param>
    /// <param name="onUserCode">设备代码提示回调（向用户展示验证码与验证网址）</param>
    public MicrosoftAuthenticator(HttpClient client,
        string? clientId = null,
        Action<string>? onUserCode = null)
    {
        _client = client;
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Chert/2.5");
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId!;
        _onUserCode = onUserCode;
    }

    /// <summary>完整的 MS 认证流程。</summary>
    public async Task<AuthSession> AuthenticateAsync(string? username, CancellationToken ct = default)
    {
        var msToken = await DeviceCodeLoginAsync(ct);

        // 后续 Xbox Live / XSTS / Minecraft 流程与授权方式无关。
        var xblToken = await XboxLiveAuthAsync(msToken, ct);
        var (xstsToken, userHash) = await XstsAuthAsync(xblToken, ct);
        var mcToken = await MinecraftAuthAsync(xstsToken, userHash, ct);
        var (uuid, name) = await GetProfileAsync(mcToken, ct);

        return new AuthSession
        {
            Username = name,
            Uuid = uuid,
            AccessToken = mcToken,
            UserType = "msa"
        };
    }

    // ---- 设备代码流（device code flow）----
    private async Task<string> DeviceCodeLoginAsync(CancellationToken ct)
    {
        // 1) 申请设备码
        var devResp = await PostFormAsync(DeviceCodeUrl, new()
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope
        }, ct);

        var userCode = devResp.GetProperty("user_code").GetString()
                      ?? throw new Exception("微软未返回设备代码（user_code）。");
        var deviceCode = devResp.GetProperty("device_code").GetString()
                         ?? throw new Exception("微软未返回设备代码（device_code）。");
        var verificationUri = devResp.GetProperty("verification_uri").GetString()
                              ?? "https://microsoft.com/devicelogin";
        var interval = devResp.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
        var expiresIn = devResp.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 900;

        // 2) 打开浏览器到验证页
        TryOpenBrowser(verificationUri);

        // 3) 向用户展示验证码
        var sb = new StringBuilder();
        sb.AppendLine("微软账号登录 - 设备代码");
        sb.AppendLine();
        sb.AppendLine($"请在浏览器打开：{verificationUri}");
        sb.AppendLine($"并输入代码：{userCode}");
        sb.AppendLine();
        sb.AppendLine($"（代码有效期约 {Math.Max(1, expiresIn / 60)} 分钟。输完代码完成登录后，启动器会自动继续。）");
        _onUserCode?.Invoke(sb.ToString());

        // 4) 轮询换取令牌
        return await PollDeviceTokenAsync(deviceCode, interval, expiresIn, ct);
    }

    private async Task<string> PollDeviceTokenAsync(string deviceCode, int interval, int expiresIn, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval * 1000, ct);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = deviceCode
            });

            using var resp = await _client.PostAsync(TokenUrl, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("access_token").GetString()
                       ?? throw new Exception("微软未返回访问令牌（access_token）。");
            }

            // 解析错误码，决定继续轮询还是失败
            string? err = null;
            try { err = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString(); }
            catch { /* 忽略解析失败，按通用错误处理 */ }

            if (err == "authorization_pending")
                continue;                       // 用户尚未完成登录，继续轮询
            if (err == "slow_down")
            {
                interval += 5;                 // 被要求放慢轮询节奏
                continue;
            }

            // authorization_declined / expired_token / access_denied / bad_client 等 → 直接失败
            throw new Exception($"微软登录失败：{err ?? body}");
        }

        throw new TimeoutException("微软设备码登录超时，请重试。");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo(url) { UseShellExecute = true };
            Process.Start(psi);
        }
        catch
        {
            // 自动打开失败则依赖回调提示用户手动访问
        }
    }

    // ---- 表单 POST（成功返回 JSON；失败抛异常并附带响应体）----
    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> data, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(data);
        using var resp = await _client.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} ({resp.StatusCode}): {body}");
        }
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    // ---- 后续 Xbox / XSTS / Minecraft 流程 ----
    private async Task<string> XboxLiveAuthAsync(string msAccessToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(XboxAuthUrl, content, ct);
        resp.EnsureSuccessStatusCode();
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        return el.GetProperty("Token").GetString()!;
    }

    private async Task<(string Token, string UserHash)> XstsAuthAsync(string xblToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(XstsAuthUrl, content, ct);
        resp.EnsureSuccessStatusCode();
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        return (el.GetProperty("Token").GetString()!, el.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!);
    }

    private async Task<string> MinecraftAuthAsync(string xstsToken, string userHash, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(McLoginUrl, content, ct);
        resp.EnsureSuccessStatusCode();
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        return el.GetProperty("access_token").GetString()!;
    }

    private async Task<(string Uuid, string Name)> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Add("Authorization", $"Bearer {mcToken}");
        using var resp = await _client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        return (el.GetProperty("id").GetString()!, el.GetProperty("name").GetString()!);
    }
}
