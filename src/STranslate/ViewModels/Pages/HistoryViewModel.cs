using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using ObservableCollections;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using System.IO;
using System.Windows;

namespace STranslate.ViewModels.Pages;

public partial class HistoryViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 20;
    private const int searchDelayMilliseconds = 500;

    private readonly SqlService _sqlService;
    private readonly ISnackbar _snackbar;
    private readonly Internationalization _i18n;
    private readonly DebounceExecutor _searchDebouncer;

    private CancellationTokenSource? _searchCts;
    private DateTime _lastCursorTime = DateTime.Now;
    private bool _disposed;
    private int _pendingLoads;
    private int _listVersion;
    private bool _hasMore = true;

    private bool CanLoadMore =>
        !_disposed && _pendingLoads == 0 && _hasMore &&
        string.IsNullOrWhiteSpace(SearchText) &&
        (TotalCount == 0 || _items.Count != TotalCount);

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ExportRequestDetails { get; set; }

    /// <summary>
    /// <see href="https://blog.coldwind.top/posts/more-observable-collections/"/>
    /// </summary>
    private readonly ObservableList<HistoryModel> _items = [];

    public INotifyCollectionChangedSynchronizedViewList<HistoryModel> HistoryItems { get; }

    [ObservableProperty] public partial HistoryModel? SelectedListItem { get; set; }

    [ObservableProperty] public partial ObservableList<object> SelectedItems { get; set; } = [];

    [ObservableProperty] public partial HistoryModel? SelectedItem { get; set; }

    [ObservableProperty] public partial long TotalCount { get; set; }

    public bool CanExportHistory => SelectedItems.Count > 0;

    public HistoryViewModel(
        SqlService sqlService,
        ISnackbar snackbar,
        Internationalization i18n)
    {
        _sqlService = sqlService;
        _snackbar = snackbar;
        _i18n = i18n;
        _searchDebouncer = new();

        HistoryItems = _items.ToNotifyCollectionChanged();
        SelectedItems.CollectionChanged += OnSelectedItemsCollectionChanged;

        _ = RefreshAsync();
    }

    partial void OnSelectedListItemChanged(HistoryModel? value) => SelectedItem = value;

    // 搜索文本变化时修改定时器
    partial void OnSearchTextChanged(string value)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _listVersion);
        _searchCts?.Cancel();
        LoadMoreCommand.NotifyCanExecuteChanged();
        _searchDebouncer.ExecuteAsync(SearchAsync, TimeSpan.FromMilliseconds(searchDelayMilliseconds));
    }

    private void OnSelectedItemsCollectionChanged(in NotifyCollectionChangedEventArgs<object> _)
        => OnPropertyChanged(nameof(CanExportHistory));

    private Task SearchAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? ReloadAsync()
            : dispatcher.InvokeAsync(ReloadAsync).Task.Unwrap();
    }

    private async Task ReloadAsync()
    {
        if (_disposed) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cancellation = _searchCts = new CancellationTokenSource();
        var token = cancellation.Token;
        var query = SearchText;
        var version = Interlocked.Increment(ref _listVersion);
        Interlocked.Increment(ref _pendingLoads);
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            var count = await _sqlService.GetCountAsync();
            token.ThrowIfCancellationRequested();
            var rows = string.IsNullOrWhiteSpace(query)
                ? (await _sqlService.GetDataCursorPagedAsync(PageSize, DateTime.Now)).ToArray()
                : (await _sqlService.GetDataAsync(query, token))?.ToArray() ?? [];
            OnUi(() =>
            {
                if (_disposed || token.IsCancellationRequested || version != _listVersion || SearchText != query) return;
                TotalCount = count;
                SelectedListItem = null;
                SelectedItem = null;
                ClearItems();
                AddItems(rows);
                _lastCursorTime = rows.LastOrDefault()?.Time ?? DateTime.Now;
                _hasMore = string.IsNullOrWhiteSpace(query) && rows.Length == PageSize;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Interlocked.Decrement(ref _pendingLoads);
            OnUi(() => { if (!_disposed) LoadMoreCommand.NotifyCanExecuteChanged(); });
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        // 刷新立即重查当前输入，并取消还没触发的防抖搜索。
        _searchDebouncer.Cancel();
        return SearchAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(HistoryModel historyModel)
    {
        if (!await ConfirmDeleteAsync(1, "BatchDeleteHistoryConfirm"))
        {
            return;
        }

        await DeleteSingleHistoryAsync(historyModel, showFailureToast: true);
    }

    [RelayCommand]
    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ClipboardHelper.SetText(text);
        _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (!CanLoadMore) return;
        var version = _listVersion;
        var cursor = _lastCursorTime;
        Interlocked.Increment(ref _pendingLoads);
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            var historyData = (await _sqlService.GetDataCursorPagedAsync(PageSize, cursor)).ToArray();

            OnUi(() =>
            {
                if (_disposed || version != _listVersion || !string.IsNullOrWhiteSpace(SearchText)) return;
                _hasMore = historyData.Length == PageSize;
                if (historyData.Length == 0) return;
                _lastCursorTime = historyData.Last().Time;
                var uniqueHistoryItems = historyData
                    .Where(h => !_items.Any(existing => existing.Id == h.Id))
                    .ToList();

                AddItems(uniqueHistoryItems);
            });
        }
        finally
        {
            Interlocked.Decrement(ref _pendingLoads);
            OnUi(() => { if (!_disposed) LoadMoreCommand.NotifyCanExecuteChanged(); });
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.Invoke(action);
    }

    [RelayCommand]
    private async Task ExportHistoryAsync()
    {
        var selected = GetSelectedItems();

        if (selected.Count == 0)
        {
            _snackbar.Show(_i18n.GetTranslation("NoHistorySelected"), Severity.Warning);
            return;
        }

        await ExportItemsAsync(selected, clearSelection: true);
    }

    [RelayCommand]
    private async Task ExportAllHistoryAsync()
    {
        var allItems = (await _sqlService.GetDataAsync()).ToList();
        if (allItems.Count == 0)
        {
            _snackbar.Show(_i18n.GetTranslation("NoHistorySelected"), Severity.Warning);
            return;
        }

        await ExportItemsAsync(allItems, clearSelection: false);
    }

    [RelayCommand]
    private async Task DeleteSelectedHistoryAsync()
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            _snackbar.Show(_i18n.GetTranslation("NoHistorySelected"), Severity.Warning);
            return;
        }

        if (!await ConfirmDeleteAsync(selected.Count, "BatchDeleteHistoryConfirm"))
        {
            return;
        }

        var successCount = 0;
        var failCount = 0;

        foreach (var item in selected)
        {
            if (await DeleteSingleHistoryAsync(item, showFailureToast: false))
                successCount++;
            else
                failCount++;
        }

        ShowBatchDeleteSummary(successCount, failCount);
    }

    [RelayCommand]
    private async Task DeleteAllHistoryAsync()
    {
        var totalCount = await _sqlService.GetCountAsync();
        if (totalCount <= 0)
        {
            _snackbar.Show(_i18n.GetTranslation("NoHistorySelected"), Severity.Warning);
            return;
        }

        if (!await ConfirmDeleteAsync(totalCount, "DeleteAllHistoryConfirm"))
        {
            return;
        }

        var success = await _sqlService.DeleteAllDataAsync();
        if (!success)
        {
            _snackbar.ShowError(_i18n.GetTranslation("OperationFailed"));
            return;
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            SelectedListItem = null;
            SelectedItem = null;
            ClearItems();
        });

        TotalCount = 0;
        _lastCursorTime = DateTime.Now;
        LoadMoreCommand.NotifyCanExecuteChanged();

        _snackbar.ShowSuccess(string.Format(_i18n.GetTranslation("BatchDeleteHistoryResult"), totalCount, 0));
    }

    private async Task ExportItemsAsync(IReadOnlyCollection<HistoryModel> items, bool clearSelection)
    {
        var exportItems = items.ToList();
        var saveFileDialog = new SaveFileDialog
        {
            Title = _i18n.GetTranslation("SaveAs"),
            Filter = "CSV Files (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = $"stranslate_history_{DateTime.Now:yyyyMMddHHmmss}.csv",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            AddToRecent = true
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            var csv = HistoryCsvHelper.BuildCsv(exportItems, GetLanguageDisplayName, ExportRequestDetails);
            await File.WriteAllTextAsync(saveFileDialog.FileName, csv, HistoryCsvHelper.Utf8BomEncoding);

            _snackbar.ShowSuccess(_i18n.GetTranslation("ExportSuccess"));
            if (clearSelection)
            {
                SelectedItems.Clear();
            }
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("ExportFailed")}: {ex.Message}");
        }
    }

    private string GetLanguageDisplayName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        var key = $"LangEnum{code}";
        var translated = _i18n.GetTranslation(key);
        return string.IsNullOrWhiteSpace(translated) || string.Equals(translated, key, StringComparison.Ordinal)
            ? code
            : translated;
    }

    private async Task<bool> ConfirmDeleteAsync(long count, string translationKey)
    {
        return await new ContentDialog
        {
            Title = _i18n.GetTranslation("Prompt"),
            CloseButtonText = _i18n.GetTranslation("Cancel"),
            PrimaryButtonText = _i18n.GetTranslation("Confirm"),
            DefaultButton = ContentDialogButton.Primary,
            Content = string.Format(_i18n.GetTranslation(translationKey), count),
        }.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> DeleteSingleHistoryAsync(HistoryModel historyModel, bool showFailureToast)
    {
        var success = await _sqlService.DeleteDataAsync(historyModel);
        if (!success)
        {
            if (showFailureToast)
            {
                _snackbar.ShowError(_i18n.GetTranslation("OperationFailed"));
            }

            return false;
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            var item = _items.FirstOrDefault(i => i.Id == historyModel.Id);
            if (item != null)
                RemoveItem(item);

            if (SelectedItem?.Id == historyModel.Id)
            {
                SelectedListItem = null;
                SelectedItem = null;
            }
        });

        TotalCount = Math.Max(0, TotalCount - 1);
        return true;
    }

    private void ShowBatchDeleteSummary(int successCount, int failCount)
    {
        var message = string.Format(_i18n.GetTranslation("BatchDeleteHistoryResult"), successCount, failCount);

        if (successCount > 0 && failCount == 0)
            _snackbar.ShowSuccess(message);
        else if (successCount > 0)
            _snackbar.Show(message, Severity.Warning);
        else
            _snackbar.ShowError(message);
    }

    private List<HistoryModel> GetSelectedItems()
    {
        return SelectedItems
            .OfType<HistoryModel>()
            .DistinctBy(h => h.Id)
            .ToList();
    }

    private void AddItems(IEnumerable<HistoryModel> models)
    {
        _items.AddRange(models);
    }

    private void RemoveItem(HistoryModel item)
    {
        _items.Remove(item);
        SelectedItems.Remove(item);
    }

    private void ClearItems()
    {
        _items.Clear();
        SelectedItems.Clear();
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
            _searchDebouncer.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            SelectedItems.CollectionChanged -= OnSelectedItemsCollectionChanged;
        }

        _disposed = true;
    }
}
