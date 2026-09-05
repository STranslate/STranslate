using STranslate.Core;
using STranslate.Helpers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Views;

/// <summary>静态贴图。只持有显示快照，图片物理矩形是窗口和辉光的共同布局依据。</summary>
public partial class PinnedImageTranslateWindow
{
    private readonly PinnedWindowController _controller;
    private readonly PinnedImageTranslateChromeWindow _chromeWindow;
    private PinnedImageTranslateSnapshot? _snapshot;
    private ObservableCollection<OcrWord> _originalWords = [];
    private ObservableCollection<OcrWord> _translatedWords = [];
    private HwndSource? _hwndSource;
    private DrawingRectangle _imageBounds;
    private DrawingPoint _dragStartCursor;
    private DrawingRectangle _dragStartBounds;
    private bool _potentialDrag;
    private bool _isDragging;
    private bool _showOriginal;
    private bool _showShadow;
    private bool _sourceInitialized;
    private bool _isClosing;
    private ContextMenu? _activeContextMenu;

    public PinnedImageTranslateWindow(PinnedWindowController controller)
    {
        _controller = controller;
        InitializeComponent();
        _chromeWindow = new PinnedImageTranslateChromeWindow();
    }

    internal void Initialize(PinnedImageTranslateSnapshot snapshot, bool showShadow)
    {
        _snapshot = snapshot;
        _showOriginal = snapshot.ShowOriginal;
        _imageBounds = snapshot.PhysicalBounds;
        _showShadow = showShadow;
        _chromeWindow.UpdateVisual(false, showShadow);
        _originalWords = new(snapshot.OriginalWords);
        _translatedWords = new(snapshot.TranslatedWords);
        ShowCurrentLayer();
        ApplyBounds();
    }

    private void ShowCurrentLayer()
    {
        if (_snapshot is not { } snapshot)
            return;
        PART_ImageZoom.Source = _showOriginal ? snapshot.AnnotatedImage : snapshot.SourceImage;
        PART_ImageZoom.OverlayDocument = _showOriginal ? null : snapshot.TranslationOverlay;
        PART_ImageZoom.OcrWords = _showOriginal ? _originalWords : _translatedWords;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _sourceInitialized = true;
        Win32Helper.HideFromAltTab(this);
        _hwndSource = Win32Helper.AddWndProcHook(this, WndProc);
        // 在任何 Chrome HWND 创建或显示之前设置截图状态。
        _controller.OnWindowSourceInitialized(this);
        ApplyBounds();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateChromeVisual();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        UpdateChromeVisual();
    }

    internal bool SetCaptureCloaked(bool cloaked)
    {
        var contentResult = !_sourceInitialized || Win32Helper.SetWindowCloaked(this, cloaked);
        var chromeResult = _chromeWindow.SetCloaked(cloaked);
        return contentResult && chromeResult;
    }

    internal void CloseTransientUiForCapture()
    {
        if (_activeContextMenu is { IsOpen: true })
            _activeContextMenu.IsOpen = false;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_isClosing)
            return;
        var point = e.GetPosition(PART_ImageZoom);
        if (PART_ImageZoom.IsPointOverSelectableText(point))
        {
            if (e.ClickCount >= 2)
            {
                PART_ImageZoom.Focus();
                PART_ImageZoom.SelectTextAtPoint(point, selectParagraph: e.ClickCount >= 3);
                e.Handled = true;
            }
            return;
        }
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            Close();
            return;
        }
        PART_ImageZoom.ClearTextSelection();
        Focus();
        if (!TryGetCursorPosition(out _dragStartCursor))
            return;
        _dragStartBounds = _imageBounds;
        _potentialDrag = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_potentialDrag || e.LeftButton != MouseButtonState.Pressed ||
            !TryGetCursorPosition(out var cursor))
            return;
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        var dpi = GetDpi();
        if (!_isDragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
            return;
        _isDragging = true;
        MoveBy(_dragStartBounds.Left + dx - _imageBounds.Left,
            _dragStartBounds.Top + dy - _imageBounds.Top, syncLogicalBounds: false);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_potentialDrag)
            return;
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_potentialDrag)
            FinishDrag();
    }

    private void FinishDrag()
    {
        var moved = _isDragging;
        _potentialDrag = _isDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (moved)
            ApplyBounds();
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (_isClosing)
            return;
        if (!PART_ImageZoom.IsPointOverTextSelection(e.GetPosition(PART_ImageZoom)))
            PART_ImageZoom.ClearTextSelection();
        OpenContextMenu();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_isClosing || _activeContextMenu is { IsOpen: true })
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.A or Key.C)
        {
            if (e.Key == Key.A)
                PART_ImageZoom.SelectAllText();
            else
                _controller.CopyText(PART_ImageZoom.SelectedText);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            OpenContextMenu();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var step = Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0),
                Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
            };
            if (dx == 0 && dy == 0)
                return;
            MoveBy(dx, dy, syncLogicalBounds: true);
            e.Handled = true;
        }
    }

    private void OpenContextMenu()
    {
        CloseTransientUiForCapture();
        var menu = new ContextMenu { PlacementTarget = PART_ImageSurface };
        AddItem("ImageTranslatePinnedCopyAll", () => _controller.CopyText(PART_ImageZoom.GetFullText()),
            PART_ImageZoom.OcrWords is { Count: > 0 });
        if (!string.IsNullOrEmpty(PART_ImageZoom.SelectedText))
            AddItem("Copy", () => _controller.CopyText(PART_ImageZoom.SelectedText));
        AddItem(_showOriginal ? "ImageTranslatePinnedShowTranslation" : "ImageTranslatePinnedShowOriginal", () =>
        {
            _showOriginal = !_showOriginal;
            ShowCurrentLayer();
        });
        var shadow = AddItem("ImageTranslatePinnedWindowShadow", () =>
        {
            _showShadow = !_showShadow;
            _controller.ShowShadow = _showShadow;
            UpdateChromeVisual();
        });
        shadow.IsCheckable = true;
        shadow.IsChecked = _showShadow;
        AddItem("Close", Close);
        menu.Closed += (_, _) =>
        {
            _activeContextMenu = null;
            UpdateChromeVisual();
        };
        _activeContextMenu = menu;
        menu.IsOpen = true;

        MenuItem AddItem(string key, Action action, bool enabled = true)
        {
            var item = new MenuItem { IsEnabled = enabled };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
    }

    private DpiScale GetDpi() => _sourceInitialized ? VisualTreeHelper.GetDpi(this) :
        Win32Helper.GetDpiScaleForPhysicalPoint(_imageBounds.Left + _imageBounds.Width / 2,
            _imageBounds.Top + _imageBounds.Height / 2);

    private void ApplyBounds()
    {
        if (_isClosing || _imageBounds.IsEmpty)
            return;
        var dpi = GetDpi();
        Left = _imageBounds.Left / dpi.DpiScaleX;
        Top = _imageBounds.Top / dpi.DpiScaleY;
        Width = PART_ImageSurface.Width = _imageBounds.Width / dpi.DpiScaleX;
        Height = PART_ImageSurface.Height = _imageBounds.Height / dpi.DpiScaleY;
        PART_ImageSurface.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ImageSurface.VerticalAlignment = VerticalAlignment.Top;
        if (!_sourceInitialized)
            return;
        Win32Helper.SetWindowPhysicalBounds(this, _imageBounds.Left, _imageBounds.Top, _imageBounds.Width, _imageBounds.Height);
        _chromeWindow.UpdateBounds(_imageBounds, dpi);
        UpdateChromeVisual();
    }

    private void MoveBy(int dx, int dy, bool syncLogicalBounds)
    {
        if (dx == 0 && dy == 0)
            return;
        _imageBounds.Offset(dx, dy);
        if (syncLogicalBounds)
        {
            ApplyBounds();
            return;
        }
        var dpi = GetDpi();
        if (!_chromeWindow.IsVisible || !Win32Helper.SetTwoWindowPhysicalBounds(this, _imageBounds,
                _chromeWindow, PinnedImageTranslateChromeWindow.CalculateOuterBounds(_imageBounds, dpi)))
        {
            Win32Helper.SetWindowPhysicalBounds(this, _imageBounds.Left, _imageBounds.Top, _imageBounds.Width, _imageBounds.Height);
            _chromeWindow.UpdateBounds(_imageBounds, dpi);
        }
    }

    private void UpdateChromeVisual()
    {
        if (_isClosing || !_sourceInitialized)
            return;
        _chromeWindow.UpdateVisual(IsActive, _showShadow);
        _chromeWindow.EnsureShownBehind(this);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x02E0) // WM_DPICHANGED：等 WPF 更新 DPI 后按原始物理尺寸重排。
            Dispatcher.BeginInvoke(ApplyBounds, DispatcherPriority.Loaded);
        else if (msg == 0x007E) // WM_DISPLAYCHANGE：拔掉显示器后，把不可见的贴图移回最近工作区。
            Dispatcher.BeginInvoke(RestoreToVisibleMonitor, DispatcherPriority.Loaded);
        return 0;
    }

    private void RestoreToVisibleMonitor()
    {
        if (_isClosing)
            return;
        var bounds = new Rect(_imageBounds.X, _imageBounds.Y, _imageBounds.Width, _imageBounds.Height);
        if (MonitorInfo.GetDisplayMonitors().Any(monitor => monitor.WorkingArea.IntersectsWith(bounds)))
            return;
        var workArea = MonitorInfo.GetNearestDisplayMonitor(new WindowInteropHelper(this).Handle).WorkingArea;
        _imageBounds = ClampToWorkArea(_imageBounds, workArea);
        ApplyBounds();
    }

    internal static DrawingRectangle ClampToWorkArea(DrawingRectangle bounds, Rect workArea) => new(
        (int)Math.Clamp(bounds.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - bounds.Width)),
        (int)Math.Clamp(bounds.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - bounds.Height)),
        bounds.Width, bounds.Height);

    private static bool TryGetCursorPosition(out DrawingPoint point)
    {
        if (PInvoke.GetCursorPos(out var cursor))
        {
            point = new(cursor.X, cursor.Y);
            return true;
        }
        point = default;
        return false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        CloseTransientUiForCapture();
        _chromeWindow.HideForOwnerClosing();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _controller.Unregister(this);
        if (_hwndSource != null)
            _hwndSource.RemoveHook(WndProc);
        _chromeWindow.CloseSafely();
        _snapshot = null;
        _originalWords.Clear();
        _translatedWords.Clear();
        ModernWindowLifecycle.Release(this, null);
        base.OnClosed(e);
    }
}
