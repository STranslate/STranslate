using STranslate.Helpers;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace STranslate.Views;

/// <summary>
/// 灵动岛窗口：以屏幕顶部居中的胶囊形态展示翻译结果，
/// </summary>
public partial class DynamicIslandWindow : IDisposable
{
    /// <summary>
    /// 用户双击灵动岛时触发（由 ViewModel 展开原翻译窗口）。
    /// </summary>
    public event EventHandler? IslandDoubleClicked;

    #region 可配置外观（由 ViewModel 从 Settings 同步）

    public TimeSpan AutoHideDuration { get; set; } = TimeSpan.FromSeconds(10);
    public double IslandMinWidth { get; set; } = 180;
    public double IslandMaxWidth { get; set; } = 520;
    public double IslandHeight { get; set; } = 54;
    public double IslandTopMargin { get; set; } = 16;
    public double IslandFontSize { get; set; } = 15;

    #endregion

    private const double ShadowPadding = 12;
    private const string LoadingText = "翻译中…";
    private const double LoadingOpacity = 0.55;

    private DispatcherTimer? _autoHideTimer;
    private Storyboard? _activeStoryboard;
    private bool _hasResult;
    private bool _isHovering;
    private bool _disposed;

    public DynamicIslandWindow()
    {
        InitializeComponent();
    }

    public bool IsIslandShowing => IsVisible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResultText.FontSize = IslandFontSize;
    }

    /// <summary>
    /// 显示灵动岛。<paramref name="text"/> 为空时进入"翻译中"状态。
    /// </summary>
    public void ShowIsland(string? text = null)
    {
        if (_disposed) return;

        _autoHideTimer?.Stop();
        StopActiveAnimation();

        ResultText.FontSize = IslandFontSize;
        SetContent(text);
        ApplySizeAndPosition(text);

        // 首帧隐藏，随后展开动画接管
        PillScale.ScaleX = 0.3;
        PillScale.ScaleY = 0.3;
        Pill.Opacity = 0;
        ResultText.Opacity = 0;

        Show();
        // 窗口句柄建立后 DPI 才准确，二次校正位置
        PositionOnScreen();

        PlayShowAnimation();
        if (_hasResult && !_isHovering)
            StartAutoHideTimer();
    }

    /// <summary>
    /// 更新灵动岛上的翻译结果文本，并重置自动隐藏计时。
    /// </summary>
    public void UpdateResult(string text)
    {
        if (_disposed || !IsVisible) return;

        StopActiveAnimation();
        SetContent(text);
        ApplySizeAndPosition(text);
        PlayContentPopAnimation();
        if (_hasResult && !_isHovering)
            StartAutoHideTimer();
    }

    /// <summary>
    /// 收起灵动岛（播放收起动画后隐藏）。
    /// </summary>
    public void HideIsland()
    {
        if (_disposed)
            return;

        _autoHideTimer?.Stop();

        if (!IsVisible)
        {
            _hasResult = false;
            return;
        }

        PlayHideAnimation();
    }

    #region 尺寸与位置

    private void ApplySizeAndPosition(string? text)
    {
        var textWidth = MeasureTextWidth(text);
        // 左侧图标 + 内边距 + 文本
        var desiredWidth = Math.Max(IslandMinWidth, Math.Min(IslandMaxWidth, textWidth + 96));
        Width = desiredWidth + ShadowPadding * 2;
        Height = IslandHeight + ShadowPadding * 2;
        Pill.CornerRadius = new CornerRadius(IslandHeight / 2);
        Pill.Height = IslandHeight;

        PositionOnScreen();
    }

    private double MeasureTextWidth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 120;

        var typeface = new Typeface(
            ResultText.FontFamily,
            ResultText.FontStyle,
            ResultText.FontWeight,
            ResultText.FontStretch);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            ResultText.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void PositionOnScreen()
    {
        try
        {
            var monitor = MonitorInfo.GetCursorDisplayMonitor();
            var topLeft = Win32Helper.TransformPixelsToDIP(this, monitor.WorkingArea.X, monitor.WorkingArea.Y);
            var bottomRight = Win32Helper.TransformPixelsToDIP(
                this,
                monitor.WorkingArea.X + monitor.WorkingArea.Width,
                monitor.WorkingArea.Y + monitor.WorkingArea.Height);

            var workWidth = bottomRight.X - topLeft.X;
            Left = topLeft.X + (workWidth - Width) / 2;
            Top = topLeft.Y + IslandTopMargin;
        }
        catch
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + IslandTopMargin;
        }
    }

    private void SetContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _hasResult = false;
            ResultText.Text = LoadingText;
            ResultText.Opacity = LoadingOpacity;
        }
        else
        {
            _hasResult = true;
            ResultText.Text = text;
            ResultText.Opacity = 1.0;
            Pill.ToolTip = text;
        }
    }

    #endregion

    #region 动画

    private void StopActiveAnimation()
    {
        if (_activeStoryboard != null)
        {
            _activeStoryboard.Stop(this);
            _activeStoryboard = null;
        }
    }

    /// <summary>
    /// 所有动画使用默认 FillBehavior.Stop，基值设为最终状态，避免中途停止时闪回。
    /// </summary>
    private void PlayShowAnimation()
    {
        var targetContentOpacity = _hasResult ? 1.0 : LoadingOpacity;

        // 基值 = 最终状态
        Pill.Opacity = 1;
        PillScale.ScaleX = 1;
        PillScale.ScaleY = 1;
        ResultText.Opacity = targetContentOpacity;

        var sb = new Storyboard();

        // 横向展开（轻微回弹）
        var scaleX = new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        // 纵向展开
        var scaleY = new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        // 胶囊淡入
        var opacity = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, Pill);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(opacity);

        // 内容延迟淡入
        var contentOpacity = new DoubleAnimation(0, targetContentOpacity, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            BeginTime = TimeSpan.FromMilliseconds(120)
        };
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        RunStoryboard(sb);
    }

    /// <summary>
    /// 结果更新时的小弹动动画。
    /// </summary>
    private void PlayContentPopAnimation()
    {
        // 基值 = 最终状态
        PillScale.ScaleX = 1;
        PillScale.ScaleY = 1;
        ResultText.Opacity = _hasResult ? 1.0 : LoadingOpacity;

        var sb = new Storyboard();

        var scaleX = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        var contentOpacity = new DoubleAnimation(0.4, _hasResult ? 1.0 : LoadingOpacity, new Duration(TimeSpan.FromMilliseconds(240)));
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        RunStoryboard(sb);
    }

    /// <summary>
    /// 收起动画：内容淡出，胶囊收缩后隐藏。
    /// </summary>
    private void PlayHideAnimation()
    {
        // 先取当前（可能仍在动画中的）值作为动画起点
        var currentContentOpacity = ResultText.Opacity;
        var currentScaleX = PillScale.ScaleX;
        var currentScaleY = PillScale.ScaleY;
        var currentPillOpacity = Pill.Opacity;

        // 基值 = 收起后的隐藏状态，动画结束后隐藏窗口
        PillScale.ScaleX = 0.3;
        PillScale.ScaleY = 0.3;
        Pill.Opacity = 0;
        ResultText.Opacity = 0;

        var sb = new Storyboard();

        var contentOpacity = new DoubleAnimation(currentContentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(120)));
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        var scaleX = new DoubleAnimation(currentScaleX, 0.3, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation(currentScaleY, 0.3, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        var opacity = new DoubleAnimation(currentPillOpacity, 0, new Duration(TimeSpan.FromMilliseconds(280)));
        Storyboard.SetTarget(opacity, Pill);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(opacity);

        _activeStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            _activeStoryboard = null;
            _hasResult = false;
            Hide();
        };
        sb.Begin(this, true);
    }

    private void RunStoryboard(Storyboard sb)
    {
        _activeStoryboard = sb;
        sb.Completed += (_, _) => _activeStoryboard = null;
        sb.Begin(this, true);
    }

    #endregion

    #region 自动隐藏

    private void StartAutoHideTimer()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDuration };
        _autoHideTimer.Tick += OnAutoHideTick;
        _autoHideTimer.Start();
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer?.Stop();
        if (_isHovering)
            return;
        HideIsland();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        _autoHideTimer?.Stop();
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        if (IsVisible && _hasResult)
            StartAutoHideTimer();
    }

    private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        e.Handled = true;
        IslandDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoHideTimer?.Stop();
        StopActiveAnimation();
        Close();
    }

    #endregion
}
