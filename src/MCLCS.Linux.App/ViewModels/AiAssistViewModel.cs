using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using MCLCS.Core.Ai;
using MCLCS.Core.Launcher;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Statistics;
using MCLCS.Linux.App;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>聊天消息：role 为 user / assistant。</summary>
public class ChatMessage : ObservableObject
{
    public string Role { get; }
    public string Content { get; }
    public bool IsUser => Role == "user";

    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>AI 助手面板（工具箱 aichat）：单页聊天界面。
/// 自由输入走 Assistant.ChatAsync；另保留崩溃解读 / Mod 翻译 / 配装推荐 / 年度总结 快捷操作，避免功能回退。</summary>
public class AiAssistViewModel : ObservableObject
{
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    private string _inputText = "";
    public string InputText { get => _inputText; set => SetField(ref _inputText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    public bool AiEnabled => Assistant.Config.Enabled;

    private Bitmap? _assistantLogo;
    public Bitmap? AssistantLogo
    {
        get => _assistantLogo;
        private set => SetField(ref _assistantLogo, value);
    }

    private bool _hasLogo;
    public bool HasLogo
    {
        get => _hasLogo;
        private set => SetField(ref _hasLogo, value);
    }

    public ICommand SendCommand => new AsyncRelayCommand(_ => SendAsync(), _ => !IsBusy);
    public ICommand CrashCommand => new AsyncRelayCommand(_ => CrashAnalyzeAsync(), _ => !IsBusy);
    public ICommand TranslateCommand => new AsyncRelayCommand(_ => TranslateAsync(), _ => !IsBusy);
    public ICommand RecommendCommand => new AsyncRelayCommand(_ => RecommendAsync(), _ => !IsBusy);
    public ICommand SummaryCommand => new AsyncRelayCommand(_ => SummaryAsync(), _ => !IsBusy);

    public AiAssistViewModel()
    {
        // 设计稿问候语（首条助手气泡）
        Messages.Add(new ChatMessage("assistant",
            "你好！我是 MCLCS AI 助手。可直接输入问题，支持崩溃分析、Mod 推荐、翻译等。"));
        _ = LoadAssistantLogoAsync();   // 异步拉取部署 AI 的 logo，失败则保持 null → emoji 兜底
    }

    // ---- 助手头像：按后端品牌拉取 favicon，失败回退 emoji ----
    private async Task LoadAssistantLogoAsync()
    {
        try
        {
            var domain = ResolveProviderDomain();
            if (string.IsNullOrEmpty(domain)) return;

            var cacheDir = Path.Combine(Path.GetTempPath(), "MCLCS");
            Directory.CreateDirectory(cacheDir);
            var cacheFile = Path.Combine(cacheDir, domain + ".png");

            byte[] data;
            if (File.Exists(cacheFile))
            {
                data = await File.ReadAllBytesAsync(cacheFile);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var url = $"https://www.google.com/s2/favicons?domain={domain}&sz=128";
                data = await client.GetByteArrayAsync(url);
                try { await File.WriteAllBytesAsync(cacheFile, data); } catch { /* 缓存写入失败忽略 */ }
            }

            // Avalonia 在构造时即完整解码，using 流安全
            using var ms = new MemoryStream(data);
            AssistantLogo = new Bitmap(ms);
            HasLogo = true;
        }
        catch
        {
            // 离线/超时/解码失败：保持 AssistantLogo=null、HasLogo=false → XAML 显示 🤖
        }
    }

    /// <summary>根据当前 AI 后端配置推断品牌域名，用于拉取 favicon。</summary>
    private static string ResolveProviderDomain()
    {
        if (Assistant.Config.Mode == AiMode.Local)
            return "ollama.com";

        var ep = Assistant.Config.Endpoint ?? "";
        if (string.IsNullOrWhiteSpace(ep)) return "";
        string host;
        try { host = new Uri(ep).Host; }
        catch { return ""; }
        if (string.IsNullOrWhiteSpace(host)) return "";
        var h = host.ToLowerInvariant();

        if (h.Contains("openai.com")) return "openai.com";
        if (h.Contains("deepseek.com")) return "deepseek.com";
        if (h.Contains("anthropic.com")) return "anthropic.com";
        if (h.Contains("moonshot.cn")) return "moonshot.cn";          // Kimi
        if (h.Contains("aliyun.com") || h.Contains("dashscope")) return "aliyun.com"; // 通义 / qwen
        if (h.Contains("mistral.ai")) return "mistral.ai";
        if (h.Contains("groq.com")) return "groq.com";
        if (h.Contains("googleapis.com")) return "google.com";
        return GetRegistrableDomain(host);
    }

    /// <summary>简化版注册域名提取（无额外依赖；未知品牌取二级域名，常见二级公共后缀单独处理）。</summary>
    private static string GetRegistrableDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var lastTwo = parts[^2] + "." + parts[^1];
        var twoLevelTlds = new[] { "co.uk", "com.cn", "org.cn", "net.cn", "com.au", "co.jp" };
        return Array.Exists(twoLevelTlds, t => t == lastTwo)
            ? parts[^3] + "." + lastTwo
            : lastTwo;
    }

    // ---- 自由对话 ----
    private async Task SendAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputText = "";
        Messages.Add(new ChatMessage("user", text));
        IsBusy = true;
        try
        {
            var reply = await Assistant.ChatAsync(text);
            Messages.Add(new ChatMessage("assistant", reply));
        }
        finally { IsBusy = false; }
    }

    // ---- 快捷操作：崩溃分析 ----
    private async Task CrashAnalyzeAsync()
    {
        IsBusy = true;
        try
        {
            var root = Services.LauncherService.Instance.GameRoot;
            var latest = CrashDetector.FindLatestCrashReport(root);
            if (latest is null)
            {
                Messages.Add(new ChatMessage("user", "帮我分析上次崩溃"));
                Messages.Add(new ChatMessage("assistant",
                    "未找到崩溃报告文件（crash-reports 目录为空）。如有日志，可直接粘贴到下方输入框，我会帮你分析。"));
                return;
            }
            Messages.Add(new ChatMessage("user", $"帮我分析上次崩溃（{Path.GetFileName(latest)}）"));
            var result = await Assistant.InterpretCrashAsync(File.ReadAllText(latest));
            Messages.Add(new ChatMessage("assistant", result));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("assistant", $"分析失败：{ex.Message}"));
        }
        finally { IsBusy = false; }
    }

    // ---- 快捷操作：Mod 描述翻译 ----
    private async Task TranslateAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = "请在输入框粘贴 Mod 描述后点击「Mod 翻译」";
            return;
        }
        Messages.Add(new ChatMessage("user", $"请翻译这段 Mod 描述：\n{text}"));
        InputText = "";
        IsBusy = true;
        try
        {
            var r = await Assistant.TranslateModDescriptionAsync(text);
            Messages.Add(new ChatMessage("assistant", r));
        }
        finally { IsBusy = false; }
    }

    // ---- 快捷操作：配装推荐 ----
    private async Task RecommendAsync()
    {
        var pref = InputText?.Trim();
        if (string.IsNullOrEmpty(pref))
        {
            StatusMessage = "请在输入框描述你的玩法偏好后点击「配装推荐」";
            return;
        }
        Messages.Add(new ChatMessage("user", $"帮我推荐适合的 Mod：{pref}"));
        InputText = "";
        IsBusy = true;
        try
        {
            var r = Assistant.Config.Enabled
                ? await Assistant.InterpretCrashAsync($"请根据以下偏好推荐5个Minecraft Mod（仅列名称和简要理由）：{pref}")
                : "AI 未启用，请在「设置 → AI 助手」中开启后使用此功能。";
            Messages.Add(new ChatMessage("assistant", r));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("assistant", $"推荐失败：{ex.Message}"));
        }
        finally { IsBusy = false; }
    }

    // ---- 快捷操作：年度总结 ----
    private async Task SummaryAsync()
    {
        Messages.Add(new ChatMessage("user", "生成我的年度总结"));
        IsBusy = true;
        try
        {
            if (!AiEnabled)
            {
                Messages.Add(new ChatMessage("assistant", "AI 未启用，请在「设置 → AI 助手」中开启后使用此功能。"));
                return;
            }
            var data = AnnualReport.GenerateFrom(Services.LauncherService.Instance.GameRoot, DateTime.Now.Year);
            var md = data.HasData ? AnnualReport.RenderMarkdown(data) : "今年还没有游玩记录。";
            var r = await Assistant.InterpretCrashAsync($"请将以下年度游戏报告总结成一段100字以内的话：\n{md}");
            Messages.Add(new ChatMessage("assistant", r));
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("assistant", $"生成失败：{ex.Message}"));
        }
        finally { IsBusy = false; }
    }
}
