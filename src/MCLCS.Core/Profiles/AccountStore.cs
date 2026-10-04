using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLCS.Core.Profiles;

/// <summary>
/// 一条账号记录。
/// <para>★ 实现 <see cref="INotifyPropertyChanged"/>：本类型是**绑定列表里的实体**
/// （设置页账号列表 / 游戏页账号下拉 / 版本列表页绑定下拉），就地改属性若不发通知，
/// 界面会静默停在旧值 —— 集合增删会发通知，但「改属性」不会。
/// 派生展示属性（<see cref="AuthTypeText"/> 等）同理依赖源属性通知。</para>
/// </summary>
public class AccountEntry : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _authType = "offline";
    private string _authlibServerUrl_ = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("displayName")]
    public string DisplayName
    {
        get => _displayName;
        set { if (Set(ref _displayName, value)) RaiseDerived(); }
    }

    [JsonPropertyName("authType")]
    public string AuthType
    {
        get => _authType;
        set { if (Set(ref _authType, value)) RaiseDerived(); }
    }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "0";

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("msExpiresAt")]
    public string? MsExpiresAt { get; set; }

    [JsonPropertyName("authlibServerUrl")]
    public string? AuthlibServerUrl
    {
        get => _authlibServerUrl_;
        set { if (Set(ref _authlibServerUrl_, value ?? "")) RaiseDerived(); }
    }

    [JsonPropertyName("lastUsed")]
    public string? LastUsed { get; set; }

    [JsonPropertyName("skinUrl")]
    public string? SkinUrl { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>就地改属性并通知（避免各处手写 OnPropertyChanged）。</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>源字段（登录方式 / 服务器地址）变化时，刷新依赖它的派生展示属性。</summary>
    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(AuthTypeText));
        OnPropertyChanged(nameof(ServerHost));
        OnPropertyChanged(nameof(SubtitleText));
    }

    // ===== 以下为纯派生的展示属性（不落盘，供列表显示）=====

    /// <summary>
    /// 登录方式的中文名（离线 / 微软 / 外置）。列表里直接显示 <see cref="AuthType"/>
    /// 只会看到 authlib / offline 这类英文技术词，多角色时完全分不清谁是谁。
    /// </summary>
    [JsonIgnore]
    public string AuthTypeText => AuthType switch
    {
        "microsoft" => "微软登录",
        "authlib" => "外置登录",
        _ => "离线",
    };

    /// <summary>
    /// 外置登录的服务器主机名（不含协议与路径），用于区分**同一服务下的不同角色**。
    /// 非外置登录返回空串。
    /// </summary>
    [JsonIgnore]
    public string ServerHost
    {
        get
        {
            if (AuthType != "authlib" || string.IsNullOrWhiteSpace(AuthlibServerUrl)) return "";
            var u = AuthlibServerUrl.Trim();
            if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = u["https://".Length..];
            else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) u = u["http://".Length..];
            u = u.TrimEnd('/');
            var slash = u.IndexOf('/');
            if (slash > 0) u = u[..slash];
            return u;
        }
    }

    /// <summary>列表副标题：外置登录显示「外置登录 · 主机名」，其余显示登录方式。</summary>
    [JsonIgnore]
    public string SubtitleText
    {
        get
        {
            var host = ServerHost;
            return string.IsNullOrEmpty(host) ? AuthTypeText : $"{AuthTypeText} · {host}";
        }
    }
}

/// <summary>多账号存储（mclcs_accounts.json）。</summary>
public static class AccountStore
{
    /// <summary>
    /// 账号列表发生变化（新增 / 更新 / 删除）时触发，参数为 gameRoot。
    /// 供各 UI 页（游戏页账号下拉、设置页账号列表、版本设置页绑定下拉）同步刷新。
    /// </summary>
    public static event Action<string>? Changed;

    private static string Path(string gameRoot) => System.IO.Path.Combine(gameRoot, "mclcs_accounts.json");

    public static List<AccountEntry> Load(string gameRoot)
    {
        var p = Path(gameRoot);
        if (!File.Exists(p)) return new List<AccountEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<AccountEntry>>(File.ReadAllText(p)) ?? new();
        }
        catch
        {
            return new List<AccountEntry>();
        }
    }

    public static void Save(string gameRoot, List<AccountEntry> accounts)
    {
        Directory.CreateDirectory(gameRoot);
        var json = JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path(gameRoot), json);
    }

    public static AccountEntry? GetLastUsed(string gameRoot)
    {
        var accounts = Load(gameRoot);
        return accounts
            .Where(a => !string.IsNullOrEmpty(a.LastUsed))
            .OrderByDescending(a => a.LastUsed)
            .FirstOrDefault()
            ?? accounts.FirstOrDefault();
    }

    /// <summary>
    /// 解析「启动某版本时应使用的账号」：优先返回 <paramref name="boundAccountId"/> 对应的账号
    /// （实现每版本独立账户绑定），找不到时回落到全局「最后使用」。
    /// </summary>
    public static AccountEntry? GetForVersion(string gameRoot, string? boundAccountId)
    {
        if (!string.IsNullOrWhiteSpace(boundAccountId))
        {
            var byId = Load(gameRoot).FirstOrDefault(a => a.Id == boundAccountId);
            if (byId is not null) return byId;
        }
        return GetLastUsed(gameRoot);
    }

    public static void MarkUsed(string gameRoot, string accountId)
    {
        var accounts = Load(gameRoot);
        var entry = accounts.Find(a => a.Id == accountId);
        if (entry is not null)
        {
            entry.LastUsed = DateTime.UtcNow.ToString("o");
            Save(gameRoot, accounts);
        }
    }

    public static void Upsert(string gameRoot, AccountEntry account)
    {
        var accounts = Load(gameRoot);
        var idx = accounts.FindIndex(a => a.Id == account.Id);
        if (idx >= 0) accounts[idx] = account;
        else accounts.Add(account);
        account.LastUsed = DateTime.UtcNow.ToString("o");
        Save(gameRoot, accounts);
        Changed?.Invoke(gameRoot);
    }

    public static bool Remove(string gameRoot, string accountId)
    {
        var accounts = Load(gameRoot);
        var removed = accounts.RemoveAll(a => a.Id == accountId) > 0;
        if (removed)
        {
            Save(gameRoot, accounts);
            Changed?.Invoke(gameRoot);
        }
        return removed;
    }
}
