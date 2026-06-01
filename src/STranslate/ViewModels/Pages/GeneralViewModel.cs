using CommunityToolkit.Mvvm.Input;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace STranslate.ViewModels.Pages;

public partial class GeneralViewModel : SearchViewModelBase
{
    public GeneralViewModel(
        Settings settings,
        DataProvider dataProvider,
        Internationalization i18n) : base(i18n, "General_")
    {
        Settings = settings;
        DataProvider = dataProvider;
        Languages = i18n.LoadAvailableLanguages();

        InitializeMainHeaderActions();

        VisibleHeaderActions.CollectionChanged += OnMainHeaderActionsChanged;
        AvailableHeaderActions.CollectionChanged += OnMainHeaderActionsChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private bool _isSyncingMainHeaderActions;
    private bool _isMainHeaderSyncPending;

    public ObservableCollection<string> VisibleHeaderActions { get; } = [];

    public ObservableCollection<string> AvailableHeaderActions { get; } = [];

    [RelayCommand]
    private void ResetFontFamily() => Settings.FontFamily = Win32Helper.GetSystemDefaultFont();

    [RelayCommand]
    private void ResetAutoTransDelay() => Settings.AutoTranslateDelayMs = 500;

    [RelayCommand]
    private void ResetFontSize() => Settings.FontSize = 14;

    [RelayCommand]
    private void ResetMainWindowMaxHeightRatio() => Settings.MainWindowMaxHeightRatio = 0.85;

    [RelayCommand]
    private void ShowAllHeaderActions()
    {
        _isSyncingMainHeaderActions = true;
        try
        {
            ReplaceItems(VisibleHeaderActions, MainHeaderActions.DefaultOrder);
            ReplaceItems(AvailableHeaderActions, []);
        }
        finally
        {
            _isSyncingMainHeaderActions = false;
        }

        SyncMainHeaderActions();
    }

    [RelayCommand]
    private void HideAllHeaderActions()
    {
        _isSyncingMainHeaderActions = true;
        try
        {
            ReplaceItems(VisibleHeaderActions, []);
            ReplaceItems(AvailableHeaderActions, MainHeaderActions.DefaultOrder);
        }
        finally
        {
            _isSyncingMainHeaderActions = false;
        }

        SyncMainHeaderActions();
    }

    public List<int> ScreenNumbers
    {
        get
        {
            var screens = MonitorInfo.GetDisplayMonitors();
            var screenNumbers = new List<int>();
            for (int i = 1; i <= screens.Count; i++)
            {
                screenNumbers.Add(i);
            }

            return screenNumbers;
        }
    }
    public Settings Settings { get; }
    public DataProvider DataProvider { get; }

    public List<I18nPair> Languages { get; }

    public string ExcludedGlobalTriggerProcessesText
    {
        get => string.Join(Environment.NewLine, Settings.ExcludedGlobalTriggerProcesses);
        set
        {
            var processes = value
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (Settings.ExcludedGlobalTriggerProcesses.SequenceEqual(processes, StringComparer.OrdinalIgnoreCase))
                return;

            Settings.ExcludedGlobalTriggerProcesses = processes;
            OnPropertyChanged();
        }
    }

    public bool IsTextSeparatorMouseHookScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.MouseHook);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.MouseHook, value);
    }

    public bool IsTextSeparatorCrosswordScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.Crossword);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.Crossword, value);
    }

    public bool IsTextSeparatorIncrementalScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.Incremental);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.Incremental, value);
    }

    public bool IsTextSeparatorClipboardMonitorScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.ClipboardMonitor);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.ClipboardMonitor, value);
    }

    public bool IsTextSeparatorScreenshotTranslateScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.ScreenshotTranslate);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.ScreenshotTranslate, value);
    }

    public bool IsTextSeparatorSilentOcrScopeEnabled
    {
        get => IsTextSeparatorScopeEnabled(TextSeparatorHandleScope.SilentOcr);
        set => SetTextSeparatorScope(TextSeparatorHandleScope.SilentOcr, value);
    }

    private bool IsTextSeparatorScopeEnabled(TextSeparatorHandleScope scope)
        => (Settings.TextSeparatorHandleScopes & scope) == scope;

    private void SetTextSeparatorScope(TextSeparatorHandleScope scope, bool isEnabled)
    {
        var current = Settings.TextSeparatorHandleScopes;
        var updated = isEnabled ? current | scope : current & ~scope;
        if (updated == current)
            return;

        Settings.TextSeparatorHandleScopes = updated;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.TextSeparatorHandleScopes))
        {
            OnTextSeparatorScopePropertiesChanged();
        }
        else if (e.PropertyName == nameof(Settings.ExcludedGlobalTriggerProcesses))
        {
            OnPropertyChanged(nameof(ExcludedGlobalTriggerProcessesText));
        }
    }

    private void OnTextSeparatorScopePropertiesChanged()
    {
        OnPropertyChanged(nameof(IsTextSeparatorMouseHookScopeEnabled));
        OnPropertyChanged(nameof(IsTextSeparatorCrosswordScopeEnabled));
        OnPropertyChanged(nameof(IsTextSeparatorIncrementalScopeEnabled));
        OnPropertyChanged(nameof(IsTextSeparatorClipboardMonitorScopeEnabled));
        OnPropertyChanged(nameof(IsTextSeparatorScreenshotTranslateScopeEnabled));
        OnPropertyChanged(nameof(IsTextSeparatorSilentOcrScopeEnabled));
    }

    private void InitializeMainHeaderActions()
    {
        Settings.EnsureMainHeaderVisibleActionsInitialized();

        var normalizedVisible = MainHeaderActions.Normalize(Settings.MainHeaderVisibleActions);

        _isSyncingMainHeaderActions = true;
        try
        {
            ReplaceItems(VisibleHeaderActions, normalizedVisible);

            var visibleSet = normalizedVisible.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableActions = MainHeaderActions.DefaultOrder
                .Where(action => !visibleSet.Contains(action))
                .ToList();

            ReplaceItems(AvailableHeaderActions, availableActions);
        }
        finally
        {
            _isSyncingMainHeaderActions = false;
        }

        Settings.ApplyMainHeaderVisibleActions(normalizedVisible);
    }

    private void OnMainHeaderActionsChanged(object? _, NotifyCollectionChangedEventArgs __)
    {
        if (_isSyncingMainHeaderActions || _isMainHeaderSyncPending)
        {
            return;
        }

        _isMainHeaderSyncPending = true;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            _isMainHeaderSyncPending = false;
            SyncMainHeaderActions();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _isMainHeaderSyncPending = false;
            SyncMainHeaderActions();
        }));
    }

    private void SyncMainHeaderActions()
    {
        if (_isSyncingMainHeaderActions)
        {
            return;
        }

        _isSyncingMainHeaderActions = true;
        try
        {
            var normalizedVisible = MainHeaderActions.Normalize(VisibleHeaderActions);
            ReplaceItemsIfDifferent(VisibleHeaderActions, normalizedVisible);

            var visibleSet = normalizedVisible.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableActions = MainHeaderActions.DefaultOrder
                .Where(action => !visibleSet.Contains(action))
                .ToList();
            ReplaceItemsIfDifferent(AvailableHeaderActions, availableActions);

            Settings.ApplyMainHeaderVisibleActions(normalizedVisible);
        }
        finally
        {
            _isSyncingMainHeaderActions = false;
        }
    }

    private static void ReplaceItems(ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static void ReplaceItemsIfDifferent(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        if (target.Count == source.Count && target.SequenceEqual(source))
        {
            return;
        }

        ReplaceItems(target, source);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }

        base.Dispose(disposing);
    }
}
