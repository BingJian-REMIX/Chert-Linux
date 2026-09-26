using Avalonia.Controls;
using MCLCS.Core.Localization;

namespace MCLCS.Linux.App.Views;

/// <summary>文件变更检测详情窗口（Toast「查看详情」打开）。承载 <see cref="Pages.FileWatchView"/>。</summary>
public partial class FileWatchWindow : Window
{
    public FileWatchWindow()
    {
        InitializeComponent();
        Title = LocaleManager.T("tool.filewatch");
    }
}
