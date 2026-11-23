using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using WebDav;

namespace STranslate.Services;

public class BackupService(Settings Settings,
    INotification notification,
    ISnackbar snackbar,
    ILogger<BackupService> logger,
    Internationalization i18n)
{
    private static readonly string S3BackupPrefix = $"{Constant.AppName}/";

    #region Local Backup

    public async Task LocalBackupAsync()
    {
        var saveFileDialog = new SaveFileDialog
        {
            Title = i18n.GetTranslation("SelectBackupFile"),
            Filter = "zip(*.zip)|*.zip",
            FileName = $"stranslate_backup_{DateTime.Now:yyyyMMddHHmmss}"
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndBackup"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var filePath = saveFileDialog.FileName;
        string[] args = [
            "backup",
            "-m", "backup",
            "-a", filePath,
            "-f", DataLocation.PluginsDirectory,
            "-f", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.InfoFilePath,
            "-w", string.Format(i18n.GetTranslation("BackupConfigSuccess"), filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.RequestShutdown(AppShutdownReason.LocalBackup);
    }

    public async Task LocalRestoreAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = i18n.GetTranslation("SelectRestoreFile"),
            Filter = "zip(*.zip)|*.zip"
        };
        if (openFileDialog.ShowDialog() != true)
            return;

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndRestore"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var filePath = openFileDialog.FileName;
        string[] args = [
            "backup",
            "-m", "restore",
            "-a", filePath,
            "-s", Constant.Plugins,
            "-t", DataLocation.PluginsDirectory,
            "-s", Constant.Settings,
            "-t", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.InfoFilePath,
            "-w", string.Format(i18n.GetTranslation("RestoreConfigSuccess"), filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.RequestShutdown(AppShutdownReason.LocalRestore);
    }

    #endregion

    #region WebDav Backup

    public async Task PreWebDavBackupAsync()
    {
        // 测试连接是否成功
        var (isSuccess, client, message) = await CreateWebDavClientAsync();
        if (!isSuccess || client is null)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Backup|CreateWebDavClientAsync|Failed Message: {Message}", message);
            return;
        }
        client.Dispose();

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndBackup"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var fileName = $"stranslate_backup_{DateTime.Now:yyyyMMddHHmmss}.zip";
        var filePath = Path.Combine(Constant.ProgramDirectory, fileName);
        string[] args = [
            "backup",
            "-m", "backup",
            "-a", filePath,
            "-f", DataLocation.PluginsDirectory,
            "-f", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.BackupFilePath,
            "-w", BackupTransferState.Format(BackupType.WebDav, filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.RequestShutdown(AppShutdownReason.WebDavBackup);
    }

    public async Task PostWebDavBackupAsync(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupUploading"));
        logger.LogInformation("Backup|PostWebDavBackupAsync|Uploading File: {FileName}", fileName);

        var (isSuccess, client, message) = await CreateWebDavClientAsync();
        if (!isSuccess || client is null)
        {
            notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Backup|CreateWebDavClientAsync|Failed Message: {Message}", message);
            return;
        }

        try
        {
            var result = await client.UploadAsync(filePath, fileName);
            var fullPath = $"{client.AbsolutePath}{fileName}";

            if (result.Success)
            {
                notification.Show(i18n.GetTranslation("Prompt"), string.Format(i18n.GetTranslation("BackupSuccess"), fullPath));
                logger.LogInformation("Backup|UploadAsync|Success File: {FileName} Path: {Path}", fileName, fullPath);
            }
            else
            {
                notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupFailed"));
                logger.LogError("Backup|UploadAsync|Error Code: {StatusCode} Description: {Description}", result.StatusCode, result.Description);
            }
        }
        catch (Exception ex)
        {
            notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupFailed"));
            logger.LogError(ex, "Backup|PostWebDavBackupAsync|Failed");
        }
        finally
        {
            client.Dispose();
            CleanupCache(filePath);
        }
    }

    public async Task WebDavRestoreAsync()
    {
        var (isSuccess, client, message) = await CreateWebDavClientAsync();
        if (!isSuccess || client is null)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Restore|CreateWebDavClientAsync|Failed Message: {Message}", message);
            return;
        }

        try
        {
            IReadOnlyList<string> files;
            try
            {
                files = await client.ListAsync();
            }
            catch (Exception ex)
            {
                snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
                logger.LogError(ex, "Restore|WebDav|ListAsync|Failed");
                return;
            }

            var contentDialog = new RemoteBackupContentDialog(
                client,
                new ObservableCollection<RemoteBackupFile>(files.Select(file => new RemoteBackupFile(file))));

            if (await contentDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = i18n.GetTranslation("Prompt"),
                PrimaryButtonText = i18n.GetTranslation("Confirm"),
                CloseButtonText = i18n.GetTranslation("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = i18n.GetTranslation("ConfirmRestartAndRestore"),
            };
            var dialogResult = await dialog.ShowAsync();
            if (dialogResult != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }

            string[] args = [
                "backup",
                "-m", "restore",
                "-a", contentDialog.FilePath,
                "-s", Constant.Plugins,
                "-t", DataLocation.PluginsDirectory,
                "-s", Constant.Settings,
                "-t", DataLocation.SettingsDirectory,
                "-r", contentDialog.FilePath,
                "-d", "3",
                "-l", DataLocation.AppExePath,
                "-c", DataLocation.InfoFilePath,
                "-w", string.Format(i18n.GetTranslation("RestoreConfigSuccess"), "WebDav")
            ];
            Utilities.ExecuteProgram(DataLocation.HostExePath, args);
            App.RequestShutdown(AppShutdownReason.WebDavRestore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore|WebDavRestoreAsync|Failed");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task<(bool isSuccess, WebDavBackupClient? client, string message)> CreateWebDavClientAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.Backup.Address))
            return (false, null, "WebDav address is empty");

        // 如果没有/结尾则添加
        // * TeraCloud强制需要以/结尾
        // * 群晖、坚果云有无均可
        var uri = new Uri(Settings.Backup.Address.EndsWith('/') ? Settings.Backup.Address : $"{Settings.Backup.Address}/");

        // TeraCloud强制需要以/结尾
        // * 群晖、坚果云有无均可
        var absolutePath = $"{uri.LocalPath.TrimEnd('/')}/{Constant.AppName}/";

        var clientParams = new WebDavClientParams
        {
            Timeout = TimeSpan.FromSeconds(Settings.HttpTimeout),
            BaseAddress = uri,
            Credentials = new NetworkCredential(Settings.Backup.Username, Settings.Backup.Password)
        };
        var client = new WebDavClient(clientParams);
        try
        {
            var linkTest = await client.Propfind(string.Empty);
            if (linkTest.IsSuccessful)
                return (true, new WebDavBackupClient(client, absolutePath), string.Empty);

            client.Dispose();
            return (false, null, $"Code: {linkTest.StatusCode} Description: {linkTest.Description}");
        }
        catch (Exception ex)
        {
            client.Dispose();
            return (false, null, ex.Message);
        }
    }

    #endregion

    #region S3 Backup

    public async Task PreS3BackupAsync()
    {
        var (isSuccess, client, message) = await CreateS3ClientAsync();
        if (!isSuccess || client is null)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Backup|CreateS3ClientAsync|Failed Message: {Message}", message);
            return;
        }
        client.Dispose();

        var dialog = new ContentDialog
        {
            Title = i18n.GetTranslation("Prompt"),
            PrimaryButtonText = i18n.GetTranslation("Confirm"),
            CloseButtonText = i18n.GetTranslation("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = i18n.GetTranslation("ConfirmRestartAndBackup"),
        };
        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var fileName = $"stranslate_backup_{DateTime.Now:yyyyMMddHHmmss}.zip";
        var filePath = Path.Combine(Constant.ProgramDirectory, fileName);
        string[] args = [
            "backup",
            "-m", "backup",
            "-a", filePath,
            "-f", DataLocation.PluginsDirectory,
            "-f", DataLocation.SettingsDirectory,
            "-d", "3",
            "-l", DataLocation.AppExePath,
            "-c", DataLocation.BackupFilePath,
            "-w", BackupTransferState.Format(BackupType.S3, filePath)
            ];
        Utilities.ExecuteProgram(DataLocation.HostExePath, args);
        App.RequestShutdown(AppShutdownReason.S3Backup);
    }

    public async Task PostS3BackupAsync(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupUploading"));
        logger.LogInformation("Backup|PostS3BackupAsync|Uploading File: {FileName}", fileName);

        var (isSuccess, client, message) = await CreateS3ClientAsync();
        if (!isSuccess || client is null)
        {
            notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Backup|CreateS3ClientAsync|Failed Message: {Message}", message);
            return;
        }

        try
        {
            var result = await client.UploadAsync(filePath, fileName);
            var fullPath = $"{S3BackupPrefix}{fileName}";

            if (result.Success)
            {
                notification.Show(i18n.GetTranslation("Prompt"), string.Format(i18n.GetTranslation("BackupSuccess"), fullPath));
                logger.LogInformation("Backup|UploadAsync|Success File: {FileName} Path: {Path}", fileName, fullPath);
            }
            else
            {
                notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupFailed"));
                logger.LogError("Backup|UploadAsync|Error Code: {StatusCode} Description: {Description}", result.StatusCode, result.Description);
            }
        }
        catch (Exception ex)
        {
            notification.Show(i18n.GetTranslation("Prompt"), i18n.GetTranslation("BackupFailed"));
            logger.LogError(ex, "Backup|PostS3BackupAsync|Failed");
        }
        finally
        {
            client.Dispose();
            CleanupCache(filePath);
        }
    }

    public async Task S3RestoreAsync()
    {
        var (isSuccess, client, message) = await CreateS3ClientAsync();
        if (!isSuccess || client is null)
        {
            snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
            logger.LogError("Restore|CreateS3ClientAsync|Failed Message: {Message}", message);
            return;
        }

        try
        {
            IReadOnlyList<string> files;
            try
            {
                files = await client.ListAsync();
            }
            catch (Exception ex)
            {
                snackbar.Show(i18n.GetTranslation("CheckConfigOrLog"));
                logger.LogError(ex, "Restore|S3|ListAsync|Failed");
                return;
            }

            var contentDialog = new RemoteBackupContentDialog(
                client,
                new ObservableCollection<RemoteBackupFile>(files.Select(file => new RemoteBackupFile(file))));

            if (await contentDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = i18n.GetTranslation("Prompt"),
                PrimaryButtonText = i18n.GetTranslation("Confirm"),
                CloseButtonText = i18n.GetTranslation("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                Content = i18n.GetTranslation("ConfirmRestartAndRestore"),
            };
            var dialogResult = await dialog.ShowAsync();
            if (dialogResult != ContentDialogResult.Primary)
            {
                CleanupCache(contentDialog.FilePath);
                return;
            }

            string[] args = [
                "backup",
                "-m", "restore",
                "-a", contentDialog.FilePath,
                "-s", Constant.Plugins,
                "-t", DataLocation.PluginsDirectory,
                "-s", Constant.Settings,
                "-t", DataLocation.SettingsDirectory,
                "-r", contentDialog.FilePath,
                "-d", "3",
                "-l", DataLocation.AppExePath,
                "-c", DataLocation.InfoFilePath,
                "-w", string.Format(i18n.GetTranslation("RestoreConfigSuccess"), "S3")
            ];
            Utilities.ExecuteProgram(DataLocation.HostExePath, args);
            App.RequestShutdown(AppShutdownReason.S3Restore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore|S3RestoreAsync|Failed");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task<(bool isSuccess, S3BackupClient? client, string message)> CreateS3ClientAsync()
    {
        var backup = Settings.Backup;
        if (string.IsNullOrWhiteSpace(backup.S3Bucket) ||
            string.IsNullOrWhiteSpace(backup.S3AccessKey) ||
            string.IsNullOrWhiteSpace(backup.S3SecretKey))
        {
            return (false, null, "S3 bucket or credentials are empty");
        }

        var endpoint = (backup.S3Endpoint ?? string.Empty).Trim();
        var region = (backup.S3Region ?? string.Empty).Trim();

        var config = new AmazonS3Config
        {
            ForcePathStyle = backup.S3ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(Settings.HttpTimeout),
            MaxErrorRetry = 2,
        };

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return (false, null, "S3 endpoint is invalid");
            }

            config.ServiceURL = endpoint;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "auto" : region;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(region))
                return (false, null, "S3 region is required when the endpoint is empty");

            try
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        var s3Client = new AmazonS3Client(
            new BasicAWSCredentials(backup.S3AccessKey, backup.S3SecretKey),
            config);

        try
        {
            // 同时验证凭证、bucket 与列举权限
            await s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = backup.S3Bucket,
                Prefix = S3BackupPrefix,
                MaxKeys = 1,
            });

            return (true, new S3BackupClient(s3Client, backup.S3Bucket, S3BackupPrefix), string.Empty);
        }
        catch (Exception ex)
        {
            s3Client.Dispose();
            return (false, null, ex.Message);
        }
    }

    #endregion

    private static void CleanupCache(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
