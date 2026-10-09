using System.Collections.ObjectModel;
using System.Windows.Input;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Toolbox;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 工具箱 → 网络诊断（对齐 WPF NetworkDiagnostics）：探测核心镜像 / API 端点连通性与延迟。
/// </summary>
public class NetworkViewModel : ObservableObject
{
    private ObservableCollection<DiagnosticResult> _results = new();
    public ObservableCollection<DiagnosticResult> Results
    {
        get => _results;
        set => SetField(ref _results, value);
    }

    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => SetField(ref _busy, value);
    }

    public ICommand DiagnoseCommand => new AsyncRelayCommand(_ => DiagnoseAsync());
    public ICommand BandwidthCommand => new AsyncRelayCommand(_ => BandwidthAsync());

    // ---- 带宽测速（对齐 WPF 工具箱网络页）----

    /// <summary>可选测速时长（秒）。</summary>
    public IReadOnlyList<int> BandwidthSeconds { get; } = new[] { 5, 10, 15, 30 };

    private int _bandwidthSeconds = 10;
    public int SelectedBandwidthSeconds
    {
        get => _bandwidthSeconds;
        set => SetField(ref _bandwidthSeconds, value);
    }

    private string _bandwidthText = "";
    /// <summary>最近一次测速结果的可读摘要。</summary>
    public string BandwidthText
    {
        get => _bandwidthText;
        set => SetField(ref _bandwidthText, value);
    }

    private async Task BandwidthAsync()
    {
        Busy = true;
        BandwidthText = "";
        Status = $"正在测速（最多 {SelectedBandwidthSeconds} 秒）…";
        try
        {
            var r = await BandwidthTester.RunAsync(SelectedBandwidthSeconds, 4);
            if (!r.Ok)
            {
                Status = "测速失败：未能取得有效速率。";
                return;
            }
            BandwidthText = $"{r.SourceName}｜单线程 {r.SingleText}｜多线程 {r.MultiText}｜"
                            + $"共下载 {r.BytesDownloaded / 1024.0 / 1024.0:F1} MB / {r.ElapsedSeconds:F1}s";
            Status = "测速完成";
        }
        catch (Exception ex)
        {
            Status = $"测速失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task DiagnoseAsync()
    {
        Busy = true;
        Status = "正在诊断网络连通性…";
        try
        {
            var list = await NetworkDiagnostics.DiagnoseAsync();
            Results = new ObservableCollection<DiagnosticResult>(list);
            var ok = 0;
            foreach (var r in list) if (r.Reachable) ok++;
            Status = $"诊断完成：{ok}/{list.Count} 个端点可达";
        }
        catch (Exception ex)
        {
            Status = $"诊断失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
}
