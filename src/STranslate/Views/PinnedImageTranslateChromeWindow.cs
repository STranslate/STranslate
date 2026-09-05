using STranslate.Helpers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Views;

/// <summary>
/// 为 Pinned 内容窗口绘制阴影与激活辉光的伴随窗。
/// </summary>
internal sealed class PinnedImageTranslateChromeWindow : Window
{
    // 为阴影与辉光预留的外围区域。
    private const double ChromeMarginDip = 10;

    private readonly ChromeSurface _surface = new();
    private bool _isActive;
    private bool _isShadowEnabled = true;
    private bool _sourceInitialized;
    private bool _isClosed;
    private bool _captureCloaked;

    internal PinnedImageTranslateChromeWindow()
    {
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;

        Content = _surface;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _sourceInitialized = true;
        Win32Helper.HideFromAltTab(this);
        Win32Helper.ConfigureClickThroughNoActivate(this);
        if (_captureCloaked && !Win32Helper.SetWindowCloaked(this, true))
            throw new InvalidOperationException("Failed to cloak the pinned window chrome during capture.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }

    internal void UpdateVisual(bool isActive, bool isShadowEnabled)
    {
        if (_isClosed)
            return;

        _isActive = isActive;
        _isShadowEnabled = isShadowEnabled;
        _surface.Update(isActive);
        _surface.Visibility = ShouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisibility();
    }

    internal void UpdateBounds(DrawingRectangle imageBounds, DpiScale dpi)
    {
        if (_isClosed || imageBounds.Width <= 0 || imageBounds.Height <= 0)
            return;

        var outer = CalculateOuterBounds(imageBounds, dpi);
        var sx = Math.Max(0.01, dpi.DpiScaleX);
        var sy = Math.Max(0.01, dpi.DpiScaleY);

        Left = outer.Left / sx;
        Top = outer.Top / sy;
        Width = outer.Width / sx;
        Height = outer.Height / sy;

        Win32Helper.SetWindowPhysicalBounds(
            this,
            outer.Left,
            outer.Top,
            outer.Width,
            outer.Height,
            showWindow: false);

        UpdateVisibility();
    }

    internal static DrawingRectangle CalculateOuterBounds(DrawingRectangle imageBounds, DpiScale dpi)
    {
        var sx = Math.Max(0.01, dpi.DpiScaleX);
        var sy = Math.Max(0.01, dpi.DpiScaleY);
        var marginX = Math.Max(1, (int)Math.Ceiling(ChromeMarginDip * sx));
        var marginY = Math.Max(1, (int)Math.Ceiling(ChromeMarginDip * sy));
        return DrawingRectangle.FromLTRB(
            imageBounds.Left - marginX,
            imageBounds.Top - marginY,
            imageBounds.Right + marginX,
            imageBounds.Bottom + marginY);
    }

    internal void EnsureShownBehind(Window contentWindow)
    {
        if (_isClosed || !ShouldBeVisible)
            return;

        if (!IsVisible)
            Show();

        Win32Helper.PlaceWindowBehind(this, contentWindow);
    }

    internal bool SetCloaked(bool cloaked)
    {
        _captureCloaked = cloaked;
        return _isClosed || !_sourceInitialized || Win32Helper.SetWindowCloaked(this, cloaked);
    }

    internal void HideForOwnerClosing()
    {
        if (!_isClosed && IsVisible)
            Hide();
    }

    internal void CloseSafely()
    {
        if (!_isClosed)
            Close();
    }

    private bool ShouldBeVisible => _isActive || _isShadowEnabled;

    private void UpdateVisibility()
    {
        if (_isClosed || !_sourceInitialized)
            return;

        if (ShouldBeVisible)
        {
            if (!IsVisible)
                Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>沿用原 WPF 模糊效果，仅为四周可见区域分配中间表面。</summary>
    private sealed class ChromeSurface : FrameworkElement
    {
        private static readonly Color GlowColor = Color.FromRgb(0x4D, 0x90, 0xFE);
        private static readonly Brush GlowBrush = CreateGlowBrush();
        private static readonly DropShadowEffect Shadow = CreateEffect(Colors.Black, 8, 0.36, RenderingBias.Performance);
        private static readonly DropShadowEffect Glow = CreateEffect(GlowColor, 6, 0.42, RenderingBias.Quality);
        private readonly ContainerVisual[] _clips = new ContainerVisual[4];
        private readonly DrawingVisual[] _casters = new DrawingVisual[4];
        private bool _active;
        private Rect _center = Rect.Empty;

        internal ChromeSurface()
        {
            IsHitTestVisible = false;
            for (var i = 0; i < 4; i++)
            {
                // 与原 Border 的 Margin 使用相同的局部原点，保留小数 DPI 下的采样位置。
                _casters[i] = new DrawingVisual { Effect = Shadow, Offset = new Vector(ChromeMarginDip, ChromeMarginDip) };
                _clips[i] = new ContainerVisual();
                _clips[i].Children.Add(_casters[i]);
                AddVisualChild(_clips[i]);
            }
        }

        internal void Update(bool active)
        {
            if (_active == active)
                return;
            _active = active;
            foreach (var caster in _casters)
                caster.Effect = active ? Glow : Shadow;
            UpdateDrawing();
        }

        protected override int VisualChildrenCount => _clips.Length;
        protected override Visual GetVisualChild(int index) => _clips[index];

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            UpdateDrawing();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateDrawing();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_center.IsEmpty)
                drawingContext.DrawRectangle(_active ? GlowBrush : Brushes.White, null, _center);
        }

        private void UpdateDrawing()
        {
            if (ActualWidth < 2 * ChromeMarginDip || ActualHeight < 2 * ChromeMarginDip)
                return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = Math.Ceiling(ChromeMarginDip * dpi.DpiScaleX) / dpi.DpiScaleX;
            var top = Math.Ceiling(ChromeMarginDip * dpi.DpiScaleY) / dpi.DpiScaleY;
            var right = Math.Floor((ActualWidth - ChromeMarginDip) * dpi.DpiScaleX) / dpi.DpiScaleX;
            var bottom = Math.Floor((ActualHeight - ChromeMarginDip) * dpi.DpiScaleY) / dpi.DpiScaleY;
            _center = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            var brush = _active ? GlowBrush : Brushes.White;
            // 中央已是完全不透明的实色；分界落在物理像素上，避免裁剪边缘重复混合。
            Rect[] clips = [new(0, 0, ActualWidth, top), new(0, bottom, ActualWidth, ActualHeight - bottom),
                new(0, top, left, _center.Height), new(right, top, ActualWidth - right, _center.Height)];
            for (var i = 0; i < _clips.Length; i++)
            {
                var clip = new RectangleGeometry(clips[i]);
                clip.Freeze();
                _clips[i].Clip = clip;
                using var drawing = _casters[i].RenderOpen();
                drawing.DrawRectangle(brush, null, new Rect(0, 0,
                    Math.Max(0, ActualWidth - 2 * ChromeMarginDip), Math.Max(0, ActualHeight - 2 * ChromeMarginDip)));
            }
            InvalidateVisual();
        }

        private static DropShadowEffect CreateEffect(Color color, double radius, double opacity, RenderingBias bias)
        {
            var effect = new DropShadowEffect { Color = color, BlurRadius = radius, Opacity = opacity,
                ShadowDepth = 0, Direction = 0, RenderingBias = bias };
            effect.Freeze();
            return effect;
        }

        private static Brush CreateGlowBrush()
        {
            var brush = new SolidColorBrush(GlowColor);
            brush.Freeze();
            return brush;
        }
    }
}
