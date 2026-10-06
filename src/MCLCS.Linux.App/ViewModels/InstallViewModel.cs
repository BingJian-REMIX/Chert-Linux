using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using MCLCS.Core.Mvvm;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 下载 → 安装 视图模型：选择类型（Vanilla / Fabric / Forge / NeoForge / Quilt）与版本号，
/// 调用 LauncherService 安装，日志实时回显。
/// <para>
/// 此前这个页全项目没有入口（死代码），而且：安装器收到的 progress 恒为 null（进度条不动）、
/// 没有取消按钮（只能等它跑完）。现在补上入口、进度、取消、版本下拉与结果提示。
/// </para>
/// </summary>
public class InstallViewModel : ObservableObject
{
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _reloadCommand;
    private readonly RelayCommand _cancelCommand;

    private string _selectedType = "Vanilla";
    private string _versionId = "";
    private string _log = "";
    private double _progress;
    private bool _isBusy;
    private bool _versionsLoaded;
    private CancellationTokenSource? _cts;

    public InstallViewModel()
    {
        _installCommand = new AsyncRelayCommand(_ => InstallAsync(), _ => CanInstall);
        _cancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        _reloadCommand = new AsyncRelayCommand(_ => LoadVersionsAsync(true), _ => !IsBusy);
    }

    public ObservableCollection<string> InstallTypes { get; } =
        new() { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" };

    /// <summary>可安装版本清单（原版 release）。首次显示本页时拉取，拉不到也能手输。</summary>
    public ObservableCollection<string> AvailableVersions { get; } = new();

    public string SelectedType
    {
        get => _selectedType;
        set => SetField(ref _selectedType, value);
    }

    public string VersionId
    {
        get => _versionId;
        set
        {
            if (SetField(ref _versionId, value)) _installCommand.RaiseCanExecuteChanged();
        }
    }

    public string Log
    {
        get => _log;
        set => SetField(ref _log, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanCancel));
            _installCommand.RaiseCanExecuteChanged();
            _cancelCommand.RaiseCanExecuteChanged();
            _reloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanInstall => !IsBusy && !string.IsNullOrWhiteSpace(VersionId);

    /// <summary>取消按钮可用性（此前根本没有取消入口）。</summary>
    public bool CanCancel => IsBusy;

    public ICommand InstallCommand => _installCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand ReloadVersionsCommand => _reloadCommand;

    /// <summary>首次显示本页时拉取可安装版本（幂等）。</summary>
    public async Task EnsureVersionsLoadedAsync() => await LoadVersionsAsync(false);

    private async Task LoadVersionsAsync(bool force)
    {
        if (_versionsLoaded && !force) return;
        _versionsLoaded = true;
        try
        {
            var list = await LauncherService.Instance.GetVanillaVersionsDetailedAsync();
            var ids = list.Where(v => v.Type == "release").Select(v => v.Id).ToList();
            if (ids.Count == 0) ids = list.Select(v => v.Id).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableVersions.Clear();
                foreach (var id in ids) AvailableVersions.Add(id);
                if (string.IsNullOrWhiteSpace(VersionId) && AvailableVersions.Count > 0)
                    VersionId = AvailableVersions[0];
            });
        }
        catch (Exception ex)
        {
            AppendLog($"获取版本清单失败（可手动输入版本号）：{ex.Message}");
        }
    }

    private async Task InstallAsync()
    {
        var version = (VersionId ?? "").Trim();
        if (version.Length == 0)
        {
            AppendLog("请先选择或输入要安装的版本（例如 1.20.1）。");
            return;
        }

        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        AppendLog($"开始安装 {SelectedType} {version} …");
        try
        {
            var progress = new Progress<double>(p => Progress = Math.Clamp(p * 100d, 0, 100));
            var id = await LauncherService.Instance.InstallVersionAsync(
                version, LoaderKeyOf(SelectedType), progress, ct);

            if (string.IsNullOrEmpty(id))
            {
                AppendLog("安装已结束，但未返回版本 Id —— 请到游戏页版本列表确认。");
            }
            else
            {
                AppendLog($"安装完成：{id}（切到游戏页即可看到）");
                ToastService.Instance.Show(new ToastOptions
                {
                    Title = "安装完成",
                    Message = $"{SelectedType} {id}"
                });
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消安装。");
        }
        catch (Exception ex)
        {
            AppendLog($"安装失败：{ex.Message}");
            ToastService.Instance.Show(new ToastOptions
            {
                Title = "安装失败",
                Message = ex.Message,
                Danger = true,
                DurationMs = 8000
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void Cancel()
    {
        if (_cts is null)
        {
            AppendLog("当前没有正在进行的安装。");
            return;
        }
        AppendLog("正在取消…（已下载的部分会保留，下次继续）");
        _cts.Cancel();
    }

    private void AppendLog(string line)
    {
        if (Log.Length > 8000) Log = Log[^4000..];
        Log += line + "\n";
    }

    private static string LoaderKeyOf(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "forge" => "forge",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => "none"
    };
}
