using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Plugin;
using STranslate.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace STranslate.Controls;

public partial class RemoteBackupContentDialog
{
    private readonly ILogger<RemoteBackupContentDialog> _logger = Ioc.Default.GetRequiredService<ILogger<RemoteBackupContentDialog>>();
    private readonly ISnackbar _snackbar = Ioc.Default.GetRequiredService<ISnackbar>();
    private readonly Internationalization _i18n = Ioc.Default.GetRequiredService<Internationalization>();
    private readonly IRemoteBackupClient _client;

    public ObservableCollection<RemoteBackupFile> Collections { get; }

    public bool SupportsRename => _client.SupportsRename;

    public string FilePath { get; private set; } = string.Empty;

    public RemoteBackupContentDialog(
        IRemoteBackupClient client,
        ObservableCollection<RemoteBackupFile> collections)
    {
        _client = client;
        Collections = collections;

        InitializeComponent();

        DataContext = this;
    }

    private ContentDialogResult _result = ContentDialogResult.None;

    public new async Task<ContentDialogResult> ShowAsync()
    {
        await base.ShowAsync();
        return _result;
    }

    [RelayCommand]
    private async Task DownloadAsync(string fullName)
    {
        try
        {
            if (File.Exists(fullName))
                File.Delete(fullName);

            var result = await _client.DownloadAsync(fullName, fullName);
            if (result.Success)
            {
                FilePath = fullName;
                _snackbar.ShowSuccess(_i18n.GetTranslation("DownloadSuccess"));
                _result = ContentDialogResult.Primary;
                Hide();
            }
            else
            {
                ShowFailure("DownloadFailed", result);
            }
        }
        catch (Exception ex)
        {
            ShowException("DownloadException", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(string fullName)
    {
        try
        {
            var result = await _client.DeleteAsync(fullName);
            if (result.Success)
                Collections.Remove(Find(fullName));
            else
                ShowFailure("DeleteFailed", result);
        }
        catch (Exception ex)
        {
            ShowException("DeleteException", ex);
        }
    }

    [RelayCommand]
    private async Task UpdateTextAsync(ValueTuple<string, string> valueTuple)
    {
        if (!_client.SupportsRename)
            return;

        var originFullName = valueTuple.Item1;
        var targetFullName = valueTuple.Item2;

        try
        {
            if (!targetFullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                targetFullName += ".zip";
                Collections.First(x => x.FullName == originFullName).FullName = targetFullName;
            }

            if (originFullName == targetFullName)
                return;

            var result = await _client.RenameAsync(originFullName, targetFullName);
            if (!result.Success)
            {
                Collections.First(x => x.FullName == targetFullName).FullName = originFullName;
                ShowFailure("RenameFailed", result);
            }
        }
        catch (Exception ex)
        {
            // 关闭页面
            _result = ContentDialogResult.None;
            Hide();

            ShowException("RenameException", ex);
        }
    }

    private void ShowFailure(string messageKey, RemoteBackupResult result)
    {
        var message = string.Format(
            _i18n.GetTranslation(messageKey),
            result.StatusCode,
            result.Description);

        _snackbar.ShowError(message);
        _logger.LogError(message);
    }

    private void ShowException(string messageKey, Exception exception)
    {
        var message = string.Format(_i18n.GetTranslation(messageKey), exception.Message);

        _snackbar.ShowError(message);
        _logger.LogError(exception, message);
    }

    private RemoteBackupFile Find(string fullName)
    {
        return Collections.First(x => x.FullName == fullName);
    }
}
