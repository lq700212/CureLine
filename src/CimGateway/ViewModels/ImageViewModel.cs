// =========================================================================
// ImageViewModel：图片查看展示（MVVM，纯显示；加载失败行内提示路径）。
// BitmapImage 必须在 UI 线程建（VM 构造在 UI 线程，满足）；
// 加载失败不抛（现场图片可能正被写入），路径后追加提示。
// =========================================================================

using CureLine.ViewModels;
using System.Windows.Media.Imaging;
using CureLine.Core.Infrastructure;

namespace CimGateway.ViewModels;

public class ImageViewModel : ViewModelBase
{
    public ImageViewModel(string imagePath)
    {
        ImagePath = imagePath;
        try
        {
            ImageSource = new BitmapImage(new Uri(imagePath));
        }
        catch
        {
            ImageSource = null;
            LoadFailed = true;
        }
    }

    public string ImagePath { get; }
    public BitmapImage? ImageSource { get; }
    public bool LoadFailed { get; }

    public string TitleText => LanguageService.Tr("图片查看");

    public string PathText => LoadFailed
        ? ImagePath + "（加载失败）"
        : ImagePath;

    public void RefreshTexts() => RefreshAll();
}
