using System.Collections.ObjectModel;
using System.Windows.Input;
using MCLCS.Core.Mvvm;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 成就展示（规格 2.3 面板 2，移植自 WPF AchievementViewModel）：复用既有的
/// <see cref="AchievementStats"/> / <see cref="AchievementScanner"/>（与版本设置的成就展示同源），
/// 扫描各存档 advancements/*.json，统计达成 / 紫色挑战数量。
/// </summary>
public class AchievementViewModel : ObservableObject
{
    private ObservableCollection<AchievementStats> _saves = new();
    private string _statusMessage = "";
    private bool _isBusy;

    public ObservableCollection<AchievementStats> Saves
    {
        get => _saves;
        set => SetField(ref _saves, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public ICommand RefreshCommand { get; }

    public AchievementViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        _ = RefreshAsync();
    }

    private Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var gameRoot = LauncherService.Instance.GameRoot;
            var stats = AchievementScanner.Scan(gameRoot);
            Saves = new ObservableCollection<AchievementStats>(stats);
            StatusMessage = stats.Count > 0
                ? $"{stats.Count} 个存档扫描完成"
                : "未找到任何存档的成就数据";
        }
        catch (Exception ex) { StatusMessage = $"扫描失败：{ex.Message}"; }
        finally { IsBusy = false; }
        return Task.CompletedTask;
    }
}
