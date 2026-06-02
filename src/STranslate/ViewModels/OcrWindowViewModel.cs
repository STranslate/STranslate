using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.ViewModels.Pages;
using STranslate.Views;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.ZKWeb;
using Bitmap = System.Drawing.Bitmap;

namespace STranslate.ViewModels;

public partial class OcrWindowViewModel : ObservableObject, IDisposable
{
    #region Constructor & DI
    public HotkeySettings HotkeySettings { get; }

    public OcrWindowViewModel(
        Settings settings,
        DataProvider dataProvider,
        ILogger<OcrWindowViewModel> logger,
        MainWindowViewModel mainWindowViewModel,
        OcrService ocrService,
        TtsService ttsService,
        Internationalization i18n,
        ISnackbar snackbar,
        INotification notification,
        HotkeySettings hotkeySettings)
    {
        _logger = logger;
        Settings = settings;
        DataProvider = dataProvider;
        _mainWindowViewModel = mainWindowViewModel;
        _ocrService = ocrService;
        _ttsService = ttsService;
        _i18n = i18n;
        _snackbar = snackbar;
        _notification = notification;

        OcrEngines = _ocrService.Services;
        SelectedOcrEngine = _ocrService.Services.FirstOrDefault(x => x.IsEnabled);

        // 订阅 OcrViewModel 中服务的 PropertyChanged 事件
        _ocrService.Services.CollectionChanged += OnServicesCollectionChanged;

        // 为现有服务订阅事件
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged += OnOcrServicePropertyChanged;
        }

        Settings.PropertyChanged += OnSettingsPropertyChanged;
        HotkeySettings = hotkeySettings;
    }


    #endregion

    #region Properties

    public Settings Settings { get; }
    public DataProvider DataProvider { get; }

    private readonly ILogger<OcrWindowViewModel> _logger;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OcrService _ocrService;
    private readonly TtsService _ttsService;
    private readonly Internationalization _i18n;
    private readonly ISnackbar _snackbar;
    private readonly INotification _notification;
    private const double WidthMultiplier = 2;
    private const double WidthAdjustment = 12;
    private bool _hasShownNoLocationInfoForSelectedEngine;

    [ObservableProperty]
    public partial bool IsExecuting { get; set; } = false;

    [ObservableProperty]
    public partial string ProcessRingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoLocationInfoVisible { get; set; } = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand))]
    public partial string Result { get; set; } = string.Empty;

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(LatexResult));

    public string LatexResult => ExtractLatex(Result);

    private OcrResult? _lastOcrResult;

    [ObservableProperty]
    public partial BitmapSource? DisplayImage { get; set; }

    private BitmapSource? _sourceImage;
    private BitmapSource? _annotatedImage;

    [ObservableProperty]
    public partial string QrCodeResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<OcrWord> OcrWords { get; set; } = [];

    [ObservableProperty]
    public partial bool IsShowingFitToWindow { get; set; } = false;

    [ObservableProperty]
    public partial ObservableCollection<Service> OcrEngines { get; set; }

    [ObservableProperty]
    public partial Service? SelectedOcrEngine { get; set; } = null;

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

            var ocrSvc = _ocrService.GetActiveSvc<IOcrPlugin>();
            if (ocrSvc == null)
            {
                Helper.PromptConfigureService(
                    _i18n.GetTranslation("OcrServiceNotFoundTitle"),
                    _i18n.GetTranslation("OcrServiceNotFoundMessage"),
                    nameof(OcrPage));
                return;
            }

            var data = Utilities.ToBytes(bitmap, Settings.GetImageFormat());

            // 尝试获取二维码结果
            var qrResult = DecodeQrCode(data);
            if (qrResult != null)
            {
                QrCodeResult = qrResult.Text;
            }

            _lastOcrResult = await ocrSvc.RecognizeAsync(new OcrRequest(data, Settings.OcrLanguage), cancellationToken);

            if (!_lastOcrResult.IsSuccess || string.IsNullOrEmpty(_lastOcrResult.Text))
            {
                _snackbar.ShowWarning(_i18n.GetTranslation("OcrFailed"));
                _logger.LogError("OCR failed: {ErrorMessage}", _lastOcrResult.ErrorMessage);
                return;
            }

            if (Settings.CopyAfterOcr)
                ClipboardHelper.SetText(_lastOcrResult.Text);

            var hasBoxPoints = Utilities.HasBoxPoints(_lastOcrResult);
            IsNoLocationInfoVisible = !hasBoxPoints && !_hasShownNoLocationInfoForSelectedEngine;
            if (IsNoLocationInfoVisible)
            {
                _hasShownNoLocationInfoForSelectedEngine = true;
            }

            _annotatedImage = GenerateAnnotatedImage(_lastOcrResult, _sourceImage);
            PopulateOcrWords(_lastOcrResult);
            Result = _lastOcrResult.Text;

            DisplayImage = Settings.IsOcrShowingAnnotated ? _annotatedImage : _sourceImage;
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private void SwitchImage() => Settings.IsOcrShowingAnnotated = !Settings.IsOcrShowingAnnotated;

    [RelayCommand]
    private async Task OcrAsync(Window? window)
    {
        window?.Hide();
        
        await Task.Delay(150);

        await _mainWindowViewModel.OcrCommand.ExecuteAsync(null);
        window?.Show();
    }

    public void QrCode(Bitmap bitmap)
    {
        Clear();
        _sourceImage = Utilities.ToBitmapImage(bitmap, Settings.GetImageFormat());
        DisplayImage = _sourceImage;
        QrCode();
    }

    [RelayCommand]
    private void QrCode()
    {
        if (_sourceImage == null || IsExecuting) return;

        // 清理当前QrCodeResult
        QrCodeResult = string.Empty;
        using var bitmap = Utilities.ToBitmap(_sourceImage, Settings.GetBitmapEncoder());
        var data = Utilities.ToBytes(bitmap);
        var qrResult = DecodeQrCode(data);
        if (qrResult == null || string.IsNullOrWhiteSpace(qrResult.Text))
        {
            _snackbar.ShowInfo(_i18n.GetTranslation("NoQrCodeFound"));
            return;
        }

        QrCodeResult = qrResult.Text;
    }

    [RelayCommand]
    private async Task ReExecuteAsync()
    {
        if (_sourceImage == null || IsExecuting) return;
        using var bitmap = Utilities.ToBitmap(_sourceImage, Settings.GetBitmapEncoder());
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    public bool CanTranslate() => !string.IsNullOrEmpty(Result);

    [RelayCommand(CanExecute = nameof(CanTranslate))]
    private void Translate()
    {
        _mainWindowViewModel.ExecuteTranslate(Result);
    }

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
    private void Copy(string? text)
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
    private void CopyImageOrText(ImageZoom? imageZoom)
    {
        var text = imageZoom?.SelectedText;
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
                return;
            }

            if (_sourceImage != null)
            {
                Clipboard.SetImage(_sourceImage);
                _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
                return;
            }

            _snackbar.ShowWarning(_i18n.GetTranslation("NoCopyContent"));
        }
        catch (Exception ex)
        {
            // 剪贴板操作在某些情况下可能失败（非 STA、其他应用占用等）
            _snackbar.ShowError($"{_i18n.GetTranslation("CopyFailed")}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SelectAllText(ImageZoom? imageZoom) => imageZoom?.SelectAllText();

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
            var encoder = CreateBitmapEncoder(saveFileDialog.FileName);
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
        else
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(StandalonePage));
    }

    [RelayCommand]
    private void ToggleTextControl() => Settings.IsOcrShowingTextControl = !Settings.IsOcrShowingTextControl;

    [RelayCommand]
    private void Cancel(Window window)
    {
        CancelOperations();
        window.Close();
    }

    #endregion

    #region Event Handlers

    private void OnServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (Service service in e.NewItems)
            {
                service.PropertyChanged += OnOcrServicePropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (Service service in e.OldItems)
            {
                service.PropertyChanged -= OnOcrServicePropertyChanged;
            }
        }
    }

    /// <summary>
    /// 监听 OcrViewModel 中服务的 IsEnabled 变化
    /// </summary>
    private void OnOcrServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Service.IsEnabled) && sender is Service service)
        {
            // 如果某个服务被启用，同步更新 SelectedOcrEngine
            if (service.IsEnabled)
            {
                SelectedOcrEngine = service;
            }
            // 如果当前选中的服务被禁用，清空选择
            else if (SelectedOcrEngine == service)
            {
                SelectedOcrEngine = null;
            }
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.IsOcrShowingTextControl):
                Settings.OcrWindowWidth = Settings.IsOcrShowingTextControl
                        ? Settings.OcrWindowWidth * WidthMultiplier - WidthAdjustment
                        : (Settings.OcrWindowWidth + WidthAdjustment) / WidthMultiplier;
                break;
            case nameof(Settings.IsOcrShowingAnnotated):
                DisplayImage = Settings.IsOcrShowingAnnotated ? _annotatedImage : _sourceImage;
                break;
        }
    }

    /// <summary>
    /// 当用户在 UI 中切换 OCR 引擎时，同步更新服务的 IsEnabled 状态
    /// </summary>
    partial void OnSelectedOcrEngineChanged(Service? oldValue, Service? newValue)
    {
        if (oldValue != newValue)
        {
            _hasShownNoLocationInfoForSelectedEngine = false;
            IsNoLocationInfoVisible = false;
        }

        // 禁用旧的引擎
        if (oldValue != null && oldValue.IsEnabled)
        {
            oldValue.IsEnabled = false;
        }

        // 启用新的引擎
        if (newValue != null && !newValue.IsEnabled)
        {
            newValue.IsEnabled = true;
        }
    }

    #endregion

    #region Private Methods

    private void Clear()
    {
        QrCodeResult = string.Empty;
        Result = string.Empty;
        _sourceImage = null;
        _annotatedImage = null;
        DisplayImage = null;
        _lastOcrResult = null;
        IsShowingFitToWindow = false;
        IsNoLocationInfoVisible = false;
        OcrWords.Clear();
    }

    private static BitmapEncoder CreateBitmapEncoder(string fileName)
    {
        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder();
    }

    private Result? DecodeQrCode(byte[] bytes)
    {
        try
        {
            // 创建 ZXing 的 BarcodeReader 实例
            var reader = new BarcodeReader();
            reader.Options.CharacterSet = "UTF-8";
            // 使用字节数组创建 System.DrawingCore.Bitmap
            using var stream = new MemoryStream(bytes);
            using var drawingCoreBitmap = new System.DrawingCore.Bitmap(stream);
            // 解码二维码
            var result = reader.Decode(drawingCoreBitmap);

            return result;
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// 从文本中提取 LaTeX 内容（支持 $$ 和 $ 两种分隔符）
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <returns>提取的 LaTeX 内容,如果没有则返回原文本</returns>
    private static string ExtractLatex(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var latexList = new List<string>();

        // 优先提取 $$ 包裹的块级公式
        var remaining = ExtractDelimitedLatex(text, "$$", latexList);

        // 如果没有提取到 $$，尝试提取 $ 包裹的行内公式
        if (latexList.Count == 0)
        {
            remaining = ExtractDelimitedLatex(text, "$", latexList);
        }

        // 如果提取到了内容，返回提取结果
        if (latexList.Count > 0)
            return string.Join(Environment.NewLine + Environment.NewLine, latexList);

        // 如果都没有匹配，检查是否整个文本就是 LaTeX（AI 可能忘记加分隔符）
        if (ContainsLatexSyntax(text))
            return text.Trim();

        // 返回原始文本
        return text;
    }

    /// <summary>
    /// 提取指定分隔符包裹的 LaTeX 内容
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <param name="delimiter">分隔符（$ 或 $$）</param>
    /// <param name="results">提取结果列表</param>
    /// <returns>移除了 LaTeX 部分后的剩余文本</returns>
    private static string ExtractDelimitedLatex(string text, string delimiter, List<string> results)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var delimiterLength = delimiter.Length;
        var currentIndex = 0;
        var processedParts = new List<string>();

        while (currentIndex < text.Length)
        {
            var startIndex = text.IndexOf(delimiter, currentIndex, StringComparison.Ordinal);

            // 没有找到起始分隔符
            if (startIndex == -1)
            {
                // 保留剩余的非 LaTeX 文本
                if (currentIndex < text.Length)
                    processedParts.Add(text.Substring(currentIndex));
                break;
            }

            // 保留分隔符之前的文本
            if (startIndex > currentIndex)
                processedParts.Add(text.Substring(currentIndex, startIndex - currentIndex));

            var contentStart = startIndex + delimiterLength;

            // 对于单 $，需要避免误匹配 $$
            var endIndex = FindClosingDelimiter(text, contentStart, delimiter);

            if (endIndex == -1)
            {
                // 没有找到结束分隔符（AI 流式输出未完成）
                var remainingText = text.Substring(contentStart).Trim();
                if (!string.IsNullOrWhiteSpace(remainingText))
                    results.Add(remainingText);
                break;
            }

            var latexContent = text.Substring(contentStart, endIndex - contentStart).Trim();

            // 过滤空白和过短的内容（避免误匹配）
            if (!string.IsNullOrWhiteSpace(latexContent) && latexContent.Length > 0)
            {
                results.Add(latexContent);
            }

            currentIndex = endIndex + delimiterLength;
        }

        return string.Join("", processedParts).Trim();
    }

    /// <summary>
    /// 查找闭合分隔符，避免 $ 和 $$ 的误匹配
    /// </summary>
    private static int FindClosingDelimiter(string text, int searchStart, string delimiter)
    {
        if (delimiter == "$")
        {
            // 对于单 $，需要确保不匹配到 $$
            var index = searchStart;
            while (index < text.Length)
            {
                index = text.IndexOf("$", index, StringComparison.Ordinal);
                if (index == -1)
                    return -1;

                // 检查是否是 $$
                var isDoubleDollar = (index + 1 < text.Length && text[index + 1] == '$') ||
                                     (index > 0 && text[index - 1] == '$');

                if (!isDoubleDollar)
                    return index;

                index++;
            }
            return -1;
        }
        else
        {
            // 对于 $$，直接查找
            return text.IndexOf(delimiter, searchStart, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 检查文本是否包含常见的 LaTeX 语法标记
    /// </summary>
    private static bool ContainsLatexSyntax(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 常见 LaTeX 命令和符号（按出现频率排序，优化性能）
        ReadOnlySpan<string> latexIndicators = new[]
        {
            "\\frac",      // 分数
            "\\sum",       // 求和
            "\\int",       // 积分
            "\\sqrt",      // 根号
            "\\lim",       // 极限
            "\\left",      // 左括号
            "\\right",     // 右括号
            "\\begin{",    // 环境开始
            "\\end{",      // 环境结束
            "\\alpha",     // 希腊字母
            "\\beta",
            "\\gamma",
            "\\theta",
            "\\pi",
            "\\lambda",
            "\\mu",
            "\\sigma",
            "\\tag{",      // 标签
            "\\label{",    // 引用标签
            "\\text{",     // 文本
            "\\mathrm{",   // 正体
            "\\mathbf{",   // 粗体
            "\\cdot",      // 点乘
            "\\times",     // 叉乘
            "\\pm",        // 加减
            "\\leq",       // 小于等于
            "\\geq",       // 大于等于
            "\\neq",       // 不等于
            "\\approx",    // 约等于
            "_{",          // 下标
            "^{",          // 上标
            "&",           // 对齐符
            "\\\\"         // 换行
        };

        foreach (var indicator in latexIndicators)
        {
            if (text.Contains(indicator, StringComparison.Ordinal))
                return true;
        }

        // 额外检查：是否包含多个反斜杠（强 LaTeX 特征）
        var backslashCount = 0;
        foreach (var c in text)
        {
            if (c == '\\')
            {
                backslashCount++;
                if (backslashCount >= 2) // 至少2个反斜杠命令
                    return true;
            }
        }

        return false;
    }

    #endregion

    #region Image Processing

    private void PopulateOcrWords(OcrResult ocrResult)
    {
        if (_sourceImage == null || ocrResult?.OcrContents == null)
            return;

        var ocrWords = new List<OcrWord>();

        foreach (var content in ocrResult.OcrContents)
        {
            if (string.IsNullOrEmpty(content.Text) ||
                content.BoxPoints == null ||
                content.BoxPoints.Count == 0)
                continue;

            var boundingBox = CalculateBoundingBox(content.BoxPoints);
            var charCount = content.Text.Length;
            var avgCharWidth = boundingBox.Width / Math.Max(charCount, 1);

            // 按字符拆分
            for (int i = 0; i < charCount; i++)
            {
                var charLeft = boundingBox.Left + avgCharWidth * i;
                var charBox = new Rect(charLeft, boundingBox.Top, avgCharWidth, boundingBox.Height);

                ocrWords.Add(new OcrWord
                {
                    Text = content.Text[i].ToString(),
                    BoundingBox = charBox
                });
            }
        }

        // 排序并构建全文索引
        var sortedWords = ocrWords
            .OrderBy(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        OcrWords.Clear();
        int currentIndex = 0;
        foreach (var word in sortedWords)
        {
            word.StartIndexInFullText = currentIndex;
            OcrWords.Add(word);
            currentIndex += word.Text.Length;
        }
    }

    private static Rect CalculateBoundingBox(List<BoxPoint> boxPoints)
    {
        var minX = boxPoints.Min(p => p.X);
        var minY = boxPoints.Min(p => p.Y);
        var maxX = boxPoints.Max(p => p.X);
        var maxY = boxPoints.Max(p => p.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static BitmapSource GenerateAnnotatedImage(OcrResult ocrResult, BitmapSource? image)
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
        _ocrService.Services.CollectionChanged -= OnServicesCollectionChanged;
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged -= OnOcrServicePropertyChanged;
        }
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    #endregion
}
