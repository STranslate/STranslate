using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Resources;
using STranslate.Services;
using STranslate.Views;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly MouseSelectionService _mouseSelectionService;
    private readonly MouseSelectionIconWindow _mouseSelectionIconWindow;
    private bool _mouseSelectionTranslationHasTopmostLease;
    private bool _incrementalHasTopmostLease;
    private int _managedTopmostLeaseCount;
    private bool _topmostBeforeManagedLeases;
    private bool _isApplyingManagedTopmost;
    private double _cacheLeft;
    private double _cacheTop;
    private bool _isAdjustingWindowPositionForContent;

    private DynamicIslandWindow? _dynamicIslandWindow;
    private bool _isDynamicIslandActive;

    public TranslateService TranslateService { get; }
    public OcrService OcrService { get; }
    public TtsService TtsService { get; }
    public VocabularyService VocabularyService { get; }
    public ObservableCollection<ServiceQuickAccessItem> QuickServiceItems { get; } = [];

    private readonly SqlService _sqlService;
    private readonly DebounceExecutor _debounceExecutor;
    private ClipboardMonitor? _clipboardMonitor;
    private bool _forceShowInputForInputTranslate;
    private bool _skipShowForNextTranslate;
    private bool _disposed;
    private readonly object _manualTranslationTaskLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _manualTranslationTaskTokens = [];
    private readonly SemaphoreSlim _manualTranslationHistoryLock = new(1, 1);
    private readonly TranslationResultCoordinator _translationCoordinator;

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
        HotkeySettings hotkeySettings,
        MouseSelectionService mouseSelectionService,
        MouseSelectionIconWindow mouseSelectionIconWindow)
    {
        DataProvider = dataProvider;
        IdentifiedLanguageOptions = DataProvider.LangEnums
            .Where(x => x.Value != LangEnum.Auto)
            .Cast<DropdownDataGeneric<LangEnum>>()
            .ToList();
        _logger = logger;
        _i18n = i18n;
        _translationCoordinator = new(i18n.GetTranslation);
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
        _mouseSelectionService = mouseSelectionService;
        _mouseSelectionIconWindow = mouseSelectionIconWindow;
        _mouseSelectionService.TextSelected += OnMouseSelectionTextSelected;
        _mouseSelectionService.IncrementalTextSelected += OnIncrementalMouseTextSelected;
        _mouseSelectionService.SelectionStarted += OnMouseSelectionStarted;
        _mouseSelectionService.IconRequested += OnMouseSelectionIconRequested;
        _mouseSelectionService.IconDismissRequested += OnMouseSelectionIconDismissRequested;
        _mouseSelectionService.StateChanged += OnMouseSelectionStateChanged;
        _mouseSelectionIconWindow.TranslateRequested += OnMouseSelectionIconTranslateRequested;

        TranslateService.Services.CollectionChanged += OnQuickServiceCollectionChanged;
        OcrService.Services.CollectionChanged += OnQuickServiceCollectionChanged;
        TtsService.Services.CollectionChanged += OnQuickServiceCollectionChanged;
        VocabularyService.Services.CollectionChanged += OnQuickServiceCollectionChanged;
        RebuildQuickServiceItems();

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

    #region Quick Service Switcher

    private void OnQuickServiceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildQuickServiceItems();

    private void RebuildQuickServiceItems()
    {
        QuickServiceItems.Clear();

        var hasPreviousGroup = false;
        AddQuickServiceGroup(TranslateService.Services, ref hasPreviousGroup);
        AddQuickServiceGroup(OcrService.Services, ref hasPreviousGroup);
        AddQuickServiceGroup(TtsService.Services, ref hasPreviousGroup);
        AddQuickServiceGroup(VocabularyService.Services, ref hasPreviousGroup);
    }

    private void AddQuickServiceGroup(IEnumerable<Service> services, ref bool hasPreviousGroup)
    {
        var isFirstItem = true;
        foreach (var service in services)
        {
            QuickServiceItems.Add(new ServiceQuickAccessItem(
                service,
                ShowSeparatorBefore: isFirstItem && hasPreviousGroup));
            isFirstItem = false;
        }

        if (!isFirstItem)
            hasPreviousGroup = true;
    }

    [RelayCommand]
    private void ToggleQuickService(Service service) => service.IsEnabled = !service.IsEnabled;

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
    [NotifyCanExecuteChangedFor(nameof(SingleTransBackCommand))]
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
            if (Settings.IsMouseSelectionTranslationEnabled &&
                !value &&
                !_isApplyingManagedTopmost)
            {
                AppMessageBox.Show(
                    _i18n.GetTranslation("MouseSelectionTranslationRequiresTopmost"),
                    _i18n.GetTranslation("Prompt"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

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
    public void ExecuteTranslate(string text, string? force = null)
    {
        ExitInputTranslateMode();
        CancelAllOperations();
        ResetTranslationLanguageState();
        InputText = text;
        TranslateCommand.Execute(force);

        var skipShow = _skipShowForNextTranslate;
        _skipShowForNextTranslate = false;

        // 灵动岛模式：取词翻译时用屏幕顶部胶囊展示结果，替代弹出原翻译窗口
        if (ShouldUseDynamicIsland())
        {
            ShowDynamicIsland();
            return;
        }

        if (skipShow)
            return;

        Show();
        UpdateCaret();
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync(object? force, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAutomaticTranslationAsync(force, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消属于正常控制流，不应让取消异常越过命令边界。
        }
    }

    private async Task ExecuteAutomaticTranslationAsync(object? force, CancellationToken cancellationToken)
    {
        // 取消防抖执行器中的待执行任务
        _debounceExecutor.Cancel();
        var operation = _translationCoordinator.BeginAutomaticOperation(
            InputText,
            Settings.SourceLang,
            Settings.TargetLang,
            cancellationToken);

        LangEnum? forcedSourceLanguage = force is LangEnum language && language != LangEnum.Auto
            ? language
            : null;

        ResetAllServices(operation);
        ApplyIdentifiedLanguageState(forcedSourceLanguage.HasValue
            ? CreateDetectedIdentifiedLanguageState(forcedSourceLanguage.Value)
            : IdentifiedLanguageState.Empty);

        // force 空则优先检查缓存
        var checkCacheFirst = force == null;

        var history = await ExecuteTranslateAsync(
            operation,
            checkCacheFirst,
            forcedSourceLanguage);

        operation.CancellationToken.ThrowIfCancellationRequested();
        if (!operation.IsLatestAutomatic)
            return;

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
                        operation.TryPublishAutomatic(
                            () =>
                            {
                                ClipboardHelper.SetText(textToCopy);
                                _snackbar.ShowSuccess(string.Format(
                                    _i18n.GetTranslation("CopiedToClipboard"),
                                    service.DisplayName));
                            });
                    }
                }
            }
        }

        #region 历史记录处理

        if (Settings.HistoryLimit > 0 && history != null && history.Data.Count != 0)
        {
            if (!operation.IsLatestAutomatic)
                return;

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

            if (!_recentTexts.Contains(operation.Text))
                _recentTexts.Insert(0, operation.Text);
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

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanSingleTranslate))]
    private async Task SingleTranslateAsync(Service service)
    {
        if (!TryStartManualTranslation(service, out var cancellationTokenSource))
            return;

        var operation = _translationCoordinator.BeginOperation(
            InputText,
            Settings.SourceLang,
            Settings.TargetLang,
            cancellationTokenSource.Token);
        try
        {
            await ExecuteSingleTranslateAsync(
                service,
                operation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // Ignore
        }
        finally
        {
            FinishManualTranslation(service, cancellationTokenSource);
        }
    }

    private async Task ExecuteSingleTranslateAsync(
        Service service,
        TranslationOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Text))
            return;

        switch (service.Plugin)
        {
            case IDictionaryPlugin dictionaryPlugin:
                if (!operation.TryPrepare(dictionaryPlugin))
                    return;

                var result = await operation.LookupAsync(dictionaryPlugin).ConfigureAwait(false);
                if (result.ResultType == DictionaryResultType.Error ||
                    !operation.IsCurrent(dictionaryPlugin.DictionaryResult))
                    return;

                if (Settings.CopyAfterTranslationNotAutomatic)
                {
                    var copied = operation.TryPublish(
                        dictionaryPlugin.DictionaryResult,
                        () =>
                        {
                            ClipboardHelper.SetText(result.Text);
                            _snackbar.ShowSuccess(string.Format(
                                _i18n.GetTranslation("CopiedToClipboard"),
                                service.DisplayName));
                        });
                    if (!copied)
                        return;
                }

                var history = await _sqlService.GetDataAsync(
                    operation.Text,
                    operation.SourceLang.ToString(),
                    operation.TargetLang.ToString());
                history ??= CreateHistoryModel(
                    operation.Text,
                    operation.SourceLang,
                    operation.TargetLang);
                // 词典手动执行保持现有历史语义：仅更新内存对象，不额外落盘。
                history.Data.Add(new(service) { DictResult = result });
                return;

            case ITranslatePlugin plugin:
                if (plugin.TransResult.IsProcessing)
                    return;

                operation.ActivateIdentifiedLanguage();
                var context = await ResolveTranslationLanguageContextAsync(
                    operation,
                    null).ConfigureAwait(false);
                if (!operation.TryPrepare(plugin))
                    return;

                var translateResult = await operation.TranslateAsync(
                    plugin,
                    context.EffectiveSource,
                    context.EffectiveTarget).ConfigureAwait(false);
                if (!translateResult.IsSuccess ||
                    !operation.IsCurrent(plugin.TransResult))
                    return;

                if (Settings.CopyAfterTranslationNotAutomatic)
                {
                    var copied = operation.TryPublish(
                        plugin.TransResult,
                        () =>
                        {
                            ClipboardHelper.SetText(translateResult.Text);
                            _snackbar.ShowSuccess(string.Format(
                                _i18n.GetTranslation("CopiedToClipboard"),
                                service.DisplayName));
                        });
                    if (!copied)
                        return;
                }

                var historyData = new HistoryData(service)
                {
                    TransResult = CloneTranslateResult(translateResult)
                };

                if (service.Options?.AutoBackTranslation ?? false)
                {
                    var backResult = await operation.BackTranslateAsync(
                        plugin,
                        translateResult.Text,
                        context.EffectiveTarget,
                        context.EffectiveSource).ConfigureAwait(false);
                    if (backResult.IsSuccess &&
                        operation.IsCurrent(plugin.TransBackResult))
                    {
                        historyData.TransBackResult = CloneTranslateResult(backResult);
                    }
                }

                await MergeManualHistoryDataAsync(
                    operation,
                    context,
                    service,
                    historyData,
                    plugin.TransResult,
                    createIfMissing: true).ConfigureAwait(false);
                return;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanSingleTransBack))]
    private async Task SingleTransBackAsync(Service service)
    {
        if (!TryStartManualTranslation(service, out var cancellationTokenSource))
            return;

        var operation = _translationCoordinator.BeginOperation(
            InputText,
            Settings.SourceLang,
            Settings.TargetLang,
            cancellationTokenSource.Token);
        try
        {
            await ExecuteSingleTransBackAsync(
                service,
                operation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            // Ignore
        }
        finally
        {
            FinishManualTranslation(service, cancellationTokenSource);
        }
    }

    private async Task ExecuteSingleTransBackAsync(
        Service service,
        TranslationOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Text) ||
            service.Plugin is not ITranslatePlugin plugin ||
            plugin.TransBackResult.IsProcessing)
            return;

        var sourceText = plugin.TransResult.Text;
        operation.ActivateIdentifiedLanguage();
        var context = await ResolveTranslationLanguageContextAsync(
            operation,
            null).ConfigureAwait(false);
        if (!operation.TryPrepareBack(plugin))
            return;

        var backResult = await operation.BackTranslateAsync(
            plugin,
            sourceText,
            context.EffectiveTarget,
            context.EffectiveSource).ConfigureAwait(false);
        if (!backResult.IsSuccess ||
            !operation.IsCurrent(plugin.TransBackResult))
            return;

        var historyData = new HistoryData(service)
        {
            TransBackResult = CloneTranslateResult(backResult)
        };

        await MergeManualHistoryDataAsync(
            operation,
            context,
            service,
            historyData,
            plugin.TransBackResult,
            createIfMissing: false).ConfigureAwait(false);
    }

    private bool CanSingleTranslate(Service? service) =>
        CanRunManualTranslation(service) &&
        service?.Plugin is ITranslatePlugin or IDictionaryPlugin;

    private bool CanSingleTransBack(Service? service) =>
        CanRunManualTranslation(service) &&
        service?.Plugin is ITranslatePlugin;

    private bool CanRunManualTranslation(Service? service) =>
        service != null &&
        CanTranslate &&
        !IsManualTranslationRunning(service);

    private bool TryStartManualTranslation(Service service, out CancellationTokenSource cancellationTokenSource)
    {
        var key = GetManualTranslationTaskKey(service);
        lock (_manualTranslationTaskLock)
        {
            if (_manualTranslationTaskTokens.ContainsKey(key))
            {
                cancellationTokenSource = null!;
                return false;
            }

            cancellationTokenSource = new CancellationTokenSource();
            _manualTranslationTaskTokens[key] = cancellationTokenSource;
        }

        NotifyManualTranslationCanExecuteChanged();
        return true;
    }

    private void FinishManualTranslation(Service service, CancellationTokenSource cancellationTokenSource)
    {
        var key = GetManualTranslationTaskKey(service);
        lock (_manualTranslationTaskLock)
        {
            if (_manualTranslationTaskTokens.TryGetValue(key, out var current) &&
                ReferenceEquals(current, cancellationTokenSource))
            {
                _manualTranslationTaskTokens.Remove(key);
            }
        }

        cancellationTokenSource.Dispose();
        NotifyManualTranslationCanExecuteChanged();
    }

    private void CancelSingleTranslationTasks()
    {
        List<CancellationTokenSource> cancellationTokenSources;
        lock (_manualTranslationTaskLock)
        {
            cancellationTokenSources = [.. _manualTranslationTaskTokens.Values];
        }

        foreach (var cancellationTokenSource in cancellationTokenSources)
        {
            if (!cancellationTokenSource.IsCancellationRequested)
                cancellationTokenSource.Cancel();
        }
    }

    private bool IsManualTranslationRunning(Service service)
    {
        lock (_manualTranslationTaskLock)
        {
            return _manualTranslationTaskTokens.ContainsKey(GetManualTranslationTaskKey(service));
        }
    }

    private void NotifyManualTranslationCanExecuteChanged()
    {
        void Notify()
        {
            SingleTranslateCommand.NotifyCanExecuteChanged();
            SingleTransBackCommand.NotifyCanExecuteChanged();
        }

        if (Application.Current.Dispatcher.CheckAccess())
            Notify();
        else
            Application.Current.Dispatcher.Invoke(Notify);
    }

    private static string GetManualTranslationTaskKey(Service service) =>
        $"{service.MetaData.PluginID}:{service.ServiceID}";

    [RelayCommand]
    private void SwapLanguage()
    {
        if (string.IsNullOrWhiteSpace(InputText) ||
            (Settings.SourceLang == Settings.TargetLang && Settings.SourceLang == LangEnum.Auto))
            return;

        CancelAllOperations();
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

    private async Task<HistoryModel?> ExecuteTranslateAsync(
        TranslationOperation operation,
        bool checkCacheFirst,
        LangEnum? forcedSourceLanguage)
    {
        var enabledSvcs = TranslateService.Services
            .Where(x => x.IsEnabled && x.Options?.ExecMode == ExecutionMode.Automatic)
            .ToList();
        if (enabledSvcs.Count == 0)
            return null;

        HistoryModel? history = null;
        var uncachedSvcs = new List<Service>(enabledSvcs);

        // 尝试从缓存加载
        if (checkCacheFirst && Settings.HistoryLimit > 0)
        {
            history = await _sqlService.GetDataAsync(
                operation.Text,
                operation.SourceLang.ToString(),
                operation.TargetLang.ToString());
            operation.CancellationToken.ThrowIfCancellationRequested();

            if (history != null)
            {
                operation.PublishIdentifiedLanguage(
                    () => ApplyIdentifiedLanguageState(CreateCacheIdentifiedLanguageState(history)));
                uncachedSvcs = PopulateResultsFromCache(
                    history,
                    enabledSvcs,
                    operation);
            }
        }

        if (uncachedSvcs.Count == 0)
            return history;

        var context = await ResolveTranslationLanguageContextAsync(
            operation,
            forcedSourceLanguage);

        history ??= CreateHistoryModel(operation.Text, context.CacheSource, context.CacheTarget);
        ApplyEffectiveLanguages(history, context.EffectiveSource, context.EffectiveTarget);

        await ExecuteTranslationForServicesAsync(
            uncachedSvcs,
            context.EffectiveSource,
            context.EffectiveTarget,
            history,
            operation);

        return history;
    }

    private HistoryModel CreateHistoryModel(string sourceText, LangEnum source, LangEnum target)
    {
        return new HistoryModel
        {
            Time = DateTime.Now,
            SourceText = sourceText,
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

    private async Task MergeManualHistoryDataAsync(
        TranslationOperation operation,
        TranslationLanguageContext context,
        Service service,
        HistoryData incomingData,
        object resultChannel,
        bool createIfMissing)
    {
        if (Settings.HistoryLimit <= 0 ||
            !operation.IsCurrent(resultChannel))
            return;

        await _manualTranslationHistoryLock.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
        try
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!operation.IsCurrent(resultChannel))
                return;

            var history = await _sqlService.GetDataAsync(
                operation.Text,
                operation.SourceLang.ToString(),
                operation.TargetLang.ToString());
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!operation.IsCurrent(resultChannel))
                return;

            if (history == null)
            {
                if (!createIfMissing)
                    return;

                history = CreateHistoryModel(operation.Text, context.CacheSource, context.CacheTarget);
            }

            ApplyEffectiveLanguages(history, context.EffectiveSource, context.EffectiveTarget);

            var existingData = history.GetData(service);
            if (existingData == null)
            {
                if (!createIfMissing)
                    return;

                history.Data.Add(incomingData);
            }
            else
            {
                MergeHistoryData(existingData, incomingData);
            }

            var enabledServices = TranslateService.Services.Where(x => x.IsEnabled).ToList();
            history.Data = [.. history.Data.OrderBy(data => enabledServices.FindIndex(svc => svc.ServiceID.Equals(data.ServiceID)))];
            if (!operation.IsCurrent(resultChannel))
                return;

            await _sqlService.InsertOrUpdateDataAsync(history, (long)Settings.HistoryLimit).ConfigureAwait(false);
        }
        finally
        {
            _manualTranslationHistoryLock.Release();
        }
    }

    private static void MergeHistoryData(HistoryData existingData, HistoryData incomingData)
    {
        existingData.ServiceDisplayName = incomingData.ServiceDisplayName ?? existingData.ServiceDisplayName;
        existingData.TransResult = incomingData.TransResult ?? existingData.TransResult;
        existingData.TransBackResult = incomingData.TransBackResult ?? existingData.TransBackResult;
        existingData.DictResult = incomingData.DictResult ?? existingData.DictResult;
    }

    private static TranslateResult CloneTranslateResult(TranslateResult result) =>
        new()
        {
            IsSuccess = result.IsSuccess,
            Text = result.Text,
            Duration = result.Duration
        };

    /// <summary>
    /// 从缓存填充翻译结果，并返回未缓存的服务列表
    /// </summary>
    private List<Service> PopulateResultsFromCache(
        HistoryModel history,
        List<Service> services,
        TranslationOperation operation)
    {
        var uncachedServices = new List<Service>();
        foreach (var service in services)
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (history.GetData(service) is { } data)
            {
                PopulateServiceResultFromData(service, data, operation);
                if (!history.HasData(service))
                    uncachedServices.Add(service);
            }
            else
            {
                uncachedServices.Add(service);
            }
        }

        return uncachedServices;
    }

    /// <summary>
    /// 根据历史数据填充单个服务的结果
    /// </summary>
    private static void PopulateServiceResultFromData(
        Service svc,
        HistoryData data,
        TranslationOperation operation)
    {
        if (svc.Plugin is ITranslatePlugin tPlugin)
        {
            if (data.TransResult != null && data.TransResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransResult.Text))
            {
                operation.TryPublish(
                    tPlugin.TransResult,
                    () => tPlugin.TransResult.Update(data.TransResult));
            }

            if ((svc.Options?.AutoBackTranslation ?? false) && data.TransBackResult != null && data.TransBackResult.IsSuccess && !string.IsNullOrWhiteSpace(data.TransBackResult.Text))
            {
                operation.TryPublish(
                    tPlugin.TransBackResult,
                    () => tPlugin.TransBackResult.Update(data.TransBackResult));
            }
        }
        else if (svc.Plugin is IDictionaryPlugin dPlugin)
        {
            if (data.DictResult != null && data.DictResult.ResultType != DictionaryResultType.Error && data.DictResult.ResultType != DictionaryResultType.None)
                operation.PublishDictionary(
                    data.DictResult,
                    dPlugin.DictionaryResult);
        }
    }

    /// <summary>
    /// 为指定的服务列表执行翻译
    /// </summary>
    private async Task ExecuteTranslationForServicesAsync(
        IEnumerable<Service> services,
        LangEnum source,
        LangEnum target,
        HistoryModel history,
        TranslationOperation operation)
    {
        var maxConcurrency = Math.Min(services.Count(), Environment.ProcessorCount * 10);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var translateTasks = services.Select(svc =>
            ExecuteTranslationHandlerAsync(
                svc,
                source,
                target,
                semaphore,
                history,
                operation));

        try
        {
            await Task.WhenAll(translateTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
    }

    private async Task ExecuteTranslationHandlerAsync(
        Service svc,
        LangEnum source,
        LangEnum target,
        SemaphoreSlim semaphore,
        HistoryModel history,
        TranslationOperation operation)
    {
        await semaphore.WaitAsync(operation.CancellationToken).ConfigureAwait(false);
        try
        {
            switch (svc.Plugin)
            {
                case ITranslatePlugin translatePlugin:
                    await ProcessTranslatePluginAsync(
                        svc,
                        translatePlugin,
                        source,
                        target,
                        history,
                        operation).ConfigureAwait(false);
                    break;
                case IDictionaryPlugin dictionaryPlugin:
                    await ProcessDictionaryPluginAsync(
                        svc,
                        dictionaryPlugin,
                        history,
                        operation).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ProcessTranslatePluginAsync(
        Service service,
        ITranslatePlugin plugin,
        LangEnum source,
        LangEnum target,
        HistoryModel history,
        TranslationOperation operation)
    {
        // 如果历史记录中没有该服务的数据，则执行全新翻译
        if (history.GetData(service) == null)
        {
            await ExecuteNewTranslationAsync(
                service,
                plugin,
                source,
                target,
                history,
                operation).ConfigureAwait(false);
        }
        // 否则，只执行反向翻译（如果需要）
        else if ((service.Options?.AutoBackTranslation ?? false) && history.GetData(service)?.TransBackResult == null)
        {
            await ExecuteBackTranslationOnlyAsync(
                service,
                plugin,
                target,
                source,
                history,
                operation).ConfigureAwait(false);
        }
    }

    private async Task ExecuteNewTranslationAsync(
        Service service,
        ITranslatePlugin plugin,
        LangEnum source,
        LangEnum target,
        HistoryModel history,
        TranslationOperation operation)
    {
        var translateResult = await operation.TranslateAsync(
            plugin,
            source,
            target).ConfigureAwait(false);
        if (!translateResult.IsSuccess ||
            !operation.IsCurrent(plugin.TransResult))
            return;

        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.TransResult = translateResult;

        if (service.Options?.AutoBackTranslation ?? false)
        {
            var backResult = await operation.BackTranslateAsync(
                plugin,
                translateResult.Text,
                target,
                source).ConfigureAwait(false);
            if (operation.IsCurrent(plugin.TransBackResult))
                historyData.TransBackResult = backResult;
        }
    }

    private async Task ExecuteBackTranslationOnlyAsync(
        Service service,
        ITranslatePlugin plugin,
        LangEnum target,
        LangEnum source,
        HistoryModel history,
        TranslationOperation operation)
    {
        if (history.GetData(service) is not { } historyData ||
            string.IsNullOrWhiteSpace(historyData.TransResult?.Text))
            return;

        var backResult = await operation.BackTranslateAsync(
            plugin,
            historyData.TransResult.Text,
            target,
            source).ConfigureAwait(false);
        if (!operation.IsCurrent(plugin.TransBackResult))
            return;

        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.TransBackResult = backResult;
    }

    private async Task ProcessDictionaryPluginAsync(
        Service service,
        IDictionaryPlugin plugin,
        HistoryModel history,
        TranslationOperation operation)
    {
        // 如果缓存中已存在数据则跳过
        if (history.HasData(service))
            return;

        var result = await operation.LookupAsync(plugin).ConfigureAwait(false);
        if (result.ResultType == DictionaryResultType.Error ||
            !operation.IsCurrent(plugin.DictionaryResult))
            return;

        var historyData = new HistoryData(service);
        history.Data.Add(historyData);
        UpdateHistoryServiceSnapshot(historyData, service);
        historyData.DictResult = result;
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
            var result = await ocrPlugin.RecognizeAsync(
                new OcrRequest(data, Settings.ScreenshotOcrLanguage, bitmap.Width, bitmap.Height),
                cancellationToken);
            Utilities.PrepareOcrResult(result);

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
            Show();
            _snackbar.ShowError($"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
            _logger.LogError(ex, "OCR execution failed");
        }
        finally
        {
            CursorHelper.Restore();
        }
    }

    [RelayCommand]
    private Task ImageTranslateAsync() => ImageTranslateInternalAsync(hideExistingWindows: true);

    internal async Task ImageTranslateInternalAsync(bool hideExistingWindows)
    {
        var ocrPlugin = GetImageTranslateOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        if (TranslateService.ImageTranslateService == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("ImageTranslateServiceNotFoundTitle"),
                _i18n.GetTranslation("ImageTranslateServiceNotFoundMessage"),
                nameof(TranslatePage));
            return;
        }

        var existingWindows = hideExistingWindows
            ? Application.Current.Windows
                .OfType<Window>()
                .Where(w => w is ImageTranslateWindow or ImageTranslateCompactWindow)
                .ToList()
            : [];
        var ocr = ocrPlugin;
        await ExecuteWithWindowsHiddenAsync(existingWindows, async () =>
        {
            using var captureResult = await _screenshot.GetScreenshotCaptureAsync();
            await ImageTranslateHandlerAsync(captureResult?.Bitmap, ocr, captureResult?.PhysicalBounds);
        });
    }

    public async Task ImageTranslateHandlerAsync(Bitmap? bitmap, IOcrPlugin? ocrPlugin = default, Rectangle? physicalBounds = default)
    {
        if (bitmap == null) return;

        ocrPlugin ??= GetImageTranslateOcrSvcAndNotify();
        if (ocrPlugin == null)
            return;

        if (Settings.ImageTranslateWindowMode == ImageTranslateWindowMode.Compact)
        {
            Task? executeTask = null;
            await SingletonWindowOpener.OpenPreparedAsync<ImageTranslateCompactWindow>(window =>
            {
                window.PlaceForCapture(physicalBounds, bitmap.Size);
                executeTask = ((ImageTranslateWindowViewModel)window.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
            });

            if (executeTask != null)
                await executeTask;
            return;
        }

        var standaloneWindow = await SingletonWindowOpener.OpenAsync<ImageTranslateWindow>();
        await ((ImageTranslateWindowViewModel)standaloneWindow.DataContext).ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private Task OcrAsync() => OcrInternalAsync(hideExistingWindow: true);

    internal async Task OcrInternalAsync(bool hideExistingWindow)
    {
        if (GetOcrSvcAndNotify() == null)
            return;

        var existingWindow = hideExistingWindow
            ? Application.Current.Windows.OfType<OcrWindow>().FirstOrDefault()
            : null;
        await ExecuteWithWindowsHiddenAsync(existingWindow, async () =>
        {
            using var bitmap = await _screenshot.GetScreenshotAsync();
            await OcrHandlerAsync(bitmap);
        });
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

        var existingWindow = Application.Current.Windows.OfType<OcrWindow>().FirstOrDefault();
        await ExecuteWithWindowsHiddenAsync(existingWindow, async () =>
        {
            using var bitmap = await _screenshot.GetScreenshotAsync();
            await QrCodeHandlerAsync(bitmap);
        });
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
            var result = await ocrPlugin.RecognizeAsync(
                new OcrRequest(data, Settings.ScreenshotOcrLanguage, bitmap.Width, bitmap.Height),
                cancellationToken);
            Utilities.PrepareOcrResult(result);
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
            Show();
            _snackbar.ShowError($"{_i18n.GetTranslation("OcrFailed")}\n{ex.Message}");
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
            Helper.PromptConfigureService(
                _i18n.GetTranslation("OcrServiceNotFoundTitle"),
                _i18n.GetTranslation("OcrServiceNotFoundMessage"),
                nameof(OcrPage));
            return default;
        }

        return svc;
    }

    private IOcrPlugin? GetImageTranslateOcrSvcAndNotify()
    {
        var svc = OcrService.GetImageTranslateOcrSvcOrDefault();
        if (svc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundTitle"),
                _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"),
                nameof(OcrPage));
            return default;
        }

        return svc;
    }

    /// <summary>
    /// 临时隐藏指定窗口，执行操作后恢复显示（无论成功、失败或用户取消截图）。
    /// 用于截图前隐藏已开的结果窗口，避免其遮挡截图选区；
    /// 操作结束后由 <see cref="SingletonWindowOpener"/> 复用同一窗口显示新结果，
    /// 此处 <see cref="Window.Show()"/> 对已可见窗口为空操作，安全。
    /// </summary>
    private static Task ExecuteWithWindowsHiddenAsync(Window? window, Func<Task> action)
        => ExecuteWithWindowsHiddenAsync(window == null ? [] : new[] { window }, action);

    private static async Task ExecuteWithWindowsHiddenAsync(
        IEnumerable<Window?> windows,
        Func<Task> action)
    {
        var toHide = windows.Where(w => w != null).Cast<Window>().ToList();
        foreach (var w in toHide)
            w.Hide();
        try
        {
            await action();
        }
        finally
        {
            foreach (var w in toHide)
                w.Show();
        }
    }

    #endregion

    #region TTS & Audio Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioAsync(string text, CancellationToken cancellationToken)
    {
        var ttsSvc = TtsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("TtsServiceNotFound"),
                nameof(TtsPage));
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
        var (success, text) = await GetTextAsync();
        if (!success || string.IsNullOrWhiteSpace(text))
            return;
        await SilentTtsHandlerAsync(text, default, cancellationToken);
    }

    public async Task SilentTtsHandlerAsync(string text, ITtsPlugin? ttsSvc = default, CancellationToken cancellationToken = default)
    {
        ttsSvc ??= TtsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("TtsServiceNotFound"),
                nameof(TtsPage));
            return;
        }

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
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("VocabularyServiceNotFound"),
                nameof(VocabularyPage));
            return;
        }

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
        if (vocabularySvc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("VocabularyServiceNotFound"),
                nameof(VocabularyPage));
            return;
        }

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
        AcquireManagedTopmost(ref _incrementalHasTopmostLease);

        // 增量翻译触发时清空原本内容（默认开启），false 时保留旧逻辑不清空
        if (Settings.IncrementalClearInput)
            InputText = string.Empty;

        UpdateCacheText();

        _mouseSelectionService.StartIncrementalCapture();
    }

    public void OnIncKeyReleased()
    {
        ReleaseManagedTopmost(ref _incrementalHasTopmostLease);
        _mouseSelectionService.StopIncrementalCapture();

        if (string.IsNullOrWhiteSpace(InputText) || _oldText == InputText)
            return;

        Show();
        // 执行翻译
        CancelAllOperations();
        TranslateCommand.Execute(null);
        UpdateCaret();
        UpdateCacheText();
    }

    private string _oldText = string.Empty;

    private void UpdateCacheText()
    {
        _oldText = InputText;
    }

    private void OnIncrementalMouseTextSelected(object? sender, string text)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            InputText += HandleCapturedText(text, TextSeparatorHandleScope.Incremental);
        });
    }

    #endregion

    #region Mouse Selection Feature

    [RelayCommand]
    private void ToggleMouseSelectionTranslation() =>
        Settings.IsMouseSelectionTranslationEnabled = !Settings.IsMouseSelectionTranslationEnabled;

    private async void OnMouseSelectionIconRequested(object? sender, System.Drawing.Point drawingPoint)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var point = new System.Windows.Point(drawingPoint.X, drawingPoint.Y);
                _mouseSelectionIconWindow.ShowAt(point);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show the mouse selection icon.");
        }
    }

    private async void OnMouseSelectionStarted(object? sender, System.Drawing.Point point)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_mouseSelectionIconWindow.ContainsPhysicalPoint(point))
                    _mouseSelectionIconWindow.HideWindow();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process mouse selection start.");
        }
    }

    private async void OnMouseSelectionIconTranslateRequested(object? sender, EventArgs e)
    {
        var text = await _mouseSelectionService.CaptureIconSelectedTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
            ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.MouseSelection));
    }

    private void OnMouseSelectionTextSelected(object? sender, string text)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ExecuteTranslate(HandleCapturedText(text, TextSeparatorHandleScope.MouseSelection));
        });
    }

    private void OnMouseSelectionIconDismissRequested(object? sender, EventArgs e)
        => _ = Application.Current.Dispatcher.InvokeAsync(_mouseSelectionIconWindow.HideWindow);

    private void OnMouseSelectionStateChanged(object? sender, EventArgs e)
        => _ = Application.Current.Dispatcher.InvokeAsync(ApplyMouseSelectionWindowMode);

    private void ApplyMouseSelectionWindowMode()
    {
        var shouldForceTopmost = Settings.IsMouseSelectionTranslationEnabled;
        if (shouldForceTopmost)
        {
            Show();
            AcquireManagedTopmost(ref _mouseSelectionTranslationHasTopmostLease);
            return;
        }

        ReleaseManagedTopmost(ref _mouseSelectionTranslationHasTopmostLease);
    }

    private void AcquireManagedTopmost(ref bool lease)
    {
        if (lease)
            return;

        if (_managedTopmostLeaseCount == 0)
            _topmostBeforeManagedLeases = IsTopmost;

        lease = true;
        _managedTopmostLeaseCount++;
        SetTopmostInternally(true);
    }

    private void ReleaseManagedTopmost(ref bool lease)
    {
        if (!lease)
            return;

        lease = false;
        _managedTopmostLeaseCount--;
        if (_managedTopmostLeaseCount == 0)
            SetTopmostInternally(_topmostBeforeManagedLeases);
    }

    private void SetTopmostInternally(bool value)
    {
        _isApplyingManagedTopmost = true;
        try
        {
            IsTopmost = value;
        }
        finally
        {
            _isApplyingManagedTopmost = false;
        }
    }

    [RelayCommand]
    private async Task CrosswordTranslateAsync()
    {
        var (success, text) = await GetTextAsync();
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
            // Ctrl+C+C 由其他前台应用触发，需要越过前台锁确保翻译窗口可见。
            using var _ = WindowActivationContext.Push(WindowActivationMode.ForceForeground);

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
            Helper.PromptConfigureService(
                _i18n.GetTranslation("ReplaceTranslateServiceNotFoundTitle"),
                _i18n.GetTranslation("ReplaceTranslateServiceNotFoundMessage"),
                nameof(TranslatePage));
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
                _snackbar.ShowWarning(_i18n.GetTranslation("LanguageDetectionFailed"));
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

    public void Show()
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

        Win32Helper.ActivateForegroundWindow(MainWindow);

        MainWindow.Activate();

        if (IsInputBoxVisible)
        {
            MainWindow.PART_Input.Focus();
            Keyboard.Focus(MainWindow.PART_Input);
        }

        // 主窗口已展示，收起灵动岛
        HideDynamicIsland();
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
            case DoubleClickTrayFunction.ToggleMouseSelectionTranslation:
                ToggleMouseSelectionTranslationCommand.Execute(null);
                break;
            case DoubleClickTrayFunction.ToggleGlobalHotkeys:
                ToggleGlobalHotkey();
                break;
            case DoubleClickTrayFunction.Exit:
                Exit(AppShutdownReason.TrayDoubleClick);
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
    private void ToggleApp()
    {
        if (IsMainWindowVisible && !IsTopmost)
            Hide();
        else
            Show();
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        if (!Settings.IsMouseSelectionTranslationEnabled)
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

    internal async Task OpenSettingsAndNavigateAsync(object? parameter)
    {
        var isAlreadyOpen = Application.Current.Windows.OfType<SettingsWindow>().Any();
        var window = await OpenSettingsInternalAsync(parameter);

        if (!isAlreadyOpen)
            window.Navigate(nameof(GeneralPage));
    }

    internal async Task<SettingsWindow> OpenSettingsInternalAsync(object? parameter)
    {
        // 如果由 ContextMenu 触发，等待关闭动画完成
        if (parameter is not null)
            await Task.Delay(ContextMenuCloseAnimationDelay);

        // 如果从主窗口打开设置，主动隐藏主窗口
        if (MainWindow.IsActive && IsMainWindowVisible && !IsTopmost)
            Hide();

        return await SingletonWindowOpener.OpenAsync<SettingsWindow>();
    }

    [RelayCommand]
    private async Task OpenHistoryAsync()
    {
        await OpenHistoryInternalAsync();
    }

    internal async Task OpenHistoryInternalAsync()
    {
        var window = await OpenSettingsInternalAsync(null);
        window.Navigate(nameof(HistoryPage));
    }

    [RelayCommand]
    private async Task NavigateAsync(Service service)
    {
        var window = await OpenSettingsInternalAsync(string.Empty);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            window.Navigate(nameof(TranslatePage), selectedService: service);
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
    private void Exit(AppShutdownReason reason) => App.RequestShutdown(reason);

    #endregion

    #region Dynamic Island

    private bool ShouldUseDynamicIsland()
        => Settings.DynamicIslandEnabled && !IsMainWindowVisible;

    private DynamicIslandWindow EnsureDynamicIslandWindow()
    {
        if (_dynamicIslandWindow == null)
        {
            _dynamicIslandWindow = new DynamicIslandWindow();
            _dynamicIslandWindow.IslandDoubleClicked += OnDynamicIslandDoubleClicked;
        }

        // 同步灵动岛尺寸、时长等配置
        _dynamicIslandWindow.AutoHideDuration = TimeSpan.FromSeconds(Math.Max(1, Settings.DynamicIslandDurationSeconds));
        _dynamicIslandWindow.IslandMinWidth = Settings.DynamicIslandMinWidth;
        _dynamicIslandWindow.IslandMaxWidth = Settings.DynamicIslandMaxWidth;
        _dynamicIslandWindow.IslandHeight = Settings.DynamicIslandHeight;
        _dynamicIslandWindow.IslandTopMargin = Settings.DynamicIslandTopMargin;
        _dynamicIslandWindow.IslandFontSize = Settings.DynamicIslandFontSize;
        return _dynamicIslandWindow;
    }

    private void ShowDynamicIsland()
    {
        EnsureDynamicIslandWindow();
        _isDynamicIslandActive = true;
        SubscribeDynamicIslandResultEvents();
        _dynamicIslandWindow!.ShowIsland();
        UpdateDynamicIslandText();
    }

    private void HideDynamicIsland()
    {
        _isDynamicIslandActive = false;
        _dynamicIslandWindow?.HideIsland();
    }

    private void OnDynamicIslandDoubleClicked(object? sender, EventArgs e)
    {
        // 双击灵动岛：收起灵动岛并弹出原翻译窗口
        HideDynamicIsland();
        Show();
    }

    /// <summary>
    /// 订阅各翻译服务的可见结果通道，翻译完成时刷新灵动岛内容。
    /// </summary>
    private void SubscribeDynamicIslandResultEvents()
    {
        foreach (var service in TranslateService.Services)
        {
            switch (service.Plugin)
            {
                case ITranslatePlugin tp:
                    tp.TransResult.PropertyChanged -= OnDynamicIslandResultChanged;
                    tp.TransResult.PropertyChanged += OnDynamicIslandResultChanged;
                    break;
                case IDictionaryPlugin dp:
                    dp.DictionaryResult.PropertyChanged -= OnDynamicIslandResultChanged;
                    dp.DictionaryResult.PropertyChanged += OnDynamicIslandResultChanged;
                    break;
            }
        }
    }

    private void OnDynamicIslandResultChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isDynamicIslandActive) return;
        if (e.PropertyName is not (nameof(TranslateResult.Text) or nameof(TranslateResult.IsProcessing))) return;

        _ = Application.Current.Dispatcher.BeginInvoke(UpdateDynamicIslandText, DispatcherPriority.Background);
    }

    private void UpdateDynamicIslandText()
    {
        if (!_isDynamicIslandActive || _dynamicIslandWindow == null) return;

        var text = GetDynamicIslandResultText();
        if (string.IsNullOrWhiteSpace(text)) return;

        _dynamicIslandWindow.UpdateResult(text);
    }

    private string GetDynamicIslandResultText()
    {
        // 优先返回成功结果
        foreach (var service in TranslateService.Services)
        {
            if (!service.IsEnabled) continue;

            switch (service.Plugin)
            {
                case ITranslatePlugin tp when tp.TransResult.IsSuccess && !string.IsNullOrWhiteSpace(tp.TransResult.Text):
                    return tp.TransResult.Text;
                case IDictionaryPlugin dp when !string.IsNullOrWhiteSpace(dp.DictionaryResult.Text):
                    return dp.DictionaryResult.Text;
            }
        }

        // 兜底：展示失败信息
        foreach (var service in TranslateService.Services)
        {
            if (!service.IsEnabled) continue;
            if (service.Plugin is ITranslatePlugin tp && !string.IsNullOrWhiteSpace(tp.TransResult.Text))
                return tp.TransResult.Text;
        }

        return string.Empty;
    }

    #endregion

    #region Text & Clipboard Manipulation

    [RelayCommand]
    private void InputClear()
    {
        CancelAllOperations();
        ResetTranslationLanguageState();
        InputText = string.Empty;

        var operation = _translationCoordinator.BeginAutomaticOperation(
            InputText,
            Settings.SourceLang,
            Settings.TargetLang,
            CancellationToken.None);
        ResetAllServices(operation);
        EnterInputTranslateMode();
        Show();
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
            RestartTranslation);

    [RelayCommand]
    private void RemoveSpaces(TextBox textBox) =>
        Utilities.TransformText(
            textBox,
            t => t.Replace(" ", ""),
            RestartTranslation);

    private void RestartTranslation()
    {
        CancelAllOperations();
        TranslateCommand.Execute(null);
    }

    [RelayCommand]
    private void CleanTransBack(ITranslatePlugin plugin)
    {
        var operation = _translationCoordinator.BeginOperation(
            InputText,
            Settings.SourceLang,
            Settings.TargetLang,
            CancellationToken.None);
        operation.TryPrepareBack(plugin);
    }

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
        _translationCoordinator.ResetIdentifiedLanguage(
            () => ApplyIdentifiedLanguageState(IdentifiedLanguageState.Empty));
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

    private async Task<TranslationLanguageContext> ResolveTranslationLanguageContextAsync(
        TranslationOperation operation,
        LangEnum? forcedSourceLanguage)
    {
        if (forcedSourceLanguage is LangEnum forcedSource && forcedSource != LangEnum.Auto)
        {
            operation.PublishIdentifiedLanguage(
                () => ApplyIdentifiedLanguageState(CreateDetectedIdentifiedLanguageState(forcedSource)));
            return CreateTranslationLanguageContext(
                operation.SourceLang,
                operation.TargetLang,
                forcedSource,
                LanguageDetector.GetTargetLanguage(forcedSource, operation.TargetLang));
        }

        var (_, source, target) = await LanguageDetector
            .GetLanguageAsync(
                operation.Text,
                operation.SourceLang,
                operation.TargetLang,
                operation.CancellationToken,
                () => operation.PublishIdentifiedLanguage(StartProcess),
                (isSuccess, detectedSource) => operation.PublishIdentifiedLanguage(
                    () => CompleteProcess(isSuccess, detectedSource)),
                () => operation.PublishIdentifiedLanguage(FinishProcess))
            .ConfigureAwait(false);

        return CreateTranslationLanguageContext(
            operation.SourceLang,
            operation.TargetLang,
            source,
            target);
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

    private static TranslationLanguageContext CreateTranslationLanguageContext(
        LangEnum cacheSource,
        LangEnum cacheTarget,
        LangEnum effectiveSource,
        LangEnum effectiveTarget)
        => new(cacheSource, cacheTarget, effectiveSource, effectiveTarget);

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

    private void ResetAllServices(TranslationOperation operation)
    {
        var services = TranslateService.Services.Where(x => x.IsEnabled).ToList();
        foreach (var service in services)
        {
            service.Options?.TemporaryDisplay = false;
            if (service.Plugin is ITranslatePlugin translatePlugin)
                operation.TryPrepare(translatePlugin);
            else if (service.Plugin is IDictionaryPlugin dictionaryPlugin)
                operation.TryPrepare(dictionaryPlugin);
        }
    }

    private void CancelAllOperations()
    {
        CancelSingleTranslationTasks();
        TranslateCancelCommand.Execute(null);
        PlayAudioCancelCommand.Execute(null);
        PlayAudioUrlCancelCommand.Execute(null);
        ScreenshotTranslateCancelCommand.Execute(null);
        SaveToVocabularyCancelCommand.Execute(null);
    }

    private async Task<(bool success, string text)> GetTextAsync()
    {
        try
        {
            var text = await ClipboardHelper.GetSelectedTextAsync(Settings.SelectedTextFetchTimeoutMs);
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("取词失败，可能：未选中文本、文本禁止复制、取词间隔过短、文本所属软件权限高于本软件");
                Show();
                _snackbar.ShowWarning(_i18n.GetTranslation("NoTextRecognizedMessage"));
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
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _mouseSelectionService.TextSelected -= OnMouseSelectionTextSelected;
            _mouseSelectionService.IncrementalTextSelected -= OnIncrementalMouseTextSelected;
            _mouseSelectionService.SelectionStarted -= OnMouseSelectionStarted;
            _mouseSelectionService.IconRequested -= OnMouseSelectionIconRequested;
            _mouseSelectionService.IconDismissRequested -= OnMouseSelectionIconDismissRequested;
            _mouseSelectionService.StateChanged -= OnMouseSelectionStateChanged;
            _mouseSelectionIconWindow.TranslateRequested -= OnMouseSelectionIconTranslateRequested;
            _clipboardMonitor?.OnClipboardTextChanged -= OnClipboardTextChanged;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            TranslateService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            OcrService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            TtsService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            VocabularyService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;

            _debounceExecutor.Dispose();
            _clipboardMonitor?.Dispose();
            _dynamicIslandWindow?.Dispose();
            _dynamicIslandWindow = null;

            // 如果窗口一直没打开过，恢复位置后再退出
            if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
            {
                Settings.MainWindowLeft = _cacheLeft;
                Settings.MainWindowTop = _cacheTop;
                Settings.Save();
            }

            _i18n.OnLanguageChanged -= OnLanguageChanged;
        }

        _disposed = true;
    }

    #endregion
}

public sealed record ServiceQuickAccessItem(Service Service, bool ShowSeparatorBefore);
