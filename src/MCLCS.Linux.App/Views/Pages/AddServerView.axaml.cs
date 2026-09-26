using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class AddServerView : UserControl
{
    public AddServerView()
    {
        InitializeComponent();
        DataContext = new AddServerViewModel();
    }
}
