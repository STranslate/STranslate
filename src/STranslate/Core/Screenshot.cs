using ScreenGrab;
using System.Drawing;
using System.Windows;

namespace STranslate.Core;

public class Screenshot(Settings settings, PinnedWindowController pinnedWindowController) : IScreenshot
{
    private const int DefaultCaptureDelayMs = 150;

    public Bitmap? GetScreenshot()
    {
        if (ScreenGrabber.IsCapturing)
            return default;
        var bitmap = ScreenGrabber.CaptureDialog(settings.ShowScreenshotAuxiliaryLines);
        if (bitmap == null)
            return default;
        return bitmap;
    }

    public async Task<Bitmap?> GetScreenshotAsync()
    {
        return await CaptureBitmapAsync();
    }

    public async Task<ScreenshotCaptureResult?> GetScreenshotCaptureAsync()
    {
        await using var captureScope = await pinnedWindowController.BeginCaptureAsync();

        if (captureScope == null || ScreenGrabber.IsCapturing)
            return default;

        if (App.Current.MainWindow.Visibility == Visibility.Visible &&
            !App.Current.MainWindow.Topmost)
            App.Current.MainWindow.Visibility = Visibility.Collapsed;

        // Allow UI to update before capturing
        await Task.Delay(DefaultCaptureDelayMs);

        // 精简窗口会把截图贴回选区原位置，要求 bitmap 尺寸严格等于选区物理尺寸，
        // 因此关闭 ScreenGrab 对 <64px 小截图的 padding 扩展，
        // 否则 padding 后的 bitmap 会被 Viewbox 缩放，导致原始内容被缩小。
        // 其他窗口模式保留默认 padding 行为以兼容历史。
        var padImage = ShouldPadImage(settings.ImageTranslateWindowMode);

        // CaptureWithRegionAsync 直接回传截图选区的物理屏幕坐标，
        // 无需事后反推（旧版 CaptureAsync 只回传 bitmap）。
        var capture = await ScreenGrabber.CaptureWithRegionAsync(settings.ShowScreenshotAuxiliaryLines, padImage);
        if (capture == null)
            return default;

        return new ScreenshotCaptureResult(capture.Bitmap, capture.Region);
    }

    internal static bool ShouldPadImage(ImageTranslateWindowMode mode) =>
        mode != ImageTranslateWindowMode.Compact;

    private async Task<Bitmap?> CaptureBitmapAsync()
    {
        await using var captureScope = await pinnedWindowController.BeginCaptureAsync();

        if (captureScope == null || ScreenGrabber.IsCapturing)
            return default;

        if (App.Current.MainWindow.Visibility == Visibility.Visible &&
            !App.Current.MainWindow.Topmost)
            App.Current.MainWindow.Visibility = Visibility.Collapsed;

        // Allow UI to update before capturing
        await Task.Delay(DefaultCaptureDelayMs);

        var bitmap = await ScreenGrabber.CaptureAsync(settings.ShowScreenshotAuxiliaryLines);
        if (bitmap == null)
            return default;

        return bitmap;
    }
}
