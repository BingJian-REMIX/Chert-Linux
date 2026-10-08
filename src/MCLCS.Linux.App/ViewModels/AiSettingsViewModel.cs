using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Input;
using MCLCS.Core.Ai;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → AI（对齐 WPF SettingsViewModel 的 AI 助手区）：总开关 + 部署方式 +
/// 本地部署面板（Ollama 安装 / 进度 / 模型目录 / 拉取 / 服务灯）+ 外部 API 面板（含测试连接）+
/// 三项能力开关。配置持久化到 LauncherProfile.Ai，保存后同步到全局 Assistant.Config 供运行时消费。
/// </summary>
public class AiSettingsViewModel : ObservableObject
{
    private readonly LauncherProfile _profile;
    private readonly AiConfig _ai;
    private readonly HashSet<string> _pulledTags = new(StringComparer.OrdinalIgnoreCase);

    // ---- 配置（绑定到 profile.Ai 的副本，Save 时回写）----
    private bool _enabled;
    private AiMode _mode = AiMode.External;
    private string _endpoint = "https://api.openai.com/v1/chat/completions";
    private string _model = "gpt-4o-mini";
    private string? _apiKey;
    private string _selectedLocalModel = OllamaModels.Default.DisplayName;
    private string _lastCommittedModel = OllamaModels.Default.DisplayName;
    private bool _crashInterpret = true;
    private bool _recommendReason = true;
    private bool _modTranslate = true;

    // ---- 服务状态（只读探测）----
    private string _status = LocaleManager.T("status.ready");
    private string _serviceStatus = "未知";
    private bool _busy;

    // ---- 本地部署（Ollama）----
    private bool _ollamaInstalled;
    private string _ollamaVersion = "";
    private bool _ollamaInstalling;
    private double _ollamaInstallProgress;
    private string _ollamaInstallText = "";
    private bool _modelDownloading;
    private double _modelDownloadProgress;
    private string _modelDownloadText = "";
    private bool _modelReady;
    private OllamaServiceStatus _ollamaServiceStatus = OllamaServiceStatus.NotRunning;
    private CancellationTokenSource? _ollamaInstallCts;
    private CancellationTokenSource? _modelCts;

    // ---- 配置 ----
    public bool Enabled { get => _enabled; set => SetField(ref _enabled, value); }
    public AiMode Mode
    {
        get => _mode;
        set
        {
            if (SetField(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsLocalMode));
                OnPropertyChanged(nameof(IsExternalMode));
            }
        }
    }
    public bool IsLocalMode => _mode == AiMode.Local;
    public bool IsExternalMode => _mode != AiMode.Local;
    public string Endpoint { get => _endpoint; set => SetField(ref _endpoint, value); }
    public string Model { get => _model; set => SetField(ref _model, value); }
    public string? ApiKey { get => _apiKey; set => SetField(ref _apiKey, value); }
    public bool CrashInterpret { get => _crashInterpret; set => SetField(ref _crashInterpret, value); }
    public bool RecommendReason { get => _recommendReason; set => SetField(ref _recommendReason, value); }
    public bool ModTranslate { get => _modTranslate; set => SetField(ref _modTranslate, value); }

    // ---- 服务状态 ----
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string ServiceStatus { get => _serviceStatus; set => SetField(ref _serviceStatus, value); }
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    // ---- 本地部署（Ollama）----
    public IReadOnlyList<LocalModelInfo> LocalModels => OllamaModels.Catalog;

    /// <summary>本地模型选择（DisplayName 语义，与 WPF ComboBox SelectedValuePath=DisplayName 对齐）。</summary>
    public string SelectedLocalModel
    {
        get => _selectedLocalModel;
        set
        {
            if (SetField(ref _selectedLocalModel, value))
            {
                OnPropertyChanged(nameof(SelectedModelSubText));
                OnPropertyChanged(nameof(SelectedModelSizeText));
                OnPropertyChanged(nameof(ModelButtonText));
                RefreshModelReady();
            }
        }
    }

    public string SelectedModelSubText => OllamaModels.ByDisplayName(SelectedLocalModel)?.SubText ?? "";
    public string SelectedModelSizeText =>
        OllamaModels.ByDisplayName(SelectedLocalModel) is { } m ? $"{m.SizeGb} GB · {m.RecommendTag}" : "";
    public string ModelButtonText =>
        ModelReady ? "已就绪"
        : (OllamaModels.ByDisplayName(SelectedLocalModel) is { } m ? $"下载模型 ({m.SizeGb}GB)" : "下载模型");

    public bool OllamaInstalled { get => _ollamaInstalled; set => SetField(ref _ollamaInstalled, value); }
    public string OllamaVersion { get => _ollamaVersion; set => SetField(ref _ollamaVersion, value); }
    public bool OllamaInstalling { get => _ollamaInstalling; set => SetField(ref _ollamaInstalling, value); }
    public double OllamaInstallProgress { get => _ollamaInstallProgress; set => SetField(ref _ollamaInstallProgress, value); }
    public string OllamaInstallText { get => _ollamaInstallText; set => SetField(ref _ollamaInstallText, value); }
    public bool ModelDownloading { get => _modelDownloading; set => SetField(ref _modelDownloading, value); }
    public double ModelDownloadProgress { get => _modelDownloadProgress; set => SetField(ref _modelDownloadProgress, value); }
    public string ModelDownloadText { get => _modelDownloadText; set => SetField(ref _modelDownloadText, value); }
    public bool ModelReady { get => _modelReady; set => SetField(ref _modelReady, value); }
    public OllamaServiceStatus OllamaServiceStatus { get => _ollamaServiceStatus; set => SetField(ref _ollamaServiceStatus, value); }

    public ICommand RefreshStatusCommand => new AsyncRelayCommand(_ => RefreshOllamaStatusAsync());
    public ICommand SaveCommand => new RelayCommand(_ => Save());
    public ICommand InstallOllamaCommand => new AsyncRelayCommand(_ => InstallOllamaAsync());
    public ICommand CancelOllamaInstallCommand => new RelayCommand(_ => _ollamaInstallCts?.Cancel());
    public ICommand PullModelCommand => new AsyncRelayCommand(_ => PullModelAsync());
    public ICommand CancelModelDownloadCommand => new RelayCommand(_ => _modelCts?.Cancel());
    public ICommand TestConnectionCommand => new AsyncRelayCommand(_ => TestConnectionAsync());

    public AiSettingsViewModel()
    {
        _profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
        _ai = _profile.Ai;
        _enabled = _ai.Enabled;
        _mode = _ai.Mode;
        _endpoint = _ai.Endpoint;
        _model = _ai.Model;
        // 落盘的是混淆串（老配置可能是明文，RevealApiKey 会兼容），输入框要的是明文
        _apiKey = Assistant.RevealApiKey(_ai);
        // 持久化存 Ollama tag，UI 用 DisplayName 展示（对齐 WPF）
        _selectedLocalModel = OllamaModels.ByTag(_ai.SelectedLocalModel)?.DisplayName ?? OllamaModels.Default.DisplayName;
        _lastCommittedModel = _selectedLocalModel;
        _crashInterpret = _ai.CrashInterpret;
        _recommendReason = _ai.RecommendReason;
        _modTranslate = _ai.ModTranslate;

        // 后台刷新 Ollama 安装 / 服务 / 已拉取模型状态（不阻塞界面）
        _ = RefreshOllamaStatusAsync();
    }

    /// <summary>检测 Ollama 安装、服务状态与已拉取模型（后台刷新，失败不阻塞界面）。</summary>
    private async Task RefreshOllamaStatusAsync()
    {
        Busy = true;
        try
        {
            var det = await OllamaManager.DetectAsync();
            OllamaInstalled = det.Installed;
            OllamaVersion = det.Version;
            OllamaServiceStatus = await OllamaManager.GetServiceStatusAsync();
            ServiceStatus = OllamaServiceStatus switch
            {
                OllamaServiceStatus.Running => "运行中",
                OllamaServiceStatus.Starting => "启动中",
                _ => "未运行"
            };

            _pulledTags.Clear();
            foreach (var m in OllamaModels.Catalog)
                if (await OllamaManager.IsModelPulledAsync(m.OllamaTag))
                    _pulledTags.Add(m.OllamaTag);

            var info = OllamaModels.ByDisplayName(SelectedLocalModel);
            ModelReady = info is not null && _pulledTags.Contains(info.OllamaTag);
        }
        catch
        {
            // 离线 / 无 Ollama 时不阻塞界面
        }
        finally
        {
            Busy = false;
        }
    }

    private void RefreshModelReady()
    {
        var info = OllamaModels.ByDisplayName(SelectedLocalModel);
        ModelReady = info is not null && _pulledTags.Contains(info.OllamaTag);
        OnPropertyChanged(nameof(ModelButtonText));
    }

    /// <summary>用户切换本地模型时的回调：已拉取直接接受；未拉取弹确认窗（对齐 WPF），取消则回退。</summary>
    public async Task TrySelectLocalModelAsync(string displayName)
    {
        var info = OllamaModels.ByDisplayName(displayName);
        if (info is null) return;
        if (displayName == _lastCommittedModel || _pulledTags.Contains(info.OllamaTag))
        {
            _lastCommittedModel = displayName;
            return;
        }

        var msg = info.OllamaTag.Contains("phi")
            ? "需额外下载 2.2GB，文件较大，是否继续？"
            : info.OllamaTag.Contains("internlm")
                ? "需额外下载 1.1GB，是否继续？"
                : $"需额外下载 {info.SizeGb}GB，是否继续？";

        if (!await Services.UIService.ConfirmAsync(msg, "下载模型"))
        {
            // 回退到上一次已确认的模型
            SelectedLocalModel = _lastCommittedModel;
            return;
        }
        _lastCommittedModel = displayName;
    }

    /// <summary>一键安装 Ollama：下载安装器并静默安装，支持取消与临时文件清理。</summary>
    private async Task InstallOllamaAsync()
    {
        _ollamaInstallCts = new CancellationTokenSource();
        OllamaInstalling = true;
        OllamaInstallProgress = 0;
        OllamaInstallText = "正在下载 Ollama 安装程序…";
        try
        {
            await OllamaManager.InstallAsync(new Progress<double>(p => OllamaInstallProgress = p), _ollamaInstallCts.Token);
            var det = await OllamaManager.DetectAsync();
            OllamaInstalled = det.Installed;
            OllamaVersion = det.Version;
            OllamaInstallText = det.Installed
                ? $"Ollama 已安装（{det.Version}）"
                : "安装完成，但未检测到 ollama 命令，请重启启动器后重试。";
            await RefreshOllamaStatusAsync();
        }
        catch (OperationCanceledException)
        {
            OllamaInstallText = "已取消安装，临时文件已清理。";
        }
        catch (Exception ex)
        {
            OllamaInstallText = $"安装失败：{ex.Message}";
        }
        finally
        {
            OllamaInstalling = false;
            _ollamaInstallCts = null;
        }
    }

    /// <summary>拉取选中的本地模型，支持进度与取消。</summary>
    private async Task PullModelAsync()
    {
        var info = OllamaModels.ByDisplayName(SelectedLocalModel);
        if (info is null) return;
        _modelCts = new CancellationTokenSource();
        ModelDownloading = true;
        ModelDownloadProgress = 0;
        ModelDownloadText = $"正在下载 {info.DisplayName}…";
        try
        {
            await OllamaManager.PullModelAsync(info.OllamaTag,
                new Progress<double>(p => ModelDownloadProgress = p), _modelCts.Token);
            _pulledTags.Add(info.OllamaTag);
            _lastCommittedModel = SelectedLocalModel;
            ModelReady = true;
            ModelDownloadText = "已就绪";
        }
        catch (OperationCanceledException)
        {
            ModelDownloadText = "已取消下载。";
        }
        catch (Exception ex)
        {
            ModelDownloadText = $"下载失败：{ex.Message}";
        }
        finally
        {
            ModelDownloading = false;
            _modelCts = null;
        }
    }

    /// <summary>测试外部 API 连通性；成功时按端点自动填充模型名（对齐 WPF）。</summary>
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            Status = "请先填写 API 地址";
            return;
        }
        Busy = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var body = new
            {
                model = string.IsNullOrWhiteSpace(Model) ? "gpt-4o-mini" : Model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1
            };
            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Content = content;
            if (!string.IsNullOrWhiteSpace(ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);

            using var resp = await client.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var suggested = Assistant.SuggestModelForEndpoint(Endpoint);
                if (!string.IsNullOrEmpty(suggested) && suggested != Model)
                {
                    Model = suggested;
                    Status = $"连接成功，已自动填充模型：{suggested}";
                }
                else
                {
                    Status = "连接成功";
                }
            }
            else
            {
                Status = $"连接失败：HTTP {(int)resp.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            Status = $"连接失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private void Save()
    {
        try
        {
            _ai.Enabled = _enabled;
            _ai.Mode = _mode;
            _ai.Endpoint = _endpoint;
            _ai.Model = _model;
            // Key 不进明文：内存留一份供本次会话直接用，落盘那一份混淆
            Assistant.CommitApiKey(_apiKey, _ai);
            // UI 存 DisplayName，持久化存 Ollama tag（对齐 WPF）
            _ai.SelectedLocalModel = OllamaModels.ByDisplayName(_selectedLocalModel)?.OllamaTag ?? OllamaModels.Default.OllamaTag;
            _ai.CrashInterpret = _crashInterpret;
            _ai.RecommendReason = _recommendReason;
            _ai.ModTranslate = _modTranslate;
            ProfileStore.Save(_profile);
            // 立即生效：运行时 AI 助手读取全局配置
            Assistant.Config = _profile.Ai;
            Status = "AI 设置已保存";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
