using System.Security.Cryptography;
using System.Text;

namespace MCLCS.Core.Profiles;

/// <summary>
/// 在线音源登录凭证的本地混淆存储。
///
/// <para><b>目标</b>：配置文件（profiles.json）不得出现明文 cookie ——
/// 它会被导出、被云同步、被用户手动查看。至少要挡住「随手打开看一眼就拿到凭证」。</para>
///
/// <para><b>不是什么</b>：这不是安全边界。密钥由当前机器的用户名/机器名派生，
/// 拿到配置文件<b>又在同一台机器上</b>的人仍然可以解开（同样的密钥材料）。
/// 真正的保护应当是操作系统凭据保险库（Windows DPAPI / Linux Secret Service），
/// 但那会把依赖引入平台相关代码、让 Core 无法双端共用 —— 因此这里只做可逆混淆，
/// 并在文档里写清楚边界，不要把它当成「加密存储」对外宣传。</para>
///
/// <para><b>跨平台降级</b>：整份配置文件被拷到另一台机器时，派生密钥不同 → 解密失败。
/// 此时 <see cref="TryUnprotect"/> 返回 false，上层按「未登录」处理 ——
/// 不会有异常、不会卡死设置页。</para>
/// </summary>
public static class ApiCredentialProtector
{
    private const string Prefix = "mclcs1:";
    private const int Iterations = 50_000;
    private const int KeySize = 32;   // AES-256
    private const int IvSize = 16;

    /// <summary>密钥派生用的固定盐——注意它不是保密材料（只用于让字典攻击不可复用）。</summary>
    private static readonly byte[] Salt =
        Encoding.UTF8.GetBytes("mclcs.music.api.credential.v1");

    /// <summary>
    /// 混淆明文凭证。
    /// </summary>
    /// <param name="plain">明文 cookie。</param>
    /// <returns>带版本前缀的 base64 串；输入为空时返回空串（不报错）。</returns>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var key = DeriveKey();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, IvSize);
            using (var crypto = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                crypto.Write(bytes, 0, bytes.Length);
                crypto.FlushFinalBlock();
            }
            return Prefix + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            // 加密失败不应阻断登录流程：宁可什么都不存，也不要把明文写下去
            return "";
        }
    }

    /// <summary>
    /// 还原凭证。
    /// </summary>
    /// <param name="stored">之前由 <see cref="Protect"/> 产生的串。</param>
    /// <returns>明文凭证；格式不符 / 密钥不匹配 / 已损坏时返回 null（调用方按未登录处理）。</returns>
    public static string? TryUnprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        try
        {
            var raw = Convert.FromBase64String(stored[Prefix.Length..]);
            if (raw.Length <= IvSize) return null;

            var key = DeriveKey();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            var iv = new byte[IvSize];
            Buffer.BlockCopy(raw, 0, iv, 0, IvSize);

            using var ms = new MemoryStream(raw, IvSize, raw.Length - IvSize);
            using var crypto = new CryptoStream(ms, aes.CreateDecryptor(key, iv), CryptoStreamMode.Read);
            using var reader = new StreamReader(crypto, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // 换机器 / 配置被手改：按未登录处理，让用户重新扫一次码即可
            return null;
        }
    }

    /// <summary>
    /// 由「当前这台机器 + 当前用户」派生 AES 密钥。
    /// <para>注意：<b>不要</b>把派生材料换成硬编码常量——那样混淆就毫无意义；
    /// 也不要期待同一份 profile 在两台机器间共享后还能解开（做不到，且刻意如此）。</para>
    /// </summary>
    private static byte[] DeriveKey()
    {
        var material = Encoding.UTF8.GetBytes($"{Environment.UserName}@{Environment.MachineName}");
        // 用静态 Pbkdf2：Rfc2898DeriveBytes 的构造函数在 .NET 10 已标记过时（SYSLIB0060）
        return Rfc2898DeriveBytes.Pbkdf2(material, Salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }
}
