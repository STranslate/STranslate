using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Services;
using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using STranslate.Views;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitmap = System.Drawing.Bitmap;

namespace STranslate.ViewModels;

public partial class ImageTranslateWindowViewModel : ObservableObject, IDisposable
{
    #region Constructor & DI

    public ImageTranslateWindowViewModel(
        ILogger<ImageTranslateWindowViewModel> logger,
        Settings settings,
        HotkeySettings hotkeySettings,
        DataProvider dataProvider,
        MainWindowViewModel mainWindowViewModel,
        OcrService ocrService,
        TranslateService translateService,
        TtsService ttsService,
        Internationalization i18n,
        ISnackbar snackbar,
        INotification notification)
    {
        _logger = logger;
        Settings = settings;
        HotkeySettings = hotkeySettings;
        DataProvider = dataProvider;
        _mainWindowViewModel = mainWindowViewModel;
        _ocrService = ocrService;
        _translateService = translateService;
        _ttsService = ttsService;
        _i18n = i18n;
        _snackbar = snackbar;
        _notification = notification;

        OcrEngines = _ocrService.Services;
        RefreshSelectedOcrEngine();
        _transCollectionView = new() { Source = _translateService.Services };
        _transCollectionView.Filter += OnTransFilter;
        SelectedTranslateEngine = _translateService.ImageTranslateService;

        _ocrService.Services.CollectionChanged += OnOcrServicesCollectionChanged;
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged += OnOcrEnginePropertyChanged;
        }
        _ocrService.PropertyChanged += OnOcrServicePropertyChanged;

        // 监听图片翻译服务切换
        _translateService.PropertyChanged += OnTranServicePropertyChanged;

        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private readonly ILogger<ImageTranslateWindowViewModel> _logger;

    #endregion

    #region Properties

    public Settings Settings { get; }
    public HotkeySettings HotkeySettings { get; }
    public DataProvider DataProvider { get; }

    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OcrService _ocrService;
    private readonly TranslateService _translateService;
    private readonly TtsService _ttsService;
    private readonly Internationalization _i18n;
    private readonly ISnackbar _snackbar;
    private readonly INotification _notification;
    private const double WidthMultiplier = 2;
    private const double WidthAdjustment = 12;

    [ObservableProperty]
    public partial BitmapSource? DisplayImage { get; set; }

    [ObservableProperty]
    public partial bool IsShowingFitToWindow { get; set; } = false;

    [ObservableProperty]
    public partial bool IsExecuting { get; set; } = false;

    [ObservableProperty]
    public partial string ProcessRingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoLocationInfoVisible { get; set; } = false;

    /// <summary>
    /// 翻译结果
    /// </summary>
    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    /// <summary>
    /// 原始图像
    /// </summary>
    private BitmapSource? _sourceImage;

    /// <summary>
    /// 标注图像（显示识别边框）
    /// </summary>
    private BitmapSource? _annotatedImage;

    /// <summary>
    /// 结果图像（显示翻译文本）
    /// </summary>
    private BitmapSource? _resultImage;

    private OcrResult? _lastOcrResult;

    [ObservableProperty]
    public partial ObservableCollection<Service> OcrEngines { get; set; }

    [ObservableProperty]
    public partial Service? SelectedOcrEngine { get; set; } = null;

    // 翻译服务过滤掉词典服务
    private readonly CollectionViewSource _transCollectionView;
    public ICollectionView TransCollectionView => _transCollectionView.View;

    [ObservableProperty]
    public partial Service? SelectedTranslateEngine { get; set; } = null;

    #endregion

    #region Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExecuteAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        if (IsExecuting) return;

        IsExecuting = true;
        ProcessRingText = _i18n.GetTranslation("RecognizingImageText");
        try
        {
            Clear();
            _sourceImage = Utilities.ToBitmapImage(bitmap, Settings.GetImageFormat());
            DisplayImage = _sourceImage;

            var ocrSvc = _ocrService.GetImageTranslateOcrSvcOrDefault();
            if (ocrSvc == null)
            {
                Helper.PromptConfigureService(
                    _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundTitle"),
                    _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"),
                    nameof(OcrPage));
                return;
            }

            var data = Utilities.ToBytes(bitmap, Settings.GetImageFormat());
            _lastOcrResult = await ocrSvc.RecognizeAsync(new OcrRequest(data, Settings.OcrLanguage), cancellationToken);

            if (!_lastOcrResult.IsSuccess || string.IsNullOrEmpty(_lastOcrResult.Text))
            {
                _snackbar.ShowError(_i18n.GetTranslation("OcrFailed"));
                return;
            }


            if (Settings.CopyAfterOcr)
                ClipboardHelper.SetText(_lastOcrResult.Text);

            IsNoLocationInfoVisible = !Utilities.HasBoxPoints(_lastOcrResult);

            // 生成原始OCR标注图像（显示识别边框）
            //var originalAnnotatedImage = GenerateAnnotatedImage(_lastOcrResult, _sourceImage);

            // 版面分析
            ApplyLayoutAnalysis(_lastOcrResult);

            // 生成版面分析后的标注图像（显示合并后的边框）
            _annotatedImage = GenerateAnnotatedImage(_lastOcrResult, _sourceImage);

            if (_translateService.ImageTranslateService?.Plugin is not ITranslatePlugin tranSvc)
            {
                _snackbar.ShowWarning(_i18n.GetTranslation("NoTranslateService"));
                return;
            }

            ProcessRingText = _i18n.GetTranslation("TranslatingText");

            await Parallel.ForEachAsync(_lastOcrResult.OcrContents, cancellationToken, async (content, cancellationToken) =>
            {
                var (isSuccess, source, target) = await LanguageDetector
                    .GetLanguageAsync(
                        content.Text,
                        Settings.ImageTranslateSourceLang,
                        Settings.ImageTranslateTargetLang,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!isSuccess)
                {
                    _logger.LogWarning($"Language detection failed for text: {content.Text}");
                    _snackbar.ShowWarning(_i18n.GetTranslation("LanguageDetectionFailed"));
                    return;
                }
                if (string.IsNullOrWhiteSpace(content.Text))
                    return;

                var result = new TranslateResult();
                await tranSvc.TranslateAsync(new TranslateRequest(content.Text, source, target), result, cancellationToken);
                content.Text = result.IsSuccess ? result.Text : content.Text;
            });

            // 生成翻译结果图像（在原图上覆盖翻译文本）
            _resultImage = GenerateTranslatedImage(_lastOcrResult, Utilities.ToBitmapImage(bitmap, Settings.GetImageFormat()));
            Result = _lastOcrResult.Text;

            DisplayImage = Settings.IsImTranShowingAnnotated ? _annotatedImage : _resultImage;
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("ImtransFailed")}\n{ex.Message}");
            _logger.LogError(ex, "Image Translate execution failed");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private async Task ImTransOcrAsync(Window? window)
    {
        window?.Hide();
        await Task.Delay(150);
        await _mainWindowViewModel.ScreenshotTranslateCommand.ExecuteAsync(null);
        window?.Show();
    }

    [RelayCommand]
    private async Task ReExecuteAsync()
    {
        if (_sourceImage == null || IsExecuting) return;
        using var bitmap = Utilities.ToBitmap(_sourceImage, Settings.GetBitmapEncoder());
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await _mainWindowViewModel.OpenSettingsInternalAsync(null);

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(OcrPage));

            if (SelectedOcrEngine != null)
                Ioc.Default.GetRequiredService<OcrViewModel>()
                    .SelectedItem = SelectedOcrEngine;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(TranslatePage));

            if (SelectedTranslateEngine != null)
                Ioc.Default.GetRequiredService<TranslateViewModel>()
                    .SelectedItem = SelectedTranslateEngine;
        }
        else
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(StandalonePage));
    }

    [RelayCommand]
    private void SwitchImage() => Settings.IsImTranShowingAnnotated = !Settings.IsImTranShowingAnnotated;

    [RelayCommand]
    private void ToggleTextControl() => Settings.IsImTranShowingTextControl = !Settings.IsImTranShowingTextControl;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioAsync(string text, CancellationToken cancellationToken)
    {
        var ttsSvc = _ttsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("TtsServiceNotFound"),
                nameof(TtsPage));
            return;
        }

        await ttsSvc.PlayAudioAsync(text, cancellationToken);
    }

    [RelayCommand]
    private void Copy(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
        }
        else
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoCopyContent"));
        }
    }

    [RelayCommand]
    private void RemoveLineBreaks(TextBox textBox) =>
        Utilities.TransformText(textBox, t => t.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "));

    [RelayCommand]
    private void RemoveSpaces(TextBox textBox) =>
        Utilities.TransformText(textBox, t => t.Replace(" ", string.Empty));

    [RelayCommand]
    private void CopyImage()
    {
        if (_sourceImage == null) return;

        Clipboard.SetImage(_sourceImage);
    }

    [RelayCommand]
    private void SaveImage()
    {
        if (_sourceImage is null)
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoImageToSave"));
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = _i18n.GetTranslation("SaveAs"),
            Filter = "PNG Files (*.png)|*.png|JPEG Files (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*",
            FileName = $"{DateTime.Now:yyyyMMddHHmmssfff}",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            AddToRecent = true
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            BitmapEncoder encoder = saveFileDialog.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? new PngBitmapEncoder()
                        : new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_sourceImage));

            using var fs = new FileStream(saveFileDialog.FileName, FileMode.Create);
            encoder.Save(fs);

            _snackbar.ShowSuccess(_i18n.GetTranslation("SaveSuccess"));
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("SaveFailed")}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenClipboardImageAsync()
    {
        var bitmapSource = Clipboard.GetImage();
        if (bitmapSource == null)
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoImageInClipboard"));
            return;
        }

        using var bitmap = Utilities.ToBitmap(bitmapSource, Settings.GetBitmapEncoder());
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task OpenImageFileAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = _i18n.GetTranslation("ImportFromFile"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
            RestoreDirectory = true
        };

        if (openFileDialog.ShowDialog() != true) return;

        await using var fs = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read);
        var bytes = new byte[fs.Length];
        _ = await fs.ReadAsync(bytes);

        using var bitmap = Utilities.ToBitmap(bytes);
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private void ZoomOut(ImageZoom? element) => element?.ZoomOut();

    [RelayCommand]
    private void ZoomIn(ImageZoom? element) => element?.ZoomIn();

    [RelayCommand]
    private void FitToWindowSize(ImageZoom? element)
    {
        IsShowingFitToWindow = false;
        element?.Reset();
    }

    [RelayCommand]
    private void FitToActualSize(ImageZoom? element)
    {
        IsShowingFitToWindow = true;
        element?.ResetActualSize();
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        CancelOperations();
        window.Close();
    }

    #endregion

    #region Event Handlers

    private void OnTransFilter(object sender, FilterEventArgs e) => e.Accepted = e.Item is Service service && service.Plugin is ITranslatePlugin;

    // 添加标志位防止循环更新
    private bool _isUpdatingTranslateEngine = false;

    private void OnTranServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranslateService.ImageTranslateService) ||
            _isUpdatingTranslateEngine)
            return;

        _isUpdatingTranslateEngine = true;
        try
        {
            SelectedTranslateEngine = _translateService.ImageTranslateService;
        }
        finally
        {
            _isUpdatingTranslateEngine = false;
        }
    }

    partial void OnSelectedTranslateEngineChanged(Service? value)
    {
        if (_isUpdatingTranslateEngine) return;

        _isUpdatingTranslateEngine = true;
        try
        {
            // 如果当前选中项被删除以后会自动触发选中临近的一项，关闭该VM绑定界面则没有该影响
            if (value == null)
                _translateService.DeactiveImTran();
            else
                _translateService.ActiveImTran(value);
        }
        finally
        {
            _isUpdatingTranslateEngine = false;
        }
    }

    private bool _isUpdatingOcrEngine = false;

    private void RefreshSelectedOcrEngine()
    {
        _isUpdatingOcrEngine = true;
        try
        {
            SelectedOcrEngine = _ocrService.GetImageTranslateOcrServiceOrDefault();
        }
        finally
        {
            _isUpdatingOcrEngine = false;
        }
    }

    private void OnOcrServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OcrService.ImageTranslateOcrService))
        {
            RefreshSelectedOcrEngine();
        }
    }

    private void OnOcrServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (Service service in e.NewItems)
            {
                service.PropertyChanged += OnOcrEnginePropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (Service service in e.OldItems)
            {
                service.PropertyChanged -= OnOcrEnginePropertyChanged;
            }
        }

        RefreshSelectedOcrEngine();
    }

    private void OnOcrEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Service.IsEnabled) &&
            _ocrService.ImageTranslateOcrService == null)
        {
            RefreshSelectedOcrEngine();
        }
    }

    /// <summary>
    /// 图片翻译窗口中的 OCR 选择映射到独立的图片翻译 OCR 服务
    /// </summary>
    partial void OnSelectedOcrEngineChanged(Service? oldValue, Service? newValue)
    {
        if (_isUpdatingOcrEngine)
            return;

        if (newValue == null)
        {
            _ocrService.DeactiveImTranOcr();
        }
        else
        {
            _ocrService.ActiveImTranOcr(newValue);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.IsImTranShowingTextControl):
                Settings.ImTranWindowWidth = Settings.IsImTranShowingTextControl
                        ? Settings.ImTranWindowWidth * WidthMultiplier - WidthAdjustment
                        : (Settings.ImTranWindowWidth + WidthAdjustment) / WidthMultiplier;
                break;
            case nameof(Settings.IsImTranShowingAnnotated):
                DisplayImage = Settings.IsImTranShowingAnnotated ? _annotatedImage : _resultImage;
                break;
        }
    }

    #endregion

    #region Private Methods

    private void Clear()
    {
        Result = string.Empty;
        _sourceImage = null;
        _annotatedImage = null;
        DisplayImage = null;
        _lastOcrResult = null;
        IsShowingFitToWindow = false;
        IsNoLocationInfoVisible = false;
    }

    #endregion

    #region Image Translation

    /// <summary>
    /// 生成带有翻译文本覆盖的图像
    /// </summary>
    /// <param name="ocrResult">包含翻译后文本的OCR结果</param>
    /// <param name="image">原始图像</param>
    /// <returns>覆盖翻译文本后的图像</returns>
    private BitmapSource GenerateTranslatedImage(OcrResult ocrResult, BitmapSource? image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (ocrResult?.OcrContents == null ||
            ocrResult.OcrContents.All(x => x.BoxPoints?.Count == 0))
        {
            return image;
        }

        // 获取源图像的 DPI 和缩放比例
        double dpiX = image.DpiX > 0 ? image.DpiX : 96;
        double dpiY = image.DpiY > 0 ? image.DpiY : 96;
        double pixelsPerDip = dpiX / 96.0;

        // ---------------------------------------------------------
        // 修复：针对小图进行超采样渲染 (Super-sampling)
        // 如果图片较小，强制放大渲染尺寸，以保证文字矢量绘制的清晰度
        // ---------------------------------------------------------
        double scaleFactor = 1.0;
        double minDimension = Math.Min(image.PixelWidth, image.PixelHeight);
        
        // 如果最小边小于 1000 像素，进行放大，最大放大倍数为 4 倍
        if (minDimension < 1000)
        {
            scaleFactor = Math.Min(4.0, 1000.0 / minDimension);
            // 确保至少放大 2 倍以获得较好的抗锯齿效果
            scaleFactor = Math.Max(scaleFactor, 2.0);
        }

        // 计算渲染目标尺寸
        int renderWidth = (int)(image.PixelWidth * scaleFactor);
        int renderHeight = (int)(image.PixelHeight * scaleFactor);

        var drawingVisual = new DrawingVisual();

        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // 关键修复：应用缩放变换
            // 1. pixelsPerDip: 抵消 WPF DPI 缩放，回归物理像素坐标
            // 2. scaleFactor: 应用超采样缩放
            double totalScale = scaleFactor / pixelsPerDip;
            drawingContext.PushTransform(new ScaleTransform(totalScale, totalScale));

            // 绘制原始图像
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));

            // 为每个文本块创建覆盖层并绘制翻译文本
            foreach (var item in ocrResult.OcrContents)
            {
                if (item.BoxPoints == null || item.BoxPoints.Count == 0 || string.IsNullOrEmpty(item.Text))
                    continue;

                // 传递 pixelsPerDip 以确保文字渲染清晰度
                DrawTranslatedTextOverlay(drawingContext, item, pixelsPerDip);
            }
            
            // 恢复变换
            drawingContext.Pop();
        }

        // 关键修复：使用源图像的 DPI，但尺寸是放大后的
        var renderBitmap = new RenderTargetBitmap(
            renderWidth,
            renderHeight,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32
        );

        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    /// <summary>
    /// 在指定区域绘制翻译文本覆盖层
    /// </summary>
    /// <param name="drawingContext">绘图上下文</param>
    /// <param name="content">包含翻译文本和位置信息的内容</param>
    /// <param name="pixelsPerDip">DPI缩放比例</param>
    private void DrawTranslatedTextOverlay(DrawingContext drawingContext, OcrContent content, double pixelsPerDip)
    {
        var boundingRect = CalculateBoundingRect(content.BoxPoints!);

        // 绘制白色背景覆盖原文
        var backgroundBrush = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
        backgroundBrush.Freeze();

        var expandedRect = new Rect(
            boundingRect.Left - 2,
            boundingRect.Top - 2,
            boundingRect.Width + 4,
            boundingRect.Height + 4);
        drawingContext.DrawRectangle(backgroundBrush, null, expandedRect);

        // 创建并绘制适配的文本
        var textBrush = new SolidColorBrush(Colors.Black);
        textBrush.Freeze();

        var formattedText = CreateOptimalText(content.Text, boundingRect, textBrush, pixelsPerDip);

        // 居中绘制文本
        var textPosition = new Point(
            Math.Max(boundingRect.Left + 4, boundingRect.Left + (boundingRect.Width - formattedText.Width) / 2),
            Math.Max(boundingRect.Top + 4, boundingRect.Top + (boundingRect.Height - formattedText.Height) / 2)
        );

        drawingContext.DrawText(formattedText, textPosition);
    }

    /// <summary>
    /// 创建适应区域的最优文本
    /// </summary>
    /// <param name="text">文本内容</param>
    /// <param name="boundingRect">目标区域</param>
    /// <param name="textBrush">文本画刷</param>
    /// <param name="pixelsPerDip">DPI缩放比例</param>
    /// <returns>格式化文本</returns>
    private FormattedText CreateOptimalText(string text, Rect boundingRect, Brush textBrush, double pixelsPerDip)
    {
        const double minFontSize = 6;
        const double maxFontSize = 48;
        const double padding = 6;

        var availableWidth = Math.Max(20, boundingRect.Width - padding);
        var availableHeight = Math.Max(15, boundingRect.Height - padding);

        // 快速估算初始字体大小
        var estimatedFontSize = Math.Min(maxFontSize,
            Math.Max(minFontSize, Math.Min(availableHeight * 0.7, availableWidth * 0.12)));

        // 使用二分查找优化字体大小
        var optimalSize = FindOptimalFontSize(text, estimatedFontSize, minFontSize, maxFontSize,
            availableWidth, availableHeight, textBrush, pixelsPerDip);

        var formattedText = CreateFormattedText(text, optimalSize, textBrush, availableWidth, pixelsPerDip);

        // 如果文本仍然过大，进行截断处理
        if (formattedText.Height > availableHeight)
        {
            var truncatedText = TruncateTextToFit(text, optimalSize, availableWidth, availableHeight, pixelsPerDip);
            formattedText = CreateFormattedText(truncatedText, optimalSize, textBrush, availableWidth, pixelsPerDip);
        }

        return formattedText;
    }

    /// <summary>
    /// 使用二分查找确定最优字体大小
    /// </summary>
    private double FindOptimalFontSize(string text, double initialSize, double minSize, double maxSize,
        double availableWidth, double availableHeight, Brush textBrush, double pixelsPerDip)
    {
        double bestSize = minSize;
        double low = minSize;
        double high = Math.Min(maxSize, initialSize);

        while (high - low > 0.5)
        {
            var mid = (low + high) / 2;
            var testText = CreateFormattedText(text, mid, textBrush, availableWidth, pixelsPerDip);

            if (testText.Height <= availableHeight && testText.Width <= availableWidth)
            {
                bestSize = mid;
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return bestSize;
    }

    /// <summary>
    /// 截断文本以适应区域
    /// </summary>
    private string TruncateTextToFit(string text, double fontSize, double availableWidth, double availableHeight, double pixelsPerDip)
    {
        // 估算可容纳的字符数
        var estimatedCharsPerLine = Math.Max(1, (int)(availableWidth / (fontSize * 0.6)));
        var estimatedLines = Math.Max(1, (int)(availableHeight / (fontSize * 1.2)));
        var maxChars = estimatedCharsPerLine * estimatedLines;

        if (text.Length <= maxChars) return text;

        // 逐步截断直到适合
        var truncated = maxChars > 3 ? text.Substring(0, maxChars - 3) + "..." : text.Substring(0, Math.Min(text.Length, maxChars));

        // 进一步验证和调整
        while (truncated.Length > 4)
        {
            var testText = CreateFormattedText(truncated, fontSize, new SolidColorBrush(Colors.Black), availableWidth, pixelsPerDip);
            if (testText.Height <= availableHeight) break;

            truncated = truncated.Length > 6
                ? truncated.Substring(0, truncated.Length - 4) + "..."
                : truncated.Substring(0, Math.Max(1, truncated.Length - 1));
        }

        return truncated;
    }

    /// <summary>
    /// 创建格式化文本对象
    /// </summary>
    private FormattedText CreateFormattedText(string text, double fontSize, Brush textBrush, double maxWidth, double pixelsPerDip)
    {
        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei, Arial, SimSun"),
            fontSize,
            textBrush,
            pixelsPerDip); // 关键修复：使用正确的缩放比例，而不是硬编码的 96

        formattedText.MaxTextWidth = maxWidth;
        return formattedText;
    }

    #endregion

    #region Border Drawing

    private BitmapSource GenerateAnnotatedImage(OcrResult ocrResult, BitmapSource? image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (!Utilities.HasBoxPoints(ocrResult))
            return image;

        var drawingVisual = new DrawingVisual();

        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // 绘制原始图像
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));

            // 创建并冻结画笔以提高性能
            var pen = new Pen(Brushes.Red, 2);
            pen.Freeze();

            // 绘制所有多边形
            foreach (var item in ocrResult.OcrContents)
            {
                if (item.BoxPoints == null || item.BoxPoints.Count == 0)
                    continue;

                var geometry = CreatePolygonGeometry(item.BoxPoints);
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }

        // 使用标准 96 DPI，Viewbox 会自动处理高 DPI 屏幕的缩放
        var renderBitmap = new RenderTargetBitmap(
            image.PixelWidth,
            image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32
        );

        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    private static StreamGeometry CreatePolygonGeometry(List<BoxPoint> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), false, true);

            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    #endregion

    #region Layout Analysis

    /// <summary>
    /// OCR 内容与其边界矩形的组合结构
    /// </summary>
    private class ContentWithRect
    {
        public OcrContent Content { get; set; } = null!;
        public Rect Rect { get; set; }
        public int Index { get; set; }
    }

    /// <summary>
    /// 应用版面分析，将 OCR 识别的逐行结果按照空间位置关系进行分组合并
    /// 直接修改 ocrResult.OcrContents，将相邻的文本块合并为新的 OcrContent
    /// </summary>
    /// <param name="ocrResult">OCR 识别结果</param>
    private void ApplyLayoutAnalysis(OcrResult ocrResult)
    {
        if (!Utilities.HasBoxPoints(ocrResult))
            return;

#if DEBUG
        var originalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"原始文本块数量: {originalCount}");
#endif

        // 创建一个副本用于分析，包含边界矩形信息
        var contentWithRects = ocrResult.OcrContents
            .Where(content => !string.IsNullOrWhiteSpace(content.Text) && content.BoxPoints?.Count > 0)
            .Select((content, index) => new ContentWithRect
            {
                Content = content,
                Rect = CalculateBoundingRect(content.BoxPoints!),
                Index = index
            })
            .OrderBy(x => x.Rect.Top)
            .ThenBy(x => x.Rect.Left)
            .ToList();

        if (contentWithRects.Count == 0)
            return;

        // 分组合并相邻的文本块
        var mergedContents = GroupAndMergeContents(contentWithRects);

        // 用合并后的内容替换原始内容
        ocrResult.OcrContents.Clear();
        ocrResult.OcrContents.AddRange(mergedContents);

#if DEBUG
        var finalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"合并后文本块数量: {finalCount}");
        System.Diagnostics.Debug.WriteLine($"合并率: {(originalCount - finalCount) / (double)originalCount * 100:F1}%");

        // 输出每个合并后的文本块
        for (int i = 0; i < ocrResult.OcrContents.Count; i++)
        {
            System.Diagnostics.Debug.WriteLine($"文本块 {i + 1}: {ocrResult.OcrContents[i].Text}");
        }
#endif
    }

    /// <summary>
    /// 将相邻的 OCR 内容分组并合并
    /// </summary>
    private List<OcrContent> GroupAndMergeContents(List<ContentWithRect> contentWithRects)
    {
        var mergedContents = new List<OcrContent>();
        var processed = new HashSet<int>();

        for (int i = 0; i < contentWithRects.Count; i++)
        {
            if (processed.Contains(i)) continue;

            var group = new List<ContentWithRect>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            processed.Add(i);

            // 使用广度优先搜索找到所有相邻的文本块
            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = contentWithRects[currentIndex];

                group.Add(current);

                // 查找与当前文本块相邻的其他文本块
                for (int j = 0; j < contentWithRects.Count; j++)
                {
                    if (processed.Contains(j)) continue;

                    var candidate = contentWithRects[j];
                    if (AreAdjacent(current.Rect, candidate.Rect))
                    {
                        queue.Enqueue(j);
                        processed.Add(j);
                    }
                }
            }

            // 合并这个组的内容
            var mergedContent = MergeContentGroup(group);
            mergedContents.Add(mergedContent);
        }

        return mergedContents;
    }

    /// <summary>
    /// 判断两个矩形是否相邻（可以合并）
    /// </summary>
    private bool AreAdjacent(Rect rect1, Rect rect2)
    {
        // 垂直相邻检测的阈值
        var verticalThreshold = Math.Min(rect1.Height, rect2.Height) * Settings.VerticalThresholdRatio;
        // 水平相邻检测的阈值
        var horizontalThreshold = Math.Min(rect1.Width, rect2.Width) * Settings.HorizontalThresholdRatio;

        // 检查垂直相邻（上下相邻）
        bool verticallyAdjacent = Math.Abs(rect1.Bottom - rect2.Top) <= verticalThreshold ||
                                 Math.Abs(rect2.Bottom - rect1.Top) <= verticalThreshold;

        // 检查水平重叠
        bool horizontallyOverlapping = !(rect1.Right < rect2.Left - horizontalThreshold ||
                                        rect2.Right < rect1.Left - horizontalThreshold);

        // 检查水平相邻（左右相邻）
        bool horizontallyAdjacent = Math.Abs(rect1.Right - rect2.Left) <= horizontalThreshold ||
                                   Math.Abs(rect2.Right - rect1.Left) <= horizontalThreshold;

        // 检查垂直重叠
        bool verticallyOverlapping = !(rect1.Bottom < rect2.Top - verticalThreshold ||
                                      rect2.Bottom < rect1.Top - verticalThreshold);

        return (verticallyAdjacent && horizontallyOverlapping) ||
               (horizontallyAdjacent && verticallyOverlapping);
    }

    /// <summary>
    /// 合并一组相邻的 OCR 内容
    /// </summary>
    private OcrContent MergeContentGroup(List<ContentWithRect> group)
    {
        if (group.Count == 1)
            return group[0].Content;

        // 按阅读顺序排序（从上到下，从左到右）
        var sortedGroup = group
            .OrderBy(item => item.Rect.Top)
            .ThenBy(item => item.Rect.Left)
            .ToList();

        // 合并文本
        var textBuilder = new StringBuilder();
        for (int i = 0; i < sortedGroup.Count; i++)
        {
            var item = sortedGroup[i];

            if (i > 0)
            {
                // 检查是否需要添加空格或换行
                var currentRect = item.Rect;
                var lastRect = sortedGroup[i - 1].Rect;

                // 如果垂直距离较大，添加换行；否则添加空格
                if (Math.Abs(currentRect.Top - lastRect.Bottom) > lastRect.Height * Settings.LineSpacingThresholdRatio)
                {
                    textBuilder.AppendLine();
                }
                else if (Math.Abs(currentRect.Left - lastRect.Right) > lastRect.Width * Settings.WordSpacingThresholdRatio)
                {
                    textBuilder.Append(' ');
                }
            }

            textBuilder.Append(item.Content.Text.Trim());
        }

        // 计算合并后的边界框
        var allPoints = sortedGroup
            .SelectMany(item => item.Content.BoxPoints)
            .ToList();

        var mergedContent = new OcrContent
        {
            Text = textBuilder.ToString().Trim()
        };

        if (allPoints.Count > 0)
        {
            var minX = allPoints.Min(p => p.X);
            var minY = allPoints.Min(p => p.Y);
            var maxX = allPoints.Max(p => p.X);
            var maxY = allPoints.Max(p => p.Y);

            // 创建合并后的边界框坐标点
            mergedContent.BoxPoints =
            [
                new(minX, minY),      // 左上
                new(maxX, minY),      // 右上
                new(maxX, maxY),      // 右下
                new(minX, maxY)       // 左下
            ];
        }

        return mergedContent;
    }

    /// <summary>
    /// 根据坐标点计算边界矩形
    /// </summary>
    private static Rect CalculateBoundingRect(List<BoxPoint> boxPoints)
    {
        if (boxPoints == null || boxPoints.Count == 0)
            return Rect.Empty;

        var minX = boxPoints.Min(p => p.X);
        var minY = boxPoints.Min(p => p.Y);
        var maxX = boxPoints.Max(p => p.X);
        var maxY = boxPoints.Max(p => p.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    #endregion

    #region Cancel & IDisposable

    public void CancelOperations()
    {
        ExecuteCancelCommand.Execute(null);
        PlayAudioCancelCommand.Execute(null);
        Clear();
    }

    public void Dispose()
    {
        // 取消订阅事件，防止内存泄漏
        _ocrService.Services.CollectionChanged -= OnOcrServicesCollectionChanged;
        _ocrService.PropertyChanged -= OnOcrServicePropertyChanged;
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged -= OnOcrEnginePropertyChanged;
        }
        _transCollectionView.Filter -= OnTransFilter;
        _translateService.PropertyChanged -= OnTranServicePropertyChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    #endregion
}
