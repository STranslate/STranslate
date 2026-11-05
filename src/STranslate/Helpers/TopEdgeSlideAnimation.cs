using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace STranslate.Helpers;

/// <summary>用系统窗口预览整体滑动内容与背景，不改变主窗口的位置、布局或视觉树。</summary>
internal sealed class TopEdgeSlideAnimation : IDisposable
{
    private readonly Window _window;
    private readonly Window _surface;
    private readonly Stopwatch _clock = new();
    private readonly nint _surfaceHwnd;
    private nint _thumbnail;
    private DispatcherOperation? _prepareOperation;
    private double _from;
    private double _target;
    private double _durationMs;
    private Action? _completed;
    private Action? _started;
    private bool _prepared;
    private bool _cloaked;
    private bool _disposed;

    public double VisibleFraction { get; private set; }
    public double VisibleHeight => Math.Max(4, _window.ActualHeight * VisibleFraction);
    public event Action? InteractionRequested;

    public static TopEdgeSlideAnimation? TryCreate(Window window, bool initiallyExpanded)
    {
        try { return new TopEdgeSlideAnimation(window, initiallyExpanded); }
        catch (COMException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private TopEdgeSlideAnimation(Window window, bool initiallyExpanded)
    {
        _window = window;
        VisibleFraction = initiallyExpanded ? 1 : 0;
        _surface = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.Transparent,
            Left = window.Left,
            Top = window.Top,
            Width = window.ActualWidth,
            Height = window.ActualHeight
        };
        try
        {
            _surfaceHwnd = new WindowInteropHelper(_surface).EnsureHandle();
            Win32Helper.HideFromAltTab(_surface);
            var source = HwndSource.FromHwnd(_surfaceHwnd);
            source.AddHook(SurfaceWndProc);
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            PInvoke.DwmExtendFrameIntoClientArea((HWND)(IntPtr)_surfaceHwnd, margins).ThrowOnFailure();
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            PInvoke.DwmRegisterThumbnail((HWND)(IntPtr)_surfaceHwnd,
                (HWND)(IntPtr)Win32Helper.GetWindowHandle(window), out _thumbnail).ThrowOnFailure();
            if (!Win32Helper.SetWindowCloaked(_surface, cloaked: true) || !ApplyFrame())
                throw new COMException("无法准备贴顶动画窗口");
            // 展开前先遮住原窗口，Show 的原生背景和 WPF 首帧都不会提前露出。
            if (!initiallyExpanded && !CloakSource())
                throw new COMException("无法准备贴顶窗口预览");
            _surface.Show();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Start(bool expand, Action completed, Action? started = null)
    {
        CompositionTarget.Rendering -= OnRendering;
        _target = expand ? 1 : 0;
        _completed = completed;
        _started = started;
        if (_prepared)
        {
            Begin();
            return;
        }
        // Loaded 优先级在布局、渲染之后运行，避免 Show 后先暴露空的窗口背景。
        _prepareOperation ??= _window.Dispatcher.InvokeAsync(Prepare, DispatcherPriority.Loaded);
    }

    private void Prepare()
    {
        _prepareOperation = null;
        if (!ApplyFrame())
        {
            Complete();
            return;
        }
        Win32Helper.FlushDesktopComposition();
        if (!Win32Helper.SetWindowCloaked(_surface, cloaked: false) || !CloakSource())
        {
            Complete();
            return;
        }
        _prepared = true;
        Begin();
    }

    private void Begin()
    {
        // 反向播放从当前画面接续，并按剩余距离缩短时长。
        _from = VisibleFraction;
        _durationMs = Math.Max(1, 180 * Math.Abs(_target - _from));
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
        var started = _started;
        _started = null;
        started?.Invoke();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        VisibleFraction = _from + (_target - _from) * eased;
        if (!ApplyFrame() || progress >= 1) Complete();
    }

    private void Complete()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        var completed = _completed;
        _completed = null;
        completed?.Invoke();
    }

    private bool ApplyFrame()
    {
        if (!PInvoke.GetWindowRect(Win32Helper.GetWindowHandle(_window), out var bounds) ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            !PInvoke.DwmQueryThumbnailSourceSize(_thumbnail, out var size).Succeeded || size.Width <= 0 || size.Height <= 0)
            return false;
        try
        {
            // 通常尺寸不变；仅内容高度或 DPI 改变时同步过渡窗口，避免每帧触发原生布局。
            if (!PInvoke.GetWindowRect(new(_surfaceHwnd), out var surfaceBounds) || !bounds.Equals(surfaceBounds))
                Win32Helper.SetWindowPhysicalBounds(_surface, bounds.left, bounds.top, bounds.Width, bounds.Height,
                    showWindow: false);
        }
        catch (Win32Exception) { return false; }
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * VisibleFraction), 1, bounds.Height);
        var sourceHeight = Math.Clamp((int)Math.Ceiling(size.Height * VisibleFraction), 1, size.Height);
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE |
                      PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT(0, 0, bounds.Width, height),
            rcSource = new RECT(0, size.Height - sourceHeight, size.Width, size.Height),
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false
        };
        if (!PInvoke.DwmUpdateThumbnailProperties(_thumbnail, properties).Succeeded) return false;
        var region = PInvoke.CreateRectRgn(0, 0, bounds.Width, height);
        if (region.IsNull) return false;
        // 裁剪专用过渡窗口，避免 WindowChrome 在 Show 时重置主窗口区域。
        if (PInvoke.SetWindowRgn((HWND)(IntPtr)_surfaceHwnd, region, true) != 0) return true;
        PInvoke.DeleteObject((HGDIOBJ)region);
        return false;
    }

    private bool CloakSource() => _cloaked || (_cloaked = Win32Helper.SetWindowCloaked(_window, cloaked: true));

    private nint SurfaceWndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE：过渡层不抢前台焦点。
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message == 0x0201 || message == 0x0204) // 点击时立即交还完整窗口。
            _window.Dispatcher.BeginInvoke(() => InteractionRequested?.Invoke());
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prepareOperation?.Abort();
        _prepareOperation = null;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _completed = null;
        _started = null;
        InteractionRequested = null;
        // 原窗口从未平移，解除遮蔽即可呈现已渲染的完整画面。
        if (_cloaked) Win32Helper.SetWindowCloaked(_window, cloaked: false);
        _cloaked = false;
        if (_thumbnail != 0) PInvoke.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
        _surface.Close();
    }
}
