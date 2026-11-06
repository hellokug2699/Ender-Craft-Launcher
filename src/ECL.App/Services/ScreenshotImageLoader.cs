// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ecl.App.Services;

/// <summary>
/// 把领域服务读出的图片字节解码成 WPF 图像。页面不直接访问文件路径，
/// 因此这里只接收字节；解码失败返回 <c>null</c>，由页面显示占位图而不是猜测内容。
/// </summary>
public interface IScreenshotImageLoader
{
    /// <summary>
    /// 解码图片。<paramref name="decodePixelWidth"/> 大于 0 时按宽度缩放解码（缩略图），
    /// 为 0 时按原始尺寸解码（预览）。返回值已冻结，可跨线程使用。
    /// </summary>
    ImageSource? Load(byte[] bytes, int decodePixelWidth);
}

public sealed class ScreenshotImageLoader : IScreenshotImageLoader
{
    public ImageSource? Load(byte[] bytes, int decodePixelWidth)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = decodePixelWidth;
            }

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException
                                              or ArgumentException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
