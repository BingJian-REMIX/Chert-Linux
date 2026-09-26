using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.MultiInstance;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Tokens;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>挂机工作流中一个动作的编辑模型（统一为宏指令格式，对齐 Core 的 AfkWorkflowToken 与 WPF 端）。</summary>
public class AfkActionItem : ObservableObject
{
    private string _actionType = "F";
    private string _param = "10";

    public string ActionType { get => _actionType; set { SetField(ref _actionType, value); UpdatePreview(); } }
    public string Param { get => _param; set { SetField(ref _param, value); UpdatePreview(); } }

    /// <summary>人类可读的标签（宏语义）。</summary>
    public string Display => ActionType switch
    {
        "F" => $"功能键 F{Param}",
        "D" => $"延时 → {Param} 秒",
        "L" => $"长按 → {Param} 秒（须紧跟按键）",
        "K" => $"按键 → {VkName(Param)}",
        "C" => Param.Contains('-')
            ? $"鼠标连点(左键) → 每{Param.Split('-').Last()}ms 连点{Param.Split('-')[0]}次"
            : $"鼠标连点(左键) → {Param}",
        "R" => Param.Contains('-')
            ? $"鼠标连点(右键) → 每{Param.Split('-').Last()}ms 连点{Param.Split('-')[0]}次"
            : $"鼠标右键 → {Param}",
        "M" => Param.Contains(',')
            ? $"鼠标移动 → 右{Param.Split(',')[0]}/下{Param.Split(',')[1]} px"
            : $"鼠标移动 → {Param}",
        "S" => $"滚轮 → {Param}",
        "T" => $"输入文本 → {(AfkWorkflowToken.DecodeText(Param) is var t ? (string.IsNullOrEmpty(t) ? "(空)" : t) : "(空)")}",
        "G" => Param.All(char.IsDigit) ? $"按住(保持) → 键码 {Param}" : $"按住(保持) → {Param}",
        "U" => Param.All(char.IsDigit) ? $"松开 → 键码 {Param}" : $"松开 → {Param}",
        "J" => $"随机等待 ≤ {Param} 秒",
        "E" => $"按键 → {Param}",
        "*" => Param == "0" ? "循环 (无限)" : $"循环 ({Param} 轮)",
        _ => $"{ActionType}:{Param}"
    };

    /// <summary>参数输入框占位提示，按动作类型变化。</summary>
    public string ParamHint => ActionType switch
    {
        "F" => "功能键编号 1-24",
        "D" => "等待秒数",
        "L" => "按住秒数（需放在某按键后）",
        "K" => "虚拟键码 1-254（如 87=W）",
        "C" => "次数-间隔毫秒，如 1-500",
        "R" => "次数-间隔毫秒，如 1-500",
        "M" => "dx,dy 像素（可负），如 100,0",
        "S" => "滚轮增量，正下负上，如 120",
        "T" => "在下方「明文」框输入，自动转 base64",
        "G" => "键名或键码，如 W 或 87",
        "U" => "键名或键码，如 W 或 87",
        "J" => "最大秒数，如 5",
        "E" => "按键名称，如 A / ENTER / UP",
        "*" => "轮数，0=无限",
        _ => "参数"
    };

    /// <summary>文本动作(T)的明文 ↔ base64 双向绑定；非 T 类型退化为 Param 本身。</summary>
    public string PlainText
    {
        get => ActionType == "T" ? (AfkWorkflowToken.DecodeText(Param) ?? Param) : Param;
        set
        {
            if (ActionType == "T") Param = AfkWorkflowToken.EncodeText(value ?? "");
            else Param = value;
        }
    }

    public string Token => $"{ActionType}{Param}";

    public static string VkName(string param)
    {
        if (!int.TryParse(param, out var code) || code < 1 || code > 254)
            return param;
        if (code is >= 0x70 and <= 0x87)
            return $"F{code - 0x70 + 1} (键码 {code})";
        return code switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x20 => "Space",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
            0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
            0x41 => "A", 0x42 => "B", 0x43 => "C", 0x44 => "D", 0x45 => "E", 0x46 => "F",
            0x47 => "G", 0x48 => "H", 0x49 => "I", 0x4A => "J", 0x4B => "K", 0x4C => "L",
            0x4D => "M", 0x4E => "N", 0x4F => "O", 0x50 => "P", 0x51 => "Q", 0x52 => "R",
            0x53 => "S", 0x54 => "T", 0x55 => "U", 0x56 => "V", 0x57 => "W", 0x58 => "X",
            0x59 => "Y", 0x5A => "Z",
            _ => $"键码 {code}"
        };
    }

    public event Action? Changed;
    private void UpdatePreview() { OnPropertyChanged(nameof(Display)); OnPropertyChanged(nameof(Token)); OnPropertyChanged(nameof(ParamHint)); OnPropertyChanged(nameof(PlainText)); Changed?.Invoke(); }
}

/// <summary>
/// 挂机工作流编辑器（对齐 WPF AfkWorkflowViewModel）：可视化编辑宏动作序列，
/// 生成 / 导入 / 保存 Token 到 profile.AfkWorkflows，并新增「运行」通过
/// <see cref="AfkRunner"/>（xdotool）把工作流派发到正在运行的 MC 窗口。
/// Linux 降级：复制走 Avalonia 剪贴板（无 MainWindow 时仅提示）。
/// </summary>
public class AfkViewModel : ObservableObject
{
    private ObservableCollection<AfkActionItem> _actions = new();
    private AfkActionItem? _selectedAction;
    private string _workflowName = "";
    private ObservableCollection<string> _savedNames = new();
    private string _importToken = "";
    private string _statusMessage = "";

    private bool _isRunning;
    private string _runStatus = "未运行";
    private string _targetText = "未选定目标";

    public ObservableCollection<AfkActionItem> Actions { get => _actions; set => SetField(ref _actions, value); }
    public AfkActionItem? SelectedAction { get => _selectedAction; set => SetField(ref _selectedAction, value); }
    public string WorkflowName { get => _workflowName; set => SetField(ref _workflowName, value); }
    public ObservableCollection<string> SavedNames { get => _savedNames; set => SetField(ref _savedNames, value); }

    /// <summary>当前序列生成的 Token。</summary>
    public string TokenText => string.Join(";", Actions.Select(a => a.Token));

    public string ImportToken { get => _importToken; set => SetField(ref _importToken, value); }

    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    public bool IsRunning { get => _isRunning; set { SetField(ref _isRunning, value); (RunCommand as RelayCommand)?.RaiseCanExecuteChanged(); (StopCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
    public string RunStatus { get => _runStatus; set => SetField(ref _runStatus, value); }
    public string TargetText { get => _targetText; set => SetField(ref _targetText, value); }

    public ICommand AddActionCommand { get; }
    public ICommand RemoveActionCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CopyTokenCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }

    private CancellationTokenSource? _cts;

    public AfkViewModel()
    {
        AddActionCommand = new RelayCommand(_ => AddAction());
        RemoveActionCommand = new RelayCommand(_ => RemoveAction(), _ => SelectedAction is not null);
        MoveUpCommand = new RelayCommand(_ => Move(-1));
        MoveDownCommand = new RelayCommand(_ => Move(1));
        SaveCommand = new RelayCommand(_ => Save());
        LoadCommand = new RelayCommand(p => Load(p as string));
        DeleteCommand = new RelayCommand(p => Delete(p as string));
        ImportCommand = new RelayCommand(_ => Import());
        CopyTokenCommand = new RelayCommand(_ => _ = CopyToken());
        RunCommand = new RelayCommand(_ => _ = RunAsync(), _ => !IsRunning && Actions.Count > 0 && !string.IsNullOrWhiteSpace(TokenText));
        StopCommand = new RelayCommand(_ => Stop(), _ => IsRunning);

        RefreshSavedList();
        RefreshTarget();
    }

    private static string GameRoot => Services.LauncherService.Instance.GameRoot;

    /// <summary>刷新「运行目标」：挑选当前仍在运行的第一个 MC 实例。</summary>
    public void RefreshTarget()
    {
        var active = InstanceTracker.ListActive();
        TargetText = active.Count > 0
            ? $"将控制：MC 实例 PID {active[0].Pid}（{active[0].VersionId}）"
            : "将控制：前台窗口（请先启动游戏）";
    }

    private void RefreshSavedList()
    {
        var profile = ProfileStore.Load(GameRoot);
        SavedNames = new ObservableCollection<string>(profile.AfkWorkflows.Keys);
    }

    private void AddAction()
    {
        var item = new AfkActionItem { ActionType = "F", Param = "10" };
        item.Changed += () => OnPropertyChanged(nameof(TokenText));
        Actions.Add(item);
        SelectedAction = item;
        OnPropertyChanged(nameof(TokenText));
    }

    private void RemoveAction()
    {
        if (SelectedAction is null) return;
        Actions.Remove(SelectedAction);
        OnPropertyChanged(nameof(TokenText));
        (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Move(int delta)
    {
        var idx = Actions.IndexOf(SelectedAction!);
        if (idx < 0) return;
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= Actions.Count) return;
        Actions.Move(idx, newIdx);
        OnPropertyChanged(nameof(TokenText));
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(WorkflowName)) { StatusMessage = "请输入工作流名称"; return; }
        var token = TokenText;
        if (string.IsNullOrEmpty(token)) { StatusMessage = "工作流不能为空"; return; }
        if (!AfkWorkflowToken.IsValid(token)) { StatusMessage = "Token 非法：" + AfkWorkflowToken.Parse(token).Error; return; }

        var profile = ProfileStore.Load(GameRoot);
        profile.AfkWorkflows[WorkflowName.Trim()] = token;
        ProfileStore.Save(profile);
        StatusMessage = $"已保存「{WorkflowName}」";
        RefreshSavedList();
    }

    private void Load(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = ProfileStore.Load(GameRoot);
        if (!profile.AfkWorkflows.TryGetValue(name, out var token)) { StatusMessage = "未找到该工作流"; return; }

        ImportFromToken(token);
        WorkflowName = name;
        StatusMessage = $"已载入「{name}」";
    }

    private void Delete(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = ProfileStore.Load(GameRoot);
        profile.AfkWorkflows.Remove(name);
        ProfileStore.Save(profile);
        RefreshSavedList();
        StatusMessage = $"已删除「{name}」";
    }

    private void Import()
    {
        if (string.IsNullOrWhiteSpace(ImportToken)) { StatusMessage = "请粘贴 Token"; return; }
        ImportFromToken(ImportToken.Trim());
        StatusMessage = "已从 Token 载入";
    }

    /// <summary>从 Token 解析为动作（宏格式）。非法片段跳过，保证编辑器不崩。</summary>
    private void ImportFromToken(string token)
    {
        Actions.Clear();
        var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length < 1) continue;
            var type = char.ToUpperInvariant(part[0]).ToString();
            var param = part.Length > 1 ? part[1..] : "";
            if (type is not ("F" or "D" or "L" or "K" or "C" or "*" or "R" or "M" or "S" or "T" or "G" or "U" or "J" or "E")) continue;
            var item = new AfkActionItem { ActionType = type, Param = param };
            item.Changed += () => OnPropertyChanged(nameof(TokenText));
            Actions.Add(item);
        }
        OnPropertyChanged(nameof(TokenText));
        (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task CopyToken()
    {
        var token = TokenText;
        if (string.IsNullOrEmpty(token)) { StatusMessage = "没有可复制的内容"; return; }
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
                && w.Clipboard is { } cb)
            {
                await cb.SetTextAsync(token);
                StatusMessage = "已复制到剪贴板";
            }
            else
            {
                StatusMessage = $"已生成 Token（复制不可用）：{token}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"复制失败：{ex.Message}";
        }
    }

    // ---- 运行 / 停止 ----

    private async Task RunAsync()
    {
        var token = TokenText;
        var parse = AfkWorkflowToken.Parse(token);
        if (!parse.Ok) { StatusMessage = "无法运行：" + parse.Error; return; }

        RefreshTarget();
        var target = InstanceTracker.ListActive().FirstOrDefault();

        _cts = new CancellationTokenSource();
        IsRunning = true;
        RunStatus = "启动中…";
        StatusMessage = "挂机工作流已启动";

        try
        {
            var progress = new Progress<AfkRunProgress>(p =>
            {
                var cycle = p.TotalCycles == 0 ? $"第{p.Cycle}轮" : $"第{p.Cycle}/{p.TotalCycles}轮";
                RunStatus = $"运行中 · {cycle} · 步骤 {p.StepIndex}/{p.TotalSteps}：{p.CurrentStep}（{p.Elapsed:mm\\:ss}）";
            });
            await AfkRunner.RunAsync(token, target?.Pid, progress, _cts.Token);
            RunStatus = "已完成";
            StatusMessage = "挂机工作流执行完毕";
        }
        catch (OperationCanceledException)
        {
            RunStatus = "已停止";
            StatusMessage = "已手动停止挂机";
        }
        catch (Exception ex)
        {
            RunStatus = "出错";
            StatusMessage = "挂机出错：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop() => _cts?.Cancel();
}
