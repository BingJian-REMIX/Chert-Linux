using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views.Pages;

public partial class RecommendationView : UserControl
{
    public RecommendationView()
    {
        InitializeComponent();
        DataContext = new RecommendationViewModel();
    }
}
