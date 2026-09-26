using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCLCS.Core.Ai;

/// <summary>Ollama 本地服务运行状态。</summary>
public enum OllamaServiceStatus
{
    NotRunning = 0,
    Starting = 1,
    Running = 2
}

/// <summary>本地部署可选模型信息。</summary>
public class LocalModelInfo
{
    /// <summary>显示名，如 Qwen2.5-Coder-1.5B。</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>对应的 Ollama tag，如 qwen2.5-coder:1.5b。</summary>
    public string OllamaTag { get; set; } = "";
    /// <summary>模型体积（GB）。</summary>
    public double SizeGb { get; set; }
    /// <summary>推荐标签，如【默认推荐】。</summary>
    public string RecommendTag { get; set; } = "";
    /// <summary>UI 副文案。</summary>
    public string SubText { get; set; } = "";

    public override string ToString() => DisplayName;
}

/// <summary>本地模型目录（设置 → AI 助手 → 本地部署）。</summary>
public static class OllamaModels
{
    public static IReadOnlyList<LocalModelInfo> Catalog { get; } = new List<LocalModelInfo>
    {
        new()
        {
            DisplayName = "Qwen2.5-Coder-1.5B",
            OllamaTag = "qwen2.5-coder:1.5b",
            SizeGb = 0.9,
            RecommendTag = "【默认推荐】",
            SubText = "中文最优，体积最小，速度最快。覆盖 90% 的崩溃翻译与 Mod 推荐。"
        },
        new()
        {
            DisplayName = "InternLM2-1.8B",
            OllamaTag = "internlm2:1.8b",
            SizeGb = 1.1,
            RecommendTag = "【长日志特化】",
            SubText = "2000 行以上超长崩溃日志分析更强，精准定位 Mod 冲突。"
        },
        new()
        {
            DisplayName = "Phi-3.5-mini-3.8B",
            OllamaTag = "phi3.5:mini",
            SizeGb = 2.2,
            RecommendTag = "【硬核高配】",
            SubText = "复杂 Forge/Fabric 混合堆栈逻辑最强。建议内存 ≥ 16GB。"
        }
    };

    public static LocalModelInfo Default => Catalog[0];

    public static LocalModelInfo? ByDisplayName(string? displayName) =>
        Catalog.FirstOrDefault(m => m.DisplayName == displayName);

    public static LocalModelInfo? ByTag(string? tag) =>
        Catalog.FirstOrDefault(m => string.Equals(m.OllamaTag, tag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Ollama 本地部署管理器：检测安装、拉取模型、查询服务状态、启动服务。
/// 所有网络/进程异常均被捕获，失败时返回安全默认值，不阻塞主流程。
/// </summary>
public static class OllamaManager
{
    public const string BaseUrl = "http://127.0.0.1:11434";

    /// <summary>安装器地址：Windows 为官方 exe，其它平台为官方安装脚本。</summary>
    public static string InstallerUrl =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "https://ollama.com/download/OllamaSetup.exe"
            : "https://ollama.com/install.sh";

    /// <summary>解析 `ollama --version` 输出中的版本号（如 "ollama version 0.1.2" → "0.1.2"）。</summary>
    public static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>检测 Ollama 是否已安装及版本号（通过 `ollama --version`）。</summary>
    public static async Task<(bool Installed, string Version)> DetectAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("ollama", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var v = ParseVersion(outp);
            return (v is not null, v ?? "");
        }
        catch
        {
            return (false, "");
        }
    }

    /// <summary>查询 Ollama 服务是否可达。</summary>
    public static async Task<OllamaServiceStatus> GetServiceStatusAsync(HttpClient? client = null)
    {
        var own = client is null;
        try
        {
            client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await client.GetAsync(BaseUrl + "/api/tags");
            return resp.IsSuccessStatusCode ? OllamaServiceStatus.Running : OllamaServiceStatus.NotRunning;
        }
        catch
        {
            return OllamaServiceStatus.NotRunning;
        }
        finally
        {
            if (own) client?.Dispose();
        }
    }

    /// <summary>判断指定模型是否已拉取（出现在 /api/tags 列表中）。</summary>
    public static async Task<bool> IsModelPulledAsync(string tag, HttpClient? client = null)
    {
        var own = client is null;
        try
        {
            client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await client.GetAsync(BaseUrl + "/api/tags");
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var n) &&
                        string.Equals(n.GetString(), tag, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (own) client?.Dispose();
        }
    }

    /// <summary>
    /// 安装 Ollama：下载安装器到临时目录并运行（静默安装），进度回报 0→1。
    /// 取消时终止安装进程并清理临时文件。仅 Windows 提供 exe 安装器；其它平台下载脚本后由用户手动执行。
    /// </summary>
    public static async Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mclcs_ollama_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Process? installer = null;
        try
        {
            var target = Path.Combine(tmp, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "OllamaSetup.exe" : "install.sh");
            await DownloadFileAsync(InstallerUrl, target, new Progress<double>(p => progress?.Report(p * 0.95)), ct);
            progress?.Report(0.96);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo(target, "/S")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                installer = Process.Start(psi);
            }
            else
            {
                // 非 Windows：赋予执行权限并运行官方安装脚本
                try { Process.Start("chmod", $"+x {target}")?.WaitForExit(); } catch { }
                installer = Process.Start("/bin/sh", target);
            }

            if (installer is null)
                throw new InvalidOperationException("无法启动 Ollama 安装程序");

            using (ct.Register(() =>
            {
                try { installer.Kill(); } catch { }
            }))
            {
                await installer.WaitForExitAsync(ct);
            }
            progress?.Report(1.0);
        }
        finally
        {
            try { installer?.Kill(); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 拉取模型（ollama pull）。从 stdout 解析下载进度（completed/total），支持取消（终止进程）。
    /// </summary>
    public static async Task PullModelAsync(string tag, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ollama", $"pull {tag}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("未找到 ollama，请先安装 Ollama");
        using (ct.Register(() =>
        {
            try { p.Kill(); } catch { }
        }))
        {
            var stdout = p.StandardOutput;
            var buf = new char[1];
            var sb = new StringBuilder();
            while (!stdout.EndOfStream && !ct.IsCancellationRequested)
            {
                int read = await stdout.ReadAsync(buf, 0, 1);
                if (read == 0) break;
                sb.Append(buf[0]);
                if (buf[0] == '\n')
                {
                    var line = sb.ToString();
                    sb.Clear();
                    var frac = ParseProgress(line);
                    if (frac.HasValue) progress?.Report(frac.Value);
                }
            }
            if (!p.HasExited) await p.WaitForExitAsync(ct);
        }
        progress?.Report(1.0);
    }

    private static double? ParseProgress(string line)
    {
        // 形如: pulling 7cdf.... [====>                          ]  10 MB/  900 MB
        var m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*(MB|GB)\s*/\s*(\d+(?:\.\d+)?)\s*(MB|GB)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var cur = ParseSize(m.Groups[1].Value, m.Groups[2].Value);
        var total = ParseSize(m.Groups[3].Value, m.Groups[4].Value);
        if (total <= 0) return null;
        return Math.Min(1.0, cur / total);
    }

    private static double ParseSize(string num, string unit)
    {
        var v = double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
        return unit.Equals("GB", StringComparison.OrdinalIgnoreCase) ? v * 1024.0 : v;
    }

    /// <summary>后台启动 Ollama 服务（ollama serve），返回进程；调用方负责轮询状态。</summary>
    public static Process? StartService()
    {
        try
        {
            var psi = new ProcessStartInfo("ollama", "serve")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[8192];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)read / total));
        }
    }
}
