using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Resources;
using STranslate.Services;
using STranslate.ViewModels.Pages;
using STranslate.Views;
using STranslate.Views.Pages;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Win32;

namespace STranslate.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    #region Constructor & DI

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Internationalization _i18n;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IScreenshot _screenshot;
    private readonly ISnackbar _snackbar;
    private readonly INotification _notification;
    private double _cacheLeft;
    private double _cacheTop;
    private bool _isAdjustingWindowPositionForContent;

    public TranslateService TranslateService { get; }
    public OcrService OcrService { get; }
    public TtsService TtsService { get; }
    public VocabularyService VocabularyService { get; }

    private readonly SqlService _sqlService;
    private readonly DebounceExecutor _debounceExecutor;
    private ClipboardMonitor? _clipboardMonitor;
    private bool _forceShowInputForInputTranslate;
    private bool _skipShowForNextTranslate;

    public Settings Settings { get; }
    public HotkeySettings HotkeySettings { get; }

    public MainWindowViewModel(
        DataProvider dataProvider,
        ILogger<MainWindowViewModel> logger,
        Internationalization i18n,
        IAudioPlayer audioPlayer,
        IScreenshot screenshot,
        ISnackbar snackbar,
        INotification notification,
        TranslateService translateService,
        OcrService ocrService,
        TtsService ttsService,
        VocabularyService vocabularyService,
        SqlService sqlService,
        Settings settings,
        HotkeySettings hotkeySettings)
    {
        DataProvider = dataProvider;
        IdentifiedLanguageOptions = DataProvider.LangEnums
            .Where(x => x.Value != LangEnum.Auto)
            .Cast<DropdownDataGeneric<LangEnum>>()
            .ToList();
        _logger = logger;
        _i18n = i18n;
        _audioPlayer = audioPlayer;
        _screenshot = screenshot;
        _snackbar = snackbar;
        _notification = notification;
        TranslateService = translateService;
        OcrService = ocrService;
        TtsService = ttsService;
        VocabularyService = vocabularyService;
        _sqlService = sqlService;
        Settings = settings;
        HotkeySettings = hotkeySettings;

        _debounceExecutor = new();
        _i18n.OnLanguageChanged += OnLanguageChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnLanguageChanged()
    {
        ApplyIdentifiedLanguageState(_identifiedLanguageState);

        if (!UACHelper.IsUserAdministrator())
            return;

        TrayToolTip = $"{Constant.AppName} # {_i18n.GetTranslation("Administrator")}";
    }

    #endregion

    #region Properties

    private MainWindow MainWindow => (Application.Current.MainWindow as MainWindow)!;
    private bool IsMainWindowVisible => MainWindow.Visibility == Visibility.Visible;

    public DataProvider DataProvider { get; }

    /// <summary>
    /// 等待ContextMenu关闭动画完成的延迟时间（毫秒）
    /// </summary>
    private const int ContextMenuCloseAnimationDelay = 150;

    private sealed record IdentifiedLanguageState(IdentifiedLanguageStateKind Kind, LangEnum? Language = null)
    {
        public static IdentifiedLanguageState Empty { get; } = new(IdentifiedLanguageStateKind.None);
    }

    private sealed record TranslationLanguageContext(
        LangEnum CacheSource,
        LangEnum CacheTarget,
        LangEnum EffectiveSource,
        LangEnum EffectiveTarget);

    [ObservableProperty]
    public partial ImageSource TrayIcon { get; set; } = BitmapImageLoc.AppIcon;

    [ObservableProperty]
    public partial string TrayToolTip { get; set; } = Constant.AppName;

    [ObservableProperty]
    public partial bool IsMouseHook { get; set; } = false;

    [ObservableProperty]
    public partial bool IsIdentifyProcessing { get; set; } = false;

    [ObservableProperty]
    public partial bool IsClipboardMonitoring { get; set; } = false;

    [ObservableProperty]
    public partial double MainWindowEffectiveMaxHeight { get; set; } = 800;

    public bool IsInputActuallyHidden
    {
        get => Settings.HideInput && !_forceShowInputForInputTranslate;
        set
        {
            if (value == IsInputActuallyHidden)
                return;

            ExitInputTranslateMode();
            Settings.HideInput = value;
        }
    }

    public bool IsInputBoxVisible => !IsInputActuallyHidden;

    public bool IsLanguageSelectControlVisible =>
        !IsInputActuallyHidden || !Settings.HideInputWithLangSelectControl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SingleTranslateCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectIdentifiedLanguageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectLanguageDetectorCommand))]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdentifiedLanguage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LangEnum SelectedIdentifiedLanguage { get; set; } = LangEnum.Auto;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectIdentifiedLanguageCommand))]
    public partial bool CanSelectIdentifiedLanguage { get; set; } = false;

    public IReadOnlyList<DropdownDataGeneric<LangEnum>> IdentifiedLanguageOptions { get; }

    private IdentifiedLanguageState _identifiedLanguageState = IdentifiedLanguageState.Empty;

    public IdentifiedLanguageStateKind CurrentIdentifiedLanguageState => _identifiedLanguageState.Kind;

    public bool IsTopmost
    {
        get => field;
        set
        {
            if (IsMouseHook && !value)
                iNKORE.UI.WPF.Modern.Controls.MessageBox.Show("监听鼠标划词时窗口必须置顶", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                SetProperty(ref field, value);
        }
    }

    public bool CanTranslate => !string.IsNullOrWhiteSpace(InputText);

    #endregion

    #region Translation Commands

    /// <summary>
    /// 执行翻译
    /// </summary>
    /// <param name="text"></param>
    /// <param name="force">不为空则跳过缓存</param>
    public void ExecuteTranslate(
        string text,
        string? force = null,
        WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        ExitInputTranslateMode();
        CancelAllOperations();
        ResetTranslationLanguageState();
        InputText = text;
        TranslateCommand.Execute(force);

        var skipShow = _skipShowForNextTranslate;
        _skipShowForNextTranslate = false;

        if (skipShow)
            return;

        Show(activationMode);
        UpdateCaret();
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync(object? force, CancellationToken cancellationToken)
    {
        // 取消防抖执行器中的待执行任务
        _debounceExecutor.Cancel();

        LangEnum? forcedSourceLanguage = force is LangEnum language && language != LangEnum.Auto
            ? language
            : null;

        ResetAllServices();
        ApplyIdentifiedLanguageState(forcedSourceLanguage.HasValue
            ? CreateDetectedIdentifiedLanguageState(forcedSourceLanguage.Value)
            : IdentifiedLanguageState.Empty);

        // force 空则优先检查缓存
        var checkCacheFirst = force == null;

        var history = await ExecuteTranslateAsync(checkCacheFirst, forcedSourceLanguage, cancellationToken);

        // 翻译后自动复制
        if (Settings.CopyAfterTranslation != CopyAfterTranslation.NoAction)
        {
            var serviceList = TranslateService.Services.Where(x => x.IsEnabled && x.Options?.ExecMode == ExecutionMode.Automatic);
            var service = Settings.CopyAfterTranslation == CopyAfterTranslation.Last ?
                serviceList.LastOrDefault() :
                serviceList.ElementAtOrDefault((int)Settings.CopyAfterTranslation - 1);
            if (service == null)
            {
                _snackbar.ShowWarning(string.Format(_i18n.GetTranslation("CopyServiceNotFound"), Settings.CopyAfterTranslation));
            }
            else
            {
                var data = history?.GetData(service);
                if (data != null)
                {
                    var textToCopy = data.TransResult?.Text ?? data.DictResult?.Text;
                    if (!string.IsNullOrWhiteSpace(textToCopy))
                    {
                        ClipboardHelper.SetText(textToCopy);
                        _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("CopiedToClipboard"), service.DisplayName));
                    }
                }
            }
        }

        #region 历史记录处理

        if (Settings.HistoryLimit > 0 && history != null && history.Data.Count != 0)
        {
            // 按服务启用顺序排序
            var enabledServices = TranslateService.Services.Where(x => x.IsEnabled).ToList();
            history.Data = [.. history.Data.OrderBy(data => enabledServices.FindIndex(svc => svc.ServiceID.Equals(data.ServiceID)))];
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
        }
        else
        {
            // 检查避免重复添加，暂定最大缓存数量为100
            if (_recentTexts.Count >= 100)
                _recentTexts.RemoveAt(_recentTexts.Count - 1);

            if (!_recentTexts.Contains(InputText))
                _recentTexts.Insert(0, InputText);
        }

        #endregion
    }

    [RelayCommand]
    private void TemporaryTranslate(Service service)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("InputContentIsEmpty"));
            return;
        }

        if (!SingleTranslateCommand.CanExecute(service))
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("WaitingForPreviousExecution"));
            return;
        }
        service.Options?.TemporaryDisplay = true;

        SingleTranslateCommand.Execute(service);
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTranslate))]
    private async Task SingleTranslateAsync(Service service, CancellationToken cancellationToken)
    {
        var history = await _sqlService.GetDataAsync(
            InputText,
            Settings.SourceLang.ToString(),
            Settings.TargetLang.ToString());

        if (service.Plugin is IDictionaryPlugin dictionaryPlugin)
        {
            var result = await ExecuteDictAsync(dictionaryPlugin, cancellationToken).ConfigureAwait(false);
            if (result.ResultType == DictionaryResultType.Error)
                return;

            if (Settings.CopyAfterTranslationNotAutomatic)
            {
                ClipboardHelper.SetText(result.Text);
                _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("CopiedToClipboard"), service.DisplayName));
            }

            history ??= CreateHistoryModel(Settings.SourceLang, Settings.TargetLang);
            // 添加新的历史数据记录并执行字典查询
            history.Data.Add(new(service) { DictResult = result });
            return;
        }

        if (service.Plugin is not ITranslatePlugin plugin || plugin.TransResult.IsProcessing)
            return;

        var context = await ResolveTranslationLanguageContextAsync(null, cancellationToken).ConfigureAwait(false);

        var translateResult = await ExecuteAsync(plugin, context.EffectiveSource, context.EffectiveTarget, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        if (Settings.CopyAfterTranslationNotAutomatic)
        {
            ClipboardHelper.SetText(translateResult.Text);
            _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("CopiedToClipboard"), service.DisplayName));
        }

        history ??= CreateHistoryModel(context);
        ApplyEffectiveLanguages(history, context.EffectiveSource, context.EffectiveTarget);
        // 添加新的历史数据记录
        var historyData = history.GetData(service);
        if (historyData == null)
        {
            historyData = new HistoryData(service);
            history.Data.Add(historyData);
        }
        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.TransResult = translateResult;

        if (service.Options?.AutoBackTranslation ?? false)
        {
            var backResult = await ExecuteBackAsync(
                plugin,
                context.EffectiveTarget,
                context.EffectiveSource,
                cancellationToken).ConfigureAwait(false);
            historyData.TransBackResult = backResult;
        }

        if (Settings.HistoryLimit > 0)
        {
            var enabledServices = TranslateService.Services.Where(x => x.IsEnabled).ToList();
            history.Data = [.. history.Data.OrderBy(data => enabledServices.FindIndex(svc => svc.ServiceID.Equals(data.ServiceID)))];
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SingleTransBackAsync(Service service, CancellationToken cancellationToken)
    {
        if (service.Plugin is not ITranslatePlugin plugin || plugin.TransBackResult.IsProcessing)
            return;

        var history = await _sqlService.GetDataAsync(
            InputText,
            Settings.SourceLang.ToString(),
            Settings.TargetLang.ToString());

        var context = await ResolveTranslationLanguageContextAsync(null, cancellationToken).ConfigureAwait(false);

        if (history != null)
            ApplyEffectiveLanguages(history, context.EffectiveSource, context.EffectiveTarget);

        var backResult = await ExecuteBackAsync(
            plugin,
            context.EffectiveTarget,
            context.EffectiveSource,
            cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        var historyData = history?.GetData(service);
        if (historyData != null)
        {
            UpdateHistoryServiceSnapshot(historyData, service);
            historyData.TransBackResult = backResult;
        }

        if (Settings.HistoryLimit > 0 && history != null)
            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
    }

    [RelayCommand]
    private void SwapLanguage()
    {
        if (string.IsNullOrWhiteSpace(InputText) ||
            (Settings.SourceLang == Settings.TargetLang && Settings.SourceLang == LangEnum.Auto))
            return;

        (Settings.SourceLang, Settings.TargetLang) = (Settings.TargetLang, Settings.SourceLang);
        TranslateCommand.Execute(null);
    }

    [RelayCommand]
    private void Explain(string text)
    {
        ExecuteTranslate(text);
    }

    [RelayCommand(CanExecute = nameof(CanSelectIdentifiedLanguageForCurrentText))]
    private async Task SelectIdentifiedLanguageAsync(LangEnum language)
    {
        CancelAllOperations();

        TranslateCommand.Execute(language);
        Show();
        UpdateCaret();
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanSelectLanguageDetectorForCurrentText))]
    private void SelectLanguageDetector(LanguageDetectorType detector)
    {
        Settings.LanguageDetector = detector;
        CancelAllOperations();

        TranslateCommand.Execute("force");
        Show();
        UpdateCaret();
    }

    #region Translation Execution Logic

    private async Task<HistoryModel?> ExecuteTranslateAsync(bool checkCacheFirst, LangEnum? forcedSourceLanguage, CancellationToken cancellationToken)
    {
        var enabledSvcs = TranslateService.Services.Where(x => x.IsEnabled && x.Options?.ExecMode == ExecutionMode.Automatic).ToList();
        if (enabledSvcs.Count == 0)
            return null;

        HistoryModel? history = null;
        var uncachedSvcs = new List<Service>(enabledSvcs);

        // 尝试从缓存加载
        if (checkCacheFirst && Settings.HistoryLimit > 0)
        {
            history = await _sqlService.GetDataAsync(
                InputText,
                Settings.SourceLang.ToString(),
                Settings.TargetLang.ToString());
            if (history != null)
            {
                ApplyIdentifiedLanguageState(CreateCacheIdentifiedLanguageState(history));
                uncachedSvcs = await PopulateResultsFromCacheAsync(history, enabledSvcs, cancellationToken);
            }
        }

        // 如果所有服务都已从缓存加载，则直接返回
        if (uncachedSvcs.Count == 0)
        {
            return history;
        }

        // 对未缓存的服务执行实时翻译
        var context = await ResolveTranslationLanguageContextAsync(forcedSourceLanguage, cancellationToken);

        history ??= CreateHistoryModel(context);
        ApplyEffectiveLanguages(history, context.EffectiveSource, context.EffectiveTarget);

        await ExecuteTranslationForServicesAsync(
            uncachedSvcs,
            context.EffectiveSource,
            context.EffectiveTarget,
            history,
            cancellationToken);

        return history;
    }

    private HistoryModel CreateHistoryModel(TranslationLanguageContext context)
        => CreateHistoryModel(context.CacheSource, context.CacheTarget);

    private HistoryModel CreateHistoryModel(LangEnum source, LangEnum target)
    {
        return new HistoryModel
        {
            Time = DateTime.Now,
            SourceText = InputText,
            SourceLang = source.ToString(),
            TargetLang = target.ToString(),
            Data = []
        };
    }

    private static void ApplyEffectiveLanguages(HistoryModel history, LangEnum source, LangEnum target)
    {
        history.EffectiveSourceLang = source.ToString();
        history.EffectiveTargetLang = target.ToString();
    }

    /// <summary>
    /// 统一维护历史记录里的服务名称快照，确保历史展示和导出不依赖当前服务配置。
    /// </summary>
    private static void UpdateHistoryServiceSnapshot(HistoryData historyData, Service service)
    {
        historyData.ServiceDisplayName = service.DisplayName;
    }

    /// <summary>
    /// 从缓存填充翻译结果，并返回未缓存的服务列表
    /// </summary>
    private async Task<List<Service>> PopulateResultsFromCacheAsync(HistoryModel history, List<Service> services, CancellationToken cancellationToken)
    {
        var uncachedServices = new List<Service>();
        var populateTasks = services.Select(async svc =>
        {
            if (history.GetData(svc) is { } data)
            {
                await PopulateServiceResultFromDataAsync(svc, data);
                if (!history.HasData(svc)) // 检查是否需要反向翻译
                {
                    uncachedServices.Add(svc);
                }
            }
            else
            {
                uncachedServices.Add(svc);
            }
        });
        await Task.WhenAll(populateTasks);
        return uncachedServices;
    }

    /// <summary>
    /// 根据历史数据填充单个服务的结果
    /// </summary>
    private async Task PopulateServiceResultFromDataAsync(Service svc, HistoryData data)
    {
        if (svc.Plugin is ITranslatePlugin tPlugin)
        {
            if (data.TransResult != null && data.TransResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransResult.Text))
                tPlugin.TransResult.Update(data.TransResult);

            if ((svc.Options?.AutoBackTranslation ?? false) && data.TransBackResult != null && data.TransBackResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransBackResult.Text))
                tPlugin.TransBackResult.Update(data.TransBackResult);
        }
        else if (svc.Plugin is IDictionaryPlugin dPlugin)
        {
            if (data.DictResult != null && data.DictResult.ResultType != DictionaryResultType.Error && data.DictResult.ResultType != DictionaryResultType.None)
            {
                dPlugin.DictionaryResult.Update(data.DictResult);
            }
        }
    }

    /// <summary>
    /// 为指定的服务列表执行翻译
    /// </summary>
    private async Task ExecuteTranslationForServicesAsync(IEnumerable<Service> services, LangEnum source, LangEnum target, HistoryModel history, CancellationToken cancellationToken)
    {
        var maxConcurrency = Math.Min(services.Count(), Environment.ProcessorCount * 10);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var translateTasks = services.Select(svc =>
            ExecuteTranslationHandlerAsync(svc, source, target, semaphore, history, cancellationToken));

        try
        {
            await Task.WhenAll(translateTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
    }

    private async Task ExecuteTranslationHandlerAsync(Service svc, LangEnum source, LangEnum target,
        SemaphoreSlim semaphore, HistoryModel history, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (svc.Plugin)
            {
                case ITranslatePlugin translatePlugin:
                    await ProcessTranslatePluginAsync(svc, translatePlugin, source, target, history, cancellationToken).ConfigureAwait(false);
                    break;
                case IDictionaryPlugin dictionaryPlugin:
                    await ProcessDictionaryPluginAsync(svc, dictionaryPlugin, history, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ProcessTranslatePluginAsync(Service service, ITranslatePlugin plugin, LangEnum source, LangEnum target,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 如果历史记录中没有该服务的数据，则执行全新翻译
        if (history.GetData(service) == null)
        {
            await ExecuteNewTranslationAsync(service, plugin, source, target, history, cancellationToken).ConfigureAwait(false);
        }
        // 否则，只执行反向翻译（如果需要）
        else if ((service.Options?.AutoBackTranslation ?? false) && history.GetData(service)?.TransBackResult == null)
        {
            await ExecuteBackTranslationOnlyAsync(service, plugin, target, source, history, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteNewTranslationAsync(Service service, ITranslatePlugin plugin, LangEnum source, LangEnum target,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 执行主翻译
        var translateResult = await ExecuteAsync(plugin, source, target, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        // 添加新的历史数据记录
        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.TransResult = translateResult;

        // 执行反向翻译（如果需要且主翻译成功）
        if (service.Options?.AutoBackTranslation ?? false)
        {
            var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
            historyData.TransBackResult = backResult;
        }
    }

    private async Task ExecuteBackTranslationOnlyAsync(Service service, ITranslatePlugin plugin, LangEnum target, LangEnum source,
        HistoryModel history, CancellationToken cancellationToken)
    {
        var backResult = await ExecuteBackAsync(plugin, target, source, cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        var historyData = history.GetData(service);
        if (historyData != null)
        {
            UpdateHistoryServiceSnapshot(historyData, service);
            historyData.TransBackResult = backResult;
        }
    }

    private async Task ProcessDictionaryPluginAsync(Service service, IDictionaryPlugin plugin,
        HistoryModel history, CancellationToken cancellationToken)
    {
        // 如果缓存中已存在数据则跳过
        if (history.HasData(service))
            return;

        var result = await ExecuteDictAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (result.ResultType == DictionaryResultType.Error)
            return;

        // 添加新的历史数据记录并执行字典查询
        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.DictResult = result;
    }

    private async Task<DictionaryResult> ExecuteDictAsync(IDictionaryPlugin plugin, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.DictionaryResult.IsProcessing = true;
            await plugin.TranslateAsync(InputText, plugin.DictionaryResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.DictionaryResult.ResultType = DictionaryResultType.Error;
            plugin.DictionaryResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.DictionaryResult.ResultType = DictionaryResultType.Error;
            plugin.DictionaryResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            if (plugin.DictionaryResult.ResultType != DictionaryResultType.NoResult)
                plugin.DictionaryResult.Duration = DateTime.Now - startTime;
            if (plugin.DictionaryResult.IsProcessing)
                plugin.DictionaryResult.IsProcessing = false;
        }

        return plugin.DictionaryResult;
    }

    private async Task<TranslateResult> ExecuteAsync(ITranslatePlugin plugin, LangEnum source, LangEnum target, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.TransResult.IsProcessing = true;
            await plugin.TranslateAsync(new TranslateRequest(InputText, source, target), plugin.TransResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.TransResult.IsSuccess = false;
            plugin.TransResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.TransResult.IsSuccess = false;
            plugin.TransResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            plugin.TransResult.Duration = DateTime.Now - startTime;
            if (plugin.TransResult.IsProcessing)
                plugin.TransResult.IsProcessing = false;
        }

        return plugin.TransResult;
    }

    private async Task<TranslateResult> ExecuteBackAsync(ITranslatePlugin plugin, LangEnum target, LangEnum source, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.ResetBack();
            plugin.TransBackResult.IsProcessing = true;
            await plugin.TranslateAsync(new TranslateRequest(plugin.TransResult.Text, target, source), plugin.TransBackResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plugin.TransBackResult.IsSuccess = false;
            plugin.TransBackResult.Text = _i18n.GetTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            plugin.TransBackResult.IsSuccess = false;
            plugin.TransBackResult.Text = $"{_i18n.GetTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            plugin.TransBackResult.Duration = DateTime.Now - startTime;
            if (plugin.TransBackResult.IsProcessing)
                plugin.TransBackResult.IsProcessing = false;
        }

        return plugin.TransBackResult;
    }

    #endregion

    #region Auto Translate

    [RelayCommand]
    private void ToggleAutoTranslate()
    {
        Settings.AutoTranslate = !Settings.AutoTranslate;
        if (Settings.AutoTranslate)
            _snackbar.ShowSuccess(_i18n.GetTranslation("AutoTranslateEnabled"));
        else
            _snackbar.ShowInfo(_i18n.GetTranslation("AutoTranslateDisabled"));
    }
    
    #endregion

    #endregion

    #region OCR & Screenshot Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScreenshotTranslateAsync(CancellationToken cancellationToken)
    {
        var ocrPlugin = GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await ScreenshotTranslateHandlerAsync(bitmap, ocrPlugin, cancellationToken);
    }

    public async Task ScreenshotTranslateHandlerAsync(Bitmap? bitmap, IOcrPlugin? ocrPlugin = default, CancellationToken cancellationToken = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        try
        {
            CursorHelper.Execute();
            var data = Utilities.ToBytes(bitmap, Settings.GetImageFormat());
            var result = await ocrPlugin.RecognizeAsync(new OcrRequest(data, LangEnum.Auto), cancellationToken);

            if (!result.IsSuccess || string.IsNullOrEmpty(result.Text))
                return;

            if (Settings.CopyAfterOcr)
                ClipboardHelper.SetText(result.Text);

            _skipShowForNextTranslate = !Settings.FocusInputAfterScreenshotTranslate && IsTopmost;
            ExecuteTranslate(HandleCapturedText(result.Text, TextSeparatorHandleScope.ScreenshotTranslate));
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _notification.Show(_i18n.GetTranslation("Prompt"), $"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    [RelayCommand]
    private async Task ImageTranslateAsync()
    {
        var ocrPlugin = GetImageTranslateOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        if (TranslateService.ImageTranslateService == null)
        {
            _notification.ShowWithButton(
                 _i18n.GetTranslation("ImageTranslateServiceNotFoundTitle"),
                 _i18n.GetTranslation("GoToSettings"),
                 () =>
                 {
                     Application.Current.Dispatcher.Invoke(() =>
                     {
                         SingletonWindowOpener
                             .Open<SettingsWindow>()
                             .Activate();

                         Application.Current.Windows
                                 .OfType<SettingsWindow>()
                                 .First()
                                 .Navigate(nameof(TranslatePage));
                     });
                 },
                 _i18n.GetTranslation("ImageTranslateServiceNotFoundMessage"));
            return;
        }


        using var bitmap = await _screenshot.GetScreenshotAsync();
        await ImageTranslateHandlerAsync(bitmap, ocrPlugin);
    }

    public async Task ImageTranslateHandlerAsync(Bitmap? bitmap, IOcrPlugin? ocrPlugin = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetImageTranslateOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        var window = await SingletonWindowOpener.OpenAsync<ImageTranslateWindow>();
        await ((ImageTranslateWindowViewModel)window.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task OcrAsync()
    {
        if (GetOcrSvcAndNotify() == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await OcrHandlerAsync(bitmap);
    }

    public async Task OcrHandlerAsync(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        var window = await SingletonWindowOpener.OpenAsync<OcrWindow>();
        await ((OcrWindowViewModel)window.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task QrCodeAsync()
    {
        if (GetOcrSvcAndNotify() == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await QrCodeHandlerAsync(bitmap);
    }

    public async Task QrCodeHandlerAsync(Bitmap? bitmap)
    {
        if (bitmap == null) return;
        var window = await SingletonWindowOpener.OpenAsync<OcrWindow>();
        ((OcrWindowViewModel)window.DataContext).QrCode(bitmap);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SilentOcrAsync(CancellationToken cancellationToken)
    {
        var ocrPlugin = GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        using var bitmap = await _screenshot.GetScreenshotAsync();
        await SilentOcrHandlerAsync(bitmap, ocrPlugin, cancellationToken);
    }

    public async Task SilentOcrHandlerAsync(Bitmap? bitmap, IOcrPlugin? ocrPlugin = default, CancellationToken cancellationToken = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;
        try
        {
            CursorHelper.Execute();
            var data = Utilities.ToBytes(bitmap, Settings.GetImageFormat());
            var result = await ocrPlugin.RecognizeAsync(new OcrRequest(data, LangEnum.Auto), cancellationToken);
            if (result.IsSuccess && !string.IsNullOrEmpty(result.Text))
            {
                ClipboardHelper.SetText(HandleSilentOcrText(result.Text));
            }
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _notification.Show(_i18n.GetTranslation("Prompt"), $"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    private IOcrPlugin? GetOcrSvcAndNotify()
    {
        var svc = OcrService.GetActiveSvc<IOcrPlugin>();
        if (svc == null)
        {
            _notification.ShowWithButton(
                _i18n.GetTranslation("OcrServiceNotFoundTitle"),
                _i18n.GetTranslation("GoToSettings"),
                () =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SingletonWindowOpener
                            .Open<SettingsWindow>()
                            .Activate();

                        Application.Current.Windows
                                .OfType<SettingsWindow>()
                                .First()
                                .Navigate(nameof(OcrPage));
                    });
                },
                _i18n.GetTranslation("OcrServiceNotFoundMessage"));
            return default;
        }

        return svc;
    }

    private IOcrPlugin? GetImageTranslateOcrSvcAndNotify()
    {
        var svc = OcrService.GetImageTranslateOcrSvcOrDefault();
        if (svc == null)
        {
            _notification.ShowWithButton(
                _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundTitle"),
                _i18n.GetTranslation("GoToSettings"),
                () =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SingletonWindowOpener
                            .Open<SettingsWindow>()
                            .Activate();

                        Application.Current.Windows
                                .OfType<SettingsWindow>()
                                .First()
                                .Navigate(nameof(OcrPage));
                    });
                },
                _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"));
            return default;
        }

        return svc;
    }

    #endregion

    #region TTS & Audio Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioAsync(string text, CancellationToken cancellationToken)
    {
        var ttsSvc = TtsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("TtsServiceNotFound"));
            return;
        }

        try
        {
            await ttsSvc.PlayAudioAsync(text, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            _snackbar.ShowInfo(_i18n.GetTranslation("TtsCancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS播放失败");
            _snackbar.ShowError(_i18n.GetTranslation("TtsFailed"));
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            await _audioPlayer.PlayAsync(url, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            _snackbar.ShowInfo(_i18n.GetTranslation("TtsCancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音频播放失败");
            _snackbar.ShowError(_i18n.GetTranslation("TtsFailed"));
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SilentTtsAsync(CancellationToken cancellationToken)
    {
        var ttsSvc = TtsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
            return;

        var (success, text) = await GetTextAsync();
        if (!success || string.IsNullOrWhiteSpace(text))
            return;
        await SilentTtsHandlerAsync(text, ttsSvc, cancellationToken);
    }

    public async Task SilentTtsHandlerAsync(string text, ITtsPlugin? ttsSvc = default, CancellationToken cancellationToken = default)
    {
        ttsSvc ??= TtsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
            return;

        try
        {
            CursorHelper.Execute();
            await ttsSvc.PlayAudioAsync(text, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    #endregion

    #region Voculary Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveToVocabularyAsync(string text, CancellationToken cancellationToken)
    {
        var vocabularySvc = VocabularyService.GetActiveSvc<IVocabularyPlugin>();
        if (vocabularySvc == null)
            return;

        var result = await vocabularySvc.SaveAsync(text, cancellationToken);
        if (result.IsSuccess)
            _snackbar.ShowSuccess(_i18n.GetTranslation("OperationSuccess"));
        else
            _snackbar.ShowError(_i18n.GetTranslation("OperationFailed"));
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveToVocabularyWithNoteAsync(Service service, CancellationToken cancellationToken)
    {
        var vocabularySvc = VocabularyService.GetActiveSvc<IVocabularyPlugin>();
        if (vocabularySvc == null) return;

        if (service.Plugin is not ITranslatePlugin plugin || plugin.TransResult.IsProcessing)
            return;

        var word = InputText;
        var note = plugin.TransResult.IsSuccess ? plugin.TransResult.Text : string.Empty;

        if (string.IsNullOrWhiteSpace(word))
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("InputContentIsEmpty"));
            return;
        }

        var result = await vocabularySvc.SaveWithNoteAsync(word, note, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        if (result.IsSuccess)
            _snackbar.ShowSuccess(_i18n.GetTranslation("OperationSuccess"));
        else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            _snackbar.ShowError(result.ErrorMessage);
        else
            _snackbar.ShowError(_i18n.GetTranslation("OperationFailed"));
    }

    #endregion

    #region History Commands

    [RelayCommand]
    private async Task HistoryPreviousAsync()
    {
        var result = Settings.HistoryLimit == HistoryLimit.NotSave ?
            await QueryRecentTextFromCacheAsync() :
            await QueryRecentTextFromHistoryAsync();

        if (!string.IsNullOrWhiteSpace(result))
            ExecuteTranslate(result);
        else
            _snackbar.ShowWarning(_i18n.GetTranslation("NavigateFailed"));
    }

    [RelayCommand]
    private async Task HistoryNextAsync()
    {
        var result = Settings.HistoryLimit == HistoryLimit.NotSave ?
            await QueryRecentTextFromCacheAsync(isNext: true) :
            await QueryRecentTextFromHistoryAsync(isNext: true);

        if (!string.IsNullOrWhiteSpace(result))
            ExecuteTranslate(result);
        else
            _snackbar.ShowWarning(_i18n.GetTranslation("NavigateFailed"));
    }

    private List<string> _recentTexts = [];

    private async Task<string?> QueryRecentTextFromCacheAsync(bool isNext = false)
    {
        if (_recentTexts.Count == 0)
            return default;

        if (string.IsNullOrWhiteSpace(InputText))
        {
            // 如果输入为空，则获取最新的一条历史记录
            return _recentTexts[0];
        }
        else
        {
            var currentIndex = _recentTexts.FindIndex(t => t.Equals(InputText, StringComparison.OrdinalIgnoreCase));
            if (currentIndex == -1)
                return default;
            var newIndex = isNext ? currentIndex - 1 : currentIndex + 1;
            if (newIndex < 0 || newIndex >= _recentTexts.Count)
                return default;
            return _recentTexts[newIndex];
        }
    }

    private async Task<string?> QueryRecentTextFromHistoryAsync(bool isNext = false)
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            // 如果输入为空，则获取最新的一条历史记录
            var histories = await _sqlService.GetDataAsync(1, 1);
            return histories?.FirstOrDefault()?.SourceText;
        }
        else
        {
            // 否则，获取当前输入文本对应的历史记录
            var current = await _sqlService.GetDataAsync(
                InputText,
                Settings.SourceLang.ToString(),
                Settings.TargetLang.ToString());
            if (current != null)
            {
                var history = isNext ? await _sqlService.GetNextAsync(current) : await _sqlService.GetPreviousAsync(current);
                return history?.SourceText;
            }
        }

        return default;
    }

    #endregion

    #region Incretemental Translate

    public void OnIncKeyPressed()
    {
        Show();
        IsTopmost = true;
        UpdateCacheText();

        _ = MouseKeyHelper.StartMouseTextSelectionAsync(() => Settings.SelectedTextFetchTimeoutMs);
        MouseKeyHelper.MouseTextSelected += OnMouseTextSelectedIncretemental;
    }

    public void OnIncKeyReleased()
    {
        IsTopmost = false;
        MouseKeyHelper.StopMouseTextSelection();
        MouseKeyHelper.MouseTextSelected -= OnMouseTextSelectedIncretemental;

        if (string.IsNullOrWhiteSpace(InputText) || _oldText == InputText)
            return;

        Show();
        // 执行翻译
        TranslateCommand.Execute(null);
        UpdateCaret();
        UpdateCacheText();
    }

    private string _oldText = string.Empty;

    private void UpdateCacheText()
    {
        _oldText = InputText;
    }

    private void OnMouseTextSelectedIncretemental(string text)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            InputText += HandleCapturedText(text, TextSeparatorHandleScope.Incremental);
        });
    }

    #endregion

    #region Mouse Hook Feature

    [RelayCommand]
    private void ToggleMouseHookTranslate() => IsMouseHook = !IsMouseHook;

    partial void OnIsMouseHookChanged(bool value) => _ = ToggleMouseHookAsync(value);

    private async Task ToggleMouseHookAsync(bool enable)
    {
        if (enable)
        {
            Show();
            IsTopmost = true;
            await MouseKeyHelper.StartMouseTextSelectionAsync(() => Settings.SelectedTextFetchTimeoutMs);
            MouseKeyHelper.MouseTextSelected += OnMouseTextSelected;
        }
        else
        {
            IsTopmost = false;
            MouseKeyHelper.StopMouseTextSelection();
            MouseKeyHelper.MouseTextSelected -= OnMouseTextSelected;
        }
    }

    private void OnMouseTextSelected(string text)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.MouseHook));
        });
    }

    [RelayCommand]
    private async Task CrosswordTranslateAsync()
    {
        var (success, text) = await GetTextAsync(showFailureNotification: false);
        if (!success || string.IsNullOrWhiteSpace(text))
        {
            HandleCrosswordFetchFailed();
            return;
        }

        ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.Crossword));
    }

    public void CrosswordTranslateByCtrlSameCHandler()
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var text = ClipboardHelper.GetText()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                HandleCrosswordFetchFailed();
                return;
            }

            ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.Crossword));
        });
    }

    [RelayCommand]
    private void ToggleClipboardMonitor() => IsClipboardMonitoring = !IsClipboardMonitoring;

    partial void OnIsClipboardMonitoringChanged(bool value) => ToggleClipboardMonitorHandler(value);

    private void ToggleClipboardMonitorHandler(bool value)
    {
        if (value)
        {
            StartClipboardMonitor();
        }
        else
        {
            StopClipboardMonitor();
        }
    }

    private void StartClipboardMonitor()
    {
        _clipboardMonitor ??= new ClipboardMonitor(MainWindow);
        _clipboardMonitor.OnClipboardTextChanged += OnClipboardTextChanged;
        _clipboardMonitor.Start();
        _notification.Show(
            _i18n.GetTranslation("Hotkey_ClipboardMonitor"),
            _i18n.GetTranslation("ClipboardMonitorStarted"));
    }

    private void StopClipboardMonitor()
    {
        if (_clipboardMonitor != null)
        {
            _clipboardMonitor.OnClipboardTextChanged -= OnClipboardTextChanged;
            _clipboardMonitor.Stop();
        }
        _notification.Show(
            _i18n.GetTranslation("Hotkey_ClipboardMonitor"),
            _i18n.GetTranslation("ClipboardMonitorStopped"));
    }

    private void OnClipboardTextChanged(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        App.Current.Dispatcher.Invoke(() =>
            ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.ClipboardMonitor)));
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ReplaceTranslateAsync(CancellationToken cancellationToken)
    {
        if (TranslateService.ReplaceService?.Plugin is not ITranslatePlugin transPlugin)
        {
            _notification.ShowWithButton(
                "无法获取替换翻译服务",
                "点击前往",
                () =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SingletonWindowOpener
                            .Open<SettingsWindow>()
                            .Activate();

                        Application.Current.Windows
                                .OfType<SettingsWindow>()
                                .First()
                                .Navigate(nameof(TranslatePage));
                    });
                },
                "当前未配置启用替换翻译服务，请先前往「设置-服务-文本翻译」配置后使用该功能");

            return;
        }

        try
        {
            CursorHelper.Execute();
            var (success, text) = await GetTextAsync();
            if (!success || string.IsNullOrWhiteSpace(text)) return;

            var (isSuccess, source, target) = await LanguageDetector.GetLanguageAsync(text, cancellationToken).ConfigureAwait(false);
            if (!isSuccess)
            {
                _logger.LogWarning($"Language detection failed for text: {text}");
                _notification.Show(_i18n.GetTranslation("Prompt"), "语言检测失败");
            }
            var result = new TranslateResult();
            await transPlugin.TranslateAsync(new TranslateRequest(text, source, target), result, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess && !string.IsNullOrEmpty(result.Text))
                InputHelper.PrintText(result.Text);
            else
                throw new Exception($"IsSuccess: {result.IsSuccess}, Text: {result.Text}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "替换翻译失败");
            CursorHelper.Error();
            await Task.Delay(1000);
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    #endregion

    #region Window & UI Control Commands

    [RelayCommand]
    private void ResetLocation()
    {
        var screen = SelectedScreen();
        Settings.MainWindowLeft = HorizonCenter(screen);
        Settings.MainWindowTop = VerticalCenter(screen);
        Show();
    }

    /// <summary>
    /// 初始化主窗口布局约束，避免窗口首次显示时沿用过期的高度上限。
    /// </summary>
    public void InitializeWindowLayoutConstraints() => UpdateMainWindowMaxHeightConstraint();

    public void Show(WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
        {
            Settings.MainWindowLeft = _cacheLeft;
            Settings.MainWindowTop = _cacheTop;
        }
        MainWindow.Visibility = Visibility.Visible;
        UpdateMainWindowMaxHeightConstraint();
        UpdatePosition();
        UpdateMainWindowMaxHeightConstraint();

        if (activationMode == WindowActivationMode.ForceForeground)
            Win32Helper.ForceSetForegroundWindow(MainWindow);
        else
            Win32Helper.SetForegroundWindow(MainWindow);

        MainWindow.Activate();

        if (IsInputBoxVisible)
        {
            MainWindow.PART_Input.Focus();
            Keyboard.Focus(MainWindow.PART_Input);
        }
    }

    public void Hide()
    {
        ExitInputTranslateMode();
        MainWindow.Visibility = Visibility.Collapsed;
    }

    [RelayCommand]
    private void DoubleClick()
    {
        switch (Settings.DoubleClickTrayFunction)
        {
            case DoubleClickTrayFunction.InputTranslate:
                InputClear();
                break;
            case DoubleClickTrayFunction.ScreenshotTranslate:
                ScreenshotTranslateCommand.Execute(null);
                break;
            case DoubleClickTrayFunction.OCR:
                OcrCommand.Execute(null);
                break;
            case DoubleClickTrayFunction.OpenSettingsWindow:
                OpenSettingsCommand.Execute(null);
                break;
            case DoubleClickTrayFunction.ToggleMouseHook:
                ToggleMouseHookTranslateCommand.Execute(null);
                break;
            case DoubleClickTrayFunction.ToggleGlobalHotkeys:
                ToggleGlobalHotkey();
                break;
            case DoubleClickTrayFunction.Exit:
                Exit();
                break;
            default:
                break;
        }
    }

    [RelayCommand]
    private void LeftClick()
    {
        // 开启后单击托盘功能禁用
        if (Settings.DoubleClickTrayFunction != DoubleClickTrayFunction.None)
            return;

        ToggleApp();
    }

    [RelayCommand]
    private void ToggleApp(WindowActivationMode? activationMode = null)
    {
        if (IsMainWindowVisible && !IsTopmost)
            Hide();
        else
            Show(activationMode ?? WindowActivationMode.Normal);
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        if (!IsMouseHook)
        {
            if (IsTopmost) IsTopmost = false;
            ExitInputTranslateMode();
            window.Visibility = Visibility.Collapsed;
        }
        CancelAllOperations();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync(object? parameter)
    {
        await OpenSettingsAndNavigateAsync(parameter);
    }

    internal async Task OpenSettingsAndNavigateAsync(
        object? parameter,
        WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        var isAlreadyOpen = Application.Current.Windows.OfType<SettingsWindow>().Any();
        var window = await OpenSettingsInternalAsync(parameter, activationMode);

        if (!isAlreadyOpen)
            window.Navigate(nameof(GeneralPage));
    }

    internal async Task<SettingsWindow> OpenSettingsInternalAsync(
        object? parameter,
        WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        // 如果由 ContextMenu 触发，等待关闭动画完成
        if (parameter is not null)
            await Task.Delay(ContextMenuCloseAnimationDelay);

        // 如果从主窗口打开设置，主动隐藏主窗口
        if (MainWindow.IsActive && IsMainWindowVisible && !IsTopmost)
            Hide();

        return await SingletonWindowOpener.OpenAsync<SettingsWindow>(activationMode);
    }

    [RelayCommand]
    private async Task OpenHistoryAsync()
    {
        await OpenHistoryInternalAsync();
    }

    internal async Task OpenHistoryInternalAsync(WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        var window = await OpenSettingsInternalAsync(null, activationMode);
        window.Navigate(nameof(HistoryPage));
    }

    [RelayCommand]
    private async Task NavigateAsync(Service service)
    {
        await OpenSettingsInternalAsync(string.Empty);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Application.Current.Windows
                    .OfType<SettingsWindow>()
                    .First()
                    .Navigate(nameof(TranslatePage));

            Ioc.Default.GetRequiredService<TranslateViewModel>().SelectedItem = service;
        }, DispatcherPriority.Normal);
    }

    [RelayCommand]
    private void CloseService(Service service) => service.IsEnabled = false;

    [RelayCommand]
    private void ToggleTopmost() => IsTopmost = !IsTopmost;

    [RelayCommand]
    private void ToggleHideInput() => IsInputActuallyHidden = !IsInputActuallyHidden;

    [RelayCommand]
    private void ChangeColorScheme()
    {
        var current = Settings.ColorScheme;
        var next = current + 1;
        if (next > ElementTheme.Dark) next = 0;
        Settings.ColorScheme = next;
    }

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    #endregion

    #region Text & Clipboard Manipulation

    [RelayCommand]
    private void InputClear(WindowActivationMode activationMode = WindowActivationMode.Normal)
    {
        CancelAllOperations();
        ResetTranslationLanguageState();
        InputText = string.Empty;

        ResetAllServices();
        EnterInputTranslateMode();
        Show(activationMode);
    }

    [RelayCommand]
    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ClipboardHelper.SetText(text);
        _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand]
    private void CopyPascalCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pascalCaseText = Utilities.ToPascalCase(text);
        ClipboardHelper.SetText(pascalCaseText);
        _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand]
    private void CopyCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pascalCaseText = Utilities.ToCamelCase(text);
        ClipboardHelper.SetText(pascalCaseText);
        _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand]
    private void CopySnakeCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pascalCaseText = Utilities.ToSnakeCase(text);
        ClipboardHelper.SetText(pascalCaseText);
        _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand]
    private async Task InsertAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 如果按住Shift则使用小写
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            text = text.ToLower();

        if (IsTopmost) IsTopmost = false;
        Hide();
        await Task.Delay(150);
        InputHelper.PrintText(text);
    }

    [RelayCommand]
    private void RemoveLineBreaks(TextBox textBox) =>
        Utilities.TransformText(
            textBox,
            t => t.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "),
            () => TranslateCommand.Execute(null));

    [RelayCommand]
    private void RemoveSpaces(TextBox textBox) =>
        Utilities.TransformText(
            textBox,
            t => t.Replace(" ", ""),
            () => TranslateCommand.Execute(null));

    [RelayCommand]
    private void CleanTransBack(ITranslatePlugin plugin) => plugin.ResetBack();

    #endregion

    #region Window Position

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.SourceLang) ||
            e.PropertyName == nameof(Settings.TargetLang))
        {
            ResetTranslationLanguageState();
        }

        if (e.PropertyName == nameof(Settings.HideInput) ||
            e.PropertyName == nameof(Settings.HideInputWithLangSelectControl))
        {
            NotifyInputVisibilityProperties();
        }

        if (e.PropertyName != nameof(Settings.MainWindowMaxHeightRatio) &&
            e.PropertyName != nameof(Settings.WindowScreen) &&
            e.PropertyName != nameof(Settings.CustomScreenNumber))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        void RefreshWindowHeightConstraint()
        {
            UpdateMainWindowMaxHeightConstraint();
            if (IsMainWindowVisible)
                AdjustPositionForContentSizeChanged();
        }

        if (dispatcher.CheckAccess())
        {
            RefreshWindowHeightConstraint();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshWindowHeightConstraint));
    }

    /// <summary>
    /// 根据当前屏幕工作区和用户配置比例刷新主窗口最大高度约束。
    /// </summary>
    /// <param name="monitor">可选。指定目标显示器，避免跟随鼠标场景下读取到旧屏幕。</param>
    private void UpdateMainWindowMaxHeightConstraint(MonitorInfo? monitor = null)
    {
        if (Application.Current?.MainWindow is not MainWindow window)
            return;

        var ratio = Math.Clamp(Settings.MainWindowMaxHeightRatio, 0.6, 1.0);
        if (Math.Abs(ratio - Settings.MainWindowMaxHeightRatio) > double.Epsilon)
        {
            // 统一写回归一化后的比例，确保各入口读取到一致约束。
            Settings.MainWindowMaxHeightRatio = ratio;
        }

        var targetMonitor = monitor ?? GetWindowMonitor();
        var workAreaTopLeft = Win32Helper.TransformPixelsToDIP(window, targetMonitor.WorkingArea.X, targetMonitor.WorkingArea.Y);
        var workAreaBottomRight = Win32Helper.TransformPixelsToDIP(
            window,
            targetMonitor.WorkingArea.X + targetMonitor.WorkingArea.Width,
            targetMonitor.WorkingArea.Y + targetMonitor.WorkingArea.Height);

        var workAreaHeight = Math.Max(0, workAreaBottomRight.Y - workAreaTopLeft.Y - 8 * 2);
        var effectiveMaxHeight = Math.Max(window.MinHeight, workAreaHeight * ratio);
        MainWindowEffectiveMaxHeight = Math.Max(window.MinHeight, effectiveMaxHeight);
    }

    private MonitorInfo GetWindowMonitor()
    {
        try
        {
            var windowHelper = new WindowInteropHelper(MainWindow);
            windowHelper.EnsureHandle();
            return MonitorInfo.GetNearestDisplayMonitor(windowHelper.Handle);
        }
        catch
        {
            return SelectedScreen();
        }
    }

    public void UpdatePosition(bool hideOnStartup = false)
    {
        if (IsTopmost) return;

        InternalUpdatePosition(hideOnStartup);
        InternalUpdatePosition(hideOnStartup);

        void InternalUpdatePosition(bool hideOnStartup)
        {
            if (hideOnStartup)
            {
                // 隐藏时缓存位置，第一次打开时恢复位置
                if (Settings.WindowScreen == WindowScreenType.RememberLastLaunchLocation &&
                    _cacheLeft == 0 && _cacheTop == 0)
                {
                    _cacheLeft = Settings.MainWindowLeft;
                    _cacheTop = Settings.MainWindowTop;
                }
                Settings.MainWindowLeft = -18000;
                Settings.MainWindowTop = -18000;
                return;
            }

            if (Settings.WindowScreen == WindowScreenType.FollowMouse)
            {
                UpdatePositionNearCursor();
                return;
            }

            if (Settings.WindowScreen == WindowScreenType.RememberLastLaunchLocation)
            {
                var previousScreenWidth = Settings.PreviousScreenWidth;
                var previousScreenHeight = Settings.PreviousScreenHeight;
                GetDpi(out var previousDpiX, out var previousDpiY);

                Settings.PreviousScreenWidth = SystemParameters.VirtualScreenWidth;
                Settings.PreviousScreenHeight = SystemParameters.VirtualScreenHeight;
                GetDpi(out var currentDpiX, out var currentDpiY);

                if (previousScreenWidth != 0 && previousScreenHeight != 0 &&
                    previousDpiX != 0 && previousDpiY != 0 &&
                    (previousScreenWidth != SystemParameters.VirtualScreenWidth ||
                     previousScreenHeight != SystemParameters.VirtualScreenHeight ||
                     previousDpiX != currentDpiX || previousDpiY != currentDpiY))
                {
                    AdjustPositionForResolutionChange();
                    return;
                }

                Settings.MainWindowLeft = Settings.MainWindowLeft;
                Settings.MainWindowTop = Settings.MainWindowTop;
            }
            else
            {
                var screen = SelectedScreen();
                switch (Settings.WindowAlign)
                {
                    case WindowAlignType.Center:
                        Settings.MainWindowLeft = HorizonCenter(screen);
                        Settings.MainWindowTop = VerticalCenter(screen);
                        break;
                    case WindowAlignType.CenterTop:
                        Settings.MainWindowLeft = HorizonCenter(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.LeftTop:
                        Settings.MainWindowLeft = HorizonLeft(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.RightTop:
                        Settings.MainWindowLeft = HorizonRight(screen);
                        Settings.MainWindowTop = VerticalTop(screen);
                        break;
                    case WindowAlignType.Custom:
                        var customLeft = Win32Helper.TransformPixelsToDIP(MainWindow,
                            screen.WorkingArea.X + Settings.CustomWindowLeft, 0);
                        var customTop = Win32Helper.TransformPixelsToDIP(MainWindow, 0,
                            screen.WorkingArea.Y + Settings.CustomWindowTop);
                        Settings.MainWindowLeft = customLeft.X;
                        Settings.MainWindowTop = customTop.Y;
                        break;
                }
            }
        }
    }

    private void UpdatePositionNearCursor()
    {
        if (!PInvoke.GetCursorPos(out var cursorPosition))
            return;

        const double horizontalOffset = 16;
        const double verticalOffset = 20;
        const double edgePadding = 8;

        var cursorDip = Win32Helper.TransformPixelsToDIP(MainWindow, cursorPosition.X, cursorPosition.Y);

        var screen = MonitorInfo.GetCursorDisplayMonitor();
        UpdateMainWindowMaxHeightConstraint(screen);
        var workAreaTopLeft = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, screen.WorkingArea.Y);
        var workAreaBottomRight = Win32Helper.TransformPixelsToDIP(
            MainWindow,
            screen.WorkingArea.X + screen.WorkingArea.Width,
            screen.WorkingArea.Y + screen.WorkingArea.Height);

        var windowWidth = MainWindow.ActualWidth > 0 ? MainWindow.ActualWidth : Settings.MainWindowWidth;
        var windowHeight = MainWindow.ActualHeight > 0 ? MainWindow.ActualHeight : MainWindow.MinHeight;

        var left = cursorDip.X + horizontalOffset;
        var top = cursorDip.Y + verticalOffset;

        if (left + windowWidth > workAreaBottomRight.X - edgePadding)
            left = cursorDip.X - windowWidth - horizontalOffset;

        if (top + windowHeight > workAreaBottomRight.Y - edgePadding)
            top = cursorDip.Y - windowHeight - verticalOffset;

        var minLeft = workAreaTopLeft.X + edgePadding;
        var minTop = workAreaTopLeft.Y + edgePadding;
        var maxLeft = workAreaBottomRight.X - windowWidth - edgePadding;
        var maxTop = workAreaBottomRight.Y - windowHeight - edgePadding;

        if (maxLeft < minLeft) maxLeft = minLeft;
        if (maxTop < minTop) maxTop = minTop;

        Settings.MainWindowLeft = Math.Clamp(left, minLeft, maxLeft);
        Settings.MainWindowTop = Math.Clamp(top, minTop, maxTop);
    }

    /// <summary>
    /// 在窗口内容尺寸变化后，确保窗口底部不会超出当前屏幕工作区。
    /// </summary>
    [RelayCommand]
    private void AdjustPositionForContentSizeChanged()
    {
        if (_isAdjustingWindowPositionForContent || !IsMainWindowVisible || MainWindow.WindowState == WindowState.Minimized)
            return;

        var windowHeight = MainWindow.ActualHeight > 0 ? MainWindow.ActualHeight : MainWindow.MinHeight;
        if (windowHeight <= 0)
            return;

        try
        {
            _isAdjustingWindowPositionForContent = true;
            AdjustVerticalPositionWithinWorkArea();
        }
        finally
        {
            _isAdjustingWindowPositionForContent = false;
        }
    }

    /// <summary>
    /// 当窗口触底时，仅向上修正 Top，避免内容被屏幕底部遮挡。
    /// </summary>
    private void AdjustVerticalPositionWithinWorkArea()
    {
        const double edgePadding = 8;

        MonitorInfo screen;
        try
        {
            // 以主窗口句柄所在屏幕为准，避免多屏时误用鼠标屏幕。
            var windowHelper = new WindowInteropHelper(MainWindow);
            windowHelper.EnsureHandle();
            screen = MonitorInfo.GetNearestDisplayMonitor(windowHelper.Handle);
        }
        catch
        {
            screen = SelectedScreen();
        }

        UpdateMainWindowMaxHeightConstraint(screen);

        var workAreaTopLeft = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, screen.WorkingArea.Y);
        var workAreaBottomRight = Win32Helper.TransformPixelsToDIP(
            MainWindow,
            screen.WorkingArea.X + screen.WorkingArea.Width,
            screen.WorkingArea.Y + screen.WorkingArea.Height);

        var windowHeight = MainWindow.ActualHeight > 0 ? MainWindow.ActualHeight : MainWindow.MinHeight;
        var currentTop = Settings.MainWindowTop;
        var bottomLimit = workAreaBottomRight.Y - edgePadding;
        var currentBottom = currentTop + windowHeight;

        // 仅在触底时上移，未触底时保持原位避免抖动。
        if (currentBottom <= bottomLimit)
            return;

        var minTop = workAreaTopLeft.Y + edgePadding;
        var maxTop = bottomLimit - windowHeight;
        if (maxTop < minTop)
            maxTop = minTop;

        var targetTop = Math.Clamp(currentTop, minTop, maxTop);
        if (targetTop >= currentTop)
            return;

        Settings.MainWindowTop = targetTop;
    }

    private void AdjustPositionForResolutionChange()
    {
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        GetDpi(out var currentDpiX, out var currentDpiY);

        var previousLeft = Settings.MainWindowLeft;
        var previousTop = Settings.MainWindowTop;
        GetDpi(out var previousDpiX, out var previousDpiY);

        var widthRatio = screenWidth / Settings.PreviousScreenWidth;
        var heightRatio = screenHeight / Settings.PreviousScreenHeight;
        var dpiXRatio = currentDpiX / previousDpiX;
        var dpiYRatio = currentDpiY / previousDpiY;

        var newLeft = previousLeft * widthRatio * dpiXRatio;
        var newTop = previousTop * heightRatio * dpiYRatio;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;

        var maxX = screenLeft + screenWidth - MainWindow.ActualWidth;
        var maxY = screenTop + screenHeight - MainWindow.ActualHeight;

        Settings.MainWindowLeft = Math.Max(screenLeft, Math.Min(newLeft, maxX));
        Settings.MainWindowTop = Math.Max(screenTop, Math.Min(newTop, maxY));
    }

    private void GetDpi(out double dpiX, out double dpiY)
    {
        var source = PresentationSource.FromVisual(MainWindow);
        if (source != null && source.CompositionTarget != null)
        {
            var matrix = source.CompositionTarget.TransformToDevice;
            dpiX = 96 * matrix.M11;
            dpiY = 96 * matrix.M22;
        }
        else
        {
            dpiX = 96;
            dpiY = 96;
        }
    }

    private MonitorInfo SelectedScreen()
    {
        MonitorInfo screen;
        switch (Settings.WindowScreen)
        {
            case WindowScreenType.Cursor:
            case WindowScreenType.FollowMouse:
                screen = MonitorInfo.GetCursorDisplayMonitor();
                break;
            case WindowScreenType.Focus:
                screen = MonitorInfo.GetNearestDisplayMonitor(Win32Helper.GetForegroundWindow());
                break;
            case WindowScreenType.Primary:
                screen = MonitorInfo.GetPrimaryDisplayMonitor();
                break;
            case WindowScreenType.Custom:
                var allScreens = MonitorInfo.GetDisplayMonitors();
                if (Settings.CustomScreenNumber <= allScreens.Count)
                    screen = allScreens[Settings.CustomScreenNumber - 1];
                else
                    screen = allScreens[0];
                break;
            default:
                screen = MonitorInfo.GetDisplayMonitors()[0];
                break;
        }

        return screen ?? MonitorInfo.GetDisplayMonitors()[0];
    }

    private double HorizonCenter(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.Width, 0);
        var left = (dip2.X - MainWindow.ActualWidth) / 2 + dip1.X;
        return left;
    }

    private double VerticalCenter(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Y);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Height);
        var top = (dip2.Y - MainWindow.PART_Input.ActualHeight) / 4 + dip1.Y;
        return top;
    }

    private double HorizonRight(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var dip2 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.Width, 0);
        var left = (dip1.X + dip2.X - MainWindow.ActualWidth) - 10;
        return left;
    }

    private double HorizonLeft(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, screen.WorkingArea.X, 0);
        var left = dip1.X + 10;
        return left;
    }

    private double VerticalTop(MonitorInfo screen)
    {
        var dip1 = Win32Helper.TransformPixelsToDIP(MainWindow, 0, screen.WorkingArea.Y);
        var top = dip1.Y + 10;
        return top;
    }

    #endregion

    #region Global Hotkeys

    public void ToggleGlobalHotkey() => Settings.DisableGlobalHotkeys = !Settings.DisableGlobalHotkeys;

    #endregion

    #region Helpers & Event Handlers

    private void EnterInputTranslateMode()
    {
        if (_forceShowInputForInputTranslate)
            return;

        _forceShowInputForInputTranslate = true;
        NotifyInputVisibilityProperties();
    }

    private void ExitInputTranslateMode()
    {
        if (!_forceShowInputForInputTranslate)
            return;

        _forceShowInputForInputTranslate = false;
        NotifyInputVisibilityProperties();
    }

    private void NotifyInputVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsInputActuallyHidden));
        OnPropertyChanged(nameof(IsInputBoxVisible));
        OnPropertyChanged(nameof(IsLanguageSelectControlVisible));
    }

    partial void OnInputTextChanged(string value)
    {
        ResetTranslationLanguageState();

        if (!Settings.AutoTranslate)
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            _debounceExecutor.Cancel();
            return;
        }

        void Execute()
        {
            CancelAllOperations();
            App.Current.Dispatcher.Invoke(() => TranslateCommand.Execute(null));
            Show();
            UpdateCaret();
        }

        _debounceExecutor.Execute(Execute, TimeSpan.FromMilliseconds(Settings.AutoTranslateDelayMs));
    }

    private void ResetTranslationLanguageState()
    {
        ApplyIdentifiedLanguageState(IdentifiedLanguageState.Empty);
    }

    private void ApplyIdentifiedLanguageState(IdentifiedLanguageState state)
    {
        _identifiedLanguageState = state;
        SelectedIdentifiedLanguage = state.Language ?? LangEnum.Auto;
        CanSelectIdentifiedLanguage = state.Kind != IdentifiedLanguageStateKind.None;
        IdentifiedLanguage = BuildIdentifiedLanguageText(state);
        OnPropertyChanged(nameof(CurrentIdentifiedLanguageState));
    }

    private string BuildIdentifiedLanguageText(IdentifiedLanguageState state)
    {
        return state.Kind switch
        {
            IdentifiedLanguageStateKind.None => string.Empty,
            IdentifiedLanguageStateKind.Cache when state.Language.HasValue => GetLanguageDisplayText(state.Language.Value),
            IdentifiedLanguageStateKind.Cache => _i18n.GetTranslation("IdentifiedUnknown"),
            IdentifiedLanguageStateKind.Detected when state.Language.HasValue => GetLanguageDisplayText(state.Language.Value),
            _ => string.Empty
        };
    }

    private string GetLanguageDisplayText(LangEnum language)
    {
        var translation = _i18n.GetTranslation($"LangEnum{language}");
        return string.IsNullOrWhiteSpace(translation) ? language.ToString() : translation;
    }

    private bool CanSelectIdentifiedLanguageForCurrentText(LangEnum language)
    {
        return CanSelectIdentifiedLanguage &&
               CanTranslate &&
               language != LangEnum.Auto;
    }

    private bool CanSelectLanguageDetectorForCurrentText(LanguageDetectorType _) => CanTranslate;

    private async Task<TranslationLanguageContext> ResolveTranslationLanguageContextAsync(LangEnum? forcedSourceLanguage, CancellationToken cancellationToken)
    {
        if (forcedSourceLanguage is LangEnum forcedSource && forcedSource != LangEnum.Auto)
        {
            ApplyIdentifiedLanguageState(CreateDetectedIdentifiedLanguageState(forcedSource));
            return CreateTranslationLanguageContext(forcedSource, LanguageDetector.GetTargetLanguage(forcedSource));
        }

        var (_, source, target) = await LanguageDetector
            .GetLanguageAsync(InputText, cancellationToken, StartProcess, CompleteProcess, FinishProcess)
            .ConfigureAwait(false);

        return CreateTranslationLanguageContext(source, target);
    }

    private IdentifiedLanguageState CreateCacheIdentifiedLanguageState(HistoryModel history)
    {
        return new IdentifiedLanguageState(
            IdentifiedLanguageStateKind.Cache,
            ParseHistoryLanguage(history.EffectiveSourceLang) ?? ParseHistoryLanguage(history.SourceLang));
    }

    private static LangEnum? ParseHistoryLanguage(string? language)
    {
        if (Enum.TryParse<LangEnum>(language, true, out var parsed) && parsed != LangEnum.Auto)
            return parsed;

        return null;
    }

    private static IdentifiedLanguageState CreateDetectedIdentifiedLanguageState(LangEnum language)
        => new(IdentifiedLanguageStateKind.Detected, language);

    private TranslationLanguageContext CreateTranslationLanguageContext(LangEnum effectiveSource, LangEnum effectiveTarget)
        => new(Settings.SourceLang, Settings.TargetLang, effectiveSource, effectiveTarget);

    private void UpdateCaret()
    {
        MainWindow.PART_Input.SetCaretIndex(InputText.Length);
    }

    private string HandleCapturedText(string text, TextSeparatorHandleScope scope)
    {
        return Utilities.CapturedTextHandler(
            text,
            Settings.LineBreakHandleType,
            Settings.TextSeparatorHandleType,
            scope,
            Settings.TextSeparatorHandleScopes);
    }

    private string HandleSilentOcrText(string text)
    {
        if (Settings.TextSeparatorHandleType == TextSeparatorHandleType.None ||
            (Settings.TextSeparatorHandleScopes & TextSeparatorHandleScope.SilentOcr) != TextSeparatorHandleScope.SilentOcr)
        {
            return text;
        }

        return HandleCapturedText(text, TextSeparatorHandleScope.SilentOcr);
    }

    private void ResetAllServices()
    {
        var services = TranslateService.Services.Where(x => x.IsEnabled).ToList();
        foreach (var service in services)
        {
            service.Options?.TemporaryDisplay = false;
            if (service.Plugin is ITranslatePlugin tPlugin) tPlugin.Reset();
            else if (service.Plugin is IDictionaryPlugin dPlugin) dPlugin.Reset();
        }
    }

    private void CancelAllOperations()
    {
        SingleTranslateCancelCommand.Execute(null);
        SingleTransBackCancelCommand.Execute(null);
        TranslateCancelCommand.Execute(null);
        PlayAudioCancelCommand.Execute(null);
        PlayAudioUrlCancelCommand.Execute(null);
        ScreenshotTranslateCancelCommand.Execute(null);
        SaveToVocabularyCancelCommand.Execute(null);
    }

    private async Task<(bool success, string text)> GetTextAsync(bool showFailureNotification = true)
    {
        try
        {
            var text = await ClipboardHelper.GetSelectedTextAsync(Settings.SelectedTextFetchTimeoutMs);
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("取词失败，可能：未选中文本、文本禁止复制、取词间隔过短、文本所属软件权限高于本软件");
                if (showFailureNotification)
                {
                    _notification.Show("未识别到文本", "请确保选中要翻译的文本\n若问题仍然存在请尝试以管理员权限重启软件");
                }
                return (false, string.Empty);
            }
            return (true, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取剪贴板异常请重试");
            return (false, string.Empty);
        }
    }

    private void HandleCrosswordFetchFailed()
    {
        switch (Settings.CrosswordFetchFailedFallbackTarget)
        {
            case CrosswordFetchFailedFallbackTarget.ShowWindow:
                Show();
                _snackbar.ShowWarning(_i18n.GetTranslation("CrosswordTranslateFetchFailedShowWindow"), 3000);
                break;
            case CrosswordFetchFailedFallbackTarget.InputTranslate:
            default:
                InputClear();
                _snackbar.ShowWarning(_i18n.GetTranslation("CrosswordTranslateFetchFailed"), 3000);
                break;
        }
    }

    private void StartProcess()
    {
        ApplyIdentifiedLanguageState(IdentifiedLanguageState.Empty);
        IsIdentifyProcessing = true;
    }
    private void FinishProcess() => IsIdentifyProcessing = false;
    private void CompleteProcess(bool _, LangEnum source)
    {
        ApplyIdentifiedLanguageState(CreateDetectedIdentifiedLanguageState(source));
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        MouseKeyHelper.MouseTextSelected -= OnMouseTextSelected;
        MouseKeyHelper.MouseTextSelected -= OnMouseTextSelectedIncretemental;
        _clipboardMonitor?.OnClipboardTextChanged -= OnClipboardTextChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;

        _debounceExecutor.Dispose();
        _clipboardMonitor?.Dispose();

        // 如果窗口一直没打开过，恢复位置后再退出
        if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
        {
            Settings.MainWindowLeft = _cacheLeft;
            Settings.MainWindowTop = _cacheTop;
            Settings.Save();
        }

        _i18n.OnLanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    #endregion
}
