using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using MCLCS.Core.Localization;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Core.Utils;
using System.Net.Http;
using MCLCS.Core.Auth;
using Avalonia.Threading;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>
/// 设置 → 账户 视图模型：列出 / 新增离线账号 / 删除（Core AccountStore 持久化到 mclcs_accounts.json），
/// 并提供微软设备代码登录（对应 WPF 的 MicrosoftAuthenticator 流程，登录结果写入同一 AccountStore）。
/// authlib 第三方登录暂未接入。
/// </summary>
public class AccountsViewModel : ObservableObject
{
    private readonly string _gameRoot = GameConstants.DefaultGameRoot;

    private ObservableCollection<AccountEntry> _accounts = new();
    public ObservableCollection<AccountEntry> Accounts
    {
        get => _accounts;
        set => SetField(ref _accounts, value);
    }

    private AccountEntry? _selectedAccount;
    public AccountEntry? SelectedAccount
    {
        get => _selectedAccount;
        set => SetField(ref _selectedAccount, value);
    }

    /// <summary>新增离线账号时输入的昵称。</summary>
    private string _newUsername = "";
    public string NewUsername
    {
        get => _newUsername;
        set => SetField(ref _newUsername, value);
    }

    private string _status = LocaleManager.T("status.ready");
    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public ICommand AddOfflineCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand LoginMicrosoftCommand { get; }

    /// <summary>把当前选中账号设为「当前账号」（对齐 WPF SetActiveAccountCommand）：
    /// 双击账号条目即可直达 —— 多角色时切账号是最高频动作。</summary>
    public ICommand SetActiveAccountCommand { get; }

    private static readonly HttpClient Http = new();

    private bool _isMsBusy;
    public bool IsMsBusy
    {
        get => _isMsBusy;
        set => SetField(ref _isMsBusy, value);
    }

    private string _msMessage = "";
    public string MsMessage
    {
        get => _msMessage;
        set => SetField(ref _msMessage, value);
    }

    public AccountsViewModel()
    {
        AddOfflineCommand = new RelayCommand(_ => AddOffline());
        RemoveCommand = new RelayCommand(p => RemoveAccount(p as AccountEntry));
        LoginMicrosoftCommand = new AsyncRelayCommand(_ => LoginMicrosoftAsync());
        SetActiveAccountCommand = new RelayCommand(_ => SetActiveAccount());
        Load();
    }

    /// <summary>把选中账号标记为「最近使用」，游戏页/版本页据此默认选中（对齐 WPF SetActiveAccount）。</summary>
    private void SetActiveAccount()
    {
        if (SelectedAccount is not { } acc) return;
        try
        {
            AccountStore.MarkUsed(_gameRoot, acc.Id);
            acc.LastUsed = DateTime.UtcNow.ToString("o");
            Status = $"已设为当前账号：{acc.DisplayName}";
        }
        catch (Exception ex)
        {
            Status = $"设置失败：{ex.Message}";
        }
    }

    private void Load()
    {
        Accounts = new ObservableCollection<AccountEntry>(AccountStore.Load(_gameRoot));
        SelectedAccount = AccountStore.GetLastUsed(_gameRoot);
    }

    private void AddOffline()
    {
        var name = NewUsername.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "请输入账号昵称";
            return;
        }
        var entry = new AccountEntry
        {
            DisplayName = name,
            Username = name,
            AuthType = "offline"
        };
        AccountStore.Upsert(_gameRoot, entry);
        Load();
        NewUsername = "";
        Status = $"已添加离线账号：{name}";
    }

    private void RemoveAccount(AccountEntry? acc)
    {
        if (acc is null) return;
        AccountStore.Remove(_gameRoot, acc.Id);
        Load();
        Status = $"已删除账号：{acc.DisplayName}";
    }

    /// <summary>微软设备代码登录：弹出浏览器 + 设备码，完成后把账号写入 AccountStore。</summary>
    private async Task LoginMicrosoftAsync()
    {
        if (_isMsBusy) return;
        IsMsBusy = true;
        MsMessage = "正在发起微软登录…（将自动打开浏览器）";
        try
        {
            var auth = new MicrosoftAuthenticator(Http, null,
                msg => Dispatcher.UIThread.InvokeAsync(() => MsMessage = msg));
            var session = await auth.AuthenticateAsync(null, CancellationToken.None);

            // 同名 uuid 的微软账号已存在则复用其 Id，避免重复登录产生重复条目。
            var existing = AccountStore.Load(_gameRoot)
                .FirstOrDefault(a => a.AuthType == "microsoft" && a.Uuid == session.Uuid);

            var entry = new AccountEntry
            {
                DisplayName = session.Username,
                Username = session.Username,
                Uuid = session.Uuid,
                AuthType = "microsoft",
                AccessToken = session.AccessToken,
                LastUsed = DateTimeOffset.UtcNow.ToString("o")
            };
            if (existing is not null) entry.Id = existing.Id;

            AccountStore.Upsert(_gameRoot, entry);
            Load();
            Status = $"已添加微软账号：{session.Username}";
            MsMessage = "";
        }
        catch (Exception ex)
        {
            Status = $"微软登录失败：{ex.Message}";
            MsMessage = $"登录失败：{ex.Message}";
        }
        finally
        {
            IsMsBusy = false;
        }
    }
}
