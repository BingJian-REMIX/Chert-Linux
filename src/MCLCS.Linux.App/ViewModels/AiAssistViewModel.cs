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

    private const string WelcomeText =
        "你好！我是 MCLCS AI 助手。可直接输入问题，支持崩溃分析、Mod 推荐、翻译等。";

    /// <summary>自由对话的人设。快捷操作各自有更贴合的 system，不共用这一条。</summary>
    private const string ChatSystemPrompt =
        "你是「MCLCS 启动器」内置的 AI 助手，用中文回答。涉及 Minecraft / 启动器 / Mod / 崩溃的问题请给出可操作步骤，" +
        "不要复述用户的问题；不确定时直说不确定。";

    // 上下文窗口：条数与字数两个闸门，任一超了就从最老的开始丢
    private const int MaxContextMessages = 20;
    private const int MaxContextChars = 12_000;

    /// <summary>发给模型的上下文。与界面气泡<b>不是一回事</b>：模型没参与的回答（未启用 / 调用失败 / 本地回退）
    /// 只显示在界面上，不进这里 —— 否则「AI 未启用」会被当成助手自己说过的话，一路污染后续对话。</summary>
    private readonly List<AiChatMessage> _context = new();

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
    public ICommand ClearCommand => new RelayCommand(_ => ClearConversation(), _ => !IsBusy);

    public AiAssistViewModel()
    {
        // 设计稿问候语（首条助手气泡）
        Messages.Add(new ChatMessage("assistant", WelcomeText));
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

    // ---- 自由对话（带上下文）----
    private async Task SendAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputText = "";

        Messages.Add(new ChatMessage("user", text));
        var context = WithPending(AiChatMessage.User(text));
        IsBusy = true;
        try
        {
            var result = await Assistant.ChatAsync(context, ChatSystemPrompt);
            AppendReply(result, userText: text);
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
            var prompt = $"帮我分析上次崩溃（{Path.GetFileName(latest)}）";
            Messages.Add(new ChatMessage("user", prompt));
            var result = await Assistant.InterpretCrashAsync(File.ReadAllText(latest));
            AppendReply(result, userText: prompt);
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
        var prompt = $"请翻译这段 Mod 描述：\n{text}";
        Messages.Add(new ChatMessage("user", prompt));
        InputText = "";
        IsBusy = true;
        try
        {
            var result = await Assistant.TranslateModDescriptionAsync(text);
            AppendReply(result, userText: prompt);
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("assistant", $"翻译失败：{ex.Message}"));
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
        var prompt = $"帮我推荐适合的 Mod：{pref}";
        Messages.Add(new ChatMessage("user", prompt));
        InputText = "";
        IsBusy = true;
        try
        {
            // 以前这里调的是崩溃解读接口，prompt 会被拼上「说明崩溃原因」—— 现在走专门的推荐接口
            var result = await Assistant.RecommendModsAsync(pref);
            AppendReply(result, userText: prompt);
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
        IsBusy = true;
        try
        {
            var prompt = "生成我的年度总结";
            Messages.Add(new ChatMessage("user", prompt));

            var data = AnnualReport.GenerateFrom(Services.LauncherService.Instance.GameRoot, DateTime.Now.Year);
            var md = data.HasData ? AnnualReport.RenderMarkdown(data) : "今年还没有游玩记录。";
            var result = await Assistant.SummarizeAsync(md, "请把这份年度游戏报告总结成一段 100 字以内的话");
            AppendReply(result, userText: prompt);
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("assistant", $"生成失败：{ex.Message}"));
        }
        finally { IsBusy = false; }
    }

    /// <summary>清空对话（界面与模型上下文一起清）。</summary>
    private void ClearConversation()
    {
        _context.Clear();
        Messages.Clear();
        Messages.Add(new ChatMessage("assistant", WelcomeText));
        StatusMessage = "已清空对话，模型不再记得前面的内容。";
    }

    // ---- 上下文维护 ----

    /// <summary>把这一轮的新消息接到历史后面（不改动历史本身，失败时可以直接丢弃）。</summary>
    private List<AiChatMessage> WithPending(AiChatMessage pending)
    {
        var list = new List<AiChatMessage>(_context.Count + 1);
        list.AddRange(_context);
        list.Add(pending);
        return list;
    }

    /// <summary>展示回复，并按「模型是否真的参与」决定是否进上下文。</summary>
    private void AppendReply(AiResult result, string? userText = null)
    {
        if (result.FromAi)
        {
            if (!string.IsNullOrEmpty(userText)) _context.Add(AiChatMessage.User(userText!));
            _context.Add(AiChatMessage.Assistant(result.Text));
            TrimContext();
            StatusMessage = "";
        }
        else
        {
            StatusMessage = result.Error ?? "这次模型没参与，下面是本地规则给出的结果。";
        }

        Messages.Add(new ChatMessage("assistant", result.Text));
    }

    private void TrimContext()
    {
        while (_context.Count > MaxContextMessages) _context.RemoveAt(0);

        long total = 0;
        foreach (var m in _context) total += m.Content.Length;
        while (total > MaxContextChars && _context.Count > 1)
        {
            total -= _context[0].Content.Length;
            _context.RemoveAt(0);
        }
    }
}
