using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace STranslate.Helpers;

/// <summary>
///     剪贴板监听器,用于监听剪贴板内容变化并触发翻译
/// </summary>
public class ClipboardMonitor(Window window) : IDisposable
{
    private readonly Window _window = window ?? throw new ArgumentNullException(nameof(window));
    private HwndSource? _hwndSource;
    private bool _isMonitoring;
    private string _lastText = string.Empty;
    private HWND _hwnd;

    /// <summary>
    ///     当剪贴板文本内容变化时触发
    /// </summary>
    public event Action<string>? OnClipboardTextChanged;

    /// <summary>
    ///     开始监听剪贴板变化
    /// </summary>
    public void Start()
    {
        if (_isMonitoring) return;

        _window.Dispatcher.Invoke(() =>
        {
            var windowHelper = new WindowInteropHelper(_window);
            windowHelper.EnsureHandle();
            var hwnd = windowHelper.Handle;
            _hwnd = new HWND(hwnd);
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            PInvoke.AddClipboardFormatListener(_hwnd);
        });

        _isMonitoring = true;
    }

    /// <summary>
    ///     停止监听剪贴板变化
    /// </summary>
    public void Stop()
    {
        if (!_isMonitoring) return;

        // 检查窗口是否已关闭或正在关闭
        if (_window == null || !_window.CheckAccess() || PresentationSource.FromVisual(_window) == null)
        {
            // 窗口已关闭，直接清理状态
            _isMonitoring = false;
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            // 使用存储的 hwnd，避免再次调用 EnsureHandle
            if (_hwnd != IntPtr.Zero)
            {
                PInvoke.RemoveClipboardFormatListener(_hwnd);
            }
            _hwndSource?.RemoveHook(WndProc);
        });

        _isMonitoring = false;
    }

    /// <summary>
    ///     重置上次记录的文本，允许相同内容再次触发
    /// </summary>
    public void ResetLastText()
    {
        _lastText = string.Empty;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == PInvoke.WM_CLIPBOARDUPDATE)
        {
            if (GlobalTriggerGuard.ShouldSkipGlobalTrigger())
            {
                handled = true;
                return nint.Zero;
            }

            _ = Task.Run(async () =>
            {
                // 延迟确保剪贴板数据已完全写入
                await Task.Delay(100);
                var text = ClipboardHelper.GetText();

                if (!string.IsNullOrWhiteSpace(text) && text != _lastText)
                {
                    _lastText = text;
                    OnClipboardTextChanged?.Invoke(text);
                    // 触发后重置，允许相同内容再次触发
                    ResetLastText();
                }
            });
            handled = true;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
