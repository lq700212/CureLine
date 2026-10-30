using CimGateway.ViewModels;
using System.Windows;
using CureLine.ViewModels;

namespace CimGateway;

/// <summary>图片查看（MVVM 的 View）。用法：new ImageWindow(全路径).Show()。</summary>
public partial class ImageWindow : Window
{
    public ImageViewModel ViewModel { get; }

    public ImageWindow(string imagePath)
    {
        ViewModel = new ImageViewModel(imagePath);
        DataContext = ViewModel;
        InitializeComponent();
    }

    /// <summary>标题条关闭钮。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
