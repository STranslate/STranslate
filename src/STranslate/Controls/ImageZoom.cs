using STranslate.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace STranslate.Controls;

/// <summary>
/// A control that allows the user to zoom and pan an image.
/// <see href="https://github.com/BRIZEIUM/LinkingMountainsImageZoomWPF"/>
/// * Modified by zggsong(2025/10/17)
/// * Add InteractionCanvas for text selection
/// </summary>
[TemplatePart(Name = PartImageContainerName, Type = typeof(Grid))]
[TemplatePart(Name = PartImageName, Type = typeof(Image))]
[TemplatePart(Name = PartScaleTransformName, Type = typeof(ScaleTransform))]
[TemplatePart(Name = PartTranslateTransformName, Type = typeof(TranslateTransform))]
[TemplatePart(Name = PartScaleTextBorderName, Type = typeof(Border))]
[TemplatePart(Name = PartInteractionCanvas, Type = typeof(Canvas))]
[TemplatePart(Name = PartViewbox, Type = typeof(Viewbox))]
public class ImageZoom : Control
{
    #region Constants

    private const string PartImageContainerName = "PART_ImageContainer";
    private const string PartImageName = "PART_Image";
    private const string PartScaleTransformName = "PART_ScaleTransform";
    private const string PartTranslateTransformName = "PART_TranslateTransform";
    private const string PartScaleTextBorderName = "PART_ScaleTextBorder";
    private const string PartInteractionCanvas = "PART_InteractionCanvas";
    private const string PartViewbox = "PART_Viewbox";

    private const double ZoomFactorDefault = 1.2;
    private const double MinZoomRatioDefault = 0.01;
    private const double MaxZoomRatioDefault = 50;
    private const double ZoomRatioDefault = 1;
    private const double TranslateXDefault = 0;
    private const double TranslateYDefault = 0;
    private const double TranslateOriginXDefault = 0.5;
    private const double TranslateOriginYDefault = 0.5;
    private const int AnimationDurationMs = 200;
    private const int ZoomValueHintAnimationDurationMs = 1000;
    private const double HighlightOpacity = 0.4;

    #endregion

    #region Fields - Template Parts

    private Grid? _imageContainer;
    private Image? _image;
    private ScaleTransform? _scaleTransform;
    private TranslateTransform? _translateTransform;
    private Border? _scaleTextBorder;
    private Canvas? _interactionCanvas;
    private Viewbox? _viewbox;

    #endregion

    #region Fields - Zoom and Pan State

    private Size _imageSize;
    private Point _lastMousePosition;
    private bool _isDragging;
    private double _translateXCurrent = TranslateXDefault;
    private double _translateYCurrent = TranslateYDefault;
    private bool _useAnimationOnSetZoomRatio = true;
    private bool _suppressZoomRatioChanged;

    #endregion

    #region Fields - Text Selection State

    private bool _isSelecting;
    private int? _selectionStartIndex;
    private int? _selectionEndIndex;
    private bool _isMouseOverText;
    private string? _fullTextCache;
    private SolidColorBrush? _highlightBrush;

    #endregion

    #region Constructor

    static ImageZoom()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ImageZoom), new FrameworkPropertyMetadata(typeof(ImageZoom)));
    }

    #endregion

    #region Dependency Properties - Source

    public ImageSource Source
    {
        get => (ImageSource)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ImageZoom),
            new FrameworkPropertyMetadata(null, OnSourceChanged));

    public ImageTranslateOverlayDocument? OverlayDocument
    {
        get => (ImageTranslateOverlayDocument?)GetValue(OverlayDocumentProperty);
        set => SetValue(OverlayDocumentProperty, value);
    }

    public static readonly DependencyProperty OverlayDocumentProperty =
        DependencyProperty.Register(
            nameof(OverlayDocument),
            typeof(ImageTranslateOverlayDocument),
            typeof(ImageZoom),
            new FrameworkPropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        if (e.NewValue is ImageSource newSource)
        {
            control._imageSize = new Size(newSource.Width, newSource.Height);
        }
        else
        {
            control._imageSize = new Size(0, 0);
        }
        control.Reset();
    }

    #endregion

    #region Dependency Properties - Zoom

    public double ZoomFactor
    {
        get => (double)GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    public static readonly DependencyProperty ZoomFactorProperty =
        DependencyProperty.Register(nameof(ZoomFactor), typeof(double), typeof(ImageZoom),
            new FrameworkPropertyMetadata(ZoomFactorDefault, null, OnCoerceZoomFactor));

    private static object OnCoerceZoomFactor(DependencyObject d, object baseValue)
    {
        var control = (ImageZoom)d;
        var newValue = (double)baseValue;

        if (double.IsNaN(newValue) || double.IsInfinity(newValue))
            return control.ZoomFactor;

        return Math.Max(newValue, 1.0);
    }

    public double ZoomRatio
    {
        get => (double)GetValue(ZoomRatioProperty);
        set => SetValue(ZoomRatioProperty, value);
    }

    public static readonly DependencyProperty ZoomRatioProperty =
        DependencyProperty.Register(nameof(ZoomRatio), typeof(double), typeof(ImageZoom),
            new FrameworkPropertyMetadata(ZoomRatioDefault, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnZoomRatioChanged, OnCoerceZoomRatio));

    private static void OnZoomRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        if (control._suppressZoomRatioChanged)
            return;

        var newZoomRatio = (double)e.NewValue;
        control.AnimateTransform(newZoomRatio, newZoomRatio, control._translateXCurrent,
            control._translateYCurrent, control._useAnimationOnSetZoomRatio, true);
    }

    private static object OnCoerceZoomRatio(DependencyObject d, object baseValue)
    {
        var control = (ImageZoom)d;
        var newValue = (double)baseValue;

        if (double.IsNaN(newValue) || double.IsInfinity(newValue))
            return control.ZoomRatio;

        return Clamp(newValue, control.MinZoomRatio, control.MaxZoomRatio);
    }

    public double MinZoomRatio
    {
        get => (double)GetValue(MinZoomRatioProperty);
        set => SetValue(MinZoomRatioProperty, value);
    }

    public static readonly DependencyProperty MinZoomRatioProperty =
        DependencyProperty.Register(nameof(MinZoomRatio), typeof(double), typeof(ImageZoom),
            new FrameworkPropertyMetadata(MinZoomRatioDefault, OnMinZoomRatioChanged, OnCoerceMinZoomRatio));

    private static void OnMinZoomRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        var newMinZoomRatio = (double)e.NewValue;
        if (control.ZoomRatio < newMinZoomRatio)
            control.ZoomTo(newMinZoomRatio);
    }

    private static object OnCoerceMinZoomRatio(DependencyObject d, object baseValue)
    {
        var control = (ImageZoom)d;
        var newValue = (double)baseValue;

        if (double.IsNaN(newValue) || double.IsInfinity(newValue) || newValue < 0)
            return control.MinZoomRatio;

        return Math.Min(newValue, control.MaxZoomRatio);
    }

    public double MaxZoomRatio
    {
        get => (double)GetValue(MaxZoomRatioProperty);
        set => SetValue(MaxZoomRatioProperty, value);
    }

    public static readonly DependencyProperty MaxZoomRatioProperty =
        DependencyProperty.Register(nameof(MaxZoomRatio), typeof(double), typeof(ImageZoom),
            new FrameworkPropertyMetadata(MaxZoomRatioDefault, OnMaxZoomRatioChanged, OnCoerceMaxZoomRatio));

    private static void OnMaxZoomRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        var newMaxZoomRatio = (double)e.NewValue;
        if (control.ZoomRatio > newMaxZoomRatio)
            control.ZoomTo(newMaxZoomRatio);
    }

    private static object OnCoerceMaxZoomRatio(DependencyObject d, object baseValue)
    {
        var control = (ImageZoom)d;
        var newValue = (double)baseValue;

        if (double.IsNaN(newValue) || double.IsInfinity(newValue) || newValue < 0)
            return control.MaxZoomRatio;

        return Math.Max(newValue, control.MinZoomRatio);
    }

    #endregion

    #region Dependency Properties - UI Settings

    public bool AlwaysHideZoomValueHint
    {
        get => (bool)GetValue(AlwaysHideZoomValueHintProperty);
        set => SetValue(AlwaysHideZoomValueHintProperty, value);
    }

    public static readonly DependencyProperty AlwaysHideZoomValueHintProperty =
        DependencyProperty.Register(nameof(AlwaysHideZoomValueHint), typeof(bool), typeof(ImageZoom),
            new FrameworkPropertyMetadata(false, OnAlwaysHideZoomValueHintChanged));

    private static void OnAlwaysHideZoomValueHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        var hide = (bool)e.NewValue;
        if (control._scaleTextBorder != null)
            control._scaleTextBorder.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool DisableAnimation
    {
        get => (bool)GetValue(DisableAnimationProperty);
        set => SetValue(DisableAnimationProperty, value);
    }

    public static readonly DependencyProperty DisableAnimationProperty =
        DependencyProperty.Register(nameof(DisableAnimation), typeof(bool), typeof(ImageZoom),
            new FrameworkPropertyMetadata(false));

    public bool DisableDoubleClickReset
    {
        get => (bool)GetValue(DisableDoubleClickResetProperty);
        set => SetValue(DisableDoubleClickResetProperty, value);
    }

    public static readonly DependencyProperty DisableDoubleClickResetProperty =
        DependencyProperty.Register(nameof(DisableDoubleClickReset), typeof(bool), typeof(ImageZoom),
            new FrameworkPropertyMetadata(false));

    public bool IsPanAndZoomEnabled
    {
        get => (bool)GetValue(IsPanAndZoomEnabledProperty);
        set => SetValue(IsPanAndZoomEnabledProperty, value);
    }

    public static readonly DependencyProperty IsPanAndZoomEnabledProperty =
        DependencyProperty.Register(nameof(IsPanAndZoomEnabled), typeof(bool), typeof(ImageZoom),
            new FrameworkPropertyMetadata(true));

    public Cursor MoveCursor
    {
        get => (Cursor)GetValue(MoveCursorProperty);
        set => SetValue(MoveCursorProperty, value);
    }

    public static readonly DependencyProperty MoveCursorProperty =
        DependencyProperty.Register(nameof(MoveCursor), typeof(Cursor), typeof(ImageZoom),
            new FrameworkPropertyMetadata(Cursors.SizeAll));

    #endregion

    #region Dependency Properties - OCR

    public ObservableCollection<OcrWord>? OcrWords
    {
        get => (ObservableCollection<OcrWord>?)GetValue(OcrWordsProperty);
        set => SetValue(OcrWordsProperty, value);
    }

    public static readonly DependencyProperty OcrWordsProperty =
        DependencyProperty.Register(nameof(OcrWords), typeof(ObservableCollection<OcrWord>), typeof(ImageZoom),
            new FrameworkPropertyMetadata(null, OnOcrWordsChanged));

    private static void OnOcrWordsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ImageZoom)d;
        // 数据源切换后旧选择索引失效。
        control.ResetSelection();
        control._fullTextCache = null; // 清除缓存
        control.UpdateSelectedText(); // 更新选中文本
    }

    public string SelectedText
    {
        get => (string)GetValue(SelectedTextProperty);
        private set => SetValue(SelectedTextPropertyKey, value);
    }

    private static readonly DependencyPropertyKey SelectedTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(SelectedText), typeof(string), typeof(ImageZoom),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedTextProperty = SelectedTextPropertyKey.DependencyProperty;

    #endregion

    #region Template Overrides

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        UnsubscribeEvents();
        GetTemplateParts();
        SubscribeEvents();

        Reset();
    }

    private void UnsubscribeEvents()
    {
        if (_imageContainer != null)
        {
            _imageContainer.MouseDown -= OnMouseDown;
            _imageContainer.MouseMove -= OnMouseMove;
            _imageContainer.MouseUp -= OnMouseUp;
            _imageContainer.LostMouseCapture -= OnLostMouseCapture;
            _imageContainer.MouseWheel -= OnMouseWheel;
            _imageContainer.ManipulationStarting -= OnManipulationStarting;
            _imageContainer.ManipulationDelta -= OnManipulationDelta;
        }

        if (_interactionCanvas != null)
        {
            _interactionCanvas.LostMouseCapture -= OnLostMouseCapture;
        }
    }

    private void GetTemplateParts()
    {
        _imageContainer = GetTemplateChild(PartImageContainerName) as Grid;
        _image = GetTemplateChild(PartImageName) as Image;
        _scaleTransform = GetTemplateChild(PartScaleTransformName) as ScaleTransform;
        _translateTransform = GetTemplateChild(PartTranslateTransformName) as TranslateTransform;
        _scaleTextBorder = GetTemplateChild(PartScaleTextBorderName) as Border;
        _interactionCanvas = GetTemplateChild(PartInteractionCanvas) as Canvas;
        _viewbox = GetTemplateChild(PartViewbox) as Viewbox;
    }

    private void SubscribeEvents()
    {
        if (_imageContainer != null)
        {
            _imageContainer.MouseDown += OnMouseDown;
            _imageContainer.MouseMove += OnMouseMove;
            _imageContainer.MouseUp += OnMouseUp;
            _imageContainer.LostMouseCapture += OnLostMouseCapture;
            _imageContainer.MouseWheel += OnMouseWheel;
            _imageContainer.ManipulationStarting += OnManipulationStarting;
            _imageContainer.ManipulationDelta += OnManipulationDelta;
        }

        if (_interactionCanvas != null)
        {
            _interactionCanvas.LostMouseCapture += OnLostMouseCapture;
        }
    }

    #endregion

    #region Event Handlers - Mouse

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPrimaryButton(e))
            return;

        Focus();

        if (HandleTextSelectionMouseDown(e))
            return;

        if (!IsPanAndZoomEnabled)
        {
            e.Handled = true;
            return;
        }

        if (HandleDoubleClick(e))
            return;

        StartDragging(e);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        UpdateMouseOverTextState(e);

        if (HandleTextSelectionMouseMove(e))
            return;

        if (_isDragging)
        {
            HandleDragging(e);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsPrimaryButton(e))
            return;

        if (_isSelecting)
        {
            StopTextSelection();
            e.Handled = true;
        }

        if (_isDragging)
        {
            StopDragging();
            e.Handled = true;
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        StopTextSelection();
        StopDragging();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsPanAndZoomEnabled)
        {
            e.Handled = true;
            return;
        }

        var isZoomIn = e.Delta > 0;

        if (TryZoomAtMousePosition(e, isZoomIn))
        {
            e.Handled = true;
            return;
        }

        // Fallback to center zoom
        if (isZoomIn)
            ZoomIn();
        else
            ZoomOut();

        e.Handled = true;
    }

    #endregion

    #region Event Handlers - Manipulation (Touch)

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        // Reserved for future touch support
        //e.ManipulationContainer = _imageContainer;
        //e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        // Reserved for future touch support
        //Vector translation = e.DeltaManipulation.Translation;
        //MoveInternal(translation.X, translation.Y, false);
    }

    #endregion

    #region Mouse Handling - Text Selection

    private bool HandleTextSelectionMouseDown(MouseButtonEventArgs e)
    {
        UpdateMouseOverTextState(e);

        if (!_isMouseOverText)
            return false;

        var mousePos = e.GetPosition(_interactionCanvas);
        if (e.ClickCount == 2 && SelectVisualLineAtPoint(mousePos))
        {
            e.Handled = true;
            return true;
        }

        ResetSelection();
        _isSelecting = true;

        _interactionCanvas?.CaptureMouse();
        _selectionStartIndex = GetCharacterIndexAtPoint(mousePos);

        e.Handled = true;
        return true;
    }

    private bool HandleTextSelectionMouseMove(MouseEventArgs e)
    {
        if (!_isSelecting)
            return false;

        var mousePos = e.GetPosition(_interactionCanvas);
        var currentIndex = GetCharacterIndexAtPoint(mousePos);

        if (currentIndex.HasValue && currentIndex.Value != _selectionEndIndex)
        {
            _selectionEndIndex = currentIndex.Value;
            UpdateSelectionHighlight();
        }

        e.Handled = true;
        return true;
    }

    private void UpdateMouseOverTextState(MouseEventArgs e)
    {
        if (_interactionCanvas == null || OcrWords == null)
            return;

        var currentPoint = e.GetPosition(_interactionCanvas);
        var isOverTextNow = IsPointOverAnyWord(currentPoint);

        if (isOverTextNow != _isMouseOverText)
        {
            _isMouseOverText = isOverTextNow;
            _interactionCanvas.Cursor = isOverTextNow ? Cursors.IBeam : null;
        }
    }

    private bool IsPointOverAnyWord(Point point) => FindWordAtPoint(point) != null;

    /// <summary>
    /// 判断 ImageZoom 坐标是否位于可选文字上。
    /// </summary>
    public bool IsPointOverSelectableText(Point pointRelativeToControl)
    {
        if (_interactionCanvas == null || OcrWords == null || OcrWords.Count == 0)
            return false;

        var pointOnCanvas = TranslatePoint(pointRelativeToControl, _interactionCanvas);
        return IsPointOverAnyWord(pointOnCanvas);
    }

    /// <summary>
    /// 清除当前文字选择。
    /// </summary>
    public void ClearTextSelection() => ResetSelection();

    internal bool IsPointOverTextSelection(Point point)
    {
        if (_interactionCanvas == null || _selectionStartIndex == null || _selectionEndIndex == null)
            return false;
        var word = FindWordAtPoint(TranslatePoint(point, _interactionCanvas));
        return word != null && IsWordInSelection(word,
            Math.Min(_selectionStartIndex.Value, _selectionEndIndex.Value),
            Math.Max(_selectionStartIndex.Value, _selectionEndIndex.Value));
    }

    // Pinned 在 Preview 事件中调用；其他 ImageZoom 保留原有的双击选行行为。
    internal void SelectTextAtPoint(Point point, bool selectParagraph)
    {
        if (_interactionCanvas == null)
            return;
        var canvasPoint = TranslatePoint(point, _interactionCanvas);
        var word = FindWordAtPoint(canvasPoint);
        if (word == null)
            return;
        if (selectParagraph)
        {
            if (OcrWordSelection.TryGetParagraphRange(OcrWords, word, out var paragraphStart, out var paragraphEnd))
            {
                _isSelecting = false;
                _selectionStartIndex = paragraphStart;
                _selectionEndIndex = paragraphEnd;
                UpdateSelectionHighlight();
            }
            return;
        }
        if (OcrWordSelection.TryGetWordRange(GetFullText(), CalculateCharacterIndexInWord(word, canvasPoint),
                out var start, out var end))
        {
            // 软换行不应截断单词，但独立段落的文字不能合成一个词。
            if (OcrWordSelection.TryGetParagraphRange(OcrWords, word, out var lineStart, out var lineEnd))
            {
                start = Math.Max(start, lineStart);
                end = Math.Min(end, lineEnd);
            }
            _isSelecting = false;
            _selectionStartIndex = start;
            _selectionEndIndex = end;
            UpdateSelectionHighlight();
        }
    }

    private bool SelectVisualLineAtPoint(Point point)
    {
        var anchorWord = FindWordAtPoint(point);
        if (!OcrWordSelection.TryGetVisualLineRange(
                OcrWords,
                anchorWord,
                out var selectionStartIndex,
                out var selectionEndIndex))
        {
            return false;
        }

        _isSelecting = false;
        _selectionStartIndex = selectionStartIndex;
        _selectionEndIndex = selectionEndIndex;
        UpdateSelectionHighlight();
        return true;
    }

    private void StopTextSelection()
    {
        _isSelecting = false;
        _interactionCanvas?.ReleaseMouseCapture();
    }

    #endregion

    #region Mouse Handling - Dragging

    private bool HandleDoubleClick(MouseButtonEventArgs e)
    {
        if (IsPanAndZoomEnabled && !DisableDoubleClickReset && e.ClickCount == 2)
        {
            Reset();
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void StartDragging(MouseButtonEventArgs e)
    {
        if (!IsPanAndZoomEnabled)
            return;

        _lastMousePosition = e.GetPosition(_imageContainer);
        _imageContainer?.CaptureMouse();
        _imageContainer?.Cursor = MoveCursor;
        _isDragging = true;
        e.Handled = true;
    }

    private void HandleDragging(MouseEventArgs e)
    {
        var currentPosition = e.GetPosition(_imageContainer);
        var delta = currentPosition - _lastMousePosition;
        _lastMousePosition = currentPosition;

        MoveInternal(delta.X, delta.Y, false);
        e.Handled = true;
    }

    private void StopDragging()
    {
        _isDragging = false;
        _imageContainer?.ReleaseMouseCapture();
        _imageContainer?.Cursor = null;
    }

    #endregion

    #region Mouse Handling - Zoom

    private bool TryZoomAtMousePosition(MouseWheelEventArgs e, bool isZoomIn)
    {
        if (!IsValidForZoom())
            return false;

        var realTimeZoomRatio = _scaleTransform!.ScaleX;
        var imageRectOnContainer = CalculateImageRectOnContainer(realTimeZoomRatio);
        var mousePositionOnContainer = e.GetPosition(_imageContainer);

        if (!imageRectOnContainer.Contains(mousePositionOnContainer))
            return false;

        var mousePositionPercent = CalculateMousePositionPercent(mousePositionOnContainer, imageRectOnContainer);
        var newZoomRatio = Clamp(
            isZoomIn ? PeekZoomInRatio() : PeekZoomOutRatio(),
            MinZoomRatio,
            MaxZoomRatio
        );

        var translation = CalculateZoomTranslation(
            imageRectOnContainer,
            mousePositionPercent,
            newZoomRatio,
            isZoomIn
        );

        ApplyZoom(newZoomRatio, translation, realTimeZoomRatio);
        return true;
    }

    private bool IsValidForZoom()
    {
        return _imageSize.Width > 0 && _imageSize.Height > 0 &&
               _viewbox?.ActualWidth > 0 && _viewbox?.ActualHeight > 0 &&
               _imageContainer?.ActualWidth > 0 && _imageContainer?.ActualHeight > 0 &&
               _scaleTransform != null && _translateTransform != null;
    }

    private Rect CalculateImageRectOnContainer(double zoomRatio)
    {
        var topLeft = _viewbox!.TranslatePoint(new Point(0, 0), _imageContainer);
        var size = new Size(
            _viewbox.ActualWidth * zoomRatio,
            _viewbox.ActualHeight * zoomRatio
        );
        return new Rect(topLeft, size);
    }

    private Point CalculateMousePositionPercent(Point mousePos, Rect imageRect)
    {
        var relativeMousePos = mousePos - imageRect.TopLeft;
        return new Point(
            relativeMousePos.X / imageRect.Width,
            relativeMousePos.Y / imageRect.Height
        );
    }

    private Point CalculateZoomTranslation(Rect imageRect, Point mousePercent, double newZoomRatio, bool isZoomIn)
    {
        var renderTransOrigin = _viewbox!.RenderTransformOrigin;
        var widthChange = Math.Abs(_viewbox.ActualWidth * newZoomRatio - imageRect.Width);
        var heightChange = Math.Abs(_viewbox.ActualHeight * newZoomRatio - imageRect.Height);

        var deltaX = widthChange * (mousePercent.X - renderTransOrigin.X);
        var deltaY = heightChange * (mousePercent.Y - renderTransOrigin.Y);

        var direction = isZoomIn ? -1 : 1;
        return new Point(
            _translateTransform!.X + direction * deltaX,
            _translateTransform.Y + direction * deltaY
        );
    }

    private void ApplyZoom(double newZoomRatio, Point translation, double oldZoomRatio)
    {
        _useAnimationOnSetZoomRatio = true;
        var hasScaleChanged = !AreClose(newZoomRatio, oldZoomRatio);

        try
        {
            _suppressZoomRatioChanged = true;
            SetCurrentValue(ZoomRatioProperty, newZoomRatio);
        }
        finally
        {
            _suppressZoomRatioChanged = false;
        }

        AnimateTransform(newZoomRatio, newZoomRatio, translation.X, translation.Y, true, hasScaleChanged);
    }

    #endregion

    #region Animation

    private void AnimateTransform(double scaleX, double scaleY, double x, double y, bool useAnimation, bool hasScaleChanged)
    {
        _translateXCurrent = x;
        _translateYCurrent = y;

        AnimateProperty(_translateTransform, TranslateTransform.XProperty, x, useAnimation);
        AnimateProperty(_translateTransform, TranslateTransform.YProperty, y, useAnimation);
        AnimateProperty(_scaleTransform, ScaleTransform.ScaleXProperty, scaleX, useAnimation);
        AnimateProperty(_scaleTransform, ScaleTransform.ScaleYProperty, scaleY, useAnimation);

        if (hasScaleChanged)
        {
            AnimateZoomHint();
        }
    }

    private void AnimateProperty(Transform? transform, DependencyProperty property, double targetValue, bool useAnimation)
    {
        if (transform == null || property == null)
            return;

        if (DisableAnimation)
        {
            transform.BeginAnimation(property, null);
            transform.SetCurrentValue(property, targetValue);
            return;
        }

        var duration = useAnimation && !DisableAnimation
            ? TimeSpan.FromMilliseconds(AnimationDurationMs)
            : TimeSpan.Zero;

        var animation = new DoubleAnimation(targetValue, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        transform.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateZoomHint()
    {
        if (_scaleTextBorder == null || AlwaysHideZoomValueHint || DisableAnimation)
            return;

        var duration = TimeSpan.FromMilliseconds(ZoomValueHintAnimationDurationMs);
        var fadeOutAnimation = new DoubleAnimation(1.0, 0.0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        _scaleTextBorder.BeginAnimation(UIElement.OpacityProperty, fadeOutAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Zoom Operations

    private void ZoomTo(double ratio, bool useAnimation = true)
    {
        _useAnimationOnSetZoomRatio = useAnimation;
        SetCurrentValue(ZoomRatioProperty, ratio);
    }

    private double PeekZoomInRatio() => ZoomRatio * ZoomFactor;

    private double PeekZoomOutRatio() => ZoomRatio / ZoomFactor;

    public void ZoomIn()
    {
        ZoomTo(PeekZoomInRatio());
    }

    public void ZoomOut()
    {
        ZoomTo(PeekZoomOutRatio());
    }

    #endregion

    #region Pan Operations

    private void MoveToInternal(double offsetX, double offsetY, bool useAnimation = true)
    {
        if (double.IsNaN(offsetX) || double.IsInfinity(offsetX))
            offsetX = _translateXCurrent;
        if (double.IsNaN(offsetY) || double.IsInfinity(offsetY))
            offsetY = _translateYCurrent;

        AnimateTransform(ZoomRatio, ZoomRatio, offsetX, offsetY, useAnimation, false);
    }

    private void MoveInternal(double deltaX, double deltaY, bool useAnimation = true)
    {
        MoveToInternal(_translateXCurrent + deltaX, _translateYCurrent + deltaY, useAnimation);
    }

    public void MoveTo(double offsetX, double offsetY)
    {
        MoveToInternal(offsetX, offsetY);
    }

    public void Left(double delta) => MoveInternal(-delta, 0);

    public void Right(double delta) => MoveInternal(delta, 0);

    public void Up(double delta) => MoveInternal(0, -delta);

    public void Down(double delta) => MoveInternal(0, delta);

    #endregion

    #region Reset Operations

    public void Reset()
    {
        Focus();
        ResetSelection();
        _image?.RenderTransformOrigin = new Point(TranslateOriginXDefault, TranslateOriginYDefault);
        ZoomTo(ZoomRatioDefault);
        MoveTo(TranslateXDefault, TranslateYDefault);
    }

    public void ResetActualSize()
    {
        ResetSelection();

        if (_viewbox == null || _imageContainer == null || _imageSize.Width == 0 || _imageSize.Height == 0)
            return;

        var uniformScale = CalculateUniformScale();
        if (uniformScale > 0)
        {
            var newZoomRatio = Clamp(1.0 / uniformScale, MinZoomRatio, MaxZoomRatio);
            ZoomTo(newZoomRatio);
        }

        MoveTo(TranslateXDefault, TranslateYDefault);
    }

    private double CalculateUniformScale()
    {
        var scaleX = _viewbox!.ActualWidth / _imageSize.Width;
        var scaleY = _viewbox.ActualHeight / _imageSize.Height;
        return Math.Min(scaleX, scaleY);
    }

    #endregion

    #region Text Selection - Highlight

    private void UpdateSelectionHighlight()
    {
        if (_interactionCanvas == null || OcrWords == null || OcrWords.Count == 0)
            return;

        _interactionCanvas.Children.Clear();

        if (_selectionStartIndex == null || _selectionEndIndex == null)
        {
            UpdateSelectedText();
            return;
        }

        var selectionStart = Math.Min(_selectionStartIndex.Value, _selectionEndIndex.Value);
        var selectionEnd = Math.Max(_selectionStartIndex.Value, _selectionEndIndex.Value);

        _highlightBrush ??= new SolidColorBrush(Colors.DodgerBlue) { Opacity = HighlightOpacity };

        var highlightGeometry = new GeometryGroup();
        foreach (var word in OcrWords)
        {
            if (IsWordInSelection(word, selectionStart, selectionEnd) && IsSelectableWord(word))
            {
                highlightGeometry.Children.Add(new RectangleGeometry(word.BoundingBox));
            }
        }

        if (highlightGeometry.Children.Count > 0)
        {
            _interactionCanvas.Children.Add(new Path
            {
                Fill = _highlightBrush,
                Data = highlightGeometry
            });
        }

        UpdateSelectedText();
    }

    private bool IsWordInSelection(OcrWord word, int selectionStart, int selectionEnd)
    {
        var wordStart = word.StartIndexInFullText;
        var wordEnd = word.StartIndexInFullText + word.Text.Length - 1;
        return wordStart <= selectionEnd && wordEnd >= selectionStart;
    }

    private void UpdateSelectedText()
    {
        if (_selectionStartIndex == null || _selectionEndIndex == null || OcrWords == null || OcrWords.Count == 0)
        {
            SelectedText = string.Empty;
            return;
        }

        var start = Math.Min(_selectionStartIndex.Value, _selectionEndIndex.Value);
        var end = Math.Max(_selectionStartIndex.Value, _selectionEndIndex.Value);
        var fullText = GetFullText();

        if (start < fullText.Length)
        {
            var length = Math.Min(end + 1, fullText.Length) - start;
            SelectedText = fullText.Substring(start, length);
        }
        else
        {
            SelectedText = string.Empty;
        }
    }

    #endregion

    #region Text Selection - Character Index

    private int? GetCharacterIndexAtPoint(Point point)
    {
        var wordAtPoint = FindWordAtPoint(point);
        if (wordAtPoint != null)
            return CalculateCharacterIndexInWord(wordAtPoint, point);

        // Find nearest word
        var nearestWord = FindNearestWord(point);
        if (nearestWord == null)
            return 0;

        return CalculateCharacterIndexNearWord(nearestWord, point);
    }

    private OcrWord? FindWordAtPoint(Point point)
    {
        if (OcrWords == null || OcrWords.Count == 0)
            return null;

        foreach (var word in OcrWords)
        {
            if (IsSelectableWord(word) && word.BoundingBox.Contains(point))
                return word;
        }

        return null;
    }

    private int CalculateCharacterIndexInWord(OcrWord word, Point point)
    {
        var relativeX = (point.X - word.BoundingBox.Left) / word.BoundingBox.Width;
        var charOffset = (int)(relativeX * word.Text.Length);
        charOffset = Math.Clamp(charOffset, 0, word.Text.Length - 1);
        return word.StartIndexInFullText + charOffset;
    }

    private OcrWord? FindNearestWord(Point point)
    {
        OcrWord? nearestWord = null;
        var minDistance = double.MaxValue;

        foreach (var word in OcrWords!)
        {
            if (!IsSelectableWord(word))
                continue;

            var distance = GetDistanceToRect(point, word.BoundingBox);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestWord = word;
            }
        }

        return nearestWord;
    }

    private int CalculateCharacterIndexNearWord(OcrWord word, Point point)
    {
        var bbox = word.BoundingBox;

        if (point.X < bbox.Left)
        {
            return word.StartIndexInFullText;
        }
        else if (point.X > bbox.Right)
        {
            return word.StartIndexInFullText + word.Text.Length - 1;
        }
        else
        {
            var relativeX = (point.X - bbox.Left) / bbox.Width;
            var charOffset = (int)Math.Round(relativeX * word.Text.Length);
            charOffset = Math.Clamp(charOffset, 0, word.Text.Length - 1);
            return word.StartIndexInFullText + charOffset;
        }
    }

    private static double GetDistanceToRect(Point point, Rect rect)
    {
        var closestX = Math.Clamp(point.X, rect.Left, rect.Right);
        var closestY = Math.Clamp(point.Y, rect.Top, rect.Bottom);

        var dx = point.X - closestX;
        var dy = point.Y - closestY;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    #endregion

    #region Text Selection - Commands

    private void ResetSelection()
    {
        _isSelecting = false;
        _selectionStartIndex = null;
        _selectionEndIndex = null;
        _interactionCanvas?.Children.Clear();
        SelectedText = string.Empty;
    }

    public void SelectAllText()
    {
        if (OcrWords == null || OcrWords.Count == 0)
            return;

        var fullText = GetFullText();
        _selectionStartIndex = 0;
        _selectionEndIndex = fullText.Length - 1;

        UpdateSelectionHighlight();
    }

    internal string GetFullText()
    {
        if (_fullTextCache != null)
            return _fullTextCache;

        if (OcrWords == null || OcrWords.Count == 0)
            return string.Empty;

        _fullTextCache = string.Concat(OcrWords.Select(w => w.Text));
        return _fullTextCache;
    }

    #endregion

    #region Utility Methods

    private static bool IsPrimaryButton(MouseButtonEventArgs e) => e.ChangedButton == MouseButton.Left;

    private static bool IsSelectableWord(OcrWord word) =>
        !word.BoundingBox.IsEmpty && word.BoundingBox.Width > 0 && word.BoundingBox.Height > 0;

    private static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static bool AreClose(double value1, double value2) => Math.Abs(value1 - value2) < 1e-10;

    #endregion
}
