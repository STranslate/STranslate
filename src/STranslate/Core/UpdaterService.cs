using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.Logging;
using STranslate.Controls;
using STranslate.Helpers;
using STranslate.Plugin;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using STranslate.Views;
using STranslate.Views.Pages;
using Velopack;
using Velopack.Sources;

namespace STranslate.Core;

/// <summary>
/// 提供手动更新与后台更新检查能力。
/// </summary>
public class UpdaterService(
    ILogger<UpdaterService> logger,
    Internationalization i18n,
    INotification notification
    )
{
    private SemaphoreSlim UpdateLock { get; } = new SemaphoreSlim(1);

    /// <summary>
    /// 手动检查并执行更新流程。
    /// </summary>
    public async Task UpdateAppAsync()
    {
        await UpdateLock.WaitAsync();
        try
        {
            notification.Show(i18n.GetTranslation("UpdateCheck"), i18n.GetTranslation("CheckingForUpdates"));

            var updateManager = new UpdateManager(new GithubSource(Constant.Github, accessToken: default, prerelease: false));

            var newUpdateInfo = await updateManager.CheckForUpdatesAsync();

            if (newUpdateInfo == null)
            {
                AppMessageBox.Show(i18n.GetTranslation("NoUpdateInfoFound"), Constant.AppName);
                logger.LogInformation("No update info found.");
                return;
            }

            var newReleaseVersion = SemanticVersioning.Version.Parse(newUpdateInfo.TargetFullRelease.Version.ToString());
            var currentVersion = SemanticVersioning.Version.Parse(Constant.Version);

            logger.LogInformation($"Future Release <{JsonSerializer.Serialize(newUpdateInfo.TargetFullRelease)}>");

            if (newReleaseVersion <= currentVersion)
            {
                AppMessageBox.Show(i18n.GetTranslation("AlreadyLatestVersion"), Constant.AppName);
                logger.LogInformation("You are already on the latest version.");
                return;
            }

            var dialogResult = await new UpdateChangelogDialog(newReleaseVersion.ToString()).ShowAsync();
            if (dialogResult != ContentDialogResult.Primary)
            {
                logger.LogInformation("User cancelled the update.");
                return;
            }

            logger.LogInformation($"New version {newReleaseVersion} found. Downloading...");

            await updateManager.DownloadUpdatesAsync(newUpdateInfo);

            if (DataLocation.PortableDataLocationInUse())
            {
                FilesFolders.CopyAll(DataLocation.PortableDataPath, DataLocation.TmpConfigDirectory, AppMessageBox.Show);

                if (!FilesFolders.VerifyBothFolderFilesEqual(DataLocation.PortableDataPath, DataLocation.TmpConfigDirectory, AppMessageBox.Show))
                    AppMessageBox.Show(string.Format(i18n.GetTranslation("PortableDataMoveError"),
                        DataLocation.PortableDataPath,
                        DataLocation.TmpConfigDirectory), Constant.AppName);
            }

            var newVersionTips = NewVersionTips(newReleaseVersion.ToString());

            notification.Show(i18n.GetTranslation("UpdateReady"), newVersionTips);
            logger.LogInformation($"Update success:{newVersionTips}");

            if (AppMessageBox.Show(newVersionTips, Constant.AppName, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                updateManager.WaitExitThenApplyUpdates(newUpdateInfo);
                Application.Current.Shutdown();
            }
        }
        catch (Exception e)
        {
            if (e is HttpRequestException or WebException or SocketException ||
                e.InnerException is TimeoutException)
                logger.LogError(e, $"Check your connection and proxy settings to api.github.com.");
            else
                logger.LogError(e, $"Error Occurred");

            notification.Show(i18n.GetTranslation("UpdateFailed"), i18n.GetTranslation("UpdateFailedMessage"));
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    /// <summary>
    /// 后台检查更新；发现新版本时发送带按钮通知，并引导到设置-关于页。
    /// </summary>
    /// <returns>是否触发了“发现新版本”通知。</returns>
    public async Task<bool> NotifyUpdateIfAvailableAsync()
    {
        await UpdateLock.WaitAsync();
        try
        {
            var updateManager = new UpdateManager(new GithubSource(Constant.Github, accessToken: default, prerelease: false));
            var newUpdateInfo = await updateManager.CheckForUpdatesAsync();

            if (newUpdateInfo == null)
            {
                logger.LogDebug("No update info found during background check.");
                return false;
            }

            var newReleaseVersion = SemanticVersioning.Version.Parse(newUpdateInfo.TargetFullRelease.Version.ToString());
            var currentVersion = SemanticVersioning.Version.Parse(Constant.Version);
            logger.LogInformation($"Future Release <{JsonSerializer.Serialize(newUpdateInfo.TargetFullRelease)}>");

            if (newReleaseVersion <= currentVersion)
            {
                logger.LogDebug("You are already on the latest version during background check.");
                return false;
            }

            var releaseVersion = newReleaseVersion.ToString();

            notification.ShowWithButton(
                i18n.GetTranslation("UpdateCheck"),
                i18n.GetTranslation("UpdateOpenAboutButton"),
                OpenUpdateSettingsPage,
                string.Format(i18n.GetTranslation("UpdateAvailableTrayMessage"), releaseVersion));

            logger.LogInformation("Found new version {Version} and notified user by tray toast.", releaseVersion);
            return true;
        }
        catch (Exception e)
        {
            if (e is HttpRequestException or WebException or SocketException ||
                e.InnerException is TimeoutException)
            {
                logger.LogWarning(e, "Background update check failed due to network/proxy issue.");
            }
            else
            {
                logger.LogError(e, "Background update check error occurred.");
            }

            return false;
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private string NewVersionTips(string version) =>
        string.Format(i18n.GetTranslation("NewVersionTips"), version);

    private void OpenUpdateSettingsPage()
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await SingletonWindowOpener.OpenAsync<SettingsWindow>();
                Application.Current.Windows
                    .OfType<SettingsWindow>()
                    .FirstOrDefault()
                    ?.Navigate(nameof(AboutPage), false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to navigate to Settings-About from update notification.");
            }
        }));
    }
}
