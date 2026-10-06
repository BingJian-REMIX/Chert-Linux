using System.Runtime.InteropServices;
using MCLCS.Core.Download;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Launcher;

/// <summary>可选的 Java 发行商（用于自动安装）。</summary>
public enum JavaVendor
{
    /// <summary>自动：先 Temurin，失败再尝试 Oracle（Windows）。</summary>
    Auto,

    /// <summary>仅 Eclipse Temurin。</summary>
    Temurin,

    /// <summary>仅 Oracle JDK。</summary>
    Oracle
}

/// <summary>
/// Java 自动安装：当系统无满足要求的 Java 时，下载并解压 Eclipse Temurin 或 Oracle JDK。
/// 默认解压到 {gameRoot}/runtime/jdk-{major}，避免依赖系统安装/管理员权限。
/// </summary>
public static class JavaInstaller
{
    /// <summary>确保存在满足 minMajor 的 Java；不存在则尝试安装。返回可用的 JavaInfo。</summary>
    public static async Task<JavaInfo?> EnsureJavaAsync(int minMajor,
        string gameRoot,
        IDownloader downloader,
        ILogger? logger = null,
        CancellationToken ct = default)
        => await EnsureJavaAsync(minMajor, gameRoot, downloader, JavaVendor.Auto, logger, ct);

    /// <summary>确保存在满足 minMajor 的 Java；按首选发行商尝试安装。</summary>
    public static async Task<JavaInfo?> EnsureJavaAsync(int minMajor,
        string gameRoot,
        IDownloader downloader,
        JavaVendor preferred,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var existing = await JavaDetector.FindBestAsync(minMajor);
        if (existing is not null)
        {
            logger?.Log($"已找到可用 Java：{existing}");
            return existing;
        }

        logger?.Log($"未找到 Java ≥ {minMajor}，尝试自动安装（首选：{preferred}）…");

        // 按首选顺序尝试：Auto => Temurin 优先，再 Oracle；Oracle => 反之；Temurin => 仅 Temurin
        JavaInfo? result = null;
        if (preferred != JavaVendor.Oracle)
            result ??= await TryInstall(() => InstallTemurinAsync(minMajor, gameRoot, downloader, logger, ct), logger);
        if (result is null && preferred != JavaVendor.Temurin && OperatingSystem.IsWindows())
            result ??= await TryInstall(() => InstallOracleAsync(minMajor, gameRoot, downloader, logger, ct), logger);
        return result;
    }

    private static async Task<JavaInfo?> TryInstall(Func<Task<JavaInfo?>> action, ILogger? logger)
    {
        try { return await action(); }
        catch (Exception ex) { logger?.Log($"Java 安装失败：{ex.Message}"); return null; }
    }

    public static async Task<JavaInfo?> InstallTemurinAsync(int major,
        string gameRoot,
        IDownloader downloader,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsMacOS() ? "mac" : "linux");
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var url = $"{GameConstants.AdoptiumApiBase}/binary/latest/{major}/ga/{os}/{arch}/jdk/hotspot/normal/eclipse?archive_type=zip";

        var runtimeDir = Path.Combine(gameRoot, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var zipPath = Path.Combine(runtimeDir, $"temurin-{major}.zip");

        logger?.Log($"下载 Temurin {major} 自 {url}");
        await downloader.DownloadAsync(new DownloadItem(new[] { url }, zipPath), null, ct);

        if (!HashUtil.IsZip(zipPath))
            throw new InvalidDataException("下载的 Java 安装包不是有效的 ZIP 文件");

        var extractDir = Path.Combine(runtimeDir, $"jdk-{major}");
        // 【修复】此前先把已有的 jdk-{major} 整个删掉再解压：解压途中失败 / 断电，
        // 用户原有的 Java 也没了。改为解压到暂存目录，成功后原子替换。
        var staging = Path.Combine(runtimeDir, $"jdk-{major}.staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Unzip.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);
        ReplaceDirectory(staging, extractDir);

        // 顶层文件夹形如 jdk-21.0.x+xx，将其内容归一到 extractDir
        FlattenSingleTopFolder(extractDir);

        var javaExeName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var javaExe = Path.Combine(extractDir, "bin", javaExeName);
        if (!File.Exists(javaExe))
            throw new FileNotFoundException("解压后未找到 java 可执行文件", javaExe);

        var (majorVer, raw) = await JavaDetector.QueryVersionAsync(javaExe);
        logger?.Log($"Temurin 安装完成：{raw} @ {javaExe}");

        return new JavaInfo { JavaExe = javaExe, MajorVersion = majorVer, RawVersion = raw };
    }

    /// <summary>
    /// 安装 Oracle JDK（仅 Windows x64 直链 ZIP；Linux/macOS 为 tar.gz，net6 无原生解包，故跳过）。
    /// Oracle 自 JDK 17/21 起 GA 直链对所有人免费，无需登录。
    /// </summary>
    public static async Task<JavaInfo?> InstallOracleAsync(int major,
        string gameRoot,
        IDownloader downloader,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger?.Log("Oracle 自动安装仅支持 Windows（需 ZIP 包），跳过。");
            return null;
        }

        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var url = $"https://download.oracle.com/java/{major}/latest/jdk-{major}_windows-{arch}_bin.zip";

        var runtimeDir = Path.Combine(gameRoot, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var zipPath = Path.Combine(runtimeDir, $"oracle-{major}.zip");

        logger?.Log($"下载 Oracle JDK {major} 自 {url}");
        await downloader.DownloadAsync(new DownloadItem(new[] { url }, zipPath), null, ct);

        if (!HashUtil.IsZip(zipPath))
            throw new InvalidDataException("下载的 Oracle JDK 不是有效的 ZIP 文件");

        var extractDir = Path.Combine(runtimeDir, $"jdk-oracle-{major}");
        var staging = Path.Combine(runtimeDir, $"jdk-oracle-{major}.staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Unzip.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);
        ReplaceDirectory(staging, extractDir);

        FlattenSingleTopFolder(extractDir);

        var javaExe = Path.Combine(extractDir, "bin", "java.exe");
        if (!File.Exists(javaExe))
            throw new FileNotFoundException("解压后未找到 java.exe", javaExe);

        var (majorVer, raw) = await JavaDetector.QueryVersionAsync(javaExe);
        logger?.Log($"Oracle JDK 安装完成：{raw} @ {javaExe}");

        return new JavaInfo { JavaExe = javaExe, MajorVersion = majorVer, RawVersion = raw };
    }

    /// <summary>
    /// 用 staging 覆盖 target：先把旧目录改名为 .old，移入新目录成功后才删除旧目录。
    /// 任何一步失败都把旧目录搬回去，绝不留下「新旧皆无」的状态。
    /// </summary>
    private static void ReplaceDirectory(string staging, string target)
    {
        var backup = target + ".old";
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        var hadOld = Directory.Exists(target);
        if (hadOld) Directory.Move(target, backup);
        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (hadOld && !Directory.Exists(target) && Directory.Exists(backup))
                Directory.Move(backup, target);
            throw;
        }
        if (hadOld && Directory.Exists(backup)) Directory.Delete(backup, true);
    }

    /// <summary>若解压后顶层只有一个子文件夹，将其下内容提升一层，统一目录结构。</summary>
    private static void FlattenSingleTopFolder(string dir)
    {
        var subs = Directory.GetFileSystemEntries(dir);
        if (subs.Length == 1 && Directory.Exists(subs[0]))
        {
            var inner = subs[0];
            foreach (var entry in Directory.GetFileSystemEntries(inner))
            {
                var name = Path.GetFileName(entry);
                var target = Path.Combine(dir, name);
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }
            Directory.Delete(inner, true);
        }
    }
}
