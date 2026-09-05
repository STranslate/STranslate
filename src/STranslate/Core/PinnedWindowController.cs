using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Views;
using System.Windows;
using System.Windows.Media.Imaging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Core;

/// <summary>只在 UI 线程管理静态贴图及截图避让，不参与 OCR 或翻译。</summary>
public sealed class PinnedWindowController(Settings settings, Internationalization i18n, ISnackbar snackbar)
{
    private readonly HashSet<PinnedImageTranslateWindow> _windows = [];
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private bool _captureActive;

    internal bool ShowShadow
    {
        get => settings.PinnedImageTranslateShowShadow;
        set => settings.PinnedImageTranslateShowShadow = value;
    }

    internal void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            snackbar.ShowError($"{i18n.GetTranslation("CopyFailed")}: {ex.Message}");
        }
    }

    internal PinnedImageTranslateWindow CreateWindow(PinnedImageTranslateSnapshot snapshot)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var window = new PinnedImageTranslateWindow(this);
        _windows.Add(window);
        try
        {
            window.Initialize(snapshot, ShowShadow);
            window.ShowActivated = !_captureActive;
            window.Show();
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    internal void Unregister(PinnedImageTranslateWindow window) => _windows.Remove(window);

    internal void OnWindowSourceInitialized(PinnedImageTranslateWindow window)
    {
        if (_captureActive && !window.SetCaptureCloaked(true))
            throw new InvalidOperationException("Failed to cloak a pinned window during capture.");
    }

    internal async ValueTask<IAsyncDisposable?> BeginCaptureAsync(CancellationToken cancellationToken = default)
    {
        // 同一截图尚未结束时忽略重复触发，不把旧输入排队成下一次截图。
        if (!await _captureGate.WaitAsync(0, cancellationToken))
            return null;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _captureActive = true;
                foreach (var window in _windows)
                {
                    window.CloseTransientUiForCapture();
                    if (!window.SetCaptureCloaked(true))
                        throw new InvalidOperationException("Failed to cloak a pinned window before capture.");
                }
                if (_windows.Count > 0)
                    Win32Helper.FlushDesktopComposition();
            });
            return new CaptureLease(this);
        }
        catch
        {
            await EndCaptureAsync();
            throw;
        }
    }

    internal void CloseAll()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(CloseAll);
            return;
        }
        foreach (var window in _windows.ToArray())
            window.Close();
    }

    private async ValueTask EndCaptureAsync()
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _captureActive = false;
                var restored = true;
                foreach (var window in _windows)
                    restored &= window.SetCaptureCloaked(false);
                if (_windows.Count > 0)
                    Win32Helper.FlushDesktopComposition();
                if (!restored)
                    throw new InvalidOperationException("Failed to restore pinned windows after capture.");
            });
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private sealed class CaptureLease(PinnedWindowController owner) : IAsyncDisposable
    {
        private PinnedWindowController? _owner = owner;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                await current.EndCaptureAsync();
        }
    }
}

/// <summary>已生成结果的独立显示快照；图片冻结，选择数据逐项复制。</summary>
internal sealed record PinnedImageTranslateSnapshot(
    BitmapSource SourceImage,
    BitmapSource AnnotatedImage,
    ImageTranslateOverlayDocument TranslationOverlay,
    IReadOnlyList<OcrWord> OriginalWords,
    IReadOnlyList<OcrWord> TranslatedWords,
    DrawingRectangle PhysicalBounds,
    bool ShowOriginal)
{
    internal static PinnedImageTranslateSnapshot Create(
        BitmapSource source, BitmapSource annotated, ImageTranslateOverlayDocument overlay,
        IReadOnlyList<OcrWord> originalWords, IReadOnlyList<OcrWord> translatedWords,
        DrawingRectangle bounds, bool showOriginal = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            source.PixelWidth != bounds.Width || source.PixelHeight != bounds.Height ||
            annotated.PixelWidth != bounds.Width || annotated.PixelHeight != bounds.Height ||
            !source.IsFrozen || !annotated.IsFrozen || overlay.IsEmpty)
            throw new ArgumentException("Pin requires a frozen, completed result matching the physical image bounds.");

        return new(source, annotated, new ImageTranslateOverlayDocument(overlay.Items.ToArray(), []),
            CloneWords(originalWords), CloneWords(translatedWords), bounds, showOriginal);
    }

    private static IReadOnlyList<OcrWord> CloneWords(IReadOnlyList<OcrWord> words) =>
        Array.AsReadOnly(words.Select(word => new OcrWord
        {
            Text = word.Text,
            BoundingBox = word.BoundingBox,
            StartIndexInFullText = word.StartIndexInFullText,
            VisualLineIndex = word.VisualLineIndex,
            ParagraphIndex = word.ParagraphIndex,
        }).ToArray());
}
