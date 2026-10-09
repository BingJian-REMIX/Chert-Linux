using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MCLCS.Core.Launcher;

/// <summary>单个 Java 的校验结果。</summary>
public sealed class JavaValidationResult
{
    /// <summary>是否通过校验（可作为可启动的 Java 使用）。</summary>
    public bool Ok { get; init; }

    /// <summary>未通过时的原因（可直接展示给用户排查）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>发行商（如 Oracle Corporation / Eclipse Adoptium）。</summary>
    public string Vendor { get; init; } = "";

    /// <summary>JVM 自述的安装目录。</summary>
    public string JavaHome { get; init; } = "";

    /// <summary>CPU 架构（如 amd64 / aarch64）。</summary>
    public string Arch { get; init; } = "";

    /// <summary>JVM 自述的完整版本串（用于与 -version 输出交叉核对）。</summary>
    public string PropertyVersion { get; init; } = "";

    /// <summary>是否为 64 位 JVM（Minecraft 需要 64 位；32 位仅作提示，不判死）。</summary>
    public bool Is64Bit { get; init; } = true;
}

/// <summary>
/// Java 稳健校验：在 <c>java -version</c> 文本解析之外，再用一次真实的 JVM 属性查询做交叉验证。
/// <para>
/// 起因：<c>java -version</c> 只是<b>打印一段文本</b>， bat/sh 包装脚本、伪装的 java.exe、
/// 损坏的安装都可能打印出形似版本的内容而并非真 JVM（历史 bug：偶发误判「假 Java」或偶然命中）。
/// 本类通过 <c>-XshowSettings:properties</c> 让 JVM <b>自己汇报</b> java.version / java.home /
/// sun.arch.data.model / java.vendor，并与 -version 输出比对，不一致即判定不可用。
/// </para>
/// </summary>
public static class JavaValidator
{
    /// <summary>匹配 <c>    java.version = 21.0.3</c> 形式的 JVM 属性输出行。</summary>
    private static readonly Regex PropertyLine =
        new(@"^\s+([\w.\-]+)\s*=\s?(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 查询 JVM 属性表（走 <c>-XshowSettings:properties -version</c>，输出在 stderr）。
    /// 失败时返回空表，不抛异常。
    /// </summary>
    public static async Task<Dictionary<string, string>> QueryPropertiesAsync(string javaExe)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-XshowSettings:properties -version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var errTask = proc.StandardError.ReadToEndAsync();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var err = await errTask;
            var outp = await outTask;
            await proc.WaitForExitAsync();

            foreach (var raw in (outp + "\n" + err).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = PropertyLine.Match(line);
                if (m.Success)
                    props[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
        }
        catch
        {
            // 查询失败视为属性不可用，交给上层判定
        }
        return props;
    }

    /// <summary>
    /// 稳健校验一个候选 Java 是否真的可用。
    /// 校验项：文件存在且非空 → 能解析出版本号 → JVM 属性可读 → 属性自述版本与 -version 一致 → java.home 目录真实存在。
    /// </summary>
    public static async Task<JavaValidationResult> ValidateAsync(string javaExe)
    {
        if (string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe))
            return new JavaValidationResult { Ok = false, Reason = "可执行文件不存在" };

        try
        {
            if (new FileInfo(javaExe).Length == 0)
                return new JavaValidationResult { Ok = false, Reason = "可执行文件为空（0 字节）" };
        }
        catch
        {
            return new JavaValidationResult { Ok = false, Reason = "无法读取可执行文件信息" };
        }

        var (major, raw) = await JavaDetector.QueryVersionAsync(javaExe);
        if (major <= 0 || string.IsNullOrWhiteSpace(raw))
            return new JavaValidationResult { Ok = false, Reason = "未能从 java -version 解析出版本号" };

        var props = await QueryPropertiesAsync(javaExe);
        if (props.Count == 0)
            return new JavaValidationResult
            {
                Ok = false,
                Reason = "JVM 未回报任何属性（可能不是真正的 Java，或安装已损坏）"
            };

        var propVersion = props.TryGetValue("java.version", out var pv) ? pv : "";
        if (!string.IsNullOrWhiteSpace(propVersion))
        {
            var propMajor = JavaDetector.MajorFromVersionString(propVersion);
            if (propMajor > 0 && propMajor != major)
                return new JavaValidationResult
                {
                    Ok = false,
                    Reason = $"版本自述不一致（-version 报 {raw}，JVM 属性报 {propVersion}）"
                };
        }

        var home = props.TryGetValue("java.home", out var homeValue) ? homeValue : "";
        if (!string.IsNullOrWhiteSpace(home) && !Directory.Exists(home))
            return new JavaValidationResult
            {
                Ok = false,
                Reason = $"java.home 指向的目录不存在：{home}"
            };

        var model = props.TryGetValue("sun.arch.data.model", out var mv) ? mv : "";
        var is64Bit = model != "32";

        return new JavaValidationResult
        {
            Ok = true,
            Vendor = props.TryGetValue("java.vendor", out var vendor) ? vendor : "",
            JavaHome = home,
            Arch = props.TryGetValue("os.arch", out var arch) ? arch : "",
            PropertyVersion = propVersion,
            Is64Bit = is64Bit
        };
    }

    /// <summary>
    /// 检测 + 校验：先取 <see cref="JavaDetector.DetectAsync"/> 的候选清单，再并发做交叉校验，
    /// 只返回真正可用的 Java，并把厂商 / 架构 / 位数等扩展信息回填到 <see cref="JavaInfo"/>。
    /// <para>并发上限为 4，避免一次启动扫到几十个 java.exe 时进程风暴。</para>
    /// </summary>
    /// <param name="extraDirs">附加扫描目录。</param>
    /// <param name="onRejected">可选回调：报告被剔除的候选及其原因（用于诊断日志）。</param>
    public static async Task<List<JavaInfo>> DetectValidatedAsync(
        IEnumerable<string>? extraDirs = null,
        Action<JavaInfo, string>? onRejected = null)
    {
        var candidates = await JavaDetector.DetectAsync(extraDirs);
        var accepted = new ConcurrentBag<JavaInfo>();

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(candidates.Select(async java =>
        {
            await gate.WaitAsync();
            try
            {
                var result = await ValidateAsync(java.JavaExe);
                if (!result.Ok)
                {
                    onRejected?.Invoke(java, result.Reason);
                    return;
                }

                java.Vendor = result.Vendor;
                java.JavaHome = result.JavaHome;
                java.Arch = result.Arch;
                java.Is64Bit = result.Is64Bit;
                java.Verified = true;
                accepted.Add(java);
            }
            finally
            {
                gate.Release();
            }
        }));

        // DetectAsync 已按路径去重；此处维持「高版本在前、同版本路径稳定」的可预期顺序
        return accepted
            .OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.JavaExe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>下拉框用的展示串，如「Java 21 · Eclipse Adoptium · 64 位」。</summary>
    public static string Describe(JavaInfo java)
    {
        var bitsText = java.Is64Bit ? "64 位" : "32 位";
        return string.IsNullOrWhiteSpace(java.Vendor)
            ? $"Java {java.MajorVersion} · {bitsText}"
            : $"Java {java.MajorVersion} · {java.Vendor} · {bitsText}";
    }
}
