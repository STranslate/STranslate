using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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

        OcrEngines = [];
        RefreshOcrEngines();
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
    private bool _disposed;
    internal bool IsSaveDialogOpen { get; private set; }
    // 显示/隐藏右侧文本面板时窗口宽度的换算：显示时翻倍再减去边距，隐藏时反向还原。
    private const double WidthMultiplier = 2;
    private const double WidthAdjustment = 12;

    [ObservableProperty]
    public partial BitmapSource? DisplayImage { get; set; }

    [ObservableProperty]
    public partial ImageTranslateOverlayDocument? DisplayOverlayDocument { get; set; }

    [ObservableProperty]
    public partial bool IsShowingFitToWindow { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPin))]
    public partial bool IsExecuting { get; set; } = false;

    public bool CanPin => !IsExecuting && !_disposed &&
        _sourceImage != null && _annotatedImage != null && _resultOverlayDocument is { IsEmpty: false };
    internal BitmapSource? SourceImage => _sourceImage;
    internal BitmapSource? AnnotatedImage => _annotatedImage;
    internal ImageTranslateOverlayDocument? ResultOverlay => _resultOverlayDocument;
    internal IReadOnlyList<OcrWord> OriginalSelectionWords => _originalSelectionWords;
    internal IReadOnlyList<OcrWord> TranslatedSelectionWords => _translatedSelectionWords;

    internal void ReportPinFailure(Exception exception)
    {
        _logger.LogError(exception, "Failed to pin image translation");
        _snackbar.ShowError($"{_i18n.GetTranslation("ImageTranslatePinFailed")}: {exception.Message}");
    }

    [ObservableProperty]
    public partial string ProcessRingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoLocationInfoVisible { get; set; } = false;

    /// <summary>
    /// 翻译结果
    /// </summary>
    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<OcrWord> OcrWords { get; set; } = [];

    /// <summary>
    /// 原始图像
    /// </summary>
    private BitmapSource? _sourceImage;

    /// <summary>
    /// 标注图像（显示识别边框）
    /// </summary>
    private BitmapSource? _annotatedImage;

    /// <summary>
    /// 译文矢量覆盖文档。
    /// </summary>
    private ImageTranslateOverlayDocument? _resultOverlayDocument;

    private OcrResult? _lastOcrResult;
    private ObservableCollection<OcrWord> _originalSelectionWords = [];
    private ObservableCollection<OcrWord> _translatedSelectionWords = [];

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
            _lastOcrResult = await ocrSvc.RecognizeAsync(
                new OcrRequest(data, Settings.ImageTranslateOcrLanguage, bitmap.Width, bitmap.Height),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Utilities.PrepareOcrResult(_lastOcrResult);

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

            // 分段逻辑
            var layoutBlocks = ApplyLayoutAnalysis(_lastOcrResult);
            _originalSelectionWords = OcrWordBuilder.CreateFromLayoutBlocks(layoutBlocks);
            RefreshSelectableOcrWords();

            // 生成分段后的标注图像（显示合并后的边框）
            _annotatedImage = ImageTranslateRenderer.GenerateAnnotatedImage(_lastOcrResult, _sourceImage);

            if (_translateService.ImageTranslateService?.Plugin is not ITranslatePlugin tranSvc)
            {
                _snackbar.ShowWarning(_i18n.GetTranslation("NoTranslateService"));
                return;
            }

            ProcessRingText = _i18n.GetTranslation("TranslatingText");

            await Parallel.ForEachAsync(layoutBlocks, cancellationToken, async (block, cancellationToken) =>
            {
                var detectOptions = new LangDetectOptions(
                    Settings.ImageTranslateLanguageDetector,
                    Settings.ImageTranslateLocalDetectorRate,
                    Settings.ImageTranslateSourceLangIfAuto,
                    Settings.ImageTranslateFirstLanguage,
                    Settings.ImageTranslateSecondLanguage);

                var (isSuccess, source, target) = await LanguageDetector
                    .GetLanguageAsync(
                        block.Text,
                        Settings.ImageTranslateSourceLang,
                        Settings.ImageTranslateTargetLang,
                        cancellationToken,
                        options: detectOptions)
                    .ConfigureAwait(false);
                if (!isSuccess)
                {
                    _logger.LogWarning($"Language detection failed for text: {block.Text}");
                    _snackbar.ShowWarning(_i18n.GetTranslation("LanguageDetectionFailed"));
                    return;
                }
                if (string.IsNullOrWhiteSpace(block.Text))
                    return;

                var result = new TranslateResult();
                await tranSvc.TranslateAsync(new TranslateRequest(block.Text, source, target), result, cancellationToken);
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                {
                    var normalizedText = ImageTranslateTextOverlayLayout.NormalizeOverlayText(result.Text);
                    if (!string.IsNullOrWhiteSpace(normalizedText))
                        block.Text = normalizedText;
                }
                else
                {
                    // 翻译失败（接口报错或返回空）：保留 OCR 原文，仅记录日志避免并发 snackbar 刷屏。
                    var reason = result.IsSuccess ? "empty translation" : result.Text;
                    _logger.LogWarning("Image translate failed for a block ({Reason}); keeping original OCR text", reason);
                }
            });

            cancellationToken.ThrowIfCancellationRequested();

            _lastOcrResult.OcrContents.Clear();
            _lastOcrResult.OcrContents.AddRange(layoutBlocks.Select(x => x.ToOcrContent()));

            // 生成原图坐标系中的矢量译文覆盖文档，不再创建超采样结果位图。
            _resultOverlayDocument = ImageTranslateRenderer.CreateTranslatedOverlay(
                layoutBlocks, GetOverlayTheme());
            _translatedSelectionWords = new ObservableCollection<OcrWord>(
                _resultOverlayDocument.SelectableWords);
            Result = _lastOcrResult.Text;

            RefreshDisplayState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
    private Task ImTransOcrAsync(Window? window)
        => _mainWindowViewModel.ImageTranslateInternalAsync(hideExistingWindows: window is not null);

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
        var window = await _mainWindowViewModel.OpenSettingsInternalAsync(null);

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            window.Navigate(nameof(OcrPage), selectedService: SelectedOcrEngine);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            window.Navigate(nameof(TranslatePage), selectedService: SelectedTranslateEngine);
        }
        else
            window.Navigate(nameof(StandalonePage));
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
    private void CopyImage(ImageZoom? imageZoom)
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
            _snackbar.ShowError($"{_i18n.GetTranslation("CopyFailed")}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SelectAllText(ImageZoom? imageZoom) => imageZoom?.SelectAllText();

    [RelayCommand]
    private void SaveImage()
    {
        var displayImage = DisplayImage;
        var overlayDocument = DisplayOverlayDocument;
        if (displayImage is null)
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

        IsSaveDialogOpen = true;
        try
        {
            if (saveFileDialog.ShowDialog() != true)
                return;
        }
        finally
        {
            IsSaveDialogOpen = false;
        }

        try
        {
            BitmapEncoder encoder = saveFileDialog.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? new PngBitmapEncoder()
                        : new JpegBitmapEncoder();
            var imageToSave = ImageTranslateRenderer.RenderDisplayImage(displayImage, overlayDocument);
            encoder.Frames.Add(BitmapFrame.Create(imageToSave));

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

    private void RefreshOcrEngines()
    {
        OcrEngines.Clear();
        foreach (var service in _ocrService.GetImageTranslateOcrServices())
            OcrEngines.Add(service);
    }

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

        RefreshOcrEngines();
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
            if (_ocrService.IsImageTranslateOcrService(newValue))
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
                RefreshDisplayState();
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
        _resultOverlayDocument = null;
        DisplayImage = null;
        DisplayOverlayDocument = null;
        _lastOcrResult = null;
        IsShowingFitToWindow = false;
        IsNoLocationInfoVisible = false;
        _originalSelectionWords = [];
        _translatedSelectionWords = [];
        OcrWords = [];
        OnPropertyChanged(nameof(CanPin));
    }

    private void RefreshSelectableOcrWords()
    {
        OcrWords = Settings.IsImTranShowingAnnotated || _resultOverlayDocument == null
            ? _originalSelectionWords
            : _translatedSelectionWords;
    }

    private void RefreshDisplayState()
    {
        if (Settings.IsImTranShowingAnnotated)
        {
            DisplayImage = _annotatedImage ?? _sourceImage;
            DisplayOverlayDocument = null;
        }
        else
        {
            DisplayImage = _sourceImage;
            DisplayOverlayDocument = _resultOverlayDocument;
        }

        RefreshSelectableOcrWords();
    }

    private ImageTranslateOverlayTheme GetOverlayTheme() =>
        Settings.ColorScheme == ElementTheme.Dark
            ? ImageTranslateOverlayTheme.Dark
            : ImageTranslateOverlayTheme.Light;

    #endregion


    #region Text Segmentation

    /// <summary>
    /// 应用分段逻辑，直接修改 OCR 结果中的内容分组。
    /// </summary>
    /// <param name="ocrResult">OCR 识别结果</param>
    private List<OcrLayoutBlock> ApplyLayoutAnalysis(OcrResult ocrResult)
    {
#if DEBUG
        var originalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"原始文本块数量: {originalCount}");
#endif

        var layoutBlocks = OcrLayoutAnalyzer.AnalyzeBlocks(ocrResult, Settings.LayoutAnalysisMode);
        ocrResult.OcrContents.Clear();
        ocrResult.OcrContents.AddRange(layoutBlocks.Select(x => x.ToOcrContent()));

#if DEBUG
        var finalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"合并后文本块数量: {finalCount}");
        if (originalCount > 0)
            System.Diagnostics.Debug.WriteLine($"合并率: {(originalCount - finalCount) / (double)originalCount * 100:F1}%");

        // 输出每个合并后的文本块
        for (int i = 0; i < layoutBlocks.Count; i++)
        {
            var block = layoutBlocks[i];
            System.Diagnostics.Debug.WriteLine(
                $"文本块 {i + 1}: [{block.Source}] confidence={block.Confidence:F2}, lines={block.LineBoxPoints.Count}, text={block.Text}");
        }
#endif

        return layoutBlocks;
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
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
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

        _disposed = true;
    }

    #endregion
}
