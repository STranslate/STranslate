using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Helpers;
using System.Drawing;

namespace STranslate.Services;

/// <summary>
/// 协调常驻鼠标划词和增量翻译对全局鼠标 Hook 的共享使用。
/// </summary>
public sealed class MouseSelectionService : IDisposable
{
    private readonly IMouseHookService _mouseHookService;
    private readonly Settings _settings;
    private readonly ILogger<MouseSelectionService> _logger;
    private readonly Func<int, Task<string?>> _getSelectedTextAsync;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private bool _directTranslationEnabled;
    private bool _iconEnabled;
    private bool _incrementalEnabled;
    private bool _disposed;

    /// <summary>
    /// 初始化鼠标划词协调服务。
    /// </summary>
    /// <param name="mouseHookService">底层全局鼠标 Hook 服务。</param>
    /// <param name="settings">应用配置。</param>
    /// <param name="logger">日志记录器。</param>
    public MouseSelectionService(
        MouseHookService mouseHookService,
        Settings settings,
        ILogger<MouseSelectionService> logger)
        : this(mouseHookService, settings, logger,
            timeout => ClipboardHelper.GetSelectedTextAsync(timeout, settings.PreferUiaForSelectedText))
    {
    }

    internal MouseSelectionService(
        IMouseHookService mouseHookService,
        Settings settings,
        ILogger<MouseSelectionService> logger,
        Func<int, Task<string?>> getSelectedTextAsync)
    {
        _mouseHookService = mouseHookService;
        _settings = settings;
        _logger = logger;
        _getSelectedTextAsync = getSelectedTextAsync;
        _mouseHookService.SelectionStarted += OnSelectionStarted;
        _mouseHookService.SelectionCompleted += OnSelectionCompleted;
    }

    /// <summary>
    /// 检测到直接翻译模式的选中文本时触发。
    /// </summary>
    public event EventHandler<string>? TextSelected;

    /// <summary>
    /// 检测到增量翻译模式的选中文本时触发。
    /// </summary>
    public event EventHandler<string>? IncrementalTextSelected;

    /// <summary>
    /// 鼠标左键开始操作时触发。
    /// </summary>
    public event EventHandler<Point>? SelectionStarted;

    /// <summary>
    /// 图标模式下完成文本拖动时触发。
    /// </summary>
    public event EventHandler<Point>? IconRequested;

    /// <summary>
    /// 需要隐藏当前悬浮图标时触发。
    /// </summary>
    public event EventHandler? IconDismissRequested;

    /// <summary>
    /// 常驻鼠标划词功能状态变化时触发。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 同步常驻鼠标划词功能状态。
    /// </summary>
    /// <param name="directTranslationEnabled">是否启用划词后直接翻译。</param>
    /// <param name="iconEnabled">是否启用划词后悬浮图标。</param>
    /// <returns>底层 Hook 是否成功启动。</returns>
    public bool ApplyPersistentFeatures(bool directTranslationEnabled, bool iconEnabled)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directTranslationEnabled == directTranslationEnabled && _iconEnabled == iconEnabled)
                return true;

            var shouldRun = directTranslationEnabled || iconEnabled;
            if (shouldRun && !_mouseHookService.IsRunning && !_mouseHookService.Start())
                return false;

            _directTranslationEnabled = directTranslationEnabled;
            _iconEnabled = iconEnabled;
            if (!shouldRun)
                StopHookWhenIdle();
        }

        IconDismissRequested?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 开始增量翻译鼠标取词会话。
    /// </summary>
    /// <returns>底层 Hook 是否成功启动。</returns>
    public bool StartIncrementalCapture()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_incrementalEnabled)
                return true;

            if (!_mouseHookService.IsRunning && !_mouseHookService.Start())
                return false;

            _incrementalEnabled = true;
        }

        IconDismissRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 结束增量翻译鼠标取词会话。
    /// </summary>
    public void StopIncrementalCapture()
    {
        lock (_stateLock)
        {
            if (!_incrementalEnabled)
                return;

            _incrementalEnabled = false;
            StopHookWhenIdle();
        }
    }

    /// <summary>
    /// 用户点击悬浮图标后获取当前选中文本。
    /// </summary>
    /// <returns>选中的文本；取词失败时返回 null。</returns>
    public async Task<string?> CaptureIconSelectedTextAsync()
    {
        if (!CanTranslateFromIcon())
            return null;

        var text = await CaptureSelectedTextAsync();
        return CanTranslateFromIcon() ? text : null;
    }

    private async Task<string?> CaptureSelectedTextAsync()
    {
        await _captureLock.WaitAsync();
        try
        {
            var timeout = Math.Max(1, _settings.SelectedTextFetchTimeoutMs);
            return await _getSelectedTextAsync(timeout);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private void OnSelectionStarted(object? sender, Point point)
        => SelectionStarted?.Invoke(this, point);

    private void OnSelectionCompleted(object? sender, MouseSelectionCompletedEventArgs e)
    {
        bool incrementalEnabled;
        bool directTranslationEnabled;
        bool iconEnabled;

        lock (_stateLock)
        {
            incrementalEnabled = _incrementalEnabled;
            directTranslationEnabled = _directTranslationEnabled;
            iconEnabled = _iconEnabled;
        }

        if (incrementalEnabled)
        {
            _ = CaptureAndPublishAsync(IncrementalTextSelected);
            return;
        }

        if (directTranslationEnabled)
        {
            _ = CaptureAndPublishAsync(TextSelected);
            return;
        }

        if (iconEnabled)
            IconRequested?.Invoke(this, e.ScreenPoint);
    }

    private async Task CaptureAndPublishAsync(EventHandler<string>? handler)
    {
        try
        {
            var text = await CaptureSelectedTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                handler?.Invoke(this, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture selected text from the mouse selection workflow.");
        }
    }

    private void StopHookWhenIdle()
    {
        if (!_directTranslationEnabled && !_iconEnabled && !_incrementalEnabled)
            _mouseHookService.Stop();
    }

    private bool CanTranslateFromIcon()
    {
        lock (_stateLock)
            return !_incrementalEnabled && !_directTranslationEnabled && _iconEnabled;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _directTranslationEnabled = false;
            _iconEnabled = false;
            _incrementalEnabled = false;
        }

        _mouseHookService.SelectionStarted -= OnSelectionStarted;
        _mouseHookService.SelectionCompleted -= OnSelectionCompleted;
        _mouseHookService.Dispose();
    }
}
