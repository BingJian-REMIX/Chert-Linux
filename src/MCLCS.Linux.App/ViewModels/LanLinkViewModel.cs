using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using MCLCS.Core.Auth;
using MCLCS.Core.Lan;
using MCLCS.Core.Launcher;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Servers;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>局域网列表里一台机器的展示卡片。</summary>
public sealed class LanPeerCard
{
    public LanPeer Peer { get; init; } = new();

    public string DisplayName => Peer.DisplayName;

    public string Detail => Peer.Endpoint;

    /// <summary>状态摘要：是否在游戏中 / 是否已开放局域网世界。</summary>
    public string StateText =>
        Peer.GameRunning
            ? (Peer.LanEndpoint.Length > 0 ? $"游戏中 · 已开放 {Peer.LanEndpoint}" : "游戏中")
            : "未启动游戏";

    public bool Paired { get; set; }

    public string PairText => Paired ? "已配对" : "未配对";

    public string VersionText => Peer.LauncherVersion;
}

/// <summary>
/// 清单 #35 ~ #40：局域网联动页（对等模式）。
/// <para>两端都跑着启动器、都开启联动开关，谁都可以邀请谁；
/// 不做向远端部署启动器这件事 —— 启动器是绿色版，对方装一次即可。</para>
/// <para>Linux 差异（相对 WPF 版）：</para>
/// <list type="bullet">
///   <item>文案硬编码中文（Linux 端不同步 <c>lan.*</c> 本地化键，不引入 LocaleManager）；</item>
///   <item>剪贴板不直接依赖 UI：由 View 在加载时注入 <see cref="ClipboardWriter"/>；</item>
///   <item>收到邀请用 <c>DialogService</c> 确认（WPF 用 MessageBox）；</item>
///   <item>投递 <c>/publish</c> 走 <see cref="LanGameChatSender"/>（xdotool），非 Win32 消息；</item>
///   <item>UI 线程切换用 Avalonia 的 <c>Dispatcher.UIThread</c>，非 WPF Dispatcher。</item>
/// </list>
/// </summary>
public class LanLinkViewModel : ObservableObject
{
    /// <summary>
    /// 进程内单例：页面每次导航都 new 一个 VM 会重复订阅 <see cref="LanLinkService"/> 的
    /// 状态 / 邀请事件（切页几次就重复弹几次邀请框），故与其它页一致走单例。
    /// </summary>
    public static LanLinkViewModel Instance { get; } = new();

    private readonly LanLinkService _service = LanLinkService.Instance;

    private bool _enabled;
    private bool _busy;
    private string _statusText = "";
    private string _serviceStatus = "";
    private string _pairCode = "";
    private string _inputPairCode = "";
    private string _manualIp = "";
    private string _localEndpoint = "";
    private string _joinEndpoint = "";
    private string _inviteCode = "";
    private string _inviteSummary = "";
    private LanPeerCard? _selected;

    public LanLinkViewModel()
    {
        Peers = new ObservableCollection<LanPeerCard>();

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ProbeIpCommand = new AsyncRelayCommand(_ => ProbeIpAsync());
        GeneratePairCodeCommand = new RelayCommand(_ => GeneratePairCode());
        PairCommand = new AsyncRelayCommand(_ => PairAsync());
        PublishCommand = new RelayCommand(_ => PublishLocal());
        DetectCommand = new AsyncRelayCommand(_ => DetectLocalAsync());
        InviteCommand = new AsyncRelayCommand(_ => InviteAsync());
        RequestOpenCommand = new AsyncRelayCommand(_ => RequestOpenAsync());
        JoinCommand = new AsyncRelayCommand(_ => JoinAsync());
        CopyShareCommand = new AsyncRelayCommand(_ => CopyShareAsync());
        GenerateInviteCommand = new RelayCommand(_ => GenerateInvite());
        CopyInviteCommand = new AsyncRelayCommand(_ => CopyInviteAsync());
        CopyPairCodeCommand = new AsyncRelayCommand(_ => CopyPairCodeAsync());

        // 服务状态 / 邀请都从后台线程回调，统一切回 UI 线程再改集合与属性
        _service.StatusChanged += () => Dispatcher.UIThread.Post(() =>
        {
            ServiceStatus = _service.StatusText;
            OnPropertyChanged(nameof(ServiceStatus));
        });
        _service.InviteReceived += request => Dispatcher.UIThread.Post(() => _ = OnInviteReceivedAsync(request));

        LoadFromProfile();
        ServiceStatus = _service.StatusText;
    }

    // ===== 属性 =====

    public ObservableCollection<LanPeerCard> Peers { get; }

    /// <summary>
    /// 剪贴板写入实现，由 View 在加载时注入（Avalonia 的剪贴板要经 <c>TopLevel</c> 取，
    /// VM 不直接依赖 UI）。未注入时复制操作降级为一条状态提示。
    /// </summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetField(ref _enabled, value)) return;
            var cfg = _service.Config;
            cfg.Enabled = value;
            cfg.Normalize();
            _service.ApplyConfig(cfg);
            Persist();
            ServiceStatus = _service.StatusText;
            StatusText = value
                ? (_service.Running ? "已开启：可被局域网内的 MCLCS 发现" : $"开启失败：{_service.LastError}")
                : "已关闭";
        }
    }

    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    public string ServiceStatus
    {
        get => _serviceStatus;
        set => SetField(ref _serviceStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string PairCode
    {
        get => _pairCode;
        set => SetField(ref _pairCode, value);
    }

    public string InputPairCode
    {
        get => _inputPairCode;
        set => SetField(ref _inputPairCode, value);
    }

    public string ManualIp
    {
        get => _manualIp;
        set => SetField(ref _manualIp, value);
    }

    public string LocalEndpoint
    {
        get => _localEndpoint;
        set
        {
            if (!SetField(ref _localEndpoint, value)) return;
            OnPropertyChanged(nameof(ShareText));
        }
    }

    public string JoinEndpoint
    {
        get => _joinEndpoint;
        set => SetField(ref _joinEndpoint, value);
    }

    public LanPeerCard? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>生成的邀请码（CHERT1: 长码）。为空表示还没生成。</summary>
    public string InviteCode
    {
        get => _inviteCode;
        set
        {
            if (!SetField(ref _inviteCode, value)) return;
            OnPropertyChanged(nameof(HasInvite));
        }
    }

    /// <summary>邀请码旁的说明：校验码 + 有效期。</summary>
    public string InviteSummary
    {
        get => _inviteSummary;
        set => SetField(ref _inviteSummary, value);
    }

    public bool HasInvite => _inviteCode.Length > 0;

    /// <summary>可复制给对方的邀请文本。</summary>
    public string ShareText =>
        LocalEndpoint.Length == 0
            ? ""
            : LanWorldShare.BuildShareText(LocalEndpoint);

    // ===== 命令 =====

    public ICommand RefreshCommand { get; }
    public ICommand ProbeIpCommand { get; }
    public ICommand GeneratePairCodeCommand { get; }
    public ICommand PairCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand RequestOpenCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand CopyShareCommand { get; }
    public ICommand GenerateInviteCommand { get; }
    public ICommand CopyInviteCommand { get; }
    public ICommand CopyPairCodeCommand { get; }

    // ===== 实现 =====

    private void LoadFromProfile()
    {
        try
        {
            var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
            var cfg = (profile.LanLink ?? LanLinkConfig.CreateDefault()).Normalize();
            _enabled = cfg.Enabled;
            OnPropertyChanged(nameof(Enabled));
            // 开关在上次会话里是开的：这里把服务重新拉起来（进程重启后监听不会自动恢复）
            if (_enabled) _service.ApplyConfig(cfg);
        }
        catch
        {
            _enabled = false;
        }
    }

    private void Persist()
    {
        try
        {
            var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
            profile.LanLink = _service.Config;
            profile.GameRoot = string.IsNullOrWhiteSpace(profile.GameRoot)
                ? GameConstants.DefaultGameRoot
                : profile.GameRoot;
            ProfileStore.Save(profile);
        }
        catch
        {
            // 持久化失败不影响当前会话
        }
    }

    private async Task RefreshAsync()
    {
        if (!_enabled)
        {
            StatusText = "请先开启「允许局域网联动」";
            return;
        }

        Busy = true;
        StatusText = "正在搜索局域网内的 MCLCS 启动器…";
        try
        {
            var found = await _service.ProbeAsync(1400);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Peers.Clear();
                foreach (var p in found)
                    Peers.Add(new LanPeerCard { Peer = p, Paired = _service.IsPaired(p) });
            });
            StatusText = found.Count == 0
                ? "没搜到。确认对方也开着 MCLCS 并开启联动；也可以直接填对方 IP 探测。"
                : $"发现 {found.Count} 台设备";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ProbeIpAsync()
    {
        var ip = (ManualIp ?? "").Trim();
        if (ip.Length == 0) { StatusText = "请先填写对方 IP"; return; }

        Busy = true;
        StatusText = $"正在探测 {ip} …";
        try
        {
            var peer = await _service.ProbeDirectAsync(ip);
            if (peer is null)
            {
                StatusText = $"{ip} 没有响应。确认对方已开启联动，且防火墙放行了发现端口。";
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Peers.All(x => x.Peer.Endpoint != peer.Endpoint))
                    Peers.Add(new LanPeerCard { Peer = peer, Paired = _service.IsPaired(peer) });
                Selected = Peers.FirstOrDefault(x => x.Peer.Endpoint == peer.Endpoint);
            });
            StatusText = $"已连接到 {peer.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusText = $"探测失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private void GeneratePairCode()
    {
        if (!_enabled) { StatusText = "请先开启联动"; return; }
        PairCode = _service.IssuePairCode();
        StatusText = $"配对码 {PairCode}（10 分钟内有效）—— 让对方在自己的启动器里输入它";
    }

    private async Task PairAsync()
    {
        if (Selected is null) { StatusText = "先在列表里选一台设备"; return; }
        var code = (InputPairCode ?? "").Trim();
        if (code.Length == 0) { StatusText = "请输入对方显示的 6 位配对码"; return; }

        Busy = true;
        StatusText = "正在配对…";
        try
        {
            var token = await _service.PairAsync(Selected.Peer, code);
            if (token is null)
            {
                StatusText = "配对失败：配对码错误或已过期";
                return;
            }

            Selected.Paired = true;
            OnPropertyChanged(nameof(Selected));
            StatusText = $"已与 {Selected.DisplayName} 配对";
        }
        catch (Exception ex)
        {
            StatusText = $"配对失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>向本机游戏投递 /publish（Linux 走 xdotool）。</summary>
    private void PublishLocal()
    {
        if (!LanGameBridge.IsGameRunning) { StatusText = "本机没有运行中的 Minecraft"; return; }

        var result = LanGameChatSender.Publish();
        StatusText = result.Ok ? result.Message : $"投递失败：{result.Message}";
    }

    /// <summary>
    /// 读取本机已开放的局域网端口。先读游戏日志（最可靠），读不到再监听 3 秒
    /// <c>224.0.2.60:4445</c> 组播兜底 —— Minecraft 开放后每 1.5 秒广播一次，
    /// 日志被清 / 版本措辞变了 / 不是本启动器拉起的游戏时，只有广播还能拿到端口。
    /// </summary>
    private async Task DetectLocalAsync()
    {
        var port = LanWorldShare.TryReadPublishedPort(GameConstants.DefaultGameRoot);

        if (port is null)
        {
            StatusText = "日志里没读到端口，正在监听 3 秒局域网广播…";
            var self = LanWorldShare.GetLocalIPv4();
            var found = await LanServerScanner.ScanAsync(3000);
            var mine = found.FirstOrDefault(x => x.Address == self) ?? found.FirstOrDefault();
            if (mine is not null) port = mine.Port;
        }

        if (port is null)
        {
            StatusText = "没读到端口。确认游戏里已经「对局域网开放」，或先点「一键开放」。";
            return;
        }

        LocalEndpoint = LanWorldShare.BuildEndpoint(LanWorldShare.GetLocalIPv4(), port.Value);
        StatusText = $"本机世界地址 {LocalEndpoint}";
        // 地址变了，旧邀请码就作废（否则对方拿到的是过期地址）
        if (HasInvite && !InviteCodeContains(LocalEndpoint))
        {
            InviteCode = "";
            InviteSummary = "世界地址变了，请重新生成邀请码";
        }
    }

    /// <summary>邀请码里是否含当前地址（粗判：长码是压缩过的，只能解出来看）。</summary>
    private bool InviteCodeContains(string endpoint)
        => LanInviteCode.TryDecode(InviteCode, out var code, out _) && code!.Endpoint == endpoint;

    private async Task InviteAsync()
    {
        if (Selected is null) { StatusText = "先选一台设备"; return; }
        if (LocalEndpoint.Length == 0) { StatusText = "先读取本机世界地址"; return; }

        Busy = true;
        StatusText = $"正在邀请 {Selected.DisplayName} …";
        try
        {
            var res = await _service.SendAsync(Selected.Peer, LanCommandKind.JoinLan,
                LocalEndpoint, Environment.MachineName);
            StatusText = res is null
                ? "对方没有响应"
                : res.Ok ? $"已发送邀请：{res.Message}" : $"对方拒绝了：{res.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"发送失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RequestOpenAsync()
    {
        if (Selected is null) { StatusText = "先选一台设备"; return; }

        Busy = true;
        StatusText = $"正在请求 {Selected.DisplayName} 开放局域网世界…";
        try
        {
            var res = await _service.SendAsync(Selected.Peer, LanCommandKind.OpenLan);
            StatusText = res is null
                ? "对方没有响应"
                : res.Ok ? res.Message : $"失败：{res.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"发送失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>加入一个局域网世界（地址来自粘贴或收到的邀请）。</summary>
    private async Task JoinAsync()
    {
        var text = (JoinEndpoint ?? "").Trim();
        if (text.Length == 0) { StatusText = "请先填写世界地址或粘贴邀请码"; return; }

        // 支持三种写法：CHERT1: 邀请码、chert-lan://host:port、裸 host:port
        var (endpointText, code, error) = LanInviteCode.Resolve(text);
        if (error.Length > 0)
        {
            StatusText = InviteErrorText(error);
            return;
        }

        if (LanWorldShare.ParseEndpoint(endpointText) is not { } parsed)
        {
            StatusText = $"地址格式不对：{text}";
            return;
        }

        // 长码：把来源与校验码回显出来，便于和对方核对（防粘贴到别人的旧码）
        if (code is not null)
        {
            var from = code.DeviceName.Length > 0 ? code.DeviceName : code.Endpoint;
            StatusText = $"来自 {from} 的邀请 · 校验码 {code.Fingerprint}";
            if (code.PairCode.Length > 0 && Selected is not null && !_service.IsPaired(Selected.Peer))
            {
                var token = await _service.PairAsync(Selected.Peer, code.PairCode);
                if (token is not null)
                {
                    Selected.Paired = true;
                    OnPropertyChanged(nameof(Selected));
                }
            }
        }

        Busy = true;
        StatusText = $"正在加入 {parsed.Host}:{parsed.Port} …";
        try
        {
            var gameRoot = GameConstants.DefaultGameRoot;
            var profile = ProfileStore.Load(gameRoot);
            var versionId = profile.LastVersionId;
            if (string.IsNullOrWhiteSpace(versionId))
            {
                StatusText = "还没有选择要启动的版本";
                return;
            }

            var java = await ResolveJavaAsync(profile.JavaPath, gameRoot, versionId!);
            if (java is null)
            {
                StatusText = "未检测到 Java，请在「设置 → 启动」中配置 Java 路径";
                return;
            }

            var options = BuildLaunchOptions(profile, gameRoot, versionId!);
            options.ServerAddress = $"{parsed.Host}:{parsed.Port}";

            // 只启动、不等退出：否则按钮会被锁到游戏关闭（WPF problem3 多实例同理）
            _ = Task.Run(() => GameLauncher.LaunchAsync(gameRoot, versionId!, java, options, null));
            StatusText = $"已启动并直连 {parsed.Host}:{parsed.Port}";
        }
        catch (Exception ex)
        {
            StatusText = $"加入失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task CopyShareAsync()
    {
        if (ShareText.Length == 0) { StatusText = "还没有可分享的地址"; return; }
        await CopyToClipboardAsync(ShareText, "邀请文本已复制到剪贴板");
    }

    /// <summary>
    /// 生成「邀请码」：把世界地址 / 设备名 / MC 版本 / 配对码 / 有效期打包成一段
    /// <c>CHERT1:</c> 开头的短文本，对方整段粘贴即可加入（此前只能手抄 host:port）。
    /// </summary>
    private void GenerateInvite()
    {
        if (LocalEndpoint.Length == 0)
        {
            StatusText = "先点「读取本机端口」拿到本机世界地址";
            return;
        }

        var code = new LanInviteCode
        {
            ExpiresAt = DateTimeOffset.UtcNow.Add(LanInviteCode.Ttl).ToUnixTimeSeconds(),
            Endpoint = LocalEndpoint,
            DeviceName = Environment.MachineName,
            McVersion = CurrentVersionId(),
            // 有待用的配对码就一起带上，对方加入后不必再单独配对
            PairCode = _service.PendingPairCode ?? ""
        }.Encode();

        InviteCode = code;
        var fingerprint = LanInviteCode.TryDecode(code, out var parsed, out _) ? parsed!.Fingerprint : "";
        InviteSummary = $"校验码 {fingerprint} · {(int)LanInviteCode.Ttl.TotalMinutes} 分钟内有效";
        StatusText = "邀请码已生成，复制整段发给对方（对方在「世界地址」里粘贴即可）";
    }

    private async Task CopyInviteAsync()
    {
        if (!HasInvite) { StatusText = "先点「读取本机端口」拿到本机世界地址"; return; }
        await CopyToClipboardAsync(InviteCode, "邀请码已复制");
    }

    private async Task CopyPairCodeAsync()
    {
        if (PairCode.Length == 0) { StatusText = "还没有配对码，先点「生成配对码」"; return; }
        await CopyToClipboardAsync(PairCode, "配对码已复制");
    }

    /// <summary>经 View 注入的实现写剪贴板；未注入 / 失败时降级为提示。</summary>
    private async Task CopyToClipboardAsync(string text, string doneText)
    {
        var writer = ClipboardWriter;
        if (writer is null) { StatusText = "复制失败：剪贴板不可用"; return; }

        try
        {
            await writer(text);
            StatusText = doneText;
        }
        catch
        {
            StatusText = "复制失败，请手动选中文本复制";
        }
    }

    /// <summary>当前选中的版本（写进邀请码，对方能提前发现版本不一致）。</summary>
    private static string CurrentVersionId()
    {
        try { return ProfileStore.Load(GameConstants.DefaultGameRoot).LastVersionId ?? ""; }
        catch { return ""; }
    }

    /// <summary>邀请码解析失败的原因 → 人话。</summary>
    private static string InviteErrorText(string error) => error switch
    {
        "invite_expired" => "邀请码已过期（10 分钟），请让对方重新生成",
        "invite_checksum" => "邀请码不完整或被改动，请让对方重新复制整段",
        "invite_version_mismatch" => "邀请码版本不兼容，双方都升级到最新版启动器",
        "invite_bad_endpoint" => "地址格式不对，应为 host:port（如 192.168.1.5:52931）",
        _ => "邀请码无法识别，请检查是否复制完整"
    };

    /// <summary>收到别人的邀请：在屏幕上确认后才执行（局域网不可信，绝不静默加入）。</summary>
    private async Task OnInviteReceivedAsync(LanInviteRequest request)
    {
        var text = $"「{request.From.DisplayName}」邀请你加入局域网世界\n" +
                   $"地址：{request.Endpoint}\n" +
                   (request.WorldName.Length > 0 ? $"世界：{request.WorldName}\n" : "") +
                   "\n是否加入？";

        var answer = await DialogService.Instance.ShowAsync(new DialogOptions
        {
            Title = "局域网联机邀请",
            Content = text,
            Buttons = new[]
            {
                new DialogButton("加入", DialogResults.Yes, DialogButtonKind.Primary, isDefault: true),
                new DialogButton("拒绝", DialogResults.No, isCancel: true)
            }
        });

        if (!string.Equals(answer as string, DialogResults.Yes, StringComparison.Ordinal))
        {
            StatusText = "已拒绝邀请";
            return;
        }

        JoinEndpoint = request.Endpoint;
        OnPropertyChanged(nameof(JoinEndpoint));
        await JoinAsync();
    }

    // ===== 启动参数（对齐 HomeViewModel 的选 Java / 填账号逻辑）=====

    private static async Task<JavaInfo?> ResolveJavaAsync(
        string? javaPath, string gameRoot, string versionId)
    {
        var list = await JavaDetector.DetectAsync();
        if (list.Count == 0) return null;
        return JavaDetector.SelectForVersion(list, gameRoot, versionId, javaPath);
    }

    private static LaunchOptions BuildLaunchOptions(LauncherProfile profile, string gameRoot, string versionId)
    {
        var options = new LaunchOptions
        {
            MaxMemoryMb = profile.MaxMemoryMb > 0 ? profile.MaxMemoryMb : 2048,
            MinMemoryMb = profile.MinMemoryMb,
            Username = profile.DefaultUsername,
            Uuid = OfflineAuthenticator.GenerateOfflineUuid(profile.DefaultUsername),
            AccessToken = "0",
            UserType = "mojang"
        };

        var account = AccountStore.GetLastUsed(gameRoot);
        if (account is { } a && !string.IsNullOrEmpty(a.Uuid))
        {
            options.Username = a.Username;
            options.Uuid = a.Uuid;
            options.AccessToken = a.AccessToken;
            options.UserType = string.Equals(a.AuthType, "microsoft", StringComparison.OrdinalIgnoreCase) ? "msa" : a.AuthType;
        }

        return options;
    }
}
