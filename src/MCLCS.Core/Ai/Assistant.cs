using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCLCS.Core.Profiles;

namespace MCLCS.Core.Ai;

/// <summary>AI 助手运行模式：本地部署（Ollama）或外部 API。</summary>
public enum AiMode { Local, External }

/// <summary>AI 助手配置（设置 → AI 助手）。</summary>
public class AiConfig
{
    public bool Enabled { get; set; }
    /// <summary>部署方式，默认外部 API（零下载、填 Key 即用）。</summary>
    public AiMode Mode { get; set; } = AiMode.External;
    /// <summary>本地部署选中的模型（Ollama tag），默认 Qwen2.5-Coder-1.5B。</summary>
    public string SelectedLocalModel { get; set; } = OllamaModels.Default.OllamaTag;

    /// <summary>
    /// API Key 的<b>落盘形态</b>：写入配置文件前一律经 <see cref="ApiCredentialProtector"/> 混淆。
    /// 老配置里可能是明文（没有版本前缀，解不出来），读取时按明文兼容 —— 见 <see cref="Assistant.RevealApiKey"/>。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>运行时明文 Key（<b>仅内存</b>，不落盘）。设置页装载后填这里，请求时优先取它。</summary>
    [JsonIgnore]
    public string? ApiKeyPlain { get; set; }

    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; set; } = "gpt-4o-mini";
    public bool CrashInterpret { get; set; } = true;
    public bool RecommendReason { get; set; } = true;
    public bool ModTranslate { get; set; } = true;
}

/// <summary>一条对话消息。role 为 system / user / assistant —— 与 OpenAI 兼容接口、Ollama /api/chat 同名同义。</summary>
public sealed record AiChatMessage(string Role, string Content)
{
    public static AiChatMessage System(string content) => new("system", content);
    public static AiChatMessage User(string content) => new("user", content);
    public static AiChatMessage Assistant(string content) => new("assistant", content);
}

/// <summary>
/// 一次 AI 调用的产出。
/// <para>关键在 <see cref="FromAi"/>：模型不可用 / 相应能力开关被关掉时，各功能都会静默回退到本地启发式或原文，
/// 返回值和「AI 真的说了一段话」长得一模一样。调用方必须靠这个字段区分，否则会把「原文」当成「翻译完成」告诉用户。</para>
/// </summary>
public sealed class AiResult
{
    public string Text { get; init; } = "";
    /// <summary>true = 这段文本来自模型；false = 本地启发式 / 原文回退。</summary>
    public bool FromAi { get; init; }
    /// <summary>失败原因（可空）。未启用时也会给出一句可直接展示给用户的话。</summary>
    public string? Error { get; init; }

    public static AiResult Ok(string text) => new() { Text = text, FromAi = true };
    public static AiResult Fallback(string text, string? error = null) => new() { Text = text, FromAi = false, Error = error };
}

/// <summary>AI 助手（全局功能 8 / 设置 AI 助手）：本地部署 Ollama 或外部 OpenAI 兼容 API，
/// 用于多轮对话、崩溃解读、推荐理由生成、Mod 描述翻译、年度总结。
/// 所有网络/进程异常都被捕获，失败时回退到本地启发式，保证不阻塞主流程。</summary>
public static class Assistant
{
    public static AiConfig Config { get; set; } = new();

    private const string DisabledHint =
        "AI 助手当前未启用。请在「设置 → AI 助手」中启用外部 API 或本地 Ollama 部署后重试。";

    private const string FailureHint =
        "这次没有拿到模型的回复（未启用、Key 不对、地址不通或服务没起）。已按本地规则给出结果，可在「设置 → AI 助手」里点「测试连接」确认。";

    // 单次提问的字数上限：崩溃日志动辄几万字符，全丢进去既烧 token 又容易被服务端直接拒掉
    private const int MaxCrashChars = 6_000;
    private const int MaxTranslateChars = 8_000;
    private const int MaxSummaryChars = 6_000;

    // 外部接口通常几秒内返回；Ollama 首次要把模型载入显存，慢得多
    private static readonly TimeSpan ExternalTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 共享 HttpClient。原来每次调用都 new 一个：短时间里连续翻译/连问会攒下一批 TIME_WAIT 套接字，
    /// 本机端口耗尽后表现为「明明有网却连不上」。
    /// </summary>
    private static readonly Lazy<HttpClient> Shared = new(() =>
        new HttpClient { Timeout = LocalTimeout + TimeSpan.FromSeconds(10) });

    /// <summary>根据 API 地址自动推断默认模型名。</summary>
    public static string SuggestModelForEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "";
        var e = endpoint.ToLowerInvariant();
        if (e.Contains("api.openai.com")) return "gpt-4o-mini";
        if (e.Contains("api.deepseek.com")) return "deepseek-chat";
        if (e.Contains("127.0.0.1") || e.Contains("localhost")) return "";
        return "";
    }

    // ===== Key 的存取 =====

    /// <summary>
    /// 取出可用的明文 Key：优先用内存里的明文，其次解落盘的那份；
    /// 解不开（老配置是明文 / 配置被拷到别的机器）就原样返回，由服务端去判它对不对。
    /// </summary>
    public static string RevealApiKey(AiConfig? config = null)
    {
        var c = config ?? Config;
        if (!string.IsNullOrEmpty(c.ApiKeyPlain)) return c.ApiKeyPlain;
        return ApiCredentialProtector.TryUnprotect(c.ApiKey) ?? c.ApiKey ?? "";
    }

    /// <summary>写入 Key：明文只留在内存，落盘的那份先混淆。<paramref name="plain"/> 为空时清空两边。</summary>
    public static void CommitApiKey(string? plain, AiConfig? config = null)
    {
        var c = config ?? Config;
        if (string.IsNullOrWhiteSpace(plain))
        {
            c.ApiKey = null;
            c.ApiKeyPlain = null;
            return;
        }
        c.ApiKeyPlain = plain;
        c.ApiKey = ApiCredentialProtector.Protect(plain);
    }

    // ===== 能力 =====

    /// <summary>多轮对话。<paramref name="history"/> 是完整的上下文（含这一轮的用户提问），
    /// 按时间顺序排列 —— 只给「本轮一句话」的话，模型答不出「那第二个呢」这类追问。</summary>
    public static async Task<AiResult> ChatAsync(
        IReadOnlyList<AiChatMessage> history,
        string? system = null,
        double temperature = 0.7,
        CancellationToken ct = default)
    {
        if (history is null || history.Count == 0) return AiResult.Fallback("");
        if (!Config.Enabled) return AiResult.Fallback(DisabledHint, DisabledHint);

        var messages = new List<AiChatMessage>(history.Count + 1);
        if (!string.IsNullOrWhiteSpace(system)) messages.Add(AiChatMessage.System(system));
        messages.AddRange(history);

        var reply = await CompleteAsync(messages, temperature, ct).ConfigureAwait(false);
        return reply is null ? AiResult.Fallback(FailureHint, FailureHint) : AiResult.Ok(reply);
    }

    /// <summary>崩溃解读：将崩溃片段转为人类可读的结论与建议。</summary>
    public static async Task<AiResult> InterpretCrashAsync(string crashText, CancellationToken ct = default)
    {
        var fallback = AiResult.Fallback(LocalCrashInterpret(crashText));
        if (!Config.Enabled) return AiResult.Fallback(fallback.Text, DisabledHint);
        if (!Config.CrashInterpret) return AiResult.Fallback(fallback.Text, "「崩溃解读」这项能力在设置里被关掉了，下面是本地规则给出的结果。");

        var reply = await CompleteAsync(new[]
        {
            AiChatMessage.System(
                "你是 Minecraft 启动器内置的崩溃分析助手。请用中文回答，先一句结论，再给 2~3 条可操作的修复步骤；不要复述日志。"),
            AiChatMessage.User("下面是崩溃日志片段：\n" + TrimForPrompt(crashText, MaxCrashChars))
        }, 0.2, ct).ConfigureAwait(false);

        return reply is null ? fallback : AiResult.Ok(reply);
    }

    /// <summary>推荐理由生成（给单个 Mod / 整合包的一句话推荐语）。</summary>
    public static async Task<AiResult> GenerateRecommendationReasonAsync(
        string itemName, string category, string description, CancellationToken ct = default)
    {
        var fallback = AiResult.Fallback($"属于{category}，{Truncate(description, 40)}");
        if (!Config.Enabled) return AiResult.Fallback(fallback.Text, DisabledHint);
        if (!Config.RecommendReason) return AiResult.Fallback(fallback.Text, "「推荐理由」这项能力在设置里被关掉了。");
        if (string.IsNullOrWhiteSpace(itemName)) return fallback;

        var reply = await CompleteAsync(new[]
        {
            AiChatMessage.System(
                "你是 Minecraft 内容推荐助手。只输出一句中文推荐语，不超过 40 字，不要出现引号和前缀。"),
            AiChatMessage.User($"请用一句中文说明为什么向玩家推荐这个{category}『{itemName}』：{Truncate(description, 300)}")
        }, 0.6, ct).ConfigureAwait(false);

        return reply is null ? fallback : AiResult.Ok(reply);
    }

    /// <summary>按玩法偏好推荐 Mod 清单（聊天页「配装推荐」）。
    /// 以前这里是拿崩溃解读的接口凑的 —— prompt 会被拼上「说明崩溃原因」，输出全靠模型自己纠错。</summary>
    public static async Task<AiResult> RecommendModsAsync(string preference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(preference)) return AiResult.Fallback("");
        var fallback = AiResult.Fallback("AI 未启用或未连通，先按常见搭配试试：性能优化装 Sodium/Lithium，探索类装 JEI + 地图mod。");
        if (!Config.Enabled) return AiResult.Fallback(fallback.Text, DisabledHint);
        if (!Config.RecommendReason) return AiResult.Fallback(fallback.Text, "「推荐理由」这项能力在设置里被关掉了。");

        var reply = await CompleteAsync(new[]
        {
            AiChatMessage.System(
                "你是 Minecraft Mod 搭配顾问。按用户偏好推荐 5 个 Mod，每条一行：「名称 —— 一句话理由」。不要多余开场白。"),
            AiChatMessage.User($"我的玩法偏好：{preference}")
        }, 0.7, ct).ConfigureAwait(false);

        return reply is null ? fallback : AiResult.Ok(reply);
    }

    /// <summary>Mod 描述翻译（默认译为中文）。</summary>
    public static async Task<AiResult> TranslateModDescriptionAsync(
        string text, string targetLang = "中文", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return AiResult.Fallback(text ?? "");
        // 未启用 / 关了翻译开关：原样返回，但 FromAi=false，调用方要如实告诉用户「没翻」
        if (!Config.Enabled) return AiResult.Fallback(text, DisabledHint);
        if (!Config.ModTranslate) return AiResult.Fallback(text, "「Mod 描述翻译」这项能力在设置里被关掉了。");

        var reply = await CompleteAsync(new[]
        {
            AiChatMessage.System(
                $"你是翻译引擎。把用户给的文本译为{targetLang}，只输出译文，不要解释、不要保留 Markdown 标题以外的额外说明。"),
            AiChatMessage.User(TrimForPrompt(text, MaxTranslateChars))
        }, 0.2, ct).ConfigureAwait(false);

        return reply is null ? AiResult.Fallback(text) : AiResult.Ok(reply);
    }

    /// <summary>把一段既有文本（如年度总结 Markdown）概括成简短结论。</summary>
    public static async Task<AiResult> SummarizeAsync(
        string text, string? instruction = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return AiResult.Fallback("");
        var ask = string.IsNullOrWhiteSpace(instruction) ? "请将以下内容总结成一段 100 字以内的话" : instruction!;
        var body = TrimForPrompt(text, MaxSummaryChars);

        if (!Config.Enabled) return AiResult.Fallback(body, DisabledHint);
        var reply = await CompleteAsync(new[]
        {
            AiChatMessage.System("你是 Minecraft 启动器助手。用中文作答，口语化、有温度，不要罗列数字。"),
            AiChatMessage.User($"{ask}：\n{body}")
        }, 0.6, ct).ConfigureAwait(false);

        return reply is null ? AiResult.Fallback(body) : AiResult.Ok(reply);
    }

    // ---- 本地启发式（无需模型，最终回退）----

    private static string LocalCrashInterpret(string crash)
    {
        var lower = crash.ToLowerInvariant();
        if (lower.Contains("outofmemoryerror") || lower.Contains("java heap space"))
            return "疑似内存不足（OutOfMemoryError）。建议在「设置 → 启动」中调大最大内存，或关闭占用内存较大的 Mod。";
        if (lower.Contains("unsupportedclassversionerror"))
            return "Java 版本不兼容（UnsupportedClassVersionError）。请安装/切换到 Java 21+ 后重试。";
        if (lower.Contains("classnotfoundexception") || lower.Contains("nosuchmethoderror"))
            return "存在缺失或版本冲突的依赖/Mod。建议检查 Mod 前置是否齐全，或禁用最近新增的 Mod。";
        if (lower.Contains("gl_") || lower.Contains("opengl") || lower.Contains("could not create context"))
            return "显卡/OpenGL 相关错误。建议更新显卡驱动，或在设置中切换渲染相关选项。";
        return "未能自动判定崩溃类型，请附带完整日志到社区或客服进一步排查。";
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    /// <summary>过长文本掐头去尾中间砍：崩溃日志里最有价值的是异常头与 Caused by 链，中间的过程栈可以丢。</summary>
    private static string TrimForPrompt(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        var head = (int)(max * 0.6);
        var tail = max - head - 16;
        if (tail <= 0) return text[..max];
        return text[..head] + "\n…（中间省略）…\n" + text[^tail..];
    }

    // ---- 统一出口 ----

    private static async Task<string?> CompleteAsync(
        IReadOnlyList<AiChatMessage> messages, double temperature, CancellationToken ct)
    {
        try
        {
            return Config.Mode == AiMode.External
                ? await CallExternalAsync(messages, temperature, ct).ConfigureAwait(false)
                : await CallOllamaAsync(messages, temperature, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> CallExternalAsync(
        IReadOnlyList<AiChatMessage> messages, double temperature, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Config.Endpoint)) return null;

        var body = new
        {
            model = Config.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature
        };
        var json = JsonSerializer.Serialize(body);

        var key = RevealApiKey();
        var raw = await PostAsync(Config.Endpoint, json,
            string.IsNullOrEmpty(key) ? null : "Bearer " + key, ExternalTimeout, ct).ConfigureAwait(false);
        if (raw is null) return null;

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            return choices[0].GetProperty("message").GetProperty("content").GetString();
        return null;
    }

    private static async Task<string?> CallOllamaAsync(
        IReadOnlyList<AiChatMessage> messages, double temperature, CancellationToken ct)
    {
        var tag = string.IsNullOrEmpty(Config.SelectedLocalModel)
            ? OllamaModels.Default.OllamaTag
            : Config.SelectedLocalModel;

        var body = new
        {
            model = tag,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = false,
            options = new { temperature }
        };

        var raw = await PostAsync(OllamaManager.BaseUrl + "/api/chat", JsonSerializer.Serialize(body),
            null, LocalTimeout, ct).ConfigureAwait(false);
        if (raw is null) return null;

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            return c.GetString();
        return null;
    }

    /// <summary>
    /// 发一次 POST。用共享 client + 每请求独立的取消计时器：外部接口与本地 Ollama 的耐心不一样，
    /// 写死在同一个 HttpClient.Timeout 上必然有一边不合适。
    /// </summary>
    private static async Task<string?> PostAsync(
        string url, string json, string? authorization, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(authorization))
                req.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var resp = await Shared.Value.SendAsync(req, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
