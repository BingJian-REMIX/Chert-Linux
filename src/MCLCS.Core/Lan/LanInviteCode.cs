using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MCLCS.Core.Lan;

/// <summary>
/// 可复制 / 可粘贴的「邀请码」（前缀 <c>CHERT1:</c>）。
/// <para>
/// 背景：此前联动页只能手抄 <c>host:port</c> 给对方（"读取本机端口"后自己拼地址），
/// 跨网段或隔着聊天软件传地址时容易抄错、漏端口。这里把「世界地址 + 设备名 + 世界名 +
/// MC 版本 + 配对码 + 过期时间」打包成一段短文本，对方整段粘贴即可加入。
/// </para>
/// <para>
/// 编码：自描述紧凑二进制（tag + varint 长度 + UTF-8 值）→ Deflate → Base64URL，
/// 加 <c>CHERT1:</c> 前缀便于识别与整段复制。典型长度 120～190 字符，二维码也装得下。
/// <b>为什么不用 CBOR</b>：<c>System.Formats.Cbor</c> 是独立 NuGet 包而非框架内置，
/// 本仓库对新增依赖很克制（绿色版、离线可用优先），而这里只需编码 8 个字段，
/// 自写 tag-length-value 反而更短、可离线验证。
/// </para>
/// <para>
/// 指纹：对「不含指纹字段」的载荷做 SHA256 取 6 位十进制。它<b>不是</b>安全机制
/// （地址本来就要给对方，且走的是不可信通道），作用是**检错** ——
/// 聊天软件截断、多复制一个字符、手动改端口都会被立刻发现，而不是连到错地址上干等。
/// 真正的鉴权仍靠配对码（<see cref="LanSessionStore"/>）。
/// </para>
/// </summary>
public sealed class LanInviteCode
{
    /// <summary>邀请码前缀（识别时大小写不敏感）。</summary>
    public const string Prefix = "CHERT1:";

    /// <summary>有效期：与配对码一致，10 分钟。</summary>
    public static readonly TimeSpan Ttl = LanPeerProtocol.PairCodeTtl;

    /// <summary>协议版本。</summary>
    public const int CurrentVersion = 1;

    // 载荷魔数：解错东西时能立刻识别，而不是把垃圾当字段读
    private const byte Magic0 = 0xC1;
    private const byte Magic1 = 0x07;

    // 字段 tag（顺序固定：指纹依赖字节序列）
    private const byte TagExp = 0x01;
    private const byte TagKind = 0x02;
    private const byte TagEndpoint = 0x03;
    private const byte TagDevice = 0x04;
    private const byte TagWorld = 0x05;
    private const byte TagMcVersion = 0x06;
    private const byte TagPairCode = 0x07;
    private const byte TagFingerprint = 0x0F;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>类型：<c>invite</c>（预留 <c>offer</c> / <c>answer</c> 给将来的广域网握手）。</summary>
    public string Kind { get; init; } = "invite";

    /// <summary>过期时间（Unix 秒）。</summary>
    public long ExpiresAt { get; init; }

    /// <summary>世界地址 host:port。</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>房主设备名（给对方看是谁发的）。</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>世界名（可空）。</summary>
    public string WorldName { get; init; } = "";

    /// <summary>MC 版本 / 加载器（可空，用于提示版本不一致）。</summary>
    public string McVersion { get; init; } = "";

    /// <summary>配对码（可空）：带上它对方可以一步完成「加入 + 配对」。</summary>
    public string PairCode { get; init; } = "";

    /// <summary>6 位检错指纹，解码时重算比对。</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>是否已过期。</summary>
    public bool IsExpired(DateTimeOffset? now = null)
        => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() > ExpiresAt;

    /// <summary>剩余有效时间（已过期返回零）。</summary>
    public TimeSpan Remaining(DateTimeOffset? now = null)
    {
        var left = ExpiresAt - (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return left > 0 ? TimeSpan.FromSeconds(left) : TimeSpan.Zero;
    }

    /// <summary>生成邀请码文本（含前缀）。</summary>
    public string Encode()
    {
        // 两遍序列化：先用不含指纹的载荷算指纹，再把指纹写进去。
        // 解码端按同样顺序复算，任何字段被改动都会对不上。
        var fingerprint = ComputeFingerprint(Serialize(null));
        var payload = Serialize(fingerprint);

        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        return Prefix + ToBase64Url(ms.ToArray());
    }

    /// <summary>
    /// 解析邀请码。只认 <c>CHERT1:</c> 前缀的码；普通 <c>host:port</c> 不是本码，
    /// 返回 false 且不报错（调用方按原路径处理即可）。
    /// </summary>
    public static bool TryDecode(string? text, out LanInviteCode? code, out string error)
    {
        code = null;
        error = "";

        var s = (text ?? "").Trim();
        // 从聊天软件复制常带首尾引号 / 全角空格
        s = s.Trim('"', '\'', '“', '”', '‘', '’', '　');
        if (s.Length == 0) { error = "invite_empty"; return false; }
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "invite_not_code";
            return false;
        }

        byte[] raw;
        try { raw = FromBase64Url(s[Prefix.Length..].Trim()); }
        catch { error = "invite_bad_base64"; return false; }

        byte[] payload;
        try { payload = Inflate(raw); }
        catch { error = "invite_bad_deflate"; return false; }

        var fields = new Dictionary<byte, byte[]>();
        int version;
        try
        {
            version = Parse(payload, fields);
        }
        catch { error = "invite_bad_payload"; return false; }

        if (version != CurrentVersion) { error = "invite_version_mismatch"; return false; }

        var fingerprint = Text(fields, TagFingerprint);
        if (!string.Equals(fingerprint, ComputeFingerprint(SerializeFrom(version, fields, null)), StringComparison.Ordinal))
        { error = "invite_checksum"; return false; }

        var endpoint = Text(fields, TagEndpoint);
        if (endpoint.Length == 0) { error = "invite_no_endpoint"; return false; }

        code = new LanInviteCode
        {
            Version = version,
            Kind = Text(fields, TagKind),
            ExpiresAt = fields.TryGetValue(TagExp, out var e) ? ReadVarintValue(e) : 0,
            Endpoint = endpoint,
            DeviceName = Text(fields, TagDevice),
            WorldName = Text(fields, TagWorld),
            McVersion = Text(fields, TagMcVersion),
            PairCode = Text(fields, TagPairCode),
            Fingerprint = fingerprint
        };
        return true;
    }

    /// <summary>
    /// 从任意粘贴文本里解析出世界地址：支持 <c>CHERT1:</c> 长码、<c>chert-lan://host:port</c>、
    /// 以及裸 <c>host:port</c>。长码过期 / 校验失败会带出原因（供 UI 提示）。
    /// </summary>
    public static (string Endpoint, LanInviteCode? Code, string Error) Resolve(string? text, DateTimeOffset? now = null)
    {
        var s = (text ?? "").Trim().Trim('"', '\'');
        if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecode(s, out var code, out var error)) return ("", null, error);
            if (code!.IsExpired(now)) return ("", null, "invite_expired");
            return (code.Endpoint, code, "");
        }

        if (s.StartsWith("chert-lan://", StringComparison.OrdinalIgnoreCase))
            s = s["chert-lan://".Length..].Trim('/');

        return LanWorldShare.ParseEndpoint(s) is { } parsed
            ? ($"{parsed.Host}:{parsed.Port}", null, "")
            : ("", null, "invite_bad_endpoint");
    }

    // ===== 序列化 =====

    private byte[] Serialize(string? fingerprint)
    {
        var dict = new Dictionary<byte, byte[]>
        {
            [TagExp] = Varint(ExpiresAt),
            [TagKind] = Utf8(Kind),
            [TagEndpoint] = Utf8(Endpoint),
            [TagDevice] = Utf8(DeviceName),
            [TagWorld] = Utf8(WorldName),
            [TagMcVersion] = Utf8(McVersion),
            [TagPairCode] = Utf8(PairCode)
        };
        return SerializeFrom(Version, dict, fingerprint);
    }

    private static byte[] SerializeFrom(int version, Dictionary<byte, byte[]> fields, string? fingerprint)
    {
        // 顺序固定，指纹依赖字节序列
        byte[] order = { TagExp, TagKind, TagEndpoint, TagDevice, TagWorld, TagMcVersion, TagPairCode };

        using var ms = new MemoryStream();
        ms.WriteByte(Magic0);
        ms.WriteByte(Magic1);
        ms.WriteByte((byte)version);
        foreach (var tag in order)
        {
            if (!fields.TryGetValue(tag, out var value)) continue;
            if (value.Length == 0 && tag != TagExp) continue;   // 空串不写，省字节
            ms.WriteByte(tag);
            WriteVarint(ms, (ulong)value.Length);
            ms.Write(value, 0, value.Length);
        }
        if (fingerprint is { Length: > 0 })
        {
            ms.WriteByte(TagFingerprint);
            var fp = Utf8(fingerprint);
            WriteVarint(ms, (ulong)fp.Length);
            ms.Write(fp, 0, fp.Length);
        }
        return ms.ToArray();
    }

    /// <summary>解析载荷，返回协议版本并把字段收进字典。</summary>
    private static int Parse(byte[] payload, Dictionary<byte, byte[]> fields)
    {
        var pos = 0;
        if (payload.Length < 3) throw new FormatException("too short");
        if (payload[pos++] != Magic0 || payload[pos++] != Magic1) throw new FormatException("bad magic");
        var version = payload[pos++];

        while (pos < payload.Length)
        {
            var tag = payload[pos++];
            var raw = ReadVarint(payload, ref pos);
            if (raw < 0 || raw > payload.Length - pos) throw new FormatException("bad length");
            var len = (int)raw;
            var value = new byte[len];
            Array.Copy(payload, pos, value, 0, len);
            pos += len;
            fields[tag] = value;
        }
        return version;
    }

    /// <summary>SHA256 取前 3 字节 → 6 位十进制。碰撞约百万分之一，用于检错足够。</summary>
    private static string ComputeFingerprint(byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        var n = ((uint)hash[0] << 16) | ((uint)hash[1] << 8) | hash[2];
        return ((int)(n % 1_000_000)).ToString("D6");
    }

    private static string Text(Dictionary<byte, byte[]> fields, byte tag)
        => fields.TryGetValue(tag, out var v) ? Encoding.UTF8.GetString(v) : "";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s ?? "");

    private static byte[] Varint(long value)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, (ulong)value);
        return ms.ToArray();
    }

    private static void WriteVarint(Stream ms, ulong value)
    {
        while (value >= 0x80)
        {
            ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }

    private static long ReadVarintValue(byte[] buffer)
    {
        var pos = 0;
        return ReadVarint(buffer, ref pos);
    }

    private static long ReadVarint(byte[] buffer, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (pos < buffer.Length)
        {
            var b = buffer[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new FormatException("varint too long");
        }
        return (long)result;
    }

    // ===== Base64URL / Deflate =====

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
        // 聊天软件常把长串换行：先去掉所有空白再补等号
        var s = string.Concat(text.Where(c => !char.IsWhiteSpace(c)))
                      .Replace('-', '+')
                      .Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("bad base64url length");
        }
        return Convert.FromBase64String(s);
    }

    private static byte[] Inflate(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>日志排查用：长码只打前 24 字符 + 总长度，避免整段落盘。</summary>
    public static string DebugSummary(string? text)
    {
        var s = text ?? "";
        return s.Length <= 24 ? s : s[..24] + "…（共 " + s.Length + " 字符）";
    }
}
