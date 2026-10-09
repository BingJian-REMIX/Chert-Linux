using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MCLCS.Core.Models;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Launcher;

/// <summary>一个 Java 安装的信息。</summary>
public class JavaInfo
{
    /// <summary>java(.exe) 完整路径。</summary>
    public string JavaExe { get; set; } = "";

    /// <summary>主版本号（如 21、17、8）。</summary>
    public int MajorVersion { get; set; }

    /// <summary>展示用版本串。</summary>
    public string RawVersion { get; set; } = "";

    /// <summary>发行商（由 <see cref="JavaValidator"/> 校验后回填，未校验时为空）。</summary>
    public string Vendor { get; set; } = "";

    /// <summary>JVM 自述的安装目录（用于识别同一安装的不同入口）。</summary>
    public string JavaHome { get; set; } = "";

    /// <summary>CPU 架构（如 amd64 / aarch64）。</summary>
    public string Arch { get; set; } = "";

    /// <summary>是否为 64 位 JVM（未知时按常见的 64 位处理）。</summary>
    public bool Is64Bit { get; set; } = true;

    /// <summary>是否已通过 <see cref="JavaValidator"/> 的交叉校验。</summary>
    public bool Verified { get; set; }

    public override string ToString() => $"Java {MajorVersion} ({JavaExe})";
}

/// <summary>
/// 智能 Java 选择：扫描 JAVA_HOME、注册表、常见目录、.minecraft\runtime，解析版本号，筛选 ≥ 要求版本。
/// </summary>
public static class JavaDetector
{
    private static readonly string JavaExeName =
        OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>从 "21.0.3" / "1.8.0_301" 解析主版本号。</summary>
    public static int MajorFromVersionString(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var m = Regex.Match(version, @"(\d+)(\.(\d+))?");
        if (!m.Success) return 0;
        if (m.Groups[1].Value == "1" && m.Groups[3].Success
            && int.TryParse(m.Groups[3].Value, out var legacy))
            return legacy; // Java 8 及更早：1.8 -> 8
        return int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
    }

    /// <summary>运行 java -version 解析主版本号（输出通常在 stderr）。</summary>
    public static async Task<(int major, string raw)> QueryVersionAsync(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var err = await proc.StandardError.ReadToEndAsync();
            var outp = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var text = outp + "\n" + err;
            var m = Regex.Match(text, @"version\s+""([^""]+)""");
            var raw = m.Success ? m.Groups[1].Value : "";
            return (MajorFromVersionString(raw), raw);
        }
        catch
        {
            return (0, "");
        }
    }

    /// <summary>扫描所有候选位置，返回发现的 Java 列表（含版本号）。</summary>
    public static async Task<List<JavaInfo>> DetectAsync(IEnumerable<string>? extraDirs = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) candidates.Add(home);

        if (OperatingSystem.IsWindows())
        {
            AddRegistryJava(candidates);
            foreach (var baseDir in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "jdk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft", "Liberica"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime")
            })
                if (Directory.Exists(baseDir)) candidates.Add(baseDir);
        }
        else
        {
            foreach (var d in new[] { "/usr/lib/jvm", "/opt/java", "/Library/Java/JavaVirtualMachines" })
                if (Directory.Exists(d)) candidates.Add(d);
        }

        if (extraDirs is not null)
            foreach (var d in extraDirs) candidates.Add(d);

        var results = new List<JavaInfo>();
        foreach (var dir in candidates)
            results.AddRange(await ScanDirectoryAsync(dir));

        return results.DistinctBy(r => r.JavaExe).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryJava(HashSet<string> candidates)
    {
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (key is null) continue;
                foreach (var sub in new[] { "Java Runtime Environment", "JDK" })
                {
                    using var sk = key.OpenSubKey(sub);
                    if (sk is null) continue;
                    foreach (var ver in sk.GetSubKeyNames())
                    {
                        using var vk = sk.OpenSubKey(ver);
                        var javaHome = vk?.GetValue("JavaHome") as string;
                        if (!string.IsNullOrEmpty(javaHome)) candidates.Add(javaHome);
                    }
                }
            }
        }
        catch { /* 忽略注册表访问错误 */ }
    }

    private static async Task<List<JavaInfo>> ScanDirectoryAsync(string dir)
    {
        var found = new List<JavaInfo>();
        if (!Directory.Exists(dir)) return found;

        // 该目录或其子目录（至多 4 层）下的 java 可执行文件
        IEnumerable<string> javaExes;
        try
        {
            javaExes = Directory.EnumerateFiles(dir, JavaExeName, SearchOption.AllDirectories)
                .Where(p => IsWithinDepth(dir, p, 4));
        }
        catch
        {
            return found;
        }

        foreach (var exe in javaExes)
        {
            var (major, raw) = await QueryVersionAsync(exe);
            if (major > 0)
                found.Add(new JavaInfo { JavaExe = exe, MajorVersion = major, RawVersion = raw });
        }
        return found;
    }

    private static bool IsWithinDepth(string root, string path, int maxDepth)
    {
        var rel = path[(root.Length + 1)..];
        var sep = Path.DirectorySeparatorChar;
        var depth = rel.Count(c => c == sep);
        return depth <= maxDepth;
    }

    /// <summary>筛选满足最小版本要求、且版本最高的 Java。</summary>
    public static async Task<JavaInfo?> FindBestAsync(int minMajor, bool exact = false, IEnumerable<string>? extraDirs = null)
    {
        var all = await DetectAsync(extraDirs);
        var filtered = exact ? all.Where(j => j.MajorVersion == minMajor) : all.Where(j => j.MajorVersion >= minMajor);
        return exact ? filtered.FirstOrDefault() : filtered.OrderByDescending(j => j.MajorVersion).FirstOrDefault();
    }

    /// <summary>从版本 Id 中解析 MC 版本号（兼容 "1.20.4"、"fabric-1.20.4"、"1.20.4-forge-..." 等写法）。</summary>
    public static string? ExtractMcVersion(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;
        var m = Regex.Match(versionId, @"(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 根据 MC 版本号推断所需的最低 Java 主版本（用于自动切换，避免高版本 Java 启动低版本 MC 失败）。
    /// <list type="bullet">
    ///   <item>1.16 及以下 → Java 8；</item>
    ///   <item>1.17 → Java 16；</item>
    ///   <item>1.18 ~ 1.20.x → Java 17；</item>
    ///   <item>1.21+ → Java 21。</item>
    /// </list>
    /// </summary>
    public static int RequiredMajorForMcVersion(string mcVersion)
    {
        var m = Regex.Match(mcVersion ?? "", @"(\d+)\.(\d+)");
        var major = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        var minor = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        if (major < 1) return 8;
        if (major == 1 && minor < 17) return 8;   // 1.16.5 及以下
        if (major == 1 && minor == 17) return 16; // 1.17 需 Java 16
        if (major == 1 && minor < 21) return 17;  // 1.18 ~ 1.20.x 需 Java 17
        return 21;                                // 1.21+ 需 Java 21
    }

    /// <summary>
    /// 读取某版本所需 Java 主版本：优先用 version.json 声明的 <c>javaVersion</c>（沿 inheritsFrom 向上查找），
    /// 无声明时回退到从版本 Id 字符串推断的规则。
    /// </summary>
    public static int RequiredMajorForVersionId(string gameRoot, string versionId)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = versionId;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
            {
                var json = PathEx.VersionJsonPath(gameRoot, current);
                if (!File.Exists(json)) break;
                var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(json));
                if (v?.JavaVersion?.MajorVersion is > 0) return v.JavaVersion.MajorVersion;
                if (v?.InheritsFrom is null) break;
                current = v.InheritsFrom;
                if (!seen.Add(current)) break;
            }
        }
        catch { /* 解析失败则回退到字符串规则 */ }

        var mc = ExtractMcVersion(versionId);
        return mc is null ? 8 : RequiredMajorForMcVersion(mc);
    }

    /// <summary>
    /// 为指定版本挑选最合适的 Java。选择规则：
    /// 1. 显式指定路径优先（用户/每版本覆盖层已决定）；
    /// 2. 否则按该版本所需 Java 主版本，挑「满足要求且尽可能低」的 Java（对老 Forge 最友好，避免高版本 Java 启动旧 MC 报错）；
    /// 3. 若没有满足要求的，退而取满足要求的最高版本；若完全没有任何 Java，返回 null。
    /// </summary>
    public static JavaInfo? SelectForVersion(List<JavaInfo> detected, string gameRoot, string versionId, string? explicitPath = null)
    {
        if (detected.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var match = detected.FirstOrDefault(j =>
                string.Equals(j.JavaExe, explicitPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                // 老版本（需精确 Java 8）不接受显式路径的高/低版本兜底，否则交由下方自动选择/下载
                var reqForExplicit = RequiredMajorForVersionId(gameRoot, versionId);
                if (reqForExplicit != 8 || match.MajorVersion == 8) return match;
            }
        }

        var required = RequiredMajorForVersionId(gameRoot, versionId);
        // 老 MC / Forge（1.16.5 及以下）只能运行在精确 Java 8 上，不能用更高版本兜底
        bool exact = required == 8;
        var satisfying = detected.Where(j => exact ? j.MajorVersion == required : j.MajorVersion >= required).ToList();
        // 满足要求时优先选最低版本（老 MC/Forge 常不兼容过高 Java）
        if (satisfying.Count > 0)
            return satisfying.OrderBy(j => j.MajorVersion).First();
        // 精确需求的版本（如 Java 8）本地无匹配时不兜底高版本，交由上层自动下载对应版本
        if (exact) return null;
        return detected.OrderByDescending(j => j.MajorVersion).First();
    }
}
