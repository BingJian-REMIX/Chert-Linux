using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 工具箱 → 日志（对齐 WPF LogManager）：列出 / 读取 / 搜索 / 过滤 / 导出游戏日志与崩溃报告。
/// 文件级操作，无副作用（导出为复制）。
/// </summary>
public class LogViewModel : ObservableObject
{
    private readonly string _gameRoot = GameConstants.DefaultGameRoot;

    // 单次最多绑定给界面的行数：日志动辄几万行，全量塞进 ObservableCollection 会卡死界面。
    // 超过就只保留**末尾**这些行（日志最新内容在末尾），并在状态栏如实报出被截断了。
    private const int MaxDisplayLines = 50000;

    private readonly DispatcherTimer _filterTimer;
    private int _loadToken;                            // 防止快速切换文件时旧加载覆盖新结果

    private ObservableCollection<LogFileInfo> _logs = new();
    public ObservableCollection<LogFileInfo> Logs
    {
        get => _logs;
        set => SetField(ref _logs, value);
    }

    private LogFileInfo? _selectedLog;
    public LogFileInfo? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (SetField(ref _selectedLog, value))
            {
                ExportCommand.RaiseCanExecuteChanged();
                _ = LoadSelected();
            }
        }
    }

    private ObservableCollection<LogLine> _lines = new();
    public ObservableCollection<LogLine> Lines
    {
        get => _lines;
        set => SetField(ref _lines, value);
    }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value))
            {
                // 去抖：停手 250ms 再过滤，否则每敲一个字符都要重建一次几万行的集合
                _filterTimer.Stop();
                _filterTimer.Start();
            }
        }
    }

    private bool _onlyErrors;
    public bool OnlyErrors
    {
        get => _onlyErrors;
        set
        {
            if (SetField(ref _onlyErrors, value))
            {
                _filterTimer.Stop();
                _filterTimer.Start();
            }
        }
    }

    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    private List<LogLine> _allLines = new();

    public ICommand RefreshCommand { get; }
    // 类型必须是 RelayCommand：选中项变化时要手动 RaiseCanExecuteChanged，否则「导出」按钮一直灰着
    public RelayCommand ExportCommand { get; }

    public LogViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        ExportCommand = new RelayCommand(_ => ExportSelected(), _ => SelectedLog is not null);
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        Refresh();
    }

    public void Refresh()
    {
        Logs = new ObservableCollection<LogFileInfo>(LogManager.ListLogs(_gameRoot));
        Status = $"共 {Logs.Count} 个日志 / 崩溃报告文件";
    }

    private void ApplyFilter()
    {
        if (SelectedLog is null) return;
        var filtered = LogManager.Filter(_allLines, FilterText, OnlyErrors, MaxDisplayLines, out int total);
        Lines = new ObservableCollection<LogLine>(filtered);
        var isFiltering = !string.IsNullOrWhiteSpace(FilterText) || OnlyErrors;
        Status = filtered.Count < total
            ? (isFiltering
                ? $"匹配 {total} 行，已显示最近 {filtered.Count} 行（文件共 {_allLines.Count} 行）"
                : $"已显示最近 {filtered.Count} 行（文件共 {_allLines.Count} 行）")
            : (isFiltering
                ? $"匹配 {total} 行（文件共 {_allLines.Count} 行）"
                : $"共 {_allLines.Count} 行");
    }

    private async Task LoadSelected()
    {
        var file = SelectedLog;
        if (file is null)
        {
            _allLines = new List<LogLine>();
            Lines = new ObservableCollection<LogLine>();
            return;
        }

        var token = ++_loadToken;
        Status = $"读取中：{file.Name}";
        List<LogLine> all;
        try
        {
            // .gz 要解压、大文件要整读，同步做会把 UI 卡住
            all = await Task.Run(() => LogManager.ParseLines(LogManager.ReadLog(file.FullPath)));
        }
        catch (Exception ex)
        {
            Status = $"读取失败：{ex.Message}";
            return;
        }
        if (token != _loadToken) return;   // 已被更新的选择覆盖，丢弃陈旧结果

        _allLines = all;
        ApplyFilter();
        var shown = Lines.Count;
        Status = shown < all.Count
            // 被截断了就如实说，别让用户以为看到的就是全部
            ? $"已加载 {file.Name}：共 {all.Count} 行，界面只显示最近 {shown} 行"
            : $"已加载 {file.Name}（{all.Count} 行）";
    }

    private void ExportSelected()
    {
        if (SelectedLog is null) return;
        var dest = Path.Combine(Path.GetTempPath(), "mclcs_" + SelectedLog.Name);
        Status = LogManager.Export(SelectedLog.FullPath, dest)
            ? $"已导出到：{dest}"
            : "导出失败";
    }
}
