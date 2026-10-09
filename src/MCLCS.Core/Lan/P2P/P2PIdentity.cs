using System.Security.Cryptography;

namespace MCLCS.Core.Lan.P2P;

/// <summary>
/// 一次直连用的临时 X25519 身份（密钥对用完即弃，不落盘）。
/// <para>
/// 用途有二：① 公钥指纹给人眼核对（防连错人）；② 与对方的公钥派生会话密钥，
/// 用于在隧道建立后证明「我确实持有对应私钥」—— 截获两条握手码的第三方算不出共享秘密，
/// 因为它没有私钥。这正是无服务器方案里唯一能对抗「码被截获」的手段。
/// </para>
/// <para>
/// 为什么不用「把 nonce 拼起来当口令」：那样只要码泄漏就永久失守；
/// X25519 保证即使两头的公开信息全被看光，攻击者仍然算不出会话密钥。
/// </para>
/// </summary>
public sealed class P2PIdentity : IDisposable
{
    /// <summary>X25519 公钥长度（字节）。Windows CNG 把 Montgomery 曲线的点导出成 (X‖Y) 共 64 字节。</summary>
    public const int KeySize = 64;

    /// <summary>
    /// 可接受的公钥长度：Windows 给 64 字节（X‖Y），其它平台（OpenSSL）只给 32 字节 X。
    /// 两种都收，因为两边总是同一个 .NET 实现协商出来的，不需要跨实现兼容。
    /// </summary>
    public static bool IsValidPublicKey(byte[]? key) => key is { Length: 32 or 64 };

    /// <summary>nonce 长度（字节）。</summary>
    public const int NonceSize = 16;

    /// <summary>
    /// 曲线的名字：Windows CNG 认 <c>curve25519</c>，OpenSSL 认 <c>X25519</c>。
    /// 顺序很重要 —— 先问 CNG，别让平台上不同的别名各自跑一半。
    /// </summary>
    private static readonly string[] CurveNames = { "curve25519", "X25519" };

    private static ECCurve? _curve;

    private static bool? _supported;
    private static string _unsupportedReason = "";

    private readonly ECDiffieHellman _key;

    private P2PIdentity(ECDiffieHellman key, byte[] publicKey)
    {
        _key = key;
        PublicKey = publicKey;
        Fingerprint = P2POffer.ComputeFingerprint(publicKey);
    }

    /// <summary>raw 32 字节公钥（写进握手码给对方）。</summary>
    public byte[] PublicKey { get; }

    /// <summary>6 位指纹，UI 上给双方人工核对。</summary>
    public string Fingerprint { get; }

    /// <summary>平台是否支持 X25519（旧 Windows / 特殊运行时可能不支持）。</summary>
    public static bool IsSupported => _supported ??= Probe(out _unsupportedReason);

    /// <summary>不支持时的原因（写日志用）。</summary>
    public static string UnsupportedReason => IsSupported ? "" : _unsupportedReason;

    /// <summary>创建临时身份；平台不支持时返回 null（调用方给出明确提示，不要静默降级）。</summary>
    public static P2PIdentity? TryCreate()
    {
        if (!IsSupported) return null;
        try
        {
            var key = ECDiffieHellman.Create(Curve());
            return new P2PIdentity(key, RawPublicOf(key));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成 nonce（requester/ responder 各自生成，一并作为 HKDF 盐）。</summary>
    public static byte[] NewNonce()
    {
        var n = new byte[NonceSize];
        RandomNumberGenerator.Fill(n);
        return n;
    }

    /// <summary>
    /// 派生会话密钥：HKDF-SHA256(ECDH(自己私钥, 对方公钥), salt = offer.nonce‖answer.nonce)。
    /// 两边都调用它可得到同一个密钥（各自私钥 + 对方公钥），用于隧道内的挑战—响应认证。
    /// </summary>
    public byte[] DeriveSessionSecret(byte[] peerPublicKey, byte[] offerNonce, byte[] answerNonce)
    {
        ArgumentNullException.ThrowIfNull(peerPublicKey);
        if (!IsValidPublicKey(peerPublicKey))
            throw new ArgumentException("公钥必须是 32 或 64 字节", nameof(peerPublicKey));

        using var peer = ImportPublic(peerPublicKey);
        var secret = _key.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);

        var salt = new byte[(offerNonce?.Length ?? 0) + (answerNonce?.Length ?? 0)];
        if (offerNonce is { Length: > 0 }) offerNonce.CopyTo(salt, 0);
        if (answerNonce is { Length: > 0 }) answerNonce.CopyTo(salt, offerNonce?.Length ?? 0);

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, System.Text.Encoding.UTF8.GetBytes("chert-p2p"));
    }

    /// <summary>挑战—响应：证明「我持有私钥」而不泄露密钥本身。</summary>
    public static byte[] Answer(byte[] sessionSecret, byte[] challenge)
        => HMACSHA256.HashData(sessionSecret, Concat(Utf8("chert-p2p-auth"), challenge));

    /// <summary>固定时间比较两条响应（不要用 SequenceEqual，比较耗时也会被侧信道区分出来）。</summary>
    public static bool Verify(byte[] expected, byte[] actual)
        => CryptographicOperations.FixedTimeEquals(expected, actual);

    public void Dispose() => _key.Dispose();

    // ===== 平台细节 =====

    /// <summary>本平台可用的 X25519 曲线（找不到返回 null）。</summary>
    private static ECCurve Curve()
    {
        if (_curve is { } cached) return cached;

        foreach (var name in CurveNames)
        {
            try
            {
                var curve = ECCurve.CreateFromFriendlyName(name);
                using var probe = ECDiffieHellman.Create(curve);
                if (probe is null) continue;
                _curve = curve;
                return curve;
            }
            catch
            {
                // 换下一个别名
            }
        }

        // 两个别名都不行：用第一个名字建，让创建阶段的原始异常暴露出来（便于排查）
        var fallback = ECCurve.CreateFromFriendlyName(CurveNames[0]);
        _curve = fallback;
        return fallback;
    }

    private static byte[] RawPublicOf(ECDiffieHellman key)
    {
        // Windows CNG：ExportParameters 给出完整的 (X‖Y)，两点等长；Montgomery 点的 Y 不能丢。
        try
        {
            var p = key.ExportParameters(false);
            var x = p.Q.X ?? Array.Empty<byte>();
            var y = p.Q.Y ?? Array.Empty<byte>();
            if (x.Length > 0) return y.Length == x.Length ? Concat(x, y) : x;
        }
        catch
        {
            // 有些平台不支持导出曲线参数（CNG 也会因为拿不到 OID 拒绝 SubjectPublicKeyInfo）
        }

        var spki = key.PublicKey.ExportSubjectPublicKeyInfo();
        return spki.Length >= 32 ? spki[^32..] : spki;
    }

    private static ECDiffieHellman ImportPublic(byte[] publicKey)
    {
        var curve = Curve();

        // 64 字节 = X‖Y；32 字节 = 只有 X（OpenSSL 风格）。导入规则在两种情况下不同，
        // 认错会被 CNG 报「Q.X / Q.Y 长度必须一致」。
        if (publicKey.Length == 64)
        {
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = curve,
                Q = new ECPoint { X = publicKey[..32], Y = publicKey[32..] }
            });
        }

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = curve,
            Q = new ECPoint { X = publicKey, Y = null }
        });
    }

    private static bool Probe(out string reason)
    {
        reason = "";
        try
        {
            using var a = TryCreateCore();
            using var b = TryCreateCore();
            if (a is null || b is null) { reason = "无法创建 X25519 密钥对"; return false; }
            using var imported = ImportPublic(b.PublicKey);
            var s1 = a._key.DeriveKeyFromHash(imported.PublicKey, HashAlgorithmName.SHA256);
            using var imported2 = ImportPublic(a.PublicKey);
            var s2 = b._key.DeriveKeyFromHash(imported2.PublicKey, HashAlgorithmName.SHA256);
            if (!s1.AsSpan().SequenceEqual(s2)) { reason = "两端派生结果不一致"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static P2PIdentity? TryCreateCore()
    {
        try
        {
            var key = ECDiffieHellman.Create(Curve());
            return new P2PIdentity(key, RawPublicOf(key));
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
