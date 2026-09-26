using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using MCLCS.Core.Mvvm;
using MCLCS.Core.Profiles;
using MCLCS.Linux.App.Services;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>光影配置参数的一条。</summary>
public class ShaderParamItem : ObservableObject
{
    private string _paramId = "";
    private string _paramValue = "";
    public string ParamId { get => _paramId; set { SetField(ref _paramId, value); OnPropertyChanged(nameof(Token)); } }
    public string ParamValue { get => _paramValue; set { SetField(ref _paramValue, value); OnPropertyChanged(nameof(Token)); } }
    public string Token => $"{ParamId}{ParamValue}";
}

/// <summary>
/// 光影配置 Token 编辑器（移植自 WPF ShaderTokenViewModel）：参数ID+值交替编码，预留版本位，
/// 完全离线解析，复制/导入。
/// </summary>
public class ShaderTokenViewModel : ObservableObject
{
    private ObservableCollection<ShaderParamItem> _params = new();
    private string _tokenName = "";
    private ObservableCollection<string> _savedNames = new();
    private string _importToken = "";
    private string _statusMessage = "";
    private string _tokenVersion = "v1";

    public ObservableCollection<ShaderParamItem> Params { get => _params; set => SetField(ref _params, value); }
    public string TokenName { get => _tokenName; set => SetField(ref _tokenName, value); }
    public ObservableCollection<string> SavedNames { get => _savedNames; set => SetField(ref _savedNames, value); }
    public string ImportToken { get => _importToken; set => SetField(ref _importToken, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public string TokenVersion { get => _tokenVersion; set => SetField(ref _tokenVersion, value); }

    /// <summary>完整 Token = 版本位 + 各参数 ID+值交替。</summary>
    public string FullToken
    {
        get
        {
            var sb = new StringBuilder(TokenVersion);
            foreach (var p in Params)
                if (!string.IsNullOrEmpty(p.ParamId))
                    sb.Append(p.ParamId).Append(p.ParamValue);
            return sb.ToString();
        }
    }

    public ICommand AddParamCommand { get; }
    public ICommand RemoveParamCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }

    public ShaderTokenViewModel()
    {
        AddParamCommand = new RelayCommand(_ => { Params.Add(new ShaderParamItem()); OnPropertyChanged(nameof(FullToken)); });
        RemoveParamCommand = new RelayCommand(p => RemoveParam(p as ShaderParamItem));
        SaveCommand = new RelayCommand(_ => Save());
        LoadCommand = new RelayCommand(p => Load(p as string));
        DeleteCommand = new RelayCommand(p => Delete(p as string));
        ImportCommand = new RelayCommand(_ => Import());

        RefreshSaved();
    }

    private void RemoveParam(ShaderParamItem? item)
    {
        if (item is null) return;
        Params.Remove(item);
        OnPropertyChanged(nameof(FullToken));
    }

    private void RefreshSaved()
    {
        var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
        SavedNames = new ObservableCollection<string>(profile.ShaderTokens.Keys);
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(TokenName)) { StatusMessage = "请输入配置名称"; return; }
        var token = FullToken;
        var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
        profile.ShaderTokens[TokenName.Trim()] = token;
        ProfileStore.Save(profile);
        StatusMessage = $"已保存 {TokenName}";
        ToastService.Instance.Show(new ToastOptions { Title = "光影配置", Message = $"已保存 {TokenName}" });
        RefreshSaved();
    }

    private void Load(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
        if (!profile.ShaderTokens.TryGetValue(name, out var token)) { StatusMessage = "未找到"; return; }
        ParseToken(token);
        TokenName = name;
    }

    private void Delete(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = ProfileStore.Load(LauncherService.Instance.GameRoot);
        profile.ShaderTokens.Remove(name);
        ProfileStore.Save(profile);
        RefreshSaved();
    }

    private void Import()
    {
        if (string.IsNullOrWhiteSpace(ImportToken)) { StatusMessage = "请粘贴 Token"; return; }
        ParseToken(ImportToken.Trim());
    }

    private void ParseToken(string token)
    {
        Params.Clear();
        if (token.StartsWith("v")) { TokenVersion = token[0..2]; token = token[2..]; }
        var i = 0;
        while (i < token.Length)
        {
            var idStart = i;
            while (i < token.Length && char.IsLetter(token[i])) i++;
            if (i == idStart) { i++; continue; }
            var id = token[idStart..i];
            var valStart = i;
            while (i < token.Length && (char.IsDigit(token[i]) || token[i] == '.' || token[i] == '-')) i++;
            var val = token[valStart..i];
            Params.Add(new ShaderParamItem { ParamId = id, ParamValue = val });
        }
        OnPropertyChanged(nameof(FullToken));
        StatusMessage = $"已解析 {Params.Count} 个参数";
    }
}
