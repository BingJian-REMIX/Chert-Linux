using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class AchievementView : UserControl
{
    public AchievementView()
    {
        InitializeComponent();
        DataContext = new AchievementViewModel();
    }
}
