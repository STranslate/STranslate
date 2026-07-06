using CommunityToolkit.Mvvm.DependencyInjection;
using Serilog;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Views;

public partial class MainWindow : IDisposable
{
    private const int WmNcHitTest = 0x0084;

    // WM_DPICHANGED：显示器/DPI 变化时系统下发，RDP 接入或跨屏移动会触发。
    // PerMonitorV2 下 WPF 默认只保持 DIP 尺寸不变，不会重新调用我们的布局约束逻辑，
    // 这里显式拦截，让运行中 DPI 切换也走一遍和启动相同的重排路径。
    private const int WmDpiChanged = 0x02E0;

    private static readonly IntPtr HtClient = new(1);
    private static readonly IntPtr HtLeft = new(10);
    private static readonly IntPtr HtRight = new(11);

    private readonly MainWindowViewModel _viewModel;
    private readonly Settings _settings;
    private bool _disposed = false;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        _viewModel = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        _settings = Ioc.Default.GetRequiredService<Settings>();

        DataContext = _viewModel;

        InitializeComponent();

        //Notification.Show("STranslate", "Welcome to STranslate!");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.InitializeWindowLayoutConstraints();
        _viewModel.UpdatePosition(_settings.HideOnStartup);

        _hwndSource = Win32Helper.AddWndProcHook(this, WndProc);
    }


    protected override void OnContentRendered(EventArgs e)
    {
        if (_settings.HideOnStartup)
        {
            _viewModel.Hide();
        }
        else
        {
            _viewModel.Show();
            Win32Helper.SetForegroundWindow(this);
        }

        base.OnContentRendered(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        if (_viewModel.IsTopmost) return;

        // win32 api和wpf层面修改窗口显隐时表现有所不同，直接使用Hide可能会导致出现在Alt-Tab栏
        // https://github.com/ZGGSONG/STranslate/issues/165
        if (_settings.HideWhenDeactivated)
            _viewModel.Hide();

        base.OnDeactivated(e);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.TaskbarCreatedMessage)
        {
            Dispatcher.BeginInvoke(RefreshNotifyIcon, DispatcherPriority.Loaded);
        }

        if (msg == WmDpiChanged)
        {
            // [DIAG] 验证 WM_DPICHANGED 是否真的被触发，并记录新旧 DPI
            var newDpiY = (uint)(lParam.ToInt64() >> 32) & 0xFFFF;
            var newDpiX = (uint)(lParam.ToInt64() >> 16) & 0xFFFF;
            var oldDpi = (uint)(wParam.ToInt64() & 0xFFFF);
            Log.Information(
                "[DPI-DIAG] WM_DPICHANGED received: oldDpi={OldDpi} newDpiX={NewDpiX} newDpiY={NewDpiY}",
                oldDpi, newDpiX, newDpiY);

            // 让 WPF 先按默认行为完成 DPI 切换与布局更新，再在后台优先级重算约束和位置，
            // 避免在系统处理过程中读到中间态的尺寸/位置值。
            Dispatcher.BeginInvoke(RefreshLayoutAfterDpiChanged, DispatcherPriority.Background);
        }

        if (msg == WmNcHitTest && TryHandleHorizontalResizeHitTest(lParam, out var hitTestResult))
        {
            handled = true;
            return hitTestResult;
        }

        return IntPtr.Zero;
    }

    private void RefreshLayoutAfterDpiChanged()
    {
        // [DIAG] 记录重排前的屏幕参数与窗口尺寸
        Log.Information(
            "[DPI-DIAG] RefreshLayout BEFORE: VirtualScreen={W}x{H} VirtualScreenLeft={L} VirtualScreenTop={T} Actual={AW}x{AH}",
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            ActualWidth, ActualHeight);

        // 与 OnLoaded 走相同的重排路径，确保运行中 DPI 变化后
        // 最大高度约束、窗口位置都与新屏幕/DPI 匹配。
        _viewModel.InitializeWindowLayoutConstraints();
        _viewModel.UpdatePosition();

        Log.Information(
            "[DPI-DIAG] RefreshLayout AFTER: MainWindowLeft={L} MainWindowTop={T} Actual={AW}x{AH}",
            _settings.MainWindowLeft, _settings.MainWindowTop, ActualWidth, ActualHeight);
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
