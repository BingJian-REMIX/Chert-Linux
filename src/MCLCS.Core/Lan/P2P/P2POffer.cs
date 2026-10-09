using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// 广域网直连的握手凭据（前缀 <c>CHERT2:</c>）。
/// <para>
/// 与局域网邀请码（<see cref="LanInviteCode"/>，CHERT1:）的区别：
/// CHERT1: 只是「世界地址的名片」（同一网段直接连），CHERT2: 是「怎么找到我 + 怎么证明是我」——
/// 里面装打洞用的候选地址、临时 X25519 公钥、防重放 nonce 与指纹。
/// 双方各生成一条（房主 offer / 加入者 answer），互换后各自都握有对方的公钥与地址。
/// </para>
/// <para>
/// 编码沿用 CHERT1: 的做法：tag-length-value → Deflate → Base64URL。含 3 个候选地址时
/// 典型 150～230 字符，二维码装得下（版本 6 左右，扫起来不吃力）。
/// </para>
/// </summary>
public sealed class P2POffer
{
    /// <summary>握手码前缀（识别时大小写不敏感）。</summary>
    public const string Prefix = "CHERT2:";

    /// <summary>有效期：10 分钟。够人工传两次码（offer → answer），又不至于让旧码长期可用。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public const int CurrentVersion = 1;

    /// <summary>握手使用的约定 IwDP 端口（与局域网发现端口不同，避免互相干扰）。</summary>
    public const int DefaultUdpPort = 47810;

    /// <summary>
    /// 检错码与载荷的分隔符。用点号是因为 Base64URL 里只可能出现
    /// <c>[A-Za-z0-9-_]</c>，点号天然不会混淆，用 <c>LastIndexOf</c> 拆也是稳的。
    /// </summary>
    private const char ChecksumSeparator = '.';

    private const byte Magic0 = 0xC2;
    private const byte Magic1 = 0x11;

    private const byte TagExp = 0x01;
    private const byte TagKind = 0x02;
    private const byte TagNonce = 0x03;
    private const byte TagPubKey = 0x04;
    private const byte TagCandidates = 0x05;
    private const byte TagDevice = 0x06;
    private const byte TagMcVersion = 0x07;
    private const byte TagWorld = 0x08;
    private const byte TagMcPort = 0x09;
    private const byte TagFingerprint = 0x0F;

    // 序列化顺序固定：指纹依赖字节序列
    private static readonly byte[] Order =
        { TagExp, TagKind, TagNonce, TagPubKey, TagCandidates, TagDevice, TagMcVersion, TagWorld, TagMcPort };

    public int Version { get; init; } = CurrentVersion;

    /// <summary><c>offer</c>（房主发的）/ <c>answer</c>（加入者回的）。</summary>
    public string Kind { get; init; } = P2POfferKind.Offer;

    /// <summary>过期时间（Unix 秒）。</summary>
    public long ExpiresAt { get; init; }

    /// <summary>16 字节随机串，防重放（会话密钥也拿它做盐）。</summary>
    public byte[] Nonce { get; init; } = Array.Empty<byte>();

    /// <summary>32 字节 raw X25519 公钥。</summary>
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();

    /// <summary>候选地址，形如 <c>udp://1.2.3.4:47810</c>（内网、IPv6、UPnP 映射后的公网都算）。</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    public string DeviceName { get; init; } = "";

    /// <summary>MC 版本 / 加载器（对方能提前发现版本不一致，而不是连上后掉线）。</summary>
    public string McVersion { get; init; } = "";

    public string WorldName { get; init; } = "";

    /// <summary>房主已开放的 MC 端口（offer 携带；answer 侧为 0）。隧道建好后转发到它。</summary>
    public int McPort { get; init; }

    /// <summary>6 位指纹（<see cref="ComputeFingerprint"/>），给人眼核对。</summary>
    public string Fingerprint { get; init; } = "";

    public bool IsExpired(DateTimeOffset? now = null)
        => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() > ExpiresAt;

    /// <summary>剩余有效时间。</summary>
    public TimeSpan Remaining(DateTimeOffset? now = null)
    {
        var left = ExpiresAt - (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return left > 0 ? TimeSpan.FromSeconds(left) : TimeSpan.Zero;
    }

    /// <summary>
    /// 对编码后的字节做 6 位检错码（SHA256 前 3 字节 → 十进制）。
    /// <para>
    /// 它管的是「这串字符在传递过程里有没有坏」，不是安全 —— 握手码本来就要交给对方。
    /// 真正防冒充的是应用层那次挑战—应答。与 <see cref="ComputeFingerprint"/> 的区别：
    /// 指纹只覆盖解压出来的内容（给人眼核对是不是同一个人），这个覆盖的是字节本身。
    /// </para>
    /// </summary>
    private static string Checksum(byte[] encodedBytes)
    {
        var hash = SHA256.HashData(encodedBytes);
        var n = ((uint)hash[0] << 16) | ((uint)hash[1] << 8) | hash[2];
        return ((int)(n % 1_000_000)).ToString("D6");
    }

    /// <summary>SHA256(公钥) 前 3 字节 → 6 位十进制。用于人工核对（碰撞约百万分之一）。</summary>
    public static string ComputeFingerprint(byte[] rawPublicKey)
    {
        if (rawPublicKey is null || rawPublicKey.Length == 0) return "";
        var hash = SHA256.HashData(rawPublicKey);
        var n = ((uint)hash[0] << 16) | ((uint)hash[1] << 8) | hash[2];
        return ((int)(n % 1_000_000)).ToString("D6");
    }

    /// <summary>序列化成可复制的握手码（含前缀）。</summary>
    public string Encode()
    {
        var fingerprint = string.IsNullOrEmpty(Fingerprint) ? ComputeFingerprint(PublicKey) : Fingerprint;
        var payload = Serialize(fingerprint);

        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        var compressed = ms.ToArray();
        return Prefix + ToBase64Url(compressed) + ChecksumSeparator + Checksum(compressed);
    }

    /// <summary>解析握手码。只认 <c>CHERT2:</c> 前缀。</summary>
    public static bool TryDecode(string? text, out P2POffer? offer, out string error)
    {
        offer = null;
        error = "";

        var s = (text ?? "").Trim().Trim('"', '\'', '“', '”', '‘', '’', '　');
        if (s.Length == 0) { error = "p2p_empty"; return false; }
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        { error = "p2p_not_code"; return false; }

        var body = s[Prefix.Length..];
        var dot = body.LastIndexOf(ChecksumSeparator);
        if (dot <= 0) { error = "p2p_checksum_missing"; return false; }

        byte[] raw;
        try { raw = FromBase64Url(body[..dot]); }
        catch { error = "p2p_bad_base64"; return false; }

        // 校验必须发生在解压之前，而且覆盖的是**实际字节**而不是解压出来的内容：
        // 实测改一个字符有 7% 的概率落在解压器根本不读的冗余字节上 —— 那段字节变了、
        // 解压结果却一模一样，于是被静默接受。让它覆盖字节本身，这种改动就必然现形。
        if (!string.Equals(body[(dot + 1)..].Trim(), Checksum(raw), StringComparison.Ordinal))
        { error = "p2p_checksum"; return false; }

        byte[] payload;
        try { payload = Inflate(raw); }
        catch { error = "p2p_bad_deflate"; return false; }

        var fields = new Dictionary<byte, byte[]>();
        int version;
        try { version = Parse(payload, fields); }
        catch { error = "p2p_bad_payload"; return false; }

        if (version != CurrentVersion) { error = "p2p_version_mismatch"; return false; }

        var fingerprint = Text(fields, TagFingerprint);
        if (!string.Equals(fingerprint, ComputeFingerprint(fields.TryGetValue(TagPubKey, out var pk) ? pk : Array.Empty<byte>()), StringComparison.Ordinal))
        { error = "p2p_fingerprint_mismatch"; return false; }

        // 空串不写档，靠 tag 缺失表达；这里统一补默认值
        var kind = Text(fields, TagKind);
        if (kind.Length == 0) kind = P2POfferKind.Offer;

        var nonce = fields.TryGetValue(TagNonce, out var n) ? n : Array.Empty<byte>();
        if (nonce.Length != P2PIdentity.NonceSize) { error = "p2p_bad_nonce"; return false; }

        var publicKey = fields.TryGetValue(TagPubKey, out var pub) ? pub : Array.Empty<byte>();
        // Windows 给 64 字节（X‖Y），其它平台给 32 字节（只有 X），两者都合法
        if (!P2PIdentity.IsValidPublicKey(publicKey)) { error = "p2p_bad_pubkey"; return false; }

        offer = new P2POffer
        {
            Version = version,
            Kind = kind,
            ExpiresAt = fields.TryGetValue(TagExp, out var e) ? ReadVarintValue(e) : 0,
            Nonce = nonce,
            PublicKey = publicKey,
            Candidates = Split(Text(fields, TagCandidates)),
            DeviceName = Text(fields, TagDevice),
            McVersion = Text(fields, TagMcVersion),
            WorldName = Text(fields, TagWorld),
            McPort = (int)(fields.TryGetValue(TagMcPort, out var p) ? ReadVarintValue(p) : 0),
            Fingerprint = fingerprint
        };
        return true;
    }

    /// <summary>日志排查用：只打前 24 字符 + 长度，避免整段银行贷款级长码落盘。</summary>
    public static string DebugSummary(string? text)
    {
        var s = text ?? "";
        return s.Length <= 24 ? s : s[..24] + "…（共 " + s.Length + " 字符）";
    }

    // ===== 序列化 =====

    private byte[] Serialize(string fingerprint)
    {
        var dict = new Dictionary<byte, byte[]>
        {
            [TagExp] = Varint(ExpiresAt),
            [TagKind] = Utf8(Kind),
            [TagNonce] = Nonce,
            [TagPubKey] = PublicKey,
            [TagDevice] = Utf8(DeviceName),
            [TagMcVersion] = Utf8(McVersion),
            [TagWorld] = Utf8(WorldName)
        };
        if (Candidates.Count > 0) dict[TagCandidates] = Utf8(string.Join('\n', Candidates));
        if (McPort > 0) dict[TagMcPort] = Varint(McPort);

        using var ms = new MemoryStream();
        ms.WriteByte(Magic0);
        ms.WriteByte(Magic1);
        ms.WriteByte((byte)Version);
        foreach (var tag in Order)
        {
            if (!dict.TryGetValue(tag, out var value)) continue;
            if (value.Length == 0 && tag != TagExp) continue;
            ms.WriteByte(tag);
            WriteVarint(ms, (ulong)value.Length);
            ms.Write(value, 0, value.Length);
        }
        if (fingerprint.Length > 0)
        {
            var fp = Utf8(fingerprint);
            ms.WriteByte(TagFingerprint);
            WriteVarint(ms, (ulong)fp.Length);
            ms.Write(fp, 0, fp.Length);
        }
        return ms.ToArray();
    }

    private static int Parse(byte[] payload, Dictionary<byte, byte[]> fields)
    {
        var pos = 0;
        if (payload.Length < 3) throw new FormatException("too short");
        if (payload[pos++] != Magic0 || payload[pos++] != Magic1) throw new FormatException("bad magic");
        var version = payload[pos++];

        while (pos < payload.Length)
        {
            var tag = payload[pos++];
            var rawLen = ReadVarint(payload, ref pos);
            if (rawLen < 0 || rawLen > payload.Length - pos) throw new FormatException("bad length");
            var len = (int)rawLen;
            var value = new byte[len];
            Array.Copy(payload, pos, value, 0, len);
            pos += len;
            fields[tag] = value;
        }
        return version;
    }

    private static List<string> Split(string joined)
        => joined.Length == 0
            ? new List<string>()
            : joined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

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

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
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
}

/// <summary>握手码的类型取值。</summary>
public static class P2POfferKind
{
    /// <summary>房主发出：我有世界，来连我。</summary>
    public const string Offer = "offer";

    /// <summary>加入者回复：这是我的地址和公钥。</summary>
    public const string Answer = "answer";

    public static bool IsValid(string? kind)
        => string.Equals(kind, Offer, StringComparison.Ordinal) || string.Equals(kind, Answer, StringComparison.Ordinal);
}
