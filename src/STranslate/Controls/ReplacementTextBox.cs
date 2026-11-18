using STranslate.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace STranslate.Controls;

/// <summary>保留原生编辑、选区和撤销，在原文上标记实际请求的最终差异。</summary>
public class ReplacementTextBox : TextBox
{
    public static readonly DependencyProperty RequestTextProperty = DependencyProperty.Register(
        nameof(RequestText), typeof(string), typeof(ReplacementTextBox),
        new PropertyMetadata(null, (d, _) => ((ReplacementTextBox)d).RefreshChanges()));

    public string? RequestText
    {
        get => (string?)GetValue(RequestTextProperty);
        set => SetValue(RequestTextProperty, value);
    }

    public static readonly DependencyProperty RequestsProperty = DependencyProperty.Register(
        nameof(Requests), typeof(IReadOnlyList<TextReplacementRequest>), typeof(ReplacementTextBox),
        new PropertyMetadata(null, (d, _) => ((ReplacementTextBox)d).RefreshChanges()));

    public IReadOnlyList<TextReplacementRequest>? Requests
    {
        get => (IReadOnlyList<TextReplacementRequest>?)GetValue(RequestsProperty);
        set => SetValue(RequestsProperty, value);
    }

    private string? CopyableRequest => Requests is { Count: > 0 } requests
        ? requests.All(request => request.RequestText == requests[0].RequestText) ? requests[0].RequestText : null
        : RequestText;

    private CancellationTokenSource? _comparison;
    private ChangeAdorner? _adorner;
    private AdornerLayer? _layer;
    private readonly ToolTip _changeToolTip = new() { MaxWidth = 440 };
    private readonly DispatcherTimer _hoverTimer;
    private TextReplacementChange? _hoveredChange;
    private Point? _hoverPosition;
    private bool _isSelecting;
    private bool _hoverRefreshQueued;
    private Window? _ownerWindow;
    private ContextMenu? _extendedMenu;
    private MenuItem? _copyRequestItem;
    private Separator? _requestSeparator;
    internal IReadOnlyList<TextReplacementChange> Changes { get; private set; } = [];

    public ReplacementTextBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        SetResourceReference(ForegroundProperty, "TextControlForeground");
        SetResourceReference(CaretBrushProperty, "TextControlForeground");
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _hoverTimer.Tick += OnHoverTimerTick;
        _changeToolTip.PlacementTarget = this;
        _changeToolTip.Placement = PlacementMode.Relative;
        _changeToolTip.StaysOpen = true;
        _changeToolTip.Focusable = false;
        _changeToolTip.IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            _ownerWindow = Window.GetWindow(this);
            if (_ownerWindow is not null) _ownerWindow.Deactivated += OnOwnerWindowDeactivated;
            AttachAdorner();
            RefreshChanges();
        };
        Unloaded += (_, _) =>
        {
            CancelComparison();
            if (_adorner is not null) _layer?.Remove(_adorner);
            _adorner = null;
            _layer = null;
            UpdateHover(null);
            if (_ownerWindow is not null) _ownerWindow.Deactivated -= OnOwnerWindowDeactivated;
            _ownerWindow = null;
            DetachContextMenu();
        };
        // TextBox 的编辑器会处理获得焦点后的鼠标事件；这里只观察事件，不干预选区和输入。
        AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(OnHoverMouseMove), handledEventsToo: true);
        AddHandler(Mouse.MouseEnterEvent, new MouseEventHandler(OnHoverMouseMove), handledEventsToo: true);
        AddHandler(Mouse.MouseLeaveEvent, new MouseEventHandler((_, _) => UpdateHover(null)), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => UpdateHover(e.GetPosition(this), selecting: true)), handledEventsToo: true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) => UpdateHover(e.GetPosition(this), HasPressedMouseButton(e))), handledEventsToo: true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, _) => CloseHover()), handledEventsToo: true);
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            CloseHover();
            _adorner?.InvalidateVisual();
        }));
        IsVisibleChanged += (_, _) => { if (!IsVisible) UpdateHover(null); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) UpdateHover(null); };
        SizeChanged += (_, _) => _adorner?.InvalidateVisual();
        ToolTip = _changeToolTip;
        ToolTipService.SetInitialShowDelay(this, 100);
        // 按高亮片段控制显示，不依赖整个 TextBox 的一次性 ToolTipOpening 和自动关闭状态。
        ToolTipService.SetIsEnabled(this, false);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property == FontWeightProperty ||
            e.Property == FlowDirectionProperty || e.Property == PaddingProperty || e.Property == ForegroundProperty)
            _adorner?.InvalidateVisual();
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        RefreshChanges();
    }

    private void CancelComparison()
    {
        _comparison?.Cancel();
        _comparison?.Dispose();
        _comparison = null;
    }

    private async void RefreshChanges()
    {
        CancelComparison();
        Changes = [];
        CloseHover();
        UpdateRequestCopyMenu();
        _adorner?.InvalidateVisual();
        if (!IsLoaded) return;
        var request = RequestText;
        var requests = Requests;
        if (requests is not { Count: > 0 } && (request is null || request == Text)) return;
        var snapshot = requests?.ToArray();
        var original = Text;
        var comparison = _comparison = new CancellationTokenSource();
        var token = comparison.Token;
        try
        {
            var changes = await Task.Run(() => snapshot is { Length: > 0 }
                ? TextReplacementDiff.CreateForRequests(original, snapshot, token)
                : TextReplacementDiff.Create(original, request!, token), token);
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || Text != original || RequestText != request || !ReferenceEquals(Requests, requests)) return;
                Changes = changes;
                AttachAdorner();
                _adorner?.InvalidateVisual();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void AttachAdorner()
    {
        if (_adorner is not null) return;
        _layer = AdornerLayer.GetAdornerLayer(this);
        if (_layer is null) return;
        _adorner = new ChangeAdorner(this);
        _layer.Add(_adorner);
    }

    private static bool HasPressedMouseButton(MouseEventArgs e) =>
        e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed ||
        e.RightButton == MouseButtonState.Pressed;

    private void OnHoverMouseMove(object sender, MouseEventArgs e) =>
        UpdateHover(e.GetPosition(this), HasPressedMouseButton(e));

    private void OnOwnerWindowDeactivated(object? sender, EventArgs e) => UpdateHover(null);

    internal void UpdateHover(Point? position, bool selecting = false)
    {
        _hoverPosition = position;
        _isSelecting = selecting;
        var change = position is { } point ? FindHoverChange(point) : null;
        if (change is null)
        {
            CloseHover();
            return;
        }
        if (change == _hoveredChange)
        {
            if (!_changeToolTip.IsOpen && !_hoverTimer.IsEnabled) _hoverTimer.Start();
            return;
        }
        _hoverTimer.Stop();
        _hoveredChange = change;
        UpdateToolTip(change);
        if (_changeToolTip.IsOpen) PositionToolTip(position!.Value);
        else _hoverTimer.Start();
    }

    private TextReplacementChange? FindHoverChange(Point point)
    {
        if (!IsLoaded || !IsEnabled || !IsVisible || _isSelecting || ContextMenu?.IsOpen == true ||
            !new Rect(new Point(), RenderSize).Contains(point)) return null;
        var change = _adorner?.HitTestChange(point);
        return change is not null && Changes.Contains(change) ? change : null;
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverPosition is not { } point || FindHoverChange(point) is not { } change)
        {
            CloseHover();
            return;
        }
        if (change != _hoveredChange)
        {
            UpdateHover(point, _isSelecting);
            return;
        }
        PositionToolTip(point);
        _changeToolTip.IsOpen = true;
    }

    private void PositionToolTip(Point point)
    {
        _changeToolTip.HorizontalOffset = point.X;
        _changeToolTip.VerticalOffset = point.Y + 18;
    }

    private void CloseHover()
    {
        _hoverTimer?.Stop();
        _hoveredChange = null;
        _changeToolTip.IsOpen = false;
    }

    private void RefreshHoverAfterRender()
    {
        if (_hoverRefreshQueued || _hoverPosition is null && !IsMouseOver) return;
        _hoverRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _hoverRefreshQueued = false;
            if (!IsLoaded) return;
            if (IsMouseOver) UpdateHover(Mouse.GetPosition(this), Mouse.LeftButton == MouseButtonState.Pressed ||
                Mouse.MiddleButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed);
            else if (_hoverPosition is { } point) UpdateHover(point, _isSelecting);
        }));
    }

    private void UpdateToolTip(TextReplacementChange change)
    {
        var groups = change.Variants?.GroupBy(variant => variant.Replacement, StringComparer.Ordinal).ToArray();
        if (groups is { Length: > 1 })
        {
            var panel = new StackPanel();
            foreach (var group in groups)
            {
                var row = new TextBlock { TextWrapping = TextWrapping.Wrap };
                row.Inlines.Add(new Run(string.Join(" / ", group.Select(variant => variant.ServiceNames).Distinct()) + ": ")
                    { FontWeight = FontWeights.SemiBold });
                row.Inlines.Add(new Run(ReplacementDisplay(group.Key, !group.First().IsChanged)));
                panel.Children.Add(row);
            }
            _changeToolTip.Content = panel;
        }
        else
        {
            var variant = change.Variants?.FirstOrDefault();
            _changeToolTip.Content = new TextBlock
            {
                Text = ReplacementDisplay(variant?.Replacement ?? change.Replacement, variant?.IsChanged == false),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private string ReplacementDisplay(string? replacement, bool unchanged = false) => replacement switch
    {
        null => TryFindResource("General_TextPreprocessingRequestUnavailable") as string ?? "Request not recorded",
        "" when unchanged => TryFindResource("General_TextPreprocessingUnchanged") as string ?? "Unchanged",
        "" => TryFindResource("General_TextPreprocessingDeleted") as string ?? "Deleted",
        _ => replacement.Length > 2048 ? replacement[..2048] + "…" : replacement
    };

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        CloseHover();
        base.OnContextMenuOpening(e);
        PrepareRequestContextMenu();
    }

    internal void PrepareRequestContextMenu()
    {
        if (ContextMenu is null)
        {
            var menu = new ContextMenu();
            foreach (var command in new[] { ApplicationCommands.Undo, ApplicationCommands.Cut, ApplicationCommands.Copy,
                         ApplicationCommands.Paste, ApplicationCommands.Delete, ApplicationCommands.SelectAll })
                menu.Items.Add(new MenuItem { Header = command.Text, Command = command, CommandTarget = this });
            ContextMenu = menu;
        }
        if (_extendedMenu == ContextMenu && _copyRequestItem is not null)
        {
            UpdateRequestCopyMenu();
            return;
        }
        DetachContextMenu();
        _extendedMenu = ContextMenu;
        _requestSeparator = new Separator();
        _copyRequestItem = new MenuItem();
        _copyRequestItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "General_TextPreprocessingCopyRequest");
        _copyRequestItem.Click += (_, _) => { if (CopyableRequest is { } text) Clipboard.SetText(text); };
        _extendedMenu.Items.Add(_requestSeparator);
        _extendedMenu.Items.Add(_copyRequestItem);
        _extendedMenu.Closed += OnContextMenuClosed;
        UpdateRequestCopyMenu();
    }

    private void UpdateRequestCopyMenu()
    {
        if (_copyRequestItem is null) return;
        _copyRequestItem.Items.Clear();
        if (CopyableRequest is not null)
        {
            _copyRequestItem.IsEnabled = true;
            return;
        }
        if (Requests is { Count: > 0 } requests)
        {
            foreach (var request in requests)
            {
                var item = new MenuItem { Header = request.ServiceNames, IsEnabled = request.RequestText is not null };
                item.Click += (_, _) => { if (request.RequestText is { } text) Clipboard.SetText(text); };
                _copyRequestItem.Items.Add(item);
            }
            _copyRequestItem.IsEnabled = requests.Any(request => request.RequestText is not null);
        }
        else _copyRequestItem.IsEnabled = false;
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        DetachContextMenu();
        if (IsMouseOver) UpdateHover(Mouse.GetPosition(this));
    }

    private void DetachContextMenu()
    {
        if (_extendedMenu is null) return;
        _extendedMenu.Closed -= OnContextMenuClosed;
        _extendedMenu.Items.Remove(_copyRequestItem);
        _extendedMenu.Items.Remove(_requestSeparator);
        _extendedMenu = null;
        _copyRequestItem = null;
        _requestSeparator = null;
    }

    private sealed class ChangeAdorner : Adorner
    {
        private readonly ReplacementTextBox owner;
        internal ChangeAdorner(ReplacementTextBox owner) : base(owner)
        {
            this.owner = owner;
            IsHitTestVisible = false;
        }
        private readonly List<(Rect Bounds, TextReplacementChange Change)> _regions = [];
        private readonly Brush _fill = new SolidColorBrush(Color.FromArgb(50, 80, 145, 220));

        public TextReplacementChange? HitTestChange(Point point)
        {
            foreach (var region in _regions)
            {
                var bounds = region.Bounds;
                bounds.Inflate(2, 2);
                if (bounds.Contains(point)) return region.Change;
            }
            return null;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            _regions.Clear();
            var changes = owner.Changes;
            if (changes.Count == 0) return;

            var lineCount = owner.LineCount;
            if (lineCount <= 0) return;
            var firstLine = owner.GetFirstVisibleLineIndex();
            var lastLine = owner.GetLastVisibleLineIndex();
            if (firstLine < 0 || firstLine >= lineCount || lastLine < firstLine || lastLine >= lineCount) return;

            var first = owner.GetCharacterIndexFromLineIndex(firstLine);
            var last = owner.GetCharacterIndexFromLineIndex(lastLine) + owner.GetLineLength(lastLine);
            var clip = new Rect(new Point(), owner.RenderSize);
            drawingContext.PushClip(new RectangleGeometry(clip));
            var pen = new Pen(owner.Foreground, 1);
            foreach (var change in changes)
            {
                if (change.Start > last + 1 || change.Start + change.Length < first) continue;
                if (change.Length == 0)
                {
                    var caret = owner.GetRectFromCharacterIndex(change.Start);
                    if (caret.IsEmpty) continue;
                    var bounds = new Rect(caret.X - 3, caret.Y, 6, Math.Max(4, caret.Height));
                    drawingContext.DrawLine(pen, new(caret.X, caret.Y), new(caret.X, caret.Bottom));
                    drawingContext.DrawLine(pen, new(caret.X - 3, caret.Y + 3), new(caret.X + 3, caret.Y + 3));
                    _regions.Add((bounds, change));
                    continue;
                }
                Rect line = Rect.Empty;
                for (var index = Math.Max(first, change.Start); index < Math.Min(last + 1, change.Start + change.Length); index++)
                {
                    var leading = owner.GetRectFromCharacterIndex(index);
                    var trailing = owner.GetRectFromCharacterIndex(index, true);
                    if (leading.IsEmpty || trailing.IsEmpty) continue;
                    var bounds = new Rect(Math.Min(leading.X, trailing.X), leading.Y, Math.Max(3, Math.Abs(trailing.X - leading.X)), leading.Height);
                    if (!line.IsEmpty && Math.Abs(line.Top - bounds.Top) > 1) { Draw(line, change); line = Rect.Empty; }
                    line.Union(bounds);
                }
                if (!line.IsEmpty) Draw(line, change);
            }
            drawingContext.Pop();
            owner.RefreshHoverAfterRender();

            void Draw(Rect bounds, TextReplacementChange change)
            {
                drawingContext.DrawRectangle(_fill, null, bounds);
                drawingContext.DrawLine(pen, new(bounds.Left, bounds.Bottom - 1), new(bounds.Right, bounds.Bottom - 1));
                if (change.IsDeletion)
                    drawingContext.DrawLine(pen, new(bounds.Left, bounds.Top + bounds.Height / 2), new(bounds.Right, bounds.Top + bounds.Height / 2));
                _regions.Add((bounds, change));
            }
        }
    }
}
