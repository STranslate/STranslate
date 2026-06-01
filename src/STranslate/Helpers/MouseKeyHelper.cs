using Gma.System.MouseKeyHook;

namespace STranslate.Helpers;

public class MouseKeyHelper
{
    private static IKeyboardMouseEvents? _mouseHook;
    private static bool _isMouseListening;
    private static string _oldText = string.Empty;
    private static Func<int> _getSelectedTextFetchTimeoutMs = () => 500;

    /// <summary>
    /// 鼠标划词文本选择事件
    /// </summary>
    public static event Action<string>? MouseTextSelected;

    /// <summary>
    /// 启动鼠标划词监听
    /// </summary>
    /// <param name="getSelectedTextFetchTimeoutMs">获取当前取词等待上限的方法，单位：毫秒。</param>
    public static async Task StartMouseTextSelectionAsync(Func<int>? getSelectedTextFetchTimeoutMs = null)
    {
        if (getSelectedTextFetchTimeoutMs != null)
        {
            _getSelectedTextFetchTimeoutMs = getSelectedTextFetchTimeoutMs;
        }

        if (_isMouseListening) return;

        _mouseHook = Hook.GlobalEvents();
        _mouseHook.MouseDragStarted += OnDragStarted;
        _mouseHook.MouseDragFinished += OnDragFinished;

        _isMouseListening = true;

        // 等待钩子启动
        await Task.Delay(100);
    }

    /// <summary>
    /// 停止鼠标划词监听
    /// </summary>
    public static void StopMouseTextSelection()
    {
        if (!_isMouseListening) return;

        _isMouseListening = false;

        if (_mouseHook != null)
        {
            _mouseHook.MouseDragStarted -= OnDragStarted;
            _mouseHook.MouseDragFinished -= OnDragFinished;
            _mouseHook.Dispose();
            _mouseHook = null;
        }

        _getSelectedTextFetchTimeoutMs = () => 500;
    }

    /// <summary>
    /// 切换鼠标划词监听状态
    /// </summary>
    public static async Task ToggleMouseTextSelection()
    {
        if (_isMouseListening)
        {
            StopMouseTextSelection();
        }
        else
        {
            await StartMouseTextSelectionAsync();
        }
    }

    /// <summary>
    /// 获取鼠标划词监听状态
    /// </summary>
    public static bool IsMouseTextSelectionListening => _isMouseListening;

    private static void OnDragStarted(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (GlobalTriggerGuard.ShouldSkipGlobalTrigger())
            return;

        _oldText = ClipboardHelper.GetText() ?? string.Empty;
    }

    private static void OnDragFinished(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left && !GlobalTriggerGuard.ShouldSkipGlobalTrigger())
        {
            // 异步处理文本获取和事件触发
            _ = Task.Run(async () =>
            {
                // 异步获取选中文本
                var selectedText = await ClipboardHelper.GetSelectedTextAsync(Math.Max(1, _getSelectedTextFetchTimeoutMs()));
                if (!string.IsNullOrEmpty(selectedText) && selectedText != _oldText)
                {
                    MouseTextSelected?.Invoke(selectedText);
                }
            });
        }
    }
}
