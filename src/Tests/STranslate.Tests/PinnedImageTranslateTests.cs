using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Views;
using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STranslate.Tests;

public class PinnedImageTranslateTests
{
    [Fact]
    public void SnapshotCreatesIndependentStaticSelectionData()
    {
        var source = CreateBitmap(8, 6, freeze: true);
        var overlay = CreateOverlay();
        var original = new OcrWord
        {
            Text = "original",
            BoundingBox = new Rect(1, 2, 3, 4),
            StartIndexInFullText = 2,
            VisualLineIndex = 3,
            ParagraphIndex = 4,
        };

        var snapshot = PinnedImageTranslateSnapshot.Create(
            source,
            overlay,
            [original],
            [new OcrWord { Text = "translated" }],
            new Rectangle(10, 20, 8, 6));

        original.Text = "changed";

        Assert.Same(source, snapshot.SourceImage);
        Assert.NotSame(overlay, snapshot.TranslationOverlay);
        Assert.Equal("original", Assert.Single(snapshot.OriginalWords).Text);
        Assert.Equal("translated", Assert.Single(snapshot.TranslatedWords).Text);
        Assert.Equal(new Rectangle(10, 20, 8, 6), snapshot.PhysicalBounds);
    }

    [Fact]
    public void SnapshotRejectsIncompleteOrMismatchedResults()
    {
        var frozenSource = CreateBitmap(8, 6, freeze: true);
        var unfrozenSource = CreateBitmap(8, 6, freeze: false);
        var overlay = CreateOverlay();

        Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(
            unfrozenSource, overlay, [], [], new Rectangle(0, 0, 8, 6)));
        Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(
            frozenSource, overlay, [], [], new Rectangle(0, 0, 9, 6)));
        Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(
            frozenSource, ImageTranslateOverlayDocument.Empty, [], [], new Rectangle(0, 0, 8, 6)));
    }

    [Fact]
    public void OriginalLayerUsesRawSourceWithoutOverlay()
    {
        var snapshot = CreateSnapshot(showOriginal: true);

        var content = snapshot.GetDisplayContent(showOriginal: true);

        Assert.Same(snapshot.SourceImage, content.Image);
        Assert.Null(content.Overlay);
    }

    [Fact]
    public void TranslationLayerUsesRawSourceWithOverlay()
    {
        var snapshot = CreateSnapshot(showOriginal: false);

        var content = snapshot.GetDisplayContent(showOriginal: false);

        Assert.Same(snapshot.SourceImage, content.Image);
        Assert.Same(snapshot.TranslationOverlay, content.Overlay);
    }

    [Fact]
    public void PinShortcutDefaultsToCtrlTAndIsScopedToImageTranslationWindows()
    {
        var settings = new HotkeySettings();
        settings.Initialize();

        Assert.Equal("Ctrl + T", settings.PinImageTranslateHotkey.Key);
        Assert.Equal("Ctrl + T", settings.PinImageTranslateHotkey.Default);

        var registration = Assert.Single(settings.RegisteredHotkeys,
            item => item.ResourceKey == "Hotkey_PinImageTranslate");
        Assert.Equal(HotkeyType.ImageTransWindow, registration.Type);
    }

    [Fact]
    public async Task CaptureCoordinatorCloaksRestoresAndRejectsConcurrentCapture()
    {
        var flushCount = 0;
        var coordinator = new PinnedCaptureCoordinator(
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            () => flushCount++);
        var target = new FakeCaptureTarget();
        List<IPinnedCaptureTarget> targets = [target];

        var lease = await coordinator.BeginAsync(targets);

        Assert.NotNull(lease);
        Assert.True(coordinator.IsActive);
        Assert.Equal([true], target.CloakStates);
        Assert.Equal(1, target.CloseTransientUiCount);
        Assert.Equal(1, flushCount);
        Assert.Null(await coordinator.BeginAsync(targets));

        var createdDuringCapture = new FakeCaptureTarget();
        targets.Add(createdDuringCapture);
        coordinator.OnTargetInitialized(createdDuringCapture);
        Assert.Equal([true], createdDuringCapture.CloakStates);

        await lease.DisposeAsync();

        Assert.False(coordinator.IsActive);
        Assert.Equal([true, false], target.CloakStates);
        Assert.Equal([true, false], createdDuringCapture.CloakStates);
        Assert.Equal(2, flushCount);

        var nextLease = await coordinator.BeginAsync(targets);
        Assert.NotNull(nextLease);
        await nextLease.DisposeAsync();
    }

    [Theory]
    [InlineData(1.0, 88, 188, 224, 144)]
    [InlineData(1.25, 85, 185, 230, 150)]
    [InlineData(1.5, 82, 182, 236, 156)]
    public void ShadowBoundsPreserveImagePositionAcrossDpi(
        double dpiScale,
        int expectedLeft,
        int expectedTop,
        int expectedWidth,
        int expectedHeight)
    {
        var imageBounds = new Rectangle(100, 200, 200, 120);

        var windowBounds = PinnedImageTranslateWindow.CalculateWindowBounds(
            imageBounds,
            new DpiScale(dpiScale, dpiScale));

        Assert.Equal(new Rectangle(expectedLeft, expectedTop, expectedWidth, expectedHeight), windowBounds);
        Assert.Equal(imageBounds.Left, windowBounds.Left + (windowBounds.Width - imageBounds.Width) / 2);
        Assert.Equal(imageBounds.Top, windowBounds.Top + (windowBounds.Height - imageBounds.Height) / 2);
    }

    private static PinnedImageTranslateSnapshot CreateSnapshot(bool showOriginal) =>
        PinnedImageTranslateSnapshot.Create(
            CreateBitmap(8, 6, freeze: true),
            CreateOverlay(),
            [new OcrWord { Text = "original" }],
            [new OcrWord { Text = "translated" }],
            new Rectangle(0, 0, 8, 6),
            showOriginal);

    private static BitmapSource CreateBitmap(int width, int height, bool freeze)
    {
        var stride = width * 4;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, new byte[stride * height], stride);
        if (freeze)
            bitmap.Freeze();
        return bitmap;
    }

    private static ImageTranslateOverlayDocument CreateOverlay()
    {
        var box = new List<BoxPoint>
        {
            new(0, 0), new(7, 0), new(7, 5), new(0, 5)
        };
        return ImageTranslateRenderer.CreateTranslatedOverlay(
            [new OcrLayoutBlock { Text = "translated", BoxPoints = box, LineBoxPoints = [box] }],
            ImageTranslateOverlayTheme.Light);
    }

    private sealed class FakeCaptureTarget : IPinnedCaptureTarget
    {
        internal List<bool> CloakStates { get; } = [];
        internal int CloseTransientUiCount { get; private set; }

        public void CloseTransientUiForCapture() => CloseTransientUiCount++;

        public bool SetCaptureCloaked(bool cloaked)
        {
            CloakStates.Add(cloaked);
            return true;
        }
    }
}
