using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Views;

public partial class MainWindow : IDisposable
{
    private const int WmNcHitTest = 0x0084;

    private static readonly IntPtr HtClient = new(1);
    private static readonly IntPtr HtLeft = new(10);
    private static readonly IntPtr HtRight = new(11);

    private readonly MainWindowViewModel _viewModel;
    private readonly Settings _settings;
    private bool _disposed = false;
    private HwndSource? _hwndSource;
    private TopEdgeAutoHideController? _topEdgeAutoHide;
    private WindowShowAnimation? _showAnimation;
    private bool _initialContentRendered;
    private bool _startupCloaked;

    public bool IsTopEdgeDocked => _topEdgeAutoHide?.IsDocked == true;
    public bool IsTopEdgeCollapsed => _topEdgeAutoHide?.IsCollapsed == true;

    public bool ExpandFromTopEdge()
    {
        if (IsTopEdgeDocked) StopShowAnimation();
        return _topEdgeAutoHide?.Expand() == true;
    }

    internal void PrepareShowAnimation()
    {
        // 首次显示已在 SourceInitialized 遮蔽，保留到启动布局完成。
        if (!_initialContentRendered) return;
        StopShowAnimation();
        if (!IsVisible && !IsTopEdgeDocked && SystemParameters.ClientAreaAnimation)
            _showAnimation = WindowShowAnimation.TryCreate(this);
    }

    internal void StartShowAnimation(Action activate)
    {
        if (_showAnimation?.IsActive != true)
        {
            activate();
            return;
        }
        var foreground = Win32Helper.GetForegroundWindow();
        var activationMode = WindowActivationContext.Current;
        _showAnimation.Start(() =>
        {
            if (!IsVisible) return;
            // 用户在动画期间切换到了其他窗口，不能在动画结束后抢回焦点。
            var currentForeground = Win32Helper.GetForegroundWindow();
            if (currentForeground != 0 && currentForeground != foreground &&
                !Win32Helper.IsForegroundWindow(this))
            {
                if (_settings.HideWhenDeactivated && !_viewModel.IsTopmost && !IsTopEdgeDocked)
                    _viewModel.Hide();
                return;
            }
            using var scope = WindowActivationContext.Push(activationMode);
            activate();
        });
    }

    internal void StopShowAnimation()
    {
        _showAnimation?.Dispose();
        _showAnimation = null;
        if (_startupCloaked) Win32Helper.SetWindowCloaked(this, cloaked: false);
        _startupCloaked = false;
    }

    private void FocusInputAfterTopEdgeExpand()
    {
        if (!_viewModel.IsInputBoxVisible || !IsVisible)
            return;

        // 感应条展开不主动激活外部应用，但主窗口内部应恢复到输入框。
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _viewModel.IsInputBoxVisible)
            {
                PART_Input.Focus();
                Keyboard.Focus(PART_Input);
            }
        }, DispatcherPriority.Input);
    }

    public MainWindow()
    {
        _viewModel = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        _settings = Ioc.Default.GetRequiredService<Settings>();

        DataContext = _viewModel;

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopShowAnimation();
        };

        InitializeComponent();

        //Notification.Show("STranslate", "Welcome to STranslate!");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_settings.HideOnStartup)
            _startupCloaked = Win32Helper.SetWindowCloaked(this, cloaked: true);
        else if (SystemParameters.ClientAreaAnimation)
            _showAnimation = WindowShowAnimation.TryCreate(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.InitializeWindowLayoutConstraints();
        _viewModel.UpdatePosition(_settings.HideOnStartup);
        // 等现代窗口模板安装完 WindowChrome 后再挂接，确保本钩子优先处理样式变更。
        _hwndSource = Win32Helper.AddWndProcHook(this, WndProc);
        Win32Helper.DisableMaximize(this);
        _topEdgeAutoHide ??= new TopEdgeAutoHideController(this, () => _settings.AutoHideAtTopEdge,
            FocusInputAfterTopEdgeExpand, () => _settings.TopEdgeAutoHideDelayMs,
            () => _showAnimation?.IsActive == true);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        // ContentRendered 在隐藏后再次显示时也可能触发，启动策略只执行一次。
        if (_initialContentRendered)
        {
            base.OnContentRendered(e);
            return;
        }
        if (_settings.HideOnStartup)
        {
            _viewModel.Hide();
        }
        else
        {
            _viewModel.Show();
        }

        _initialContentRendered = true;

        base.OnContentRendered(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        // 准备和移动窗口可能产生临时失焦；动画结束后才执行置前与输入聚焦。
        if (_showAnimation?.IsActive == true)
        {
            base.OnDeactivated(e);
            return;
        }
        _topEdgeAutoHide?.Update();
        if (IsTopEdgeDocked)
        {
            base.OnDeactivated(e);
            return;
        }
        if (_viewModel.IsTopmost) return;

        // win32 api和wpf层面修改窗口显隐时表现有所不同，直接使用Hide可能会导致出现在Alt-Tab栏
        // https://github.com/ZGGSONG/STranslate/issues/165
        if (_settings.HideWhenDeactivated)
            _viewModel.Hide();

        base.OnDeactivated(e);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        StopShowAnimation();
        _topEdgeAutoHide?.Dispose();
        _topEdgeAutoHide = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0231) _topEdgeAutoHide?.SetMoving(true); // WM_ENTERSIZEMOVE
        if (msg == 0x0232) _topEdgeAutoHide?.SetMoving(false); // WM_EXITSIZEMOVE
        // 隐藏标题栏按钮不会禁止系统最大化，统一拦截双击、拖拽和快捷键等入口。
        if (Win32Helper.HandleMaximizeMessage(msg, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == Win32Helper.TaskbarCreatedMessage)
        {
            Dispatcher.BeginInvoke(RefreshNotifyIcon, DispatcherPriority.Loaded);
        }

        if (msg == WmNcHitTest && TryHandleHorizontalResizeHitTest(lParam, out var hitTestResult))
        {
            handled = true;
            return hitTestResult;
        }

        return IntPtr.Zero;
    }

    private bool TryHandleHorizontalResizeHitTest(IntPtr lParam, out IntPtr hitTestResult)
    {
        hitTestResult = IntPtr.Zero;

        var hwnd = Win32Helper.GetWindowHandle(this);
        if (!PInvoke.GetWindowRect(hwnd, out var windowRect))
            return false;

        var cursorX = GetSignedLowWord(lParam);
        var cursorY = GetSignedHighWord(lParam);
        var resizeBorder = GetResizeBorderThickness();

        var isLeftBorder = cursorX >= windowRect.left && cursorX < windowRect.left + resizeBorder.Width;
        var isRightBorder = cursorX <= windowRect.right && cursorX > windowRect.right - resizeBorder.Width;
        if (isLeftBorder)
        {
            hitTestResult = HtLeft;
            return true;
        }

        if (isRightBorder)
        {
            hitTestResult = HtRight;
            return true;
        }

        var isTopBorder = cursorY >= windowRect.top && cursorY < windowRect.top + resizeBorder.Height;
        var isBottomBorder = cursorY <= windowRect.bottom && cursorY > windowRect.bottom - resizeBorder.Height;
        if (isTopBorder || isBottomBorder)
        {
            // 高度交给 SizeToContent 跟随内容变化，边缘命中退回 client 可阻止手动纵向 resize。
            hitTestResult = HtClient;
            return true;
        }

        return false;
    }

    private static Size GetResizeBorderThickness()
    {
        var paddedBorder = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXPADDEDBORDER);
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSIZEFRAME) + paddedBorder;
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSIZEFRAME) + paddedBorder;

        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private static int GetSignedLowWord(IntPtr value) => unchecked((short)((long)value & 0xffff));

    private static int GetSignedHighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xffff));

    private void RefreshNotifyIcon()
    {
        var shouldHide = _settings.HideNotifyIcon;

        // 如果配置显示托盘图标，则不需要刷新
        if (!shouldHide) return;

        _settings.HideNotifyIcon = false;
        _settings.HideNotifyIcon = shouldHide;
    }

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                StopShowAnimation();
                _topEdgeAutoHide?.Dispose();
                _topEdgeAutoHide = null;
                _hwndSource?.Dispose();
                PART_NotifyIcon.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
