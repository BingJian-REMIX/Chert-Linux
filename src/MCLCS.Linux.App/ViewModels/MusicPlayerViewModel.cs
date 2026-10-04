using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using MCLCS.Core.Music;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Utils;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>实际音频解码宿主（由界面层用 BASS 实现并注入）。</summary>
public interface IMediaPlayer
{
    void LoadAndPlay(string path);
    void Pause();
    void Resume();
    void Stop();
    void SetVolume(int volume);

    /// <summary>当前播放位置（秒）；未播放/不支持时返回 0（对齐 WPF IMediaPlayer.PositionSec）。</summary>
    double PositionSec { get; }

    /// <summary>当前音源总时长（秒）；未知（在线流媒体等）返回 0（对齐 WPF IMediaPlayer.DurationSec）。</summary>
    double DurationSec { get; }

    /// <summary>跳转到指定位置（秒）；不支持时忽略。</summary>
    void Seek(double seconds);

    event Action? Ended;
}

/// <summary>
/// 音乐播放器（工具箱面板 14，规格 2.3）。
/// 三音源：本地文件夹（MP3/FLAC/OGG/WAV）、在线流媒体（预设 + 自定义）、MC 原声（自动提取 assets）。
/// 播放列表导航逻辑在 <see cref="MusicPlaylist"/>(Core)，本 VM 负责状态、命令与三音源切换；
/// 实际解码交给注入的 <see cref="IMediaPlayer"/> 宿主（界面层 BASS）。
/// 迷你条（状态栏）与工具箱面板共用本单例，从而实现「切页保持播放状态」。
/// </summary>
public class MusicPlayerViewModel : ObservableObject
{
    public static MusicPlayerViewModel Instance { get; } = new();

    private readonly MusicPlaylist _playlist = new();

    public ObservableCollection<Track> Tracks { get; } = new();

    private string _sourceKind = "Local"; // Local / Online / McOst / Client
    private bool _isPlaying;
    private Track? _currentTrack;
    private string _statusText = "未播放";
    private int _volume = 60;
    private PlayMode _mode = PlayMode.LoopAll;
    private string _onlineUrl = "";
    private bool _autoDuck = true;
    private bool _expanded;

    // ===== 歌词（对齐 WPF：Core.Music.LyricEngine + LyricService 网易云源）=====

    /// <summary>歌词引擎（单一实例，避免重复解析 LRC）。</summary>
    public LyricEngine Lyric { get; } = new();

    private readonly LyricService _lyricService = new();
    private string _lyricText = "";
    private int _lyricLineCount;

    /// <summary>当前应显示的歌词文本（多行用换行分隔）。</summary>
    public string LyricText
    {
        get => _lyricText;
        private set => SetField(ref _lyricText, value);
    }

    /// <summary>当前应显示的歌词行数（0 = 无歌词，界面应隐藏歌词区）。</summary>
    public int LyricLineCount
    {
        get => _lyricLineCount;
        private set { if (SetField(ref _lyricLineCount, value)) OnPropertyChanged(nameof(HasLyric)); }
    }

    /// <summary>是否有歌词可显示（界面据此隐藏歌词区，无需弹提示）。</summary>
    public bool HasLyric => LyricLineCount > 0;

    // ===== 本地客户端模式（把客户端当遥控器，对齐 WPF 规格实现项 2 / 6.1）=====

    private string _clientExePath = "";
    private string _clientStatus = "";
    private string _clientRunning = "";
    private System.Diagnostics.Process? _clientProcess;

    /// <summary>本地客户端模式设置（含容错归一）。</summary>
    public MusicClientPrefs ClientPrefs
    {
        get
        {
            var p = ProfileStore.Load(GameConstants.DefaultGameRoot).MusicClient;
            return (p ?? new MusicClientPrefs()).Normalized();
        }
    }

    /// <summary>外部客户端可执行文件路径（空 = 尚未选择）。</summary>
    public string ClientExePath
    {
        get => _clientExePath;
        private set
        {
            if (SetField(ref _clientExePath, value))
            {
                OnPropertyChanged(nameof(ClientDisplayName));
                OnPropertyChanged(nameof(HasClientExe));
                SavePrefs();
            }
        }
    }

    /// <summary>是否已选定客户端程序（据此决定显示「选择程序」还是「启动」）。</summary>
    public bool HasClientExe => !string.IsNullOrWhiteSpace(_clientExePath);

    /// <summary>「本地客户端模式」入口是否可用：总开关关闭时直接隐藏入口，
    /// 用户不会点到一个注定被拒的模式（对齐 WPF）。</summary>
    public bool ClientModeAvailable => ClientPrefs.Enabled;

    /// <summary>客户端显示名（取文件名去扩展名）。</summary>
    public string ClientDisplayName
    {
        get
        {
            if (!HasClientExe) return "";
            try { return Path.GetFileNameWithoutExtension(_clientExePath); }
            catch { return _clientExePath; }
        }
    }

    /// <summary>客户端模式状态说明（状态卡片正文）。</summary>
    public string ClientStatus
    {
        get => _clientStatus;
        private set => SetField(ref _clientStatus, value);
    }

    /// <summary>由启动器拉起 / 正在运行的客户端进程（状态卡片副文本）。</summary>
    public string ClientRunning
    {
        get => _clientRunning;
        private set => SetField(ref _clientRunning, value);
    }

    /// <summary>当前是否处于本地客户端模式（启动器不放音，播放由外部客户端负责）。</summary>
    public bool IsLocalClient => SourceKind == "Client";
    private string _mcOstStatus = "";

    private IMediaPlayer? _host;

    /// <summary>实际解码宿主（BASS），由主窗口注入。注入后即启动进度采样定时器。</summary>
    public IMediaPlayer? Host
    {
        get => _host;
        set
        {
            if (ReferenceEquals(_host, value)) return;
            _host = value;
            if (value is not null)
                StartProgressTimer();      // 有宿主才能采样位置/时长
            else
                StopProgressTimer();
        }
    }

    /// <summary>
    /// 在线音源服务（<see cref="MusicSourceMode.Api"/>：由启动器自己去取流并播放）。
    /// <para>与歌词用的 LyricService 是两套东西：那个只负责歌词文本，
    /// 这个负责搜索 / 歌单 / 换播放直链，两者的数据源可以完全不同。</para>
    /// </summary>
    public OnlineMusicService Online { get; }

    /// <summary>在线音源是否可用（总开关打开且已填服务地址）。</summary>
    public bool ApiEnabled => Online.IsAvailable;

    /// <summary>搜索到的曲目。</summary>
    public ObservableCollection<OnlineTrack> OnlineResults { get; } = new();

    /// <summary>搜索到的公开歌单（数据源不支持搜索时恒为空）。</summary>
    public ObservableCollection<OnlinePlaylist> OnlinePlaylists { get; } = new();

    /// <summary>「我的歌单」（需登录且数据源支持登录）。</summary>
    public ObservableCollection<OnlinePlaylist> MyPlaylists { get; } = new();

    private string _onlineKeyword = "";
    private string _playlistIdInput = "";
    private bool _onlineBusy;
    private string _onlineStatus = "";
    private string _loginQrDataUrl = "";
    private bool _loginPanelVisible;
    private string _loginStatusText = "";
    private string? _loginKey;
    private CancellationTokenSource? _loginCts;
    private CancellationTokenSource? _onlineLyricCts;

    /// <summary>当前正在播的在线曲目（在线歌词刷新要用它的 id 去取歌词）。</summary>
    private OnlineTrack? _currentOnline;

    /// <summary>搜索关键词。</summary>
    public string OnlineKeyword
    {
        get => _onlineKeyword;
        set => SetField(ref _onlineKeyword, value);
    }

    /// <summary>歌单 ID 输入框内容（Meting 协议没有「搜歌单」能力，只能手填 ID）。</summary>
    public string PlaylistIdInput
    {
        get => _playlistIdInput;
        set => SetField(ref _playlistIdInput, value);
    }

    public bool OnlineBusy
    {
        get => _onlineBusy;
        private set
        {
            if (SetField(ref _onlineBusy, value)) OnPropertyChanged(nameof(OnlineIdle));
        }
    }

    /// <summary>「非忙」：按钮 IsEnabled 绑它比再写一个反向转换器省事。</summary>
    public bool OnlineIdle => !_onlineBusy;

    /// <summary>在线音源操作的结果提示（按钮/列表下方的那行小字）。</summary>
    public string OnlineStatus
    {
        get => _onlineStatus;
        private set => SetField(ref _onlineStatus, value);
    }

    /// <summary>当前数据源是否支持扫码登录。</summary>
    public bool CanOnlineLogin => Online.CanLogin;

    public bool IsOnlineLoggedIn => Online.IsLoggedIn;

    /// <summary>账户展示文本（没昵称时退回「已登录 / 未登录」）。</summary>
    public string ApiAccountLabel
    {
        get
        {
            var nick = Online.Account?.Nickname;
            if (!string.IsNullOrWhiteSpace(nick)) return nick!;
            return Online.IsLoggedIn ? "已登录" : "未登录";
        }
    }

    /// <summary>二维码图片的 data URL；界面层解码成各自的图像类型。</summary>
    public string LoginQrDataUrl
    {
        get => _loginQrDataUrl;
        private set => SetField(ref _loginQrDataUrl, value);
    }

    public bool LoginPanelVisible
    {
        get => _loginPanelVisible;
        private set => SetField(ref _loginPanelVisible, value);
    }

    public string LoginStatusText
    {
        get => _loginStatusText;
        private set => SetField(ref _loginStatusText, value);
    }

    /// <summary>搜索在线歌曲。</summary>
    public ICommand SearchOnlineCommand { get; }

    /// <summary>导入在线歌单（参数为 <see cref="OnlinePlaylist"/>；留空则取输入框里的 ID）。</summary>
    public ICommand ImportPlaylistCommand { get; }

    /// <summary>点播一条搜索结果。</summary>
    public ICommand PlayOnlineTrackCommand { get; }

    public ICommand StartApiLoginCommand { get; }
    public ICommand CancelApiLoginCommand { get; }
    public ICommand ApiLogoutCommand { get; }
    public ICommand RefreshMyPlaylistsCommand { get; }

    /// <summary>MC 原声按分类分组（扫描后填充）。</summary>
    public ObservableCollection<McOstGroup> McOstGroups { get; } = new();

    private MusicPlayerViewModel()
    {
        PlayPauseCommand = new RelayCommand(_ => PlayPause());
        NextCommand = new RelayCommand(_ => Next());
        PreviousCommand = new RelayCommand(_ => Previous());
        LoadLocalFolderCommand = new RelayCommand(_ => _ = LoadLocalFolderAsync());
        SetSourceCommand = new RelayCommand(p => SetSource(p as string));
        SetModeCommand = new RelayCommand(_ => CycleMode());
        AddOnlineCommand = new RelayCommand(_ => AddOnline());
        ScanMcOstCommand = new RelayCommand(_ => ScanMcOst());
        PlayTrackCommand = new RelayCommand(p => PlayTrack(p));
        LaunchClientCommand = new RelayCommand(_ => LaunchClient());
        BrowseClientCommand = new RelayCommand(_ => BrowseClientExe());
        SyncClientTrackCommand = new RelayCommand(_ => SyncClientLyric());
        ExpandCommand = new RelayCommand(_ => Expanded = !Expanded);

        // ===== 在线音源（搜索 / 歌单 / 登录），对齐 WPF =====
        SearchOnlineCommand = new AsyncRelayCommand(_ => SearchOnlineAsync());
        ImportPlaylistCommand = new AsyncRelayCommand(p => ImportPlaylistAsync(p as OnlinePlaylist));
        PlayOnlineTrackCommand = new AsyncRelayCommand(p => PlayOnlineTrackAsync(p as OnlineTrack));
        StartApiLoginCommand = new AsyncRelayCommand(_ => StartApiLoginAsync());
        CancelApiLoginCommand = new RelayCommand(_ => CancelApiLogin());
        ApiLogoutCommand = new RelayCommand(_ => ApiLogout());
        RefreshMyPlaylistsCommand = new AsyncRelayCommand(_ => LoadMyPlaylistsAsync());

        var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
        _autoDuck = profile.MusicAutoDuck;
        _volume = profile.MusicVolume;

        // 进度心跳：宿主注入后才有意义，故此处只备好定时器，实际启动见 Host setter。
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += OnProgressTick;

        // 本地客户端模式：恢复上次选择的客户端路径（仅总开关开启时有意义）
        _clientExePath = profile.MusicClientExePath ?? "";
        if (HasClientExe)
            ClientStatus = $"已选择客户端：{ClientDisplayName}";
        else if (ClientPrefs.Enabled)
            ClientStatus = "尚未选择客户端程序";

        // 在线音源：Reload/构造内部会从（已混淆的）凭证里恢复登录态。
        // 放在构造函数末尾而不是用到再说 —— 否则第一次打开音乐页时登录态是空的。
        Online = new OnlineMusicService(profile.MusicApi);
        Online.LoginStateChanged += () => RunOnUi(UpdateLoginUi);
        if (Online.IsLoggedIn && Online.CanLogin)
        {
            // 昵称/头像要联网才拿得到，放后台补：**不要**在这里 await，
            // 构造函数里 await 网络请求会把启动流程拖住。
            _ = Task.Run(async () =>
            {
                if (await Online.RefreshAccountAsync()) PersistApiCredential();
                await LoadMyPlaylistsAsync();
            });
        }
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand LoadLocalFolderCommand { get; }
    public ICommand SetSourceCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand AddOnlineCommand { get; }
    public ICommand ScanMcOstCommand { get; }
    public ICommand PlayTrackCommand { get; }

    /// <summary>拉起外部客户端（把客户端当遥控器）。</summary>
    public ICommand LaunchClientCommand { get; }

    /// <summary>选择客户端可执行文件。</summary>
    public ICommand BrowseClientCommand { get; }

    /// <summary>手动重新拉取当前曲目的在线歌词。</summary>
    public ICommand SyncClientTrackCommand { get; }
    public ICommand ExpandCommand { get; }

    public string SourceKind
    {
        get => _sourceKind;
        private set
        {
            if (SetField(ref _sourceKind, value))
            {
                OnPropertyChanged(nameof(IsLocal));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsMcOst));
            }
        }
    }

    public bool IsLocal => SourceKind == "Local";
    public bool IsOnline => SourceKind == "Online";
    public bool IsMcOst => SourceKind == "McOst";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    public Track? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (SetField(ref _currentTrack, value))
            {
                OnPropertyChanged(nameof(CurrentTrackDisplay));
                OnPropertyChanged(nameof(CurrentTrackMetaText));
                OnPropertyChanged(nameof(HasTrack));
                // 换曲即换歌词：本地 .lrc 优先，读不到再走在线 API
                LoadLyricForCurrentTrack();
            }
        }
    }

    /// <summary>当前曲目元数据副标题「歌手 · 专辑」（迷你条曲目名下方一行，对齐 WPF CurrentTrackMetaText）。</summary>
    public string CurrentTrackMetaText => CurrentTrack?.MetaText ?? "";

    /// <summary>是否有已选中的曲目（用于状态栏迷你条显隐）。</summary>
    public bool HasTrack => CurrentTrack is not null;

    // ===== 播放进度（迷你条细进度条 + 音乐页进度条，对齐 WPF _progressTimer）=====

    private double _positionSec;
    /// <summary>当前播放位置（秒）。由 500ms 定时器从宿主采样刷新。</summary>
    public double PositionSec
    {
        get => _positionSec;
        private set { if (SetField(ref _positionSec, value)) { OnPropertyChanged(nameof(PositionText)); OnPropertyChanged(nameof(ProgressRatio)); } }
    }

    private double _durationSec;
    /// <summary>当前音源总时长（秒）。在线流媒体等长度未知时为 0（此时进度条禁用）。</summary>
    public double DurationSec
    {
        get => _durationSec;
        private set { if (SetField(ref _durationSec, value)) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(HasProgress)); OnPropertyChanged(nameof(ProgressRatio)); } }
    }

    /// <summary>是否有可用的总时长（未知时进度条应禁用，对齐 WPF HasProgress）。</summary>
    public bool HasProgress => DurationSec > 0;

    /// <summary>播放进度 0~1（供 Slider Maximum=1 绑定）。无时长时为 0。</summary>
    public double ProgressRatio => DurationSec > 0 ? Math.Clamp(PositionSec / DurationSec, 0, 1) : 0;

    /// <summary>当前位置文本（mm:ss）。</summary>
    public string PositionText => FormatTime(PositionSec);

    /// <summary>总时长文本（mm:ss）；未知显示 --:--。</summary>
    public string DurationText => DurationSec > 0 ? FormatTime(DurationSec) : "--:--";

    private static string FormatTime(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private readonly DispatcherTimer _progressTimer;

    private void StartProgressTimer()
    {
        // 定时器已在构造函数建好并挂 Tick，这里只负责启停。
        if (!_progressTimer.IsEnabled) _progressTimer.Start();
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
    }

    private bool _isSeeking;

    /// <summary>用户是否正在拖拽进度条。拖拽期间 500ms 定时器<b>不</b>回写位置，
    /// 否则会出现「slider 刚被拖到某处、下一拍又被采样值拽回去」的抖动（对齐 WPF wasSeeking / IsSeeking）。</summary>
    public bool IsSeeking
    {
        get => _isSeeking;
        set => SetField(ref _isSeeking, value);
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        var host = Host;
        if (host is null) return;
        DurationSec = host.DurationSec;
        if (IsSeeking) return;          // 拖拽中：只刷新时长，不覆盖用户选中的位置
        PositionSec = host.PositionSec;

        // 歌词与进度同频更新（500ms 一跳，逐行切换观感与音乐歌词一致）
        RefreshLyricTexts();
    }

    /// <summary>歌词固定方式跟随设置（切换隔离等场景下实时生效）。</summary>
    private LyricPinMode LyricPinModeSetting => ClientPrefs.LyricPin;

    /// <summary>为当前曲目载入歌词：本地优先读同目录 .lrc，读不到再走在线 API。</summary>
    private void LoadLyricForCurrentTrack()
    {
        try
        {
            // 本地客户端模式下，歌词是否显示由「客户端模式下仍用 API 获取歌词」决定
            if (IsLocalClient && !ClientPrefs.LyricEnabled) { Lyric.Clear(); RefreshLyricTexts(); return; }

            Lyric.PinMode = LyricPinModeSetting;

            // 在线曲目的 .lrc 不在本地，只能从数据源或在线歌词服务取
            if (IsOnline && _currentOnline is not null)
            {
                RefreshLyricTexts();
                _ = LoadOnlineLyricAsync(_currentOnline);
                return;
            }

            // 本地客户端模式：文件在客户端自己的音乐库里，本地没有 .lrc —— 走在线 API
            if (IsLocalClient)
            {
                RefreshLyricTexts();
                _ = SyncLyricFromApiAsync();
                return;
            }

            Lyric.TryLoadFromFile(CurrentTrack?.Path);
            RefreshLyricTexts();
            // 本地没有 .lrc 时异步补一次在线歌词
            if (!Lyric.HasLyric) _ = SyncLyricFromApiAsync();
        }
        catch
        {
            Lyric.Clear();
            RefreshLyricTexts();
        }
    }

    /// <summary>
    /// 拉取当前曲目的在线歌词（网易云源，标题/歌手分开传）。
    /// 命中后写入引擎并刷新显示；失败静默 —— 歌词属装饰性，不该弹提示打扰用户。
    /// </summary>
    private async Task SyncLyricFromApiAsync()
    {
        var track = CurrentTrack;
        if (track is null) return;
        if (string.IsNullOrWhiteSpace(track.Title)) return;

        try
        {
            var lrc = await _lyricService.GetLrcAsync(track.Title, track.Artist).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(lrc)) return;
            // 曲目可能已切换，确认仍是同一首再应用
            if (!ReferenceEquals(track, CurrentTrack)) return;
            Lyric.Load("在线", LyricEngine.ParseLrc(lrc));
            RefreshLyricTexts();
        }
        catch
        {
            // 在线歌词失败：保留本地 .lrc 结果或维持无歌词，不打扰用户
        }
    }

    /// <summary>按当前进度刷新歌词显示行（行数由「歌词固定方式」决定）。
    /// 行内容不变时**不通知**，避免每 500ms 重建控件造成闪烁。</summary>
    private void RefreshLyricTexts()
    {
        if (IsLocalClient && !ClientPrefs.LyricEnabled)
        {
            LyricText = "";
            LyricLineCount = 0;
            return;
        }

        try { Lyric.PinMode = LyricPinModeSetting; } catch { /* 设置异常不阻塞显示 */ }

        var lines = Lyric.GetDisplayLines(PositionSec);
        var text = string.Join(Environment.NewLine, lines.Select(l => l.Text));
        if (!string.Equals(text, LyricText, StringComparison.Ordinal))
            LyricText = text;
        LyricLineCount = lines.Count;
    }

    /// <summary>迷你条/音乐页拖拽进度条跳转（对齐 WPF Seek）。
    /// 拖拽过程中（<see cref="IsSeeking"/> 为 true）只更新界面不真正 seek，
    /// 松手（<paramref name="commit"/> 为 true）才定位 —— 否则拖动会反复触发 seek 卡顿。</summary>
    public void SeekTo(double ratio, bool commit = true)
    {
        var host = Host;
        if (host is null || DurationSec <= 0) return;

        var target = Math.Clamp(ratio, 0, 1) * DurationSec;
        PositionSec = target;                       // 界面立即反映
        if (commit) host.Seek(target);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public int Volume
    {
        get => _volume;
        set
        {
            if (SetField(ref _volume, value))
            {
                Host?.SetVolume(value);
                SavePrefs();
            }
        }
    }

    public PlayMode Mode
    {
        get => _mode;
        private set => SetField(ref _mode, value);
    }

    /// <summary>模式的中文名（供状态栏 / 面板展示）。</summary>
    public string ModeText => MusicPlaylist.ModeText(_mode);

    public string OnlineUrl
    {
        get => _onlineUrl;
        set => SetField(ref _onlineUrl, value);
    }

    /// <summary>游戏启动时自动降低音量（规格 2.3）。</summary>
    public bool AutoDuck
    {
        get => _autoDuck;
        set
        {
            if (SetField(ref _autoDuck, value))
                SavePrefs();
        }
    }

    /// <summary>状态栏迷你条是否展开为完整列表。</summary>
    public bool Expanded
    {
        get => _expanded;
        set => SetField(ref _expanded, value);
    }

    public string McOstStatus
    {
        get => _mcOstStatus;
        private set => SetField(ref _mcOstStatus, value);
    }

    public string CurrentTrackDisplay => CurrentTrack?.Display ?? "未选择曲目";

    // ---- 命令实现 ----

    private void SetSource(string? kind)
    {
        if (kind is not ("Local" or "Online" or "McOst" or "Client")) return;

        // 本地客户端模式总开关关闭时直接拒绝（用户不该点到一个注定被拒的模式）
        if (kind == "Client" && !ClientPrefs.Enabled)
        {
            StatusText = "本地客户端模式未启用，请先在「设置 → 音乐」中开启";
            return;
        }

        SourceKind = kind!;
        if (kind == "McOst" && McOstGroups.Count == 0)
            ScanMcOst();

        if (kind == "Client")
        {
            // 防叠音不变量（对齐 WPF）：本地客户端模式下启动器不放音 ——
            // 停掉自己的音源并清空播放列表，避免与外部客户端同时出声。
            StopPlaybackForClientMode();
            RefreshLyricTexts();   // 按「客户端模式下仍用 API 取歌词」决定显隐
            StatusText = "本地客户端模式：音乐由外部客户端播放，启动器不再输出音频";

            // 切回客户端模式时若客户端未运行，替用户把它拉起来（当遥控器用）
            if (!IsClientRunning) LaunchClient();
        }
    }

    /// <summary>客户端进程是否仍在运行。</summary>
    public bool IsClientRunning
    {
        get
        {
            try { return _clientProcess is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>进入本地客户端模式前停掉自身音源（防叠音）。</summary>
    private void StopPlaybackForClientMode()
    {
        try { Host?.Stop(); } catch { /* 宿主不可用时忽略 */ }
        IsPlaying = false;
        Tracks.Clear();
        CurrentTrack = null;
        Lyric.Clear();
        LyricText = "";
        LyricLineCount = 0;
    }

    /// <summary>拉起外部客户端（把客户端当遥控器）。已运行则不重复拉起。</summary>
    private void LaunchClient()
    {
        if (!HasClientExe)
        {
            ClientStatus = "尚未选择客户端程序，请先在下方「选择程序」";
            return;
        }
        if (IsClientRunning) { ClientStatus = "客户端已在运行"; return; }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClientExePath,
                UseShellExecute = true,      // Linux 下 .desktop/AppImage 需经 shell 拉起
            };
            _clientProcess = Process.Start(psi);
            ClientRunning = $"已启动：{ClientDisplayName}";
            ClientStatus = "启动器作为遥控器显示信息，播放由客户端负责";
        }
        catch (Exception ex)
        {
            _clientProcess = null;
            ClientRunning = "";
            ClientStatus = $"启动客户端失败：{ex.Message}";
        }
    }

    /// <summary>打开文件选择器让用户挑选客户端可执行文件（Avalonia StorageProvider 异步版）。</summary>
    private async void BrowseClientExe()
    {
        try
        {
            var picked = await Services.UIService.PickFileAsync(
                title: "选择音乐客户端程序",
                filterPattern: "*.exe;*.AppImage;*.sh").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(picked)) return;
            ClientExePath = picked!;
            ClientStatus = HasClientExe ? "已选择客户端，可启动" : "尚未选择客户端程序";
        }
        catch (Exception ex)
        {
            ClientStatus = $"选择失败：{ex.Message}";
        }
    }

    /// <summary>手动重新拉取当前曲目的在线歌词（设置页「客户端模式仍用 API 歌词」旁的重试）。</summary>
    private void SyncClientLyric() => _ = SyncLyricFromApiAsync();

    /// <summary>
    /// 设置页改了本地客户端相关设置后由 VM 调用：刷新入口可用性与歌词显隐
    /// （对齐 WPF <c>OnClientPrefsChanged</c>）。
    /// </summary>
    public void OnClientPrefsChanged()
    {
        OnPropertyChanged(nameof(ClientModeAvailable));
        OnPropertyChanged(nameof(HasClientExe));
        OnPropertyChanged(nameof(ClientDisplayName));
        RefreshLyricTexts();
    }

    private void PlayPause()
    {
        if (CurrentTrack is null)
        {
            if (Tracks.Count == 0) { StatusText = "播放列表为空，请先加载音源"; return; }
            SelectAndPlay(0);
            return;
        }
        if (IsPlaying)
        {
            IsPlaying = false;
            Host?.Pause();
            StatusText = "已暂停：" + CurrentTrack.Display;
        }
        else
        {
            IsPlaying = true;
            Host?.Resume();
            StatusText = "正在播放：" + CurrentTrack.Display;
        }
    }

    private void Next() => Advance(userTriggered: true);
    private void Previous() => Advance(userTriggered: true, backward: true);

    private void Advance(bool userTriggered, bool backward = false)
    {
        var t = backward ? _playlist.Previous() : _playlist.Next(userTriggered);
        if (t is null)
        {
            IsPlaying = false;
            Host?.Stop();
            StatusText = "播放列表结束";
            return;
        }
        SelectAndPlay(_playlist.CurrentIndex);
    }

    /// <summary>宿主报告一曲播放结束（顺序播放到尾则停止）。</summary>
    public void OnTrackEnded()
    {
        var t = _playlist.Next(userTriggered: false);
        if (t is null)
        {
            IsPlaying = false;
            Host?.Stop();
            StatusText = "播放列表结束";
            return;
        }
        SelectAndPlay(_playlist.CurrentIndex);
    }

    private void SelectAndPlay(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        _playlist.Select(index);
        CurrentTrack = _playlist.Current;
        IsPlaying = true;
        Host?.LoadAndPlay(CurrentTrack!.Path);
        StatusText = "正在播放：" + CurrentTrack.Display;
        OnPropertyChanged(nameof(CurrentTrackDisplay));
    }

    private void CycleMode()
    {
        Mode = _playlist.CycleMode();
        StatusText = "循环模式：" + ModeText;
    }

    /// <summary>直接播放指定曲目（来自 MC 原声列表或本地 / 在线列表）。</summary>
    private void PlayTrack(object? track)
    {
        Track? t = track switch
        {
            Track tr => tr,
            McOstTrack mt => mt.ToTrack(),
            _ => null
        };
        if (t is null) return;
        int idx = _playlist.Tracks.ToList().IndexOf(t);
        if (idx < 0)
        {
            _playlist.Add(t);
            SyncTracks();
            idx = _playlist.Count - 1;
        }
        SelectAndPlay(idx);
    }

    private async Task LoadLocalFolderAsync()
    {
        var folder = await FolderPicker.PickAsync("选择音乐文件夹");
        AddFolder(folder);
    }

    /// <summary>直接加载本地文件夹（供 UI 代码 / harness 使用）。</summary>
    public void AddFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        var added = _playlist.AddFolder(folder, recursive: true);
        SyncTracks();
        StatusText = added > 0 ? $"已添加 {added} 首本地曲目" : "未找到支持的音频文件";
    }

    private void AddOnline()
    {
        if (string.IsNullOrWhiteSpace(OnlineUrl)) { StatusText = "请填写在线流媒体地址"; return; }
        var track = new Track
        {
            Path = OnlineUrl,
            Title = OnlineUrl,
            Artist = "在线流媒体"
        };
        _playlist.Add(track);
        SyncTracks();
        StatusText = "已添加在线音源";
    }

    private void ScanMcOst()
    {
        var root = GameConstants.DefaultGameRoot;
        var groups = McOstExtractor.Scan(root);
        McOstGroups.Clear();
        foreach (var g in groups) McOstGroups.Add(g);

        var total = groups.Sum(g => g.Tracks.Count);
        McOstStatus = total > 0 ? $"已提取 {total} 首 MC 原声" : "未找到 MC 原声（assets 缺失或不支持）";
    }

    private void SyncTracks()
    {
        Tracks.Clear();
        foreach (var t in _playlist.Tracks) Tracks.Add(t);
        OnPropertyChanged(nameof(CurrentTrackDisplay));
    }

    /// <summary>游戏启动联动：依照设置降低音量（保留播放）。</summary>
    public void OnGameLaunch()
    {
        if (!AutoDuck) return;
        if (IsPlaying)
        {
            Host?.SetVolume(Math.Min(Volume, 15));
            StatusText = "游戏启动：音乐已降低音量";
        }
    }

    /// <summary>游戏退出联动：恢复音量。</summary>
    public void OnGameExit()
    {
        if (!AutoDuck) return;
        Host?.SetVolume(Volume);
    }

    /// <summary>宿主注入后把当前音量推送到解码器（构造函数里 Host 尚为空）。</summary>
    public void SetVolumeFromHost() => Host?.SetVolume(_volume);

    private void SavePrefs()
    {
        try
        {
            var p = ProfileStore.Load(GameConstants.DefaultGameRoot);
            p.MusicAutoDuck = _autoDuck;
            p.MusicVolume = _volume;
            ProfileStore.Save(p);
        }
        catch { /* 忽略持久化失败 */ }
    }

    // ===== 在线音源（规格：MusicSourceMode.Api）—— 对齐 WPF MusicPlayerViewModel =====

    /// <summary>把后台线程的 UI 通知送回 UI 线程（事件回调可能来自任意线程）。</summary>
    private static void RunOnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch { /* 应用退出时的回调无所谓 */ }
    }

    private async Task SearchOnlineAsync()
    {
        if (OnlineBusy) return;
        if (!ApiEnabled) { OnlineStatus = "在线音源未启用"; return; }

        var kw = (OnlineKeyword ?? "").Trim();
        if (kw.Length == 0) { OnlineStatus = "请输入歌曲名"; return; }

        OnlineBusy = true;
        OnlineStatus = "搜索中…";
        try
        {
            var songs = await Online.SearchSongsAsync(kw);
            OnlineResults.Clear();
            foreach (var s in songs) OnlineResults.Add(s);
            OnlineStatus = songs.Count > 0 ? $"找到 {songs.Count} 首" : "没有找到匹配的歌曲";

            OnlinePlaylists.Clear();
            if (Online.CanSearchPlaylists)
            {
                foreach (var pl in await Online.SearchPlaylistsAsync(kw)) OnlinePlaylists.Add(pl);
            }
        }
        catch (MusicApiException ex)
        {
            OnlineStatus = ex.Message;
        }
        catch
        {
            OnlineStatus = "搜索失败，请检查网络或数据源配置";
        }
        finally { OnlineBusy = false; }
    }

    /// <summary>
    /// 导入歌单到播放列表。每首都必须再取一次直链 —— 直链有有效期且与音质绑定，
    /// 现在取好是为了让列表里的曲目随时能点播。
    /// </summary>
    private async Task ImportPlaylistAsync(OnlinePlaylist? playlist)
    {
        if (OnlineBusy) return;
        if (!ApiEnabled) { OnlineStatus = "在线音源未启用"; return; }

        var id = playlist?.Id ?? (PlaylistIdInput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { OnlineStatus = "请先搜索并选择一个歌单，或填写歌单 ID"; return; }

        OnlineBusy = true;
        OnlineStatus = "正在读取歌单…";
        try
        {
            var tracks = await Online.GetPlaylistTracksAsync(id);
            if (tracks.Count == 0) { OnlineStatus = "歌单为空或 ID 不正确"; return; }

            OnlineStatus = $"歌单共 {tracks.Count} 首，正在获取播放地址…";
            var added = await MaterializeAsync(tracks);
            OnlineStatus = added > 0
                ? $"已导入 {added} 首" + (added < tracks.Count ? $"（{tracks.Count - added} 首因版权无法获取）" : "")
                : "没有可播放的曲目（多为版权限制）";
        }
        catch (MusicApiException ex) { OnlineStatus = ex.Message; }
        catch { OnlineStatus = "读取歌单失败"; }
        finally { OnlineBusy = false; }
    }

    /// <summary>
    /// 并发换直链、收集完再按原顺序加入播放列表。
    /// <para>为什么「先并发取 URL，最后统一 Add」：<see cref="MusicPlaylist"/> 不是线程安全的，
    /// 并发 Add 会打乱 CurrentIndex 语义；而 URL 请求是纯网络，并行能省下大量等待。</para>
    /// </summary>
    private async Task<int> MaterializeAsync(IReadOnlyList<OnlineTrack> tracks, int maxParallel = 8)
    {
        var urls = new string?[tracks.Count];
        using var sem = new SemaphoreSlim(maxParallel);

        await Task.WhenAll(tracks.Select((t, i) => Task.Run(async () =>
        {
            await sem.WaitAsync();
            try { urls[i] = await Online.GetPlayableUrlAsync(t); }
            catch { urls[i] = null; }
            finally { sem.Release(); }
        })));

        var added = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(urls[i])) continue;
            _playlist.Add(tracks[i].ToTrack(urls[i]!));
            added++;
        }
        SyncTracks();
        return added;
    }

    /// <summary>点播一条搜索结果（先换直链，再进播放列表播放）。</summary>
    private async Task PlayOnlineTrackAsync(OnlineTrack? track)
    {
        if (track is null || OnlineBusy) return;
        if (!ApiEnabled) { OnlineStatus = "在线音源未启用"; return; }

        OnlineBusy = true;
        OnlineStatus = "正在获取播放地址…";
        try
        {
            // 重复点同一首时不要往列表里加第二份：按「标题 + 歌手」找已有条目
            var existing = FindOnlineInPlaylist(track);
            if (existing >= 0)
            {
                _currentOnline = track;
                SelectAndPlay(existing);
                OnlineStatus = "正在播放：" + (CurrentTrack?.Display ?? track.Title);
                return;
            }

            var url = await Online.GetPlayableUrlAsync(track);
            if (string.IsNullOrWhiteSpace(url))
            {
                OnlineStatus = "无法获取播放地址（多为版权或会员限制）";
                return;
            }

            SourceKind = "Online";
            _currentOnline = track;
            _playlist.Add(track.ToTrack(url!));
            SyncTracks();
            SelectAndPlay(_playlist.Count - 1);
            OnlineStatus = "正在播放：" + CurrentTrack!.Display;
        }
        catch (MusicApiException ex) { OnlineStatus = ex.Message; }
        catch { OnlineStatus = "播放失败"; }
        finally { OnlineBusy = false; }
    }

    /// <summary>在播放列表里找同一首在线曲目（标题 + 歌手都相等）。返回 -1 表示没有。</summary>
    private int FindOnlineInPlaylist(OnlineTrack track)
    {
        var list = _playlist.Tracks.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].IsRemote && list[i].Title == track.Title && list[i].Artist == track.Artist)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 取在线曲目的歌词：先看数据源给不给，拿不到再退到在线歌词服务。
    /// <b>全程静默</b> —— 没歌词就隐藏歌词区，不弹错误。
    /// </summary>
    private async Task LoadOnlineLyricAsync(OnlineTrack track)
    {
        _onlineLyricCts?.Cancel();
        var cts = new CancellationTokenSource();
        _onlineLyricCts = cts;
        try
        {
            var lrc = await Online.GetLyricAsync(track, cts.Token);
            if (string.IsNullOrWhiteSpace(lrc) && ClientPrefs.LyricEnabled)
                lrc = await _lyricService.GetLrcAsync(track.Title, track.Artist, cts.Token);
            if (cts.IsCancellationRequested) return;

            Lyric.PinMode = LyricPinModeSetting;
            if (string.IsNullOrWhiteSpace(lrc)) Lyric.Clear();
            else Lyric.Load("online:" + track.Id, LyricEngine.ParseLrc(lrc));

            RefreshLyricTexts();
        }
        catch (OperationCanceledException) { /* 被新一轮取代 */ }
        catch { /* 歌词属装饰性，失败不影响播放 */ }
        finally
        {
            if (ReferenceEquals(_onlineLyricCts, cts)) { _onlineLyricCts.Dispose(); _onlineLyricCts = null; }
        }
    }

    // ---- 登录 ----

    private async Task StartApiLoginAsync()
    {
        if (!Online.CanLogin)
        {
            LoginStatusText = "当前数据源不支持扫码登录，请改用网易云 API";
            return;
        }
        if (!Online.IsAvailable)
        {
            LoginStatusText = "在线音源不可用，请先配置服务地址";
            return;
        }

        CancelApiLogin();
        var cts = new CancellationTokenSource();
        _loginCts = cts;
        try
        {
            LoginStatusText = "正在获取二维码…";
            var session = await Online.CreateQrLoginAsync(cts.Token);
            _loginKey = session.Key;
            LoginQrDataUrl = session.QrDataUrl;
            LoginPanelVisible = true;
            LoginStatusText = "请用手机扫描下方二维码";

            // 轮询节奏 1.5s：太快会被限流，太慢用户会觉得「点了没反应」
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1500, cts.Token);
                var r = await Online.PollQrLoginAsync(session.Key, cts.Token);
                LoginStatusText = r.Message;

                if (r.State == QrLoginState.Confirmed)
                {
                    await Online.CompleteLoginAsync(r.Cookie, cts.Token);
                    PersistApiCredential();
                    LoginPanelVisible = false;
                    LoginQrDataUrl = "";
                    await LoadMyPlaylistsAsync();
                    UpdateLoginUi();
                    break;
                }
                if (r.State == QrLoginState.Expired) break;
            }
        }
        catch (OperationCanceledException) { /* 用户取消 */ }
        catch (MusicApiException ex) { LoginStatusText = ex.Message; }
        catch { LoginStatusText = "登录失败，请重试"; }
        finally
        {
            if (ReferenceEquals(_loginCts, cts)) { _loginCts.Dispose(); _loginCts = null; }
        }
    }

    private void CancelApiLogin()
    {
        try { _loginCts?.Cancel(); } catch { /* 已结束的轮询忽略 */ }
        _loginCts = null;
        _loginKey = null;
        LoginPanelVisible = false;
        LoginQrDataUrl = "";
    }

    private void ApiLogout()
    {
        Online.Logout();
        MyPlaylists.Clear();
        PersistApiCredential();
        UpdateLoginUi();
        LoginStatusText = "已退出登录";
    }

    private async Task LoadMyPlaylistsAsync()
    {
        if (!Online.IsLoggedIn || !Online.CanLogin) return;
        try
        {
            var list = await Online.GetMyPlaylistsAsync();
            RunOnUi(() =>
            {
                MyPlaylists.Clear();
                foreach (var p in list) MyPlaylists.Add(p);
            });
        }
        catch { /* 登录态失效：静默清空即可 */ }
    }

    /// <summary>把当前登录态（凭证 + 昵称头像）写回配置。</summary>
    private void PersistApiCredential()
    {
        try
        {
            var p = ProfileStore.Load(GameConstants.DefaultGameRoot);
            var exp = Online.ExportCredential();
            p.MusicApi.Credential = exp?.Credential ?? "";
            p.MusicApi.AccountName = exp?.Nickname ?? "";
            p.MusicApi.AccountAvatar = exp?.AvatarUrl ?? "";
            ProfileStore.Save(p);
        }
        catch { /* 持久化失败不影响本次会话 */ }
    }

    private void UpdateLoginUi()
    {
        OnPropertyChanged(nameof(IsOnlineLoggedIn));
        OnPropertyChanged(nameof(CanOnlineLogin));
        OnPropertyChanged(nameof(ApiAccountLabel));
    }

    /// <summary>
    /// 设置页改了在线音源的任一项后调用：重建数据源并让界面即时反映。
    /// 协议类型 / 服务地址一变，provider 内部状态（含登录态）必须整体重来，所以要 Reload 而不是改字段。
    /// </summary>
    public void OnApiPrefsChanged()
    {
        try
        {
            var prefs = ProfileStore.Load(GameConstants.DefaultGameRoot).MusicApi;
            Online.Reload(prefs);

            OnPropertyChanged(nameof(ApiEnabled));
            UpdateLoginUi();

            // 在线音源被关掉时，若正停在该音源则退回本地文件夹
            if (IsOnline && !ApiEnabled)
            {
                SourceKind = "Local";
                StatusText = "在线音源已关闭，已切回本地文件夹";
            }

            _ = LoadMyPlaylistsAsync();
        }
        catch { /* 联动属非关键 */ }
    }
}
