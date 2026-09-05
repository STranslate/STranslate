using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace STranslate.Views;

public partial class ImageTranslateCompactWindow
{
    private const double FallbackScreenWidthRatio = 0.85;
    private const double FallbackScreenHeightRatio = 0.85;
    private const double FallbackMinWidth = 320;
    private const double FallbackMinHeight = 180;
    private const double ToolbarReservedHeight = 64;
    // 按钮条宽度初值，仅用于首次 Measure 兜底；实际宽度在 PlaceOnPhysicalBounds 中实测。
    private const double ToolbarWidthFallback = 270;
    private const double ExecutingTextMinWidth = 180;
    private const double ExecutingTextMinHeight = 104;
    private const double ExecutingTextHorizontalPadding = 24;
    private const double ExecutingProgressRingSize = 60;
    private const double ExecutingProgressRingMinSize = 12;
    private const double ExecutingProgressRingTextGap = 16;
    private const double GapH = 8;
    private const double GapV = 6;
    private const double WindowMargin = 8;

    private readonly ImageTranslateWindowViewModel _viewModel;
    // 独立 DI scope：从 root provider 直接解析 Transient + IDisposable 的 ViewModel 不会被
    // 任何 scope 跟踪，其 Dispose()（取消对 OcrService/TranslateService/Settings 等单例的事件订阅）
    // 永不触发，单例通过事件委托反向持有 VM 导致泄漏。用独立 scope 解析并在窗口关闭时释放，
    // 确保 Dispose() 执行、VM 可被 GC 回收。
    private readonly IServiceScope _serviceScope;
    private bool _isContextMenuOpen;
    private bool _isToolbarDropDownOpen;
    private bool _isClosing;
    private CompactWindowLayout? _layout;
    private DrawingRectangle? _pendingPhysicalBounds;
    private DrawingRectangle _imagePhysicalBounds;
    private bool _isPinning;

    private bool ShouldSuppressAutoClose =>
        _isContextMenuOpen ||
        _isToolbarDropDownOpen ||
        _isPinning ||
        _viewModel.IsSaveDialogOpen ||
        _isClosing;

    public ImageTranslateCompactWindow()
    {
        _serviceScope = Ioc.Default.CreateScope();
        _viewModel = _serviceScope.ServiceProvider.GetRequiredService<ImageTranslateWindowViewModel>();
        DataContext = _viewModel;

        InitializeComponent();
        PART_PinButton.Command = new RelayCommand(PinCurrentResult);
    }

    public void PlaceForCapture(DrawingRectangle? physicalBounds, DrawingSize bitmapSize)
    {
        if (physicalBounds is { Width: > 0, Height: > 0 } bounds)
        {
            PlaceOnPhysicalBounds(bounds);
            return;
        }

        PlaceNearCursorScreen(bitmapSize);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32Helper.HideFromAltTab(this);

        if (_pendingPhysicalBounds is { } bounds)
        {
            _pendingPhysicalBounds = null;
            Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (ShouldSuppressAutoClose)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!ShouldSuppressAutoClose && IsVisible && !IsActive)
                Close();
        }, DispatcherPriority.Background);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _viewModel.CancelOperations();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 主动拆解视觉树：移除 NoticeBar、SnackbarContainer 等控件并清空 Content、InputBindings，
        // 断开 WPF 静态 PropertyDescriptor._propertyMap 通过 PropertyChangeTracker
        // 对窗口内部控件的锚定，避免已关闭窗口被静态缓存钉死无法 GC。
        // VM 由独立 DI scope 持有，释放 scope 触发 ViewModel.Dispose()。
        ModernWindowLifecycle.Release(this, _serviceScope.Dispose);
        base.OnClosed(e);
    }

    private void PlaceOnPhysicalBounds(DrawingRectangle bounds)
    {
        _imagePhysicalBounds = bounds;
        var dpiScale = GetDpiScale(bounds);
        var workArea = GetPhysicalWorkArea(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

        // 实测按钮条所需宽度（DIP），避免常量估算偏差导致居中错位
        var toolbarWidthDip = MeasureToolbarWidthDip();

        _layout = ImageTranslateCompactWindowPlacement.CreateLayout(
            imageBounds: bounds,
            workArea: workArea,
            dpiScaleX: dpiScale.DpiScaleX,
            dpiScaleY: dpiScale.DpiScaleY,
            minWidthDip: MinWidth,
            minImageHeightDip: MinHeight,
            toolbarWidthDip: toolbarWidthDip,
            toolbarHeightDip: ToolbarReservedHeight,
            gapHDip: GapH,
            gapVDip: GapV,
            windowMarginDip: WindowMargin);

        PlaceOnPhysicalWindowBounds(_layout.WindowBounds, dpiScale);
        ApplyLayoutToVisualTree();
    }

    /// <summary>
    /// 实测按钮条 Border 所需 DIP 宽度。Measure 后取 DesiredSize.Width，
    /// 兜底用 ToolbarWidthFallback 常量。
    /// </summary>
    private double MeasureToolbarWidthDip()
    {
        PART_ToolbarBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = PART_ToolbarBorder.DesiredSize.Width;
        return width > 0 ? width : ToolbarWidthFallback;
    }

    /// <summary>
    /// 兜底定位：当无法拿到截图选区物理坐标时（理论上 CaptureWithRegionAsync 总会返回坐标，
    /// 仅在异常情况下为空），将窗口贴在光标左上方并约束在光标所在屏幕内。
    /// 不再依赖 ScreenshotSelectionResolver 反推选区位置。
    /// </summary>
    private void PlaceNearCursorScreen(DrawingSize bitmapSize)
    {
        if (!PInvoke.GetCursorPos(out var cursorPoint))
        {
            PlaceCenteredOnPrimaryScreen(bitmapSize);
            return;
        }

        var cursorPosition = new DrawingPoint(cursorPoint.X, cursorPoint.Y);

        // 找到光标所在屏幕，用于把窗口约束在屏幕内
        var screen = MonitorInfo.GetDisplayMonitors()
            .Select(monitor => new DrawingRectangle(
                (int)Math.Round(monitor.Bounds.X),
                (int)Math.Round(monitor.Bounds.Y),
                (int)Math.Round(monitor.Bounds.Width),
                (int)Math.Round(monitor.Bounds.Height)))
            .FirstOrDefault(s => s.Contains(cursorPosition));

        // 默认贴在光标左上方
        var left = cursorPosition.X - bitmapSize.Width;
        var top = cursorPosition.Y - bitmapSize.Height;

        // 约束在屏幕工作区内
        if (screen is { Width: > 0, Height: > 0 } s)
        {
            left = Math.Max(s.Left, Math.Min(left, s.Right - bitmapSize.Width));
            top = Math.Max(s.Top, Math.Min(top, s.Bottom - bitmapSize.Height));
        }

        PlaceOnPhysicalBounds(new DrawingRectangle(left, top, bitmapSize.Width, bitmapSize.Height));
    }

    private void PlaceCenteredOnPrimaryScreen(DrawingSize bitmapSize)
    {
        var screen = MonitorInfo.GetPrimaryDisplayMonitor();
        var workArea = new DrawingRectangle(
            (int)Math.Round(screen.WorkingArea.Left),
            (int)Math.Round(screen.WorkingArea.Top),
            (int)Math.Round(screen.WorkingArea.Width),
            (int)Math.Round(screen.WorkingArea.Height));
        var dpiScale = GetDpiScale(workArea);
        var windowBounds = ImageTranslateCompactWindowPlacement.CreateCenteredOnWorkArea(
            workArea,
            bitmapSize,
            dpiScale.DpiScaleX,
            dpiScale.DpiScaleY,
            FallbackMinWidth,
            FallbackMinHeight,
            ToolbarReservedHeight,
            FallbackScreenWidthRatio,
            FallbackScreenHeightRatio);

        PlaceOnPhysicalBounds(new DrawingRectangle(windowBounds.Location, bitmapSize));
    }

    private void PinCurrentResult()
    {
        if (_isPinning || _isClosing || !_viewModel.CanPin)
            return;

        _isPinning = true;
        try
        {
            var snapshot = PinnedImageTranslateSnapshot.Create(
                _viewModel.SourceImage!, _viewModel.AnnotatedImage!, _viewModel.ResultOverlay!,
                _viewModel.OriginalSelectionWords, _viewModel.TranslatedSelectionWords, _imagePhysicalBounds,
                _viewModel.Settings.IsImTranShowingAnnotated);
            Ioc.Default.GetRequiredService<PinnedWindowController>().CreateWindow(snapshot);
            Close();
        }
        catch (Exception ex)
        {
            Activate();
            _viewModel.ReportPinFailure(ex);
        }
        finally
        {
            _isPinning = false;
        }
    }

    /// <summary>
    /// 两步定位(对齐 ScreenGrab 的做法):
    /// 1. 先用 DIP 写回 WPF 的 Left/Top/Width/Height,让布局系统认这个逻辑位置;
    /// 2. 再用 SetWindowPos 以物理像素精确校正 HWND。
    /// 只做第 2 步会导致 WPF 布局周期把窗口拉回逻辑坐标对应位置,在非 100% 缩放下产生偏移。
    /// </summary>
    private void PlaceOnPhysicalWindowBounds(DrawingRectangle bounds, System.Windows.DpiScale dpiScale)
    {
        var dipBounds = ImageTranslateCompactWindowPlacement.ToDipBounds(bounds, dpiScale.DpiScaleX, dpiScale.DpiScaleY);
        Left = dipBounds.Left;
        Top = dipBounds.Top;
        Width = dipBounds.Width;
        Height = dipBounds.Height;

        if (IsVisible)
            Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        else
            _pendingPhysicalBounds = bounds;
    }

    /// <summary>
    /// 把布局结果换算成 DIP 并应用到 ImageZoom、按钮条 Border 与执行遮罩：
    /// 固定 Width/Height 保证图片 1:1 显示，左上对齐 + Margin 把图片钉在选区绝对位置、
    /// 按钮条钉到布局算法算出的位置。按钮条 ZIndex 高于图片（XAML 内 PART_ToolbarBorder Panel.ZIndex=20）。
    /// 执行遮罩只覆盖图片区域（透明窗口下避免遮罩铺满整窗造成背景闪烁）。
    /// </summary>
    private void ApplyLayoutToVisualTree()
    {
        if (_layout is null) return;
        var dpi = GetDpiScale(_layout.WindowBounds);
        var sx = dpi.DpiScaleX;
        var sy = dpi.DpiScaleY;

        var imageWidthDip = _layout.ImageWidth / sx;
        var imageHeightDip = _layout.ImageHeight / sy;
        var imageMargin = new Thickness(_layout.ImageOffsetX / sx, _layout.ImageOffsetY / sy, 0, 0);

        // 图片：固定尺寸 + 左上对齐 + Margin 偏移（钉在选区绝对位置）
        PART_ImageZoom.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ImageZoom.VerticalAlignment = VerticalAlignment.Top;
        PART_ImageZoom.Width = imageWidthDip;
        PART_ImageZoom.Height = imageHeightDip;
        PART_ImageZoom.Margin = imageMargin;

        // 按钮条：固定尺寸 + 左上对齐 + Margin 偏移，ZIndex 高于图片
        PART_ToolbarBorder.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ToolbarBorder.VerticalAlignment = VerticalAlignment.Top;
        PART_ToolbarBorder.Width = _layout.ToolbarWidth / sx;
        PART_ToolbarBorder.Height = _layout.ToolbarHeight / sy;
        PART_ToolbarBorder.Margin = new Thickness(_layout.ToolbarX / sx, _layout.ToolbarY / sy, 0, 0);

        // 执行遮罩：只覆盖图片区域，与图片同尺寸同位置，避免铺满透明窗口造成背景闪烁
        PART_ExecutingOverlay.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ExecutingOverlay.VerticalAlignment = VerticalAlignment.Top;
        PART_ExecutingOverlay.Width = imageWidthDip;
        PART_ExecutingOverlay.Height = imageHeightDip;
        PART_ExecutingOverlay.Margin = imageMargin;
        ApplyExecutingOverlayContentLayout(imageWidthDip, imageHeightDip);
    }

    private void ApplyExecutingOverlayContentLayout(double imageWidthDip, double imageHeightDip)
    {
        var showText = imageWidthDip >= ExecutingTextMinWidth && imageHeightDip >= ExecutingTextMinHeight;
        var ringSize = showText
            ? ExecutingProgressRingSize
            : Math.Max(
                ExecutingProgressRingMinSize,
                Math.Min(ExecutingProgressRingSize, Math.Min(imageWidthDip, imageHeightDip) * 0.65));

        PART_ExecutingProgressRing.Width = ringSize;
        PART_ExecutingProgressRing.Height = ringSize;
        PART_ExecutingProgressRing.Margin = showText
            ? new Thickness(0, 0, 0, ExecutingProgressRingTextGap)
            : new Thickness();

        PART_ExecutingText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        PART_ExecutingText.MaxWidth = Math.Max(0, imageWidthDip - ExecutingTextHorizontalPadding);
    }

    private static System.Windows.DpiScale GetDpiScale(DrawingRectangle bounds) =>
        Win32Helper.GetDpiScaleForPhysicalPoint(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

    /// <summary>
    /// 获取包含指定物理坐标的屏幕工作区（物理像素）。
    /// MonitorInfo.Bounds/WorkingArea 直接来自 Win32 RECT，本身就是物理像素，无需 DPI 换算。
    /// </summary>
    private static DrawingRectangle GetPhysicalWorkArea(int physicalX, int physicalY)
    {
        var monitor = MonitorInfo.GetDisplayMonitors()
            .FirstOrDefault(m =>
            {
                var b = m.Bounds;
                return physicalX >= b.X && physicalX < b.X + b.Width
                    && physicalY >= b.Y && physicalY < b.Y + b.Height;
            }) ?? MonitorInfo.GetPrimaryDisplayMonitor();

        var w = monitor.WorkingArea;
        return new DrawingRectangle(
            (int)Math.Round(w.X),
            (int)Math.Round(w.Y),
            (int)Math.Round(w.Width),
            (int)Math.Round(w.Height));
    }

    private void OnImageContextMenuOpened(object sender, RoutedEventArgs e) => _isContextMenuOpen = true;

    private void OnImageContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        if (!_viewModel.IsSaveDialogOpen)
            Activate();
    }

    private void OnToolbarComboBoxDropDownOpened(object sender, EventArgs e) => _isToolbarDropDownOpen = true;

    private void OnToolbarComboBoxDropDownClosed(object sender, EventArgs e)
    {
        _isToolbarDropDownOpen = false;
        if (!_isClosing && IsVisible)
            Activate();
    }
}
