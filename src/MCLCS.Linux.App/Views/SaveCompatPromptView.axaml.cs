using Avalonia.Controls;
using MCLCS.Linux.App.ViewModels;

namespace MCLCS.Linux.App.Views;

public partial class SaveCompatPromptView : Window
{
    public SaveCompatPromptView()
    {
        InitializeComponent();
    }

    public SaveCompatPromptView(SaveCompatPromptViewModel vm) : this()
    {
        DataContext = vm;
        vm.Decision += _ => Close();
    }
}
