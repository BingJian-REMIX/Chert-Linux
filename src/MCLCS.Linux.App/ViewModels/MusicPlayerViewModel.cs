using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Threading;
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

    private string _sourceKind = "Local"; // Local / Online / McOst
    private bool _isPlaying;
    private Track? _currentTrack;
    private string _statusText = "未播放";
    private int _volume = 60;
    private PlayMode _mode = PlayMode.LoopAll;
    private string _onlineUrl = "";
    private bool _autoDuck = true;
    private bool _expanded;
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

    public ObservableCollection<string> OnlinePresets { get; } = new()
    {
        "https://stream.example.com/minecraft-radio",
        "https://radio.example.org/ambient"
    };

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
        ExpandCommand = new RelayCommand(_ => Expanded = !Expanded);

        var profile = ProfileStore.Load(GameConstants.DefaultGameRoot);
        _autoDuck = profile.MusicAutoDuck;
        _volume = profile.MusicVolume;

        // 进度心跳：宿主注入后才有意义，故此处只备好定时器，实际启动见 Host setter。
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += OnProgressTick;
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
        if (kind is "Local" or "Online" or "McOst")
        {
            SourceKind = kind!;
            if (kind == "McOst" && McOstGroups.Count == 0)
                ScanMcOst();
        }
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
}
