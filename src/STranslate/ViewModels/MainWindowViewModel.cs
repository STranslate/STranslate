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
    private readonly CursorOcrService _cursorOcrService;
    private double _cacheLeft;
    private double _cacheTop;
    private bool _isAdjustingWindowPositionForContent;
    private bool _avoidCursorForNextShow;

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
        CursorOcrService cursorOcrService,
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
        _cursorOcrService = cursorOcrService;
        TranslateService = translateService;
        OcrService = ocrService;
        TtsService = ttsService;
        VocabularyService = vocabularyService;
        _sqlService = sqlService;
        Settings = settings;
        HotkeySettings = hotkeySettings;

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

    private sealed record ManualTranslationSnapshot(
        string Text,
        LangEnum SourceLang,
        LangEnum TargetLang);

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
            if (IsMouseHook && !value)
                AppMessageBox.Show("监听鼠标划词时窗口必须置顶", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
    public void ExecuteTranslate(string text, string? force = null)
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

        Show();
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

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanSingleTranslate))]
    private async Task SingleTranslateAsync(Service service)
    {
        var snapshot = CreateManualTranslationSnapshot();
        if (!TryStartManualTranslation(service, out var cancellationTokenSource))
            return;

        try
        {
            await ExecuteSingleTranslateAsync(service, snapshot, cancellationTokenSource.Token).ConfigureAwait(false);
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
        ManualTranslationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Text))
            return;

        switch (service.Plugin)
        {
            case IDictionaryPlugin dictionaryPlugin:
                var result = await ExecuteDictAsync(dictionaryPlugin, snapshot.Text, cancellationToken).ConfigureAwait(false);
                if (result.ResultType == DictionaryResultType.Error)
                    return;

                if (Settings.CopyAfterTranslationNotAutomatic)
                {
                    ClipboardHelper.SetText(result.Text);
                    _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("CopiedToClipboard"), service.DisplayName));
                }

                var history = await _sqlService.GetDataAsync(
                    snapshot.Text,
                    snapshot.SourceLang.ToString(),
                    snapshot.TargetLang.ToString());
                history ??= CreateHistoryModel(snapshot.Text, snapshot.SourceLang, snapshot.TargetLang);
                // 词典手动执行保持现有历史语义：仅更新内存对象，不额外落盘。
                history.Data.Add(new(service) { DictResult = result });
                return;

            case ITranslatePlugin plugin:
                if (plugin.TransResult.IsProcessing)
                    return;

                var context = await ResolveTranslationLanguageContextAsync(snapshot, null, cancellationToken).ConfigureAwait(false);
                var translateResult = await ExecuteAsync(plugin, snapshot.Text, context.EffectiveSource, context.EffectiveTarget, cancellationToken).ConfigureAwait(false);
                if (!plugin.TransResult.IsSuccess)
                    return;

                if (Settings.CopyAfterTranslationNotAutomatic)
                {
                    ClipboardHelper.SetText(translateResult.Text);
                    _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("CopiedToClipboard"), service.DisplayName));
                }

                var historyData = new HistoryData(service)
                {
                    TransResult = CloneTranslateResult(translateResult)
                };

                if (service.Options?.AutoBackTranslation ?? false)
                {
                    var backResult = await ExecuteBackAsync(
                        plugin,
                        context.EffectiveTarget,
                        context.EffectiveSource,
                        cancellationToken).ConfigureAwait(false);
                    historyData.TransBackResult = CloneTranslateResult(backResult);
                }

                await MergeManualHistoryDataAsync(snapshot, context, service, historyData, createIfMissing: true, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanSingleTransBack))]
    private async Task SingleTransBackAsync(Service service)
    {
        var snapshot = CreateManualTranslationSnapshot();
        if (!TryStartManualTranslation(service, out var cancellationTokenSource))
            return;

        try
        {
            await ExecuteSingleTransBackAsync(service, snapshot, cancellationTokenSource.Token).ConfigureAwait(false);
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
        ManualTranslationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Text) ||
            service.Plugin is not ITranslatePlugin plugin ||
            plugin.TransBackResult.IsProcessing)
            return;

        var context = await ResolveTranslationLanguageContextAsync(snapshot, null, cancellationToken).ConfigureAwait(false);
        var backResult = await ExecuteBackAsync(
            plugin,
            context.EffectiveTarget,
            context.EffectiveSource,
            cancellationToken).ConfigureAwait(false);
        if (!plugin.TransResult.IsSuccess)
            return;

        var historyData = new HistoryData(service)
        {
            TransBackResult = CloneTranslateResult(backResult)
        };

        await MergeManualHistoryDataAsync(snapshot, context, service, historyData, createIfMissing: false, cancellationToken).ConfigureAwait(false);
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

    private ManualTranslationSnapshot CreateManualTranslationSnapshot() =>
        new(InputText, Settings.SourceLang, Settings.TargetLang);

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
        => CreateHistoryModel(InputText, source, target);

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
        ManualTranslationSnapshot snapshot,
        TranslationLanguageContext context,
        Service service,
        HistoryData incomingData,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (Settings.HistoryLimit <= 0)
            return;

        await _manualTranslationHistoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var history = await _sqlService.GetDataAsync(
                snapshot.Text,
                snapshot.SourceLang.ToString(),
                snapshot.TargetLang.ToString());

            if (history == null)
            {
                if (!createIfMissing)
                    return;

                history = CreateHistoryModel(snapshot.Text, context.CacheSource, context.CacheTarget);
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

    private Task<DictionaryResult> ExecuteDictAsync(IDictionaryPlugin plugin, CancellationToken cancellationToken) =>
        ExecuteDictAsync(plugin, InputText, cancellationToken);

    private async Task<DictionaryResult> ExecuteDictAsync(IDictionaryPlugin plugin, string text, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.DictionaryResult.IsProcessing = true;
            await plugin.TranslateAsync(text, plugin.DictionaryResult, cancellationToken).ConfigureAwait(false);
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

    private Task<TranslateResult> ExecuteAsync(ITranslatePlugin plugin, LangEnum source, LangEnum target, CancellationToken cancellationToken) =>
        ExecuteAsync(plugin, InputText, source, target, cancellationToken);

    private async Task<TranslateResult> ExecuteAsync(ITranslatePlugin plugin, string text, LangEnum source, LangEnum target, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        try
        {
            plugin.Reset();
            plugin.TransResult.IsProcessing = true;
            await plugin.TranslateAsync(new TranslateRequest(text, source, target), plugin.TransResult, cancellationToken).ConfigureAwait(false);
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
    private async Task ImageTranslateAsync()
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

        var existingWindows = Application.Current.Windows
            .OfType<Window>()
            .Where(w => w is ImageTranslateWindow or ImageTranslateCompactWindow)
            .ToList();
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
    private async Task OcrAsync()
    {
        if (GetOcrSvcAndNotify() == null)
            return;

        var existingWindow = Application.Current.Windows.OfType<OcrWindow>().FirstOrDefault();
        await ExecuteWithWindowsHiddenAsync(existingWindow, async () =>
        {
            using var bitmap = await _screenshot.GetScreenshotAsync();
            await OcrHandlerAsync(bitmap);
        });
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CursorOcrTranslateAsync(CancellationToken cancellationToken)
    {
        var result = await _cursorOcrService.RecognizeWordUnderCursorAsync(cancellationToken);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Text))
        {
            Show();
            _snackbar.ShowWarning(string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? _i18n.GetTranslation("CursorOcrTranslateFailed")
                : result.ErrorMessage);
            return;
        }

        _avoidCursorForNextShow = true;
        ExecuteTranslate(result.Text);
        if (HotkeySettings.CursorOcrAutoPlayAudio)
            await PlayAudioAsync(result.Text, cancellationToken);
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
        IsTopmost = true;

        // 增量翻译触发时清空原本内容（默认开启），false 时保留旧逻辑不清空
        if (Settings.IncrementalClearInput)
            InputText = string.Empty;

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
        var avoidCursor = _avoidCursorForNextShow;
        _avoidCursorForNextShow = false;

        if (Settings.MainWindowLeft <= -18000 && Settings.MainWindowTop <= -18000)
        {
            Settings.MainWindowLeft = _cacheLeft;
            Settings.MainWindowTop = _cacheTop;
        }
        MainWindow.Visibility = Visibility.Visible;
        UpdateMainWindowMaxHeightConstraint();
        if (avoidCursor)
            UpdatePositionAwayFromCapturedWord();
        else
        {
            UpdatePosition();
            UpdateMainWindowMaxHeightConstraint();
        }

        Win32Helper.ActivateForegroundWindow(MainWindow);

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
    private void Exit() => Application.Current.Shutdown();

    #endregion

    #region Text & Clipboard Manipulation

    [RelayCommand]
    private void InputClear()
    {
        CancelAllOperations();
        ResetTranslationLanguageState();
        InputText = string.Empty;

        ResetAllServices();
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

    private void UpdatePositionAwayFromCapturedWord()
    {
        if (!PInvoke.GetCursorPos(out var cursorPosition))
        {
            UpdatePosition();
            return;
        }

        const double wordGap = 44;
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
        var minLeft = workAreaTopLeft.X + edgePadding;
        var minTop = workAreaTopLeft.Y + edgePadding;
        var maxLeft = Math.Max(minLeft, workAreaBottomRight.X - windowWidth - edgePadding);

        var availableBelow = Math.Max(0, workAreaBottomRight.Y - edgePadding - cursorDip.Y - wordGap);
        var availableAbove = Math.Max(0, cursorDip.Y - wordGap - minTop);
        var placeBelow = availableBelow >= availableAbove;
        var availableHeight = placeBelow ? availableBelow : availableAbove;

        MainWindowEffectiveMaxHeight = Math.Max(MainWindow.MinHeight, availableHeight);
        MainWindow.UpdateLayout();

        var windowHeight = Math.Min(
            MainWindow.ActualHeight > 0 ? MainWindow.ActualHeight : MainWindow.MinHeight,
            MainWindowEffectiveMaxHeight);
        var left = cursorDip.X <= (workAreaTopLeft.X + workAreaBottomRight.X) / 2
            ? maxLeft
            : minLeft;
        var top = placeBelow
            ? cursorDip.Y + wordGap
            : cursorDip.Y - wordGap - windowHeight;

        Settings.MainWindowLeft = Math.Clamp(left, minLeft, maxLeft);
        Settings.MainWindowTop = Math.Clamp(top, minTop, Math.Max(minTop, workAreaBottomRight.Y - windowHeight - edgePadding));
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

    private async Task<TranslationLanguageContext> ResolveTranslationLanguageContextAsync(
        ManualTranslationSnapshot snapshot,
        LangEnum? forcedSourceLanguage,
        CancellationToken cancellationToken)
    {
        if (forcedSourceLanguage is LangEnum forcedSource && forcedSource != LangEnum.Auto)
        {
            ApplyIdentifiedLanguageState(CreateDetectedIdentifiedLanguageState(forcedSource));
            return CreateTranslationLanguageContext(
                snapshot.SourceLang,
                snapshot.TargetLang,
                forcedSource,
                LanguageDetector.GetTargetLanguage(forcedSource, snapshot.TargetLang));
        }

        var (_, source, target) = await LanguageDetector
            .GetLanguageAsync(
                snapshot.Text,
                snapshot.SourceLang,
                snapshot.TargetLang,
                cancellationToken,
                StartProcess,
                CompleteProcess,
                FinishProcess)
            .ConfigureAwait(false);

        return CreateTranslationLanguageContext(snapshot.SourceLang, snapshot.TargetLang, source, target);
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
            MouseKeyHelper.MouseTextSelected -= OnMouseTextSelected;
            MouseKeyHelper.MouseTextSelected -= OnMouseTextSelectedIncretemental;
            _clipboardMonitor?.OnClipboardTextChanged -= OnClipboardTextChanged;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            TranslateService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            OcrService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            TtsService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;
            VocabularyService.Services.CollectionChanged -= OnQuickServiceCollectionChanged;

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
        }

        _disposed = true;
    }

    #endregion
}

public sealed record ServiceQuickAccessItem(Service Service, bool ShowSeparatorBefore);
