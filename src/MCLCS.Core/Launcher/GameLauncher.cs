using System.Diagnostics;
using MCLCS.Core.Download;
using MCLCS.Core.Models;
using MCLCS.Core.MultiInstance;
using MCLCS.Core.Profiles;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Launcher;

/// <summary>一次启动的结果。</summary>
public class LaunchResult
{
    public int ExitCode { get; set; }
    public string? CrashReportPath { get; set; }
    public bool Crashed => CrashReportPath is not null;

    /// <summary>崩溃时的分析报告（无崩溃时为 null）。</summary>
    public CrashAnalysis? Analysis { get; set; }

    /// <summary>针对本次崩溃的自动修复方案（无崩溃时为 null）。</summary>
    public CrashRepairPlan? RepairPlan { get; set; }
}

/// <summary>
/// 游戏启动：合并版本、构建 classpath 与参数、解压 natives、启动 Java 进程，
/// 退出后检测崩溃报告。
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// 游戏进程已启动事件。所有启动路径（首页/版本列表/游戏详情/崩溃恢复）共用同一条
    /// GameLauncher.LaunchAsync，由 MCLCS.App 订阅以触发 HUD 叠加层
    /// （bug #28：此前仅在 LaunchCoordinator 一条路径触发，且用固定 1.5s 延时存在竞态）。
    /// 参数为已注册到 InstanceTracker 的游戏进程。
    /// </summary>
    public static event Action<System.Diagnostics.Process, long>? GameProcessStarted;

    /// <summary>从 JVM 参数解析 -Xmx（最大堆内存，MB），用于 HUD 内存百分比显示。解析失败返回 0。</summary>
    private static long ParseMaxMemoryMb(System.Collections.Generic.IEnumerable<string> jvmArgs)
    {
        foreach (var a in jvmArgs)
        {
            var s = (a ?? string.Empty).Trim();
            if (!s.StartsWith("-Xmx", System.StringComparison.OrdinalIgnoreCase)) continue;
            var v = s.Substring(4).Trim();
            if (v.Length == 0) continue;
            var last = char.ToLowerInvariant(v[^1]);
            var numStr = char.IsDigit(last) ? v : v[..^1];
            if (!double.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n))
                continue;
            double mb = last switch
            {
                'k' => n / 1024.0,
                'm' => n,
                'g' => n * 1024.0,
                _ => n / (1024.0 * 1024.0) // 无单位按字节处理
            };
            return (long)System.Math.Round(mb);
        }
        return 0;
    }

    /// <summary>构造 ${...} 变量字典（含 classpath、natives_directory 等）。</summary>
    public static Dictionary<string, string> BuildVariables(VersionJson merged,
        string gameRoot, string leafId, string nativesDir, LaunchOptions options, string gameDir)
    {
        var classpath = ClasspathBuilder.ComputeClasspath(gameRoot, leafId, merged);
        var assetsIndexName = merged.Assets ?? merged.AssetIndex?.Id ?? "";

        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = options.Username,
            ["auth_uuid"] = options.Uuid,
            ["auth_access_token"] = options.AccessToken,
            ["auth_session"] = options.AccessToken,
            ["auth_xuid"] = options.UserType == "msa" ? options.Uuid : "0",
            ["user_type"] = options.UserType,
            ["user_properties"] = options.UserProperties,
            ["version_name"] = merged.Id,
            ["version_type"] = merged.Type,
            ["assets_root"] = PathEx.AssetsDir(gameRoot),
            ["assets_index_name"] = assetsIndexName,
            // 隔离版本（整合包）指向 versions/<id>，否则共用 .minecraft；每版本可经 options.GameDir 覆盖
            ["game_directory"] = gameDir,
            ["natives_directory"] = nativesDir,
            ["library_directory"] = PathEx.LibrariesDir(gameRoot),
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = GameConstants.LauncherName,
            ["launcher_version"] = GameConstants.LauncherVersion,
            ["max_mem"] = $"{options.MaxMemoryMb}M"
        };

        if (options.Resolution.HasValue)
        {
            vars["resolution_width"] = options.Resolution.Value.Width.ToString();
            vars["resolution_height"] = options.Resolution.Value.Height.ToString();
        }

        return vars;
    }

    /// <summary>把一行启动器日志同时推给调用方 logger（状态栏）与磁盘日志文件（logs/mclcs_launcher.log，可在日志页查看）。</summary>
    private static void LogLine(ILogger? logger, string gameRoot, string message)
    {
        logger?.Log(message);
        AppendLauncherLog(gameRoot, message);
    }

    /// <summary>追加一行到启动器自身日志文件（logs/mclcs_launcher.log），供日志页查看。</summary>
    private static void AppendLauncherLog(string gameRoot, string message)
    {
        try
        {
            var dir = LogManager.LogsDir(gameRoot);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mclcs_launcher.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* 日志写入失败不得阻塞启动 */ }
    }

    /// <summary>启动游戏并等待退出，随后检测崩溃报告。</summary>
    public static async Task<LaunchResult> LaunchAsync(string gameRoot,
        string versionId,
        JavaInfo java,
        LaunchOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var merged = VersionMerger.Merge(gameRoot, versionId);
        var nativesDir = PathEx.NativesDir(gameRoot, versionId);
        Directory.CreateDirectory(nativesDir);

        // 有效游戏工作目录：每版本覆盖（options.GameDir）优先，否则按隔离标记决定
        var gameDir = options.GameDir ?? VersionIsolation.GameDirFor(gameRoot, versionId);
        if (!string.Equals(gameDir, gameRoot, StringComparison.Ordinal))
        {
            VersionIsolation.EnsureFolders(gameDir);
            LogLine(logger, gameRoot, $"版本 {versionId} 已启用隔离/自定义目录，工作目录：{gameDir}");
        }

        // 注入 logging 日志配置（下载 log4j XML 并注入 -Dlog4j.configurationFile）
        var loggingArgs = await InjectLoggingConfigAsync(gameRoot, versionId, merged, ct);

        var variables = BuildVariables(merged, gameRoot, versionId, nativesDir, options, gameDir);
        var resolved = ArgumentProcessor.Process(merged, variables, options, nativesDir);

        // 将 logging 参数追加到 JVM 参数末尾
        if (loggingArgs.Count > 0)
            resolved.JvmArgs.AddRange(loggingArgs);

        // 直接连入服务器：追加 --server <host> --port <port>
        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            var parts = options.ServerAddress.Split(':');
            resolved.GameArgs.Add("--server");
            resolved.GameArgs.Add(parts[0].Trim());
            resolved.GameArgs.Add("--port");
            resolved.GameArgs.Add(parts.Length > 1 ? parts[1].Trim() : "25565");
        }

        // 全屏启动
        if (options.Fullscreen)
            resolved.GameArgs.Add("--fullscreen");

        // 解压原生库
        var natives = ClasspathBuilder.GetNativeEntries(gameRoot, merged, nativesDir);
        ClasspathBuilder.ExtractNatives(natives);

        var psi = new ProcessStartInfo
        {
            FileName = java.JavaExe,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = gameDir
        };

        foreach (var a in resolved.JvmArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(resolved.MainClass);
        foreach (var a in resolved.GameArgs) psi.ArgumentList.Add(a);

        LogLine(logger, gameRoot, $"启动版本 {merged.Id}（{java.MajorVersion}）：{resolved.MainClass}");

        // 调试导出：原样打印完整启动命令，便于在本机 build 后从日志里复制逐字可运行的命令行。
        // 含空格的参数加引号，方便直接粘贴。
        var cmdLine = string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        LogLine(logger, gameRoot, $"启动命令：{psi.FileName} {cmdLine}");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动游戏进程");

        InstanceTracker.Register(proc.Id, versionId);

        // bug #28：进程已就绪，通知上层触发 HUD 叠加层（覆盖全部启动路径，无 1.5s 竞态）。
        // 同时把 -Xmx 解析出的最大堆内存（MB）传给 HUD，用于内存占用百分比显示。
        var maxMemoryMb = ParseMaxMemoryMb(resolved.JvmArgs);
        GameProcessStarted?.Invoke(proc, maxMemoryMb);

        await proc.WaitForExitAsync(ct);
        var exitCode = proc.ExitCode;

        // 隔离版本的崩溃报告落在自己的工作目录下
        var crash = CrashDetector.FindLatestCrashReport(gameDir);
        if (crash is not null)
            LogLine(logger, gameRoot, $"检测到崩溃报告：{crash}（退出码 {exitCode}）");
        else
            LogLine(logger, gameRoot, $"游戏进程已退出（退出码 {exitCode}），未检测到崩溃报告。");

        var result = new LaunchResult { ExitCode = exitCode, CrashReportPath = crash };

        // 崩溃时自动分析并规划修复方案（离线、非破坏性路径）
        if (crash is not null)
        {
            try
            {
                var text = await File.ReadAllTextAsync(crash, ct);
                var analysis = CrashAnalyzer.Analyze(text);
                analysis.RawReport = text;
                var profile = ProfileStore.Load(gameRoot);
                var plan = CrashRepairEngine.BuildPlan(analysis, profile, java, gameRoot, versionId);
                result.Analysis = analysis;
                result.RepairPlan = plan;
                LogLine(logger, gameRoot, $"崩溃类别：{analysis.Category}，可自动修复：{plan.CanRepair}（{plan.Strategy}）");
            }
            catch (Exception ex)
            {
                LogLine(logger, gameRoot, $"崩溃分析失败：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 下载 log4j 配置文件并注入 -Dlog4j.configurationFile 等 JVM 参数。
    /// 解析 version.json 中的 logging.client，下载配置文件到版本目录。
    /// </summary>
    private static async Task<List<string>> InjectLoggingConfigAsync(string gameRoot,
        string versionId, VersionJson merged, CancellationToken ct)
    {
        var args = new List<string>();
        var logging = merged.Logging?.Client;
        if (logging is null || logging.File is null) return args;

        try
        {
            // 下载日志配置文件到版本目录
            var loggingDir = Path.Combine(PathEx.VersionDir(gameRoot, versionId), "logging");
            Directory.CreateDirectory(loggingDir);

            var destPath = Path.Combine(loggingDir, logging.File.Url?.Split('/').Last() ?? "log4j.xml");
            if (!File.Exists(destPath) && logging.File.Url is not null)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var data = await client.GetByteArrayAsync(logging.File.Url, ct);
                await File.WriteAllBytesAsync(destPath, data, ct);
            }

            if (File.Exists(destPath))
            {
                // 注入 log4j 配置
                args.Add($"-Dlog4j.configurationFile={destPath}");
            }

            // 如果有 argument 模板（如 "-Dlog4j2.formatMsgNoLookups=true"），也注入
            if (!string.IsNullOrEmpty(logging.Argument))
            {
                // argument 可能含 ${path} 占位，替换为实际路径
                var arg = logging.Argument.Replace("${path}", destPath);
                args.Add(arg);
            }
        }
        catch
        {
            // logging 配置失败不阻塞启动
        }

        return args;
    }
}
