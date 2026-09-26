namespace MCLCS.Core.Toolbox;

/// <summary>播放模式。</summary>
public enum PlayMode
{
    /// <summary>顺序播放（播完最后一首停止）。</summary>
    Sequential,
    /// <summary>列表循环。</summary>
    LoopAll,
    /// <summary>单曲循环。</summary>
    LoopOne,
    /// <summary>随机播放（一轮内不重复）。</summary>
    Shuffle
}

/// <summary>一首曲目。</summary>
public class Track
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }

    /// <summary>专辑名（从 ID3v1/v2、Vorbis Comment 读取，读不到为 null）。</summary>
    public string? Album { get; set; }

    /// <summary>时长（秒），未知为 0。</summary>
    public double DurationSec { get; set; }

    public string DurationText => DurationSec <= 0
        ? "--:--"
        : TimeSpan.FromSeconds(DurationSec).ToString(DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

    /// <summary>元数据副标题：歌手 · 专辑。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist!);
            if (!string.IsNullOrWhiteSpace(Album)) parts.Add(Album!);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>从音频文件读取标签信息填充本曲目。解析失败时保留已有标题（文件名）。</summary>
    public void LoadMetadata()
    {
        var tag = AudioMetadata.Read(Path);
        if (!string.IsNullOrWhiteSpace(tag.Title)) Title = tag.Title!;
        Artist ??= tag.Artist;
        Album ??= tag.Album;
        if (DurationSec <= 0) DurationSec = tag.DurationSec;
    }
}

/// <summary>
/// 音乐播放器的播放列表逻辑（工具箱面板 14）。
/// 只负责"下一首是哪首"这类纯逻辑，实际解码交给界面层的 MediaPlayer，
/// 这样核心逻辑可以在无音频设备的环境里被自检覆盖。
/// </summary>
public class MusicPlaylist
{
    /// <summary>支持的音频扩展名。</summary>
    public static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma" };

    private readonly List<Track> _tracks = new();
    private readonly List<int> _shuffleOrder = new();
    private int _shufflePos = -1;
    private readonly Random _random;

    public MusicPlaylist(int? seed = null) =>
        _random = seed.HasValue ? new Random(seed.Value) : new Random();

    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>当前曲目索引，-1 表示未选中。</summary>
    public int CurrentIndex { get; private set; } = -1;

    public Track? Current => CurrentIndex >= 0 && CurrentIndex < _tracks.Count ? _tracks[CurrentIndex] : null;

    public PlayMode Mode { get; set; } = PlayMode.LoopAll;

    /// <summary>音量 0-100。</summary>
    public int Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 100);
    }
    private int _volume = 60;

    public int Count => _tracks.Count;
    public bool IsEmpty => _tracks.Count == 0;

    /// <summary>是否在导入时读取音频标签。默认开启，批量导入大目录时可关闭以提高速度。</summary>
    public bool ReadMetadataOnAdd { get; set; } = true;

    public void Add(Track track)
    {
        if (ReadMetadataOnAdd) track.LoadMetadata();
        _tracks.Add(track);
        InvalidateShuffle();
    }

    public void AddRange(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
        {
            if (ReadMetadataOnAdd) t.LoadMetadata();
            _tracks.Add(t);
        }
        InvalidateShuffle();
    }

    /// <summary>扫描目录导入音频文件（不递归子目录之外的类型）。</summary>
    public int AddFolder(string dir, bool recursive = true)
    {
        if (!Directory.Exists(dir)) return 0;

        var before = _tracks.Count;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", option);
        }
        catch
        {
            return 0;
        }

        foreach (var f in files.Where(IsSupported).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // 导入时读取标题/歌手/专辑/时长，列表不再只显示文件名
            var track = new Track { Path = f, Title = Path.GetFileNameWithoutExtension(f) };
            if (ReadMetadataOnAdd) track.LoadMetadata();
            _tracks.Add(track);
        }

        InvalidateShuffle();
        return _tracks.Count - before;
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _tracks.Count) return false;
        _tracks.RemoveAt(index);

        if (CurrentIndex == index) CurrentIndex = _tracks.Count == 0 ? -1 : Math.Min(index, _tracks.Count - 1);
        else if (CurrentIndex > index) CurrentIndex--;

        InvalidateShuffle();
        return true;
    }

    public void Clear()
    {
        _tracks.Clear();
        CurrentIndex = -1;
        InvalidateShuffle();
    }

    /// <summary>选中指定索引；越界返回 false。</summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= _tracks.Count) return false;
        CurrentIndex = index;
        if (Mode == PlayMode.Shuffle) SyncShufflePos(index);
        return true;
    }

    /// <summary>
    /// 计算下一首的索引。<paramref name="userTriggered"/> 为 true 表示用户点了"下一首"，
    /// 此时单曲循环也会切到下一首（与自然播完的行为不同）。返回 -1 表示应停止播放。
    /// </summary>
    public int PeekNext(bool userTriggered = false)
    {
        if (_tracks.Count == 0) return -1;
        if (CurrentIndex < 0) return 0;

        switch (Mode)
        {
            case PlayMode.LoopOne:
                return userTriggered ? (CurrentIndex + 1) % _tracks.Count : CurrentIndex;

            case PlayMode.Sequential:
                return CurrentIndex + 1 < _tracks.Count ? CurrentIndex + 1 : (userTriggered ? -1 : -1);

            case PlayMode.LoopAll:
                return (CurrentIndex + 1) % _tracks.Count;

            case PlayMode.Shuffle:
                EnsureShuffle();
                if (_shuffleOrder.Count == 0) return -1;
                var next = _shufflePos + 1;
                if (next >= _shuffleOrder.Count) next = 0;   // 一轮结束重新洗牌
                return _shuffleOrder[next];

            default:
                return -1;
        }
    }

    /// <summary>切到下一首并返回它；返回 null 表示播放结束。</summary>
    public Track? Next(bool userTriggered = false)
    {
        var idx = PeekNext(userTriggered);
        if (idx < 0) return null;

        if (Mode == PlayMode.Shuffle)
        {
            EnsureShuffle();
            _shufflePos++;
            if (_shufflePos >= _shuffleOrder.Count)
            {
                Reshuffle();
                _shufflePos = 0;
            }
            CurrentIndex = _shuffleOrder[_shufflePos];
            return Current;
        }

        CurrentIndex = idx;
        return Current;
    }

    /// <summary>上一首（随机模式下回退洗牌序列）。</summary>
    public Track? Previous()
    {
        if (_tracks.Count == 0) return null;

        if (Mode == PlayMode.Shuffle)
        {
            EnsureShuffle();
            _shufflePos = _shufflePos <= 0 ? _shuffleOrder.Count - 1 : _shufflePos - 1;
            CurrentIndex = _shuffleOrder[_shufflePos];
            return Current;
        }

        CurrentIndex = CurrentIndex <= 0 ? _tracks.Count - 1 : CurrentIndex - 1;
        return Current;
    }

    /// <summary>随机模式：重新生成一轮不重复的播放顺序。</summary>
    public void Reshuffle()
    {
        _shuffleOrder.Clear();
        _shuffleOrder.AddRange(Enumerable.Range(0, _tracks.Count));
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }
        _shufflePos = CurrentIndex >= 0 ? _shuffleOrder.IndexOf(CurrentIndex) : -1;
    }

    /// <summary>当前洗牌顺序（自检用）。</summary>
    public IReadOnlyList<int> ShuffleOrder
    {
        get
        {
            EnsureShuffle();
            return _shuffleOrder;
        }
    }

    /// <summary>模式的中文名。</summary>
    public static string ModeText(PlayMode mode) => mode switch
    {
        PlayMode.Sequential => "顺序播放",
        PlayMode.LoopAll => "列表循环",
        PlayMode.LoopOne => "单曲循环",
        PlayMode.Shuffle => "随机播放",
        _ => "未知"
    };

    /// <summary>循环切换播放模式。</summary>
    public PlayMode CycleMode()
    {
        Mode = Mode switch
        {
            PlayMode.Sequential => PlayMode.LoopAll,
            PlayMode.LoopAll => PlayMode.LoopOne,
            PlayMode.LoopOne => PlayMode.Shuffle,
            _ => PlayMode.Sequential
        };
        if (Mode == PlayMode.Shuffle) InvalidateShuffle();
        return Mode;
    }

    private void EnsureShuffle()
    {
        if (_shuffleOrder.Count != _tracks.Count) Reshuffle();
    }

    private void InvalidateShuffle()
    {
        _shuffleOrder.Clear();
        _shufflePos = -1;
    }

    private void SyncShufflePos(int trackIndex)
    {
        EnsureShuffle();
        var pos = _shuffleOrder.IndexOf(trackIndex);
        if (pos >= 0) _shufflePos = pos;
    }
}
