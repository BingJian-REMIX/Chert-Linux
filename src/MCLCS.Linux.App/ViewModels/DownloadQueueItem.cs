using System.ComponentModel;
using System.Threading;
using MCLCS.Core.Models;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>整合包在线来源的绑定包装（ComboBox 用）。</summary>
public class ModpackSourceEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsAvailable { get; init; }
    public string? UnavailableReason { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>下载队列中的一项。</summary>
public class DownloadQueueItem : INotifyPropertyChanged
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string TargetDir { get; init; } = "";
    public string? GameVersion { get; init; }
    public LoaderType Loader { get; init; }

    /// <summary>队列项摘要（卡片副标题），用于队列列表二行展示。</summary>
    public string Summary { get; init; } = "";

    /// <summary>整合包来源（modrinth），仅 Kind=modpack 使用。</summary>
    public string Source { get; init; } = "modrinth";

    /// <summary>
    /// 队列项类别，决定执行时走哪条下载/安装路径：
    /// mod / shader / resourcepack（Modrinth 文件下载）、modpack（整合包）、map（像素茶艺地图）、
    /// version（Minecraft 版本安装，配合 <see cref="InstallLoader"/>）。
    /// </summary>
    public string Kind { get; init; } = "mod";

    /// <summary>地图 slug（Kind=map 时用于回查详情直链）。</summary>
    public string? Slug { get; init; }

    /// <summary>版本安装所选加载器（none / forge / fabric / neoforge / quilt），仅 Kind=version 使用。</summary>
    public string InstallLoader { get; init; } = "none";

    public CancellationTokenSource? Cts { get; set; }

    private string _status = "排队中";
    /// <summary>
    /// 失败原因。此前 catch 里只把状态写成「失败」，异常信息被丢掉，
    /// 用户只知道没下成，不知道为什么。
    /// </summary>
    public string ErrorMessage { get; set; } = "";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(nameof(Progress)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
