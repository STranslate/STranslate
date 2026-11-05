using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace STranslate.Helpers;

/// <summary>用独立感应条收起贴顶窗口，避免负坐标污染记忆位置或露到上方显示器。</summary>
internal sealed class TopEdgeAutoHideController : IDisposable
{
    private const double StripHeight = 6;
    private readonly Window _window;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _hideDelayMs;
    private readonly Action? _onExpanded;
    private readonly Func<bool>? _isShowing;
    private readonly DispatcherTimer _timer;
    private TopEdgeSlideAnimation? _animation;
    private Window? _strip;
    private BindingBase? _topmostBinding;
    private bool _previousTopmost;
    private bool _changingVisibility;
    private bool _moving;
    private long _leaveTime;
    private double _edgeTop;

    public bool IsDocked { get; private set; }
    public bool IsCollapsed { get; private set; }
    internal bool IsAnimating => _animation is not null;

    public TopEdgeAutoHideController(Window window, Func<bool> enabled, Action? onExpanded = null,
        Func<int>? hideDelayMs = null, Func<bool>? isShowing = null)
    {
        _window = window;
        _enabled = enabled;
        _hideDelayMs = hideDelayMs ?? (() => 600);
        _onExpanded = onExpanded;
        _isShowing = isShowing;
        _window.IsVisibleChanged += OnVisibilityChanged;
        _window.PreviewKeyDown += OnKeyDown;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => Update(), window.Dispatcher);
    }

    public void SetMoving(bool moving)
    {
        if (moving && IsAnimating) Expand(animate: false, notifyExpanded: false);
        _moving = moving;
        _leaveTime = 0;
        if (!moving) Update();
    }

    internal void Update()
    {
        if (_changingVisibility) return;
        if (_window.Visibility == Visibility.Collapsed)
        {
            Undock(restoreWindow: false);
            return;
        }
        // 普通唤出结束后再吸附，避免上浮的临时位置触发贴顶。
        if (_isShowing?.Invoke() == true)
        {
            _leaveTime = 0;
            return;
        }
        if (!_enabled() || _window.WindowState != WindowState.Normal)
        {
            Undock();
            return;
        }

        if (_moving || Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (!_window.IsVisible && !IsCollapsed) return;

        var monitor = MonitorInfo.GetNearestDisplayMonitor(Win32Helper.GetWindowHandle(_window));
        var top = Win32Helper.TransformPixelsToDIP(_window, monitor.WorkingArea.Left, monitor.WorkingArea.Top).Y;
        if (!IsCollapsed && Math.Abs(_window.Top - top) > 8)
        {
            Undock();
            return;
        }

        // 分辨率或屏幕布局变化时先恢复窗口，再按新工作区重新判断。
        if (IsDocked && Math.Abs(_edgeTop - top) > 1)
        {
            Undock();
            return;
        }

        if (!IsDocked)
        {
            IsDocked = true;
            _edgeTop = top;
            _window.SetCurrentValue(Window.TopProperty, top);
            _previousTopmost = _window.Topmost;
            _topmostBinding = BindingOperations.GetBindingBase(_window, Window.TopmostProperty);
            BindingOperations.ClearBinding(_window, Window.TopmostProperty);
            _window.Topmost = true;
            _leaveTime = 0;
        }

        if (!PInvoke.GetCursorPos(out var cursor)) return;
        var point = Win32Helper.TransformPixelsToDIP(_window, cursor.X, cursor.Y);
        var bounds = IsAnimating
            ? new Rect(_window.Left, _edgeTop, _window.ActualWidth, _animation!.VisibleHeight)
            : IsCollapsed
            ? new Rect(_window.Left, _edgeTop, _window.ActualWidth, StripHeight)
            : new Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
        if (bounds.Contains(point))
        {
            Expand();
            _leaveTime = 0;
        }
        else if (!IsCollapsed)
        {
            // 下拉框、右键菜单、模态窗口和输入操作期间保持展开。
            if (Mouse.Captured is not null || !_window.IsEnabled ||
                _window.OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
            {
                _leaveTime = 0;
                return;
            }
            if (_leaveTime == 0) _leaveTime = Environment.TickCount64;
            if (Environment.TickCount64 - _leaveTime >= Math.Clamp(_hideDelayMs(), 100, 10000)) Collapse();
        }
    }

    internal void Collapse(bool? animate = null)
    {
        if (!IsDocked || IsCollapsed) return;
        IsCollapsed = true;
        if (animate ?? SystemParameters.ClientAreaAnimation)
        {
            PrepareAnimation(initiallyExpanded: true);
            if (_animation is not null)
            {
                _animation.Start(expand: false, FinishCollapse);
                return;
            }
        }
        FinishCollapse();
    }

    private void FinishCollapse()
    {
        _strip ??= CreateStripWindow();
        _strip.Left = _window.Left;
        _strip.Top = _edgeTop;
        _strip.Width = _window.ActualWidth;
        _changingVisibility = true;
        try
        {
            _strip.Show();
            ApplyStripRegion();
            _window.Hide();
            StopAnimation();
        }
        finally { _changingVisibility = false; }
    }

    private Window CreateStripWindow()
    {
        var strip = new Window
        {
            // 全局现代窗口样式会为标题栏预留高度，将 6 DIP 感应条的内容挤出窗口。
            Style = null,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Height = StripHeight,
            MinHeight = 0,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        // 感应条是辅助窗口，在首次显示前将其从 Alt+Tab 切换列表中排除。
        strip.SourceInitialized += (_, _) => Win32Helper.HideFromAltTab(strip);

        // 感应条在启动主题初始化之后才创建，需要主动加载主题资源，否则画刷会解析为空。
        ThemeManager.SetRequestedTheme(strip, ThemeManager.GetActualTheme(_window) == ElementTheme.Dark
            ? ElementTheme.Dark : ElementTheme.Light);

        var indicator = new System.Windows.Controls.Border
        {
            Margin = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(1.5),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 7,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = System.Windows.Media.Colors.Black
            }
        };
        var indicatorStyle = new System.Windows.Style(typeof(System.Windows.Controls.Border));
        indicatorStyle.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Border.BackgroundProperty,
            new System.Windows.DynamicResourceExtension(ThemeKeys.AccentAAFillColorDefaultBrushKey)));
        indicatorStyle.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Border.BorderBrushProperty,
            new System.Windows.DynamicResourceExtension(ThemeKeys.ControlStrokeColorDefaultBrushKey)));
        var hoverTrigger = new System.Windows.Trigger
        {
            Property = System.Windows.Controls.Border.IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new System.Windows.Setter(
            System.Windows.Controls.Border.BackgroundProperty,
            new System.Windows.DynamicResourceExtension(ThemeKeys.SystemControlBackgroundAccentBrushKey)));
        indicatorStyle.Triggers.Add(hoverTrigger);
        indicator.Style = indicatorStyle;
        strip.Content = indicator;
        return strip;
    }

    private void ApplyStripRegion()
    {
        if (_strip is null)
            return;

        var hwnd = Win32Helper.GetWindowHandle(_strip, ensure: true);
        if (!PInvoke.GetWindowRect(hwnd, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // 用原生区域保证窗口本身也拥有圆角，避免透明窗口四角仍参与命中。
        var radius = Math.Max(1, (int)Math.Round(bounds.Height * 0.35));
        var region = PInvoke.CreateRoundRectRgn(0, 0, bounds.Width, bounds.Height, radius, radius);
        if (region.IsNull)
            return;

        if (PInvoke.SetWindowRgn(hwnd, region, true) == 0)
            PInvoke.DeleteObject((HGDIOBJ)region);
    }

    public bool Expand(bool? animate = null, Action? expanded = null, bool notifyExpanded = true)
    {
        if (IsCollapsed)
        {
            IsCollapsed = false;
            _changingVisibility = true;
            // 悬停只展示窗口，不夺走其他软件的输入焦点。
            var showActivated = _window.ShowActivated;
            try
            {
                if (animate ?? SystemParameters.ClientAreaAnimation)
                    PrepareAnimation(initiallyExpanded: false);
                else
                    StopAnimation();
                _window.ShowActivated = false;
                _window.Show();
                var expandedCallback = expanded ?? (notifyExpanded ? _onExpanded : null);
                if (_animation is not null)
                    _animation.Start(expand: true, () =>
                    {
                        StopAnimation();
                        expandedCallback?.Invoke();
                    }, () => _strip?.Hide());
                else
                {
                    _strip?.Hide();
                    expandedCallback?.Invoke();
                }
            }
            finally
            {
                _window.ShowActivated = showActivated;
                _changingVisibility = false;
            }
        }
        else if (animate == false)
        {
            StopAnimation();
        }
        _leaveTime = 0;
        return IsDocked;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_changingVisibility) return;
        if (!_window.IsVisible) Undock(restoreWindow: false);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (IsAnimating) Expand(animate: false);
        _leaveTime = Environment.TickCount64;
    }

    private void StopAnimation()
    {
        _animation?.Dispose();
        _animation = null;
        if (!IsCollapsed) _strip?.Hide();
    }

    private void PrepareAnimation(bool initiallyExpanded)
    {
        if (_animation is not null) return;
        _animation = TopEdgeSlideAnimation.TryCreate(_window, initiallyExpanded);
        if (_animation is not null)
            _animation.InteractionRequested += () => Expand(animate: false);
    }

    private void Undock(bool restoreWindow = true)
    {
        if (!IsDocked) return;
        if (restoreWindow) Expand(animate: false, notifyExpanded: false);
        else StopAnimation();
        _strip?.Hide();
        IsCollapsed = false;
        IsDocked = false;
        _leaveTime = 0;
        if (_topmostBinding is not null)
            BindingOperations.SetBinding(_window, Window.TopmostProperty, _topmostBinding);
        else
            _window.Topmost = _previousTopmost;
        _topmostBinding = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.PreviewKeyDown -= OnKeyDown;
        Undock(restoreWindow: false);
        _strip?.Close();
        _strip = null;
    }
}
