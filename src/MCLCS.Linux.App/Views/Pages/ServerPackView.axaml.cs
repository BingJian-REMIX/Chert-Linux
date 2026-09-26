using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class ServerPackView : UserControl
{
    public ServerPackView()
    {
        InitializeComponent();
        DataContext = new ServerPackViewModel();
    }
}
