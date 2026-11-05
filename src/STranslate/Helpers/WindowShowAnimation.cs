using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;

namespace STranslate.Helpers;

/// <summary>直接移动真实窗口上浮渐显，保留原生背景、圆角和阴影。</summary>
internal sealed class WindowShowAnimation : IDisposable
{
    private const double DurationMs = 180;
    private const double FadeDurationMs = 140;
    private enum AnimationState { Ready, Started, Completing, Disposed }

    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly Stopwatch _clock = new();
    private DispatcherOperation? _prepareOperation;
    private BindingBase? _topBinding;
    private BindingBase? _topmostBinding;
    private bool _previousTopmost;
    private bool _topmostPrepared;
    private AnimationState _state;
    private int _targetTopPixels;
    private double _dpiScaleY;
    private bool _positionPrepared;
    private bool _cloaked;
    private bool _closed;
    private bool _fading;
    private Action? _completed;

    internal bool IsActive => _state != AnimationState.Disposed;

    internal static WindowShowAnimation? TryCreate(Window window)
    {
        try { return new WindowShowAnimation(window); }
        catch (COMException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private WindowShowAnimation(Window window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(Win32Helper.GetWindowHandle(window, ensure: true));
        try
        {
            // 首次布局完成前遮蔽，避免先在终点闪现一帧。
            _cloaked = Win32Helper.SetWindowCloaked(window, cloaked: true);
            if (!_cloaked) throw new COMException("无法准备窗口显示动画");
            _source.AddHook(SourceWndProc);
            _window.IsVisibleChanged += OnVisibilityChanged;
            _window.PreviewKeyDown += OnInput;
            _window.PreviewMouseDown += OnInput;
            _window.Closed += OnClosed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Start(Action? completed = null)
    {
        if (_state != AnimationState.Ready) return;
        _state = AnimationState.Started;
        _completed = completed;
        _prepareOperation = _window.Dispatcher.InvokeAsync(Prepare, DispatcherPriority.Loaded);
    }

    private void Prepare()
    {
        _prepareOperation = null;
        if (!IsActive) return;
        if (!_window.IsVisible)
        {
            Dispose();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation || _window.WindowState != WindowState.Normal ||
            !double.IsFinite(_window.Top))
        {
            Complete();
            return;
        }

        var targetTop = _window.Top;
        if (!PInvoke.GetWindowRect(Win32Helper.GetWindowHandle(_window), out var bounds))
        {
            Complete();
            return;
        }
        _targetTopPixels = bounds.top;
        _dpiScaleY = VisualTreeHelper.GetDpi(_window).DpiScaleY;
        _topBinding = BindingOperations.GetBindingBase(_window, Window.TopProperty);
        _positionPrepared = true;
        // 临时替换双向绑定，防止逐帧位置写回设置；保持有效坐标，避免清空为 NaN。
        if (_topBinding is not null)
            BindingOperations.SetBinding(_window, Window.TopProperty,
                new Binding { Source = targetTop, Mode = BindingMode.OneWay });
        if (!IsActive) return;
        if (!ApplyFrame(0)) { Complete(); return; }
        _previousTopmost = _window.Topmost;
        _topmostBinding = BindingOperations.GetBindingBase(_window, Window.TopmostProperty);
        _topmostPrepared = true;
        // 临时置顶不写回用户的置顶开关，也不提前抢占输入焦点。
        BindingOperations.SetBinding(_window, Window.TopmostProperty,
            new Binding { Source = true, Mode = BindingMode.OneWay });
        if (!IsActive) return;
        try { Win32Helper.RaiseWindowWithoutActivation(_window, showWindow: false); }
        catch (Win32Exception) { Complete(); return; }
        if (!IsActive) return;
        // 不修改 WPF Opacity/AllowsTransparency；临时使用 HWND 的整窗透明度。
        // 已有分层窗口可能由 WPF 逐像素合成，不能用整窗 alpha 覆盖它。
        if (!Win32Helper.IsWindowLayered(_window))
        {
            _fading = true;
            try { Win32Helper.SetWindowLayered(_window, true); }
            catch (Win32Exception) { Complete(); return; }
            if (!IsActive) return;
            if (!ApplyOpacity(0)) { Complete(); return; }
        }
        Uncloak();
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsActive) return;
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        if (!SystemParameters.ClientAreaAnimation || elapsed >= DurationMs ||
            _window.WindowState != WindowState.Normal || !ApplyOpacity(elapsed) || !ApplyFrame(elapsed))
            Complete();
    }

    private bool ApplyFrame(double elapsed)
    {
        // 按物理坐标移动，避免 WPF 在混合 DPI 屏幕上重新解释横向位置。
        // 定位会同步触发消息；隐藏/关闭可能在此处重入 Dispose。
        if (!PInvoke.GetWindowRect(Win32Helper.GetWindowHandle(_window), out var bounds)) return false;
        var top = _targetTopPixels + (int)Math.Round(GetOffset(elapsed) * _dpiScaleY);
        if (bounds.top == top) return IsActive; // 像素取整后未移动的帧无需再次发送定位消息。
        try
        {
            Win32Helper.SetWindowPhysicalBounds(_window, bounds.left, top,
                bounds.Width, bounds.Height, showWindow: false);
            return IsActive;
        }
        catch (Win32Exception) { return false; }
    }

    private void Complete()
    {
        if (_state is AnimationState.Disposed or AnimationState.Completing) return;
        _state = AnimationState.Completing;
        var completed = _completed;
        _completed = null;
        StopRendering();
        try
        {
            RestorePosition();
            if (!IsActive) return;
            RestoreOpacity();
            Uncloak();
            // 激活完成后才撤销临时置顶，避免交接瞬间落到原前台窗口后面。
            if (!_closed && _window.IsVisible) completed?.Invoke();
        }
        finally { Dispose(); }
    }

    private void RestorePosition()
    {
        if (!_positionPrepared || _closed) return;
        _positionPrepared = false;
        ApplyFrame(DurationMs);
        if (!_closed && _topBinding is not null)
            BindingOperations.SetBinding(_window, Window.TopProperty, _topBinding);
    }

    internal static byte GetAlpha(double elapsedMilliseconds)
    {
        var progress = Math.Clamp(elapsedMilliseconds / FadeDurationMs, 0, 1);
        return (byte)Math.Round(255 * (1 - Math.Pow(1 - progress, 3)));
    }

    private bool ApplyOpacity(double elapsed) => !_fading ||
        Win32Helper.SetWindowAlpha(_window, GetAlpha(elapsed));

    private void RestoreOpacity()
    {
        if (!_fading || _closed) return;
        _fading = false;
        Win32Helper.SetWindowAlpha(_window, 255);
        Win32Helper.SetWindowLayered(_window, false);
    }

    internal static double GetOffset(double elapsedMilliseconds)
    {
        var progress = Math.Clamp(elapsedMilliseconds / DurationMs, 0, 1);
        return 18 * Math.Pow(1 - progress, 3);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible) Dispose();
    }

    private void OnInput(object sender, InputEventArgs e) => Complete();
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Dispose();
    }

    private nint SourceWndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF 刷新扩展样式时会清除分层位，淡入期间保持它直到主动恢复。
        if (_fading && Win32Helper.PreserveWindowLayeredStyle(message, wParam, lParam)) handled = true;
        if (message == 0x0231) Complete(); // WM_ENTERSIZEMOVE
        return 0;
    }

    public void Dispose()
    {
        if (!IsActive) return;
        _state = AnimationState.Disposed;
        _completed = null;
        _prepareOperation?.Abort();
        _prepareOperation = null;
        StopRendering();
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.PreviewKeyDown -= OnInput;
        _window.PreviewMouseDown -= OnInput;
        _window.Closed -= OnClosed;
        _source.RemoveHook(SourceWndProc);
        RestorePosition();
        RestoreOpacity();
        if (_topmostPrepared && !_closed)
        {
            if (_topmostBinding is not null)
                BindingOperations.SetBinding(_window, Window.TopmostProperty, _topmostBinding);
            else
                _window.SetValue(Window.TopmostProperty, _previousTopmost);
        }
        Uncloak();
    }

    private void StopRendering()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    private void Uncloak()
    {
        if (_cloaked && !_closed) Win32Helper.SetWindowCloaked(_window, cloaked: false);
        _cloaked = false;
    }
}
