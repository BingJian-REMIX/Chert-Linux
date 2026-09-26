using System.Windows.Input;
using MCLCS.Core.Mvvm;

namespace MCLCS.Linux.App.ViewModels;

/// <summary>服务器添加 / 编辑对话框的视图模型（移植自 WPF AddServerViewModel）。</summary>
public class AddServerViewModel : ObservableObject
{
    private string _name = "";
    private string _address = "";
    private string _error = "";

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Address { get => _address; set => SetField(ref _address, value); }
    public string Error { get => _error; set { if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }

    /// <summary>是否有错误文本需要显示（用于绑定 IsVisible）。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>是否有效（名称和地址均非空）。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Address);

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>关闭前设置为 true 表示用户点了确定。</summary>
    public bool Confirmed { get; internal set; }

    /// <summary>无参构造：供后续需要时以对象元素方式声明 DataContext 使用。</summary>
    public AddServerViewModel() : this(null, null) { }

    public AddServerViewModel(string? existingName, string? existingAddress)
    {
        Name = existingName ?? "";
        Address = existingAddress ?? "";
        OkCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(Name)) { Error = "名称不能为空"; return; }
            if (string.IsNullOrWhiteSpace(Address)) { Error = "地址不能为空（如 example.com:25565）"; return; }
            Confirmed = true;
        });
        CancelCommand = new RelayCommand(_ => { Confirmed = false; });
    }
}
