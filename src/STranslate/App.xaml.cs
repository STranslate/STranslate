using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.ViewModels;
using STranslate.Views;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Velopack;

namespace STranslate;

public partial class App : ISingleInstanceApp, INavigation, IDisposable
{
    #region Fields & Properties

    private static Settings? _settings;
    private readonly HotkeySettings? _hotkeySettings;
    private readonly ServiceSettings? _svcSettings;

    private ILogger<App>? _logger;
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _mainWindowViewModel;
    private PluginManager? _pluginManager;
    private Notification? _notification;
    private AutoUpdateCheckerService? _autoUpdateCheckerService;
    private static bool _disposed;

    public bool IsNavigated { get; set; }

    #endregion

    #region Constructor

    public App()
    {
        // Do not use bitmap cache since it can cause WPF second window freezing issue
        ShadowAssist.UseBitmapCache = false;

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Verbose);

        try
        {
            var appStorage = new AppStorage<Settings>();
            _settings = appStorage.Load();
            _settings.SetStorage(appStorage);

            var hotkeyStorage = new AppStorage<HotkeySettings>();
            _hotkeySettings = hotkeyStorage.Load();
            _hotkeySettings.SetStorage(hotkeyStorage);

            var svcStorage = new AppStorage<ServiceSettings>();
            _svcSettings = svcStorage.Load();
            _svcSettings.SetStorage(svcStorage);
        }
        catch (Exception e)
        {
            ShowErrorMsgBoxAndFailFast("Cannot load setting storage, please check local data directory", e);
            return;
        }

        try
        {
            var host = Host.CreateDefaultBuilder()
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureServices((services) =>
                {
                    // 注册日志服务
                    services.AddLogging(builder =>
                    {
                        builder.AddSerilog();
                        builder.SetMinimumLevel(LogLevel.Trace);
                    });
                    services.AddSingleton(levelSwitch);

                    // 注册配置
                    services.AddSingleton(_settings.NonNull());
                    services.AddSingleton(_hotkeySettings.NonNull());
                    services.AddSingleton(_svcSettings.NonNull());

                    // 注册核心服务
                    services.AddSingleton<PluginManager>();
                    services.AddSingleton<ServiceManager>();
                    services.AddSingleton<PluginService>();
                    services.AddSingleton<TranslateService>();
                    services.AddSingleton<OcrService>();
                    services.AddSingleton<TtsService>();
                    services.AddSingleton<VocabularyService>();
                    services.AddSingleton<Internationalization>();

                    // 注册HTTP客户端
                    services.AddHttpClient(Constant.HttpClientName, client =>
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                        client.Timeout = TimeSpan.FromSeconds(30);
                    })
                    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                    {
                        var settings = serviceProvider.GetRequiredService<Settings>();
                        return ProxyHelper.CreateHttpHandler(settings.Proxy);
                    });
                    services.AddSingleton<IHttpService, HttpService>();

                    // 注册应用程序服务
                    services.AddSingleton<INotification, Notification>();
                    services.AddSingleton<IAudioPlayer, AudioPlayer>();
                    services.AddSingleton<IScreenshot, Screenshot>();
                    services.AddSingleton<ISnackbar, Snackbar>();

                    services.AddSingleton<BackupService>();

                    // 注册数据提供程序
                    services.AddSingleton<DataProvider>();

                    // 注册ViewModels
                    services.AddSingleton<MainWindowViewModel>();
                    services.AddTransient<SettingsWindowViewModel>();
                    services.AddTransient<OcrWindowViewModel>();
                    services.AddTransient<ImageTranslateWindowViewModel>();

                    // 自动注册页面
                    services.AddScopedFromNamespace("STranslate.ViewModels.Pages", Assembly.GetExecutingAssembly());
                    services.AddScopedFromNamespace("STranslate.Views.Pages", Assembly.GetExecutingAssembly());

                    services.AddSingleton<UpdaterService>();
                    services.AddSingleton<AutoUpdateCheckerService>();
                    services.AddSingleton<ExternalCallService>();
                    services.AddSingleton<SqlService>();
                })
                .Build();
            Ioc.Default.ConfigureServices(host.Services);
        }
        catch (Exception e)
        {
            ShowErrorMsgBoxAndFailFast("Cannot configure dependency injection container, please open new issue in STranslate", e);
            return;
        }

        try
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
                .MinimumLevel.ControlledBy(levelSwitch)
                .WriteTo.File(
                    path: Path.Combine(DataLocation.VersionLogDirectory, ".log"),
                    encoding: Encoding.UTF8,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}]: {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            if (_settings is null || _hotkeySettings is null)
                throw new Exception("settings or hotkeySettings is null when initialize executing");

            _settings.Initialize();
            _hotkeySettings.Initialize();
        }
        catch (Exception ex)
        {
            ShowErrorMsgBoxAndFailFast("Cannot initialize settings, please open new issue in STranslate", ex);
            return;
        }
    }

    #endregion

    #region App Events

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 注册编码提供程序以支持 GBK 等编码
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _logger = Ioc.Default.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Begin STranslate startup ----------------------------------------------------");

        _notification = Ioc.Default.GetRequiredService<INotification>() as Notification;
        _notification?.Install();

        _pluginManager = Ioc.Default.GetRequiredService<PluginManager>();
        _pluginManager.LoadPlugins();
        Ioc.Default.GetRequiredService<ServiceManager>().LoadServices();
        Ioc.Default.GetRequiredService<SqlService>().InitializeDB();

        RegisterAppDomainExceptions();
        RegisterDispatcherUnhandledException();
        RegisterTaskSchedulerUnhandledException();

        _mainWindowViewModel = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        _autoUpdateCheckerService = Ioc.Default.GetRequiredService<AutoUpdateCheckerService>();
        _mainWindow = new MainWindow();
        Current.MainWindow = _mainWindow;
        Current.MainWindow.Title = Constant.AppName;
        _mainWindow.Loaded += (s, e) =>
        {
            _settings?.LazyInitialize();
            _hotkeySettings?.LazyInitialize();
            UpdateToolTip();
            CheckAndShowInfo();
            WebDavBackupOperation();
            _autoUpdateCheckerService?.Start();
        };

        RegisterExitEvents();

        _logger.LogInformation("End STranslate startup ----------------------------------------------------");
    }

    private void UpdateToolTip()
    {
        if (!UACHelper.IsUserAdministrator()) return;
        _mainWindowViewModel?.TrayToolTip = $"{Constant.AppName} # " +
            $"{Ioc.Default.GetRequiredService<Internationalization>().GetTranslation("Administrator")}";
    }

    private void CheckAndShowInfo()
    {
        if (!File.Exists(DataLocation.InfoFilePath))
            return;

        try
        {
            var info = File.ReadAllText(DataLocation.InfoFilePath);
            if (string.IsNullOrWhiteSpace(info))
                return;

            _notification?.Show(Constant.AppName, info);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Cannot show info message box");
        }
        finally
        {
            try
            {
                File.Delete(DataLocation.InfoFilePath);
            }
            catch
            {
                _logger?.LogWarning("Cannot delete the information file.");
            }
        }
    }

    private void WebDavBackupOperation()
    {
        if (!File.Exists(DataLocation.BackupFilePath))
            return;

        try
        {
            var filePath = File.ReadAllText(DataLocation.BackupFilePath);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger?.LogWarning("The backup file is empty, will not show the message box.");
                return;
            }

            _ = Ioc.Default.GetRequiredService<BackupService>()
                .PostWebDavBackupAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Cannot get backup message");
        }
        finally
        {
            try
            {
                File.Delete(DataLocation.BackupFilePath);
            }
            catch { }
        }
    }

    #endregion

    #region Main

    [STAThread]
    public static void Main()
    {
        // Start the application as a single instance
        if (!SingleInstance<App>.InitializeAsFirstInstance())
            return;

        VelopackApp
            .Build()
            .OnAfterUpdateFastCallback(_ => TryGetPortableConfig())
            .Run();

        if (NeedAdmin())
        {
            SingleInstance<App>.Cleanup();
#if DEBUG
            // 7 秒延迟是 Visual Studio 调试器的正常行为 生产环境不会有这个延迟(Ctrl+F5能避免该延迟)
            Process.GetCurrentProcess().Kill();
#endif
            return;
        }

        using var application = new App();
        application.InitializeComponent();
        application.Run();
    }

    private static void TryGetPortableConfig()
    {
        try
        {
            if (!Directory.Exists(DataLocation.TmpConfigDirectory))
                return;

            FilesFolders.CopyAll(DataLocation.TmpConfigDirectory, DataLocation.PortableDataPath);
        }
        catch
        {
            Debug.WriteLine("Cannot move tmp config directory to portable data directory.");
        }
        finally
        {
            try
            {
                Directory.Delete(DataLocation.TmpConfigDirectory, true);
            }
            catch
            {
                Debug.WriteLine("Cannot delete tmp config directory.");
            }
        }
    }

    private static bool NeedAdmin()
    {
        var filePath = Path.Combine(DataLocation.SettingsDirectory, "Settings.json");
        if (!File.Exists(filePath))
            return false;

        try
        {
            var jsonContent = File.ReadAllText(filePath);
            var parsedData = System.Text.Json.Nodes.JsonNode.Parse(jsonContent);

            if (!Enum.TryParse<StartMode>(parsedData?["StartMode"]?.ToString(), true, out var mode)
                || mode == StartMode.Normal)
                return false;

            // 如果已经是管理员模式,则不需要再次提升
            if (UACHelper.IsUserAdministrator())
                return false;

            // 如果是跳过UAC管理员模式,则检查,如果缺失则先创建计划任务
            if (mode == StartMode.SkipUACAdmin)
            {
                UACHelper.Create();
            }

            UACHelper.Run(mode, waitPid: Environment.ProcessId, waitTimeoutSec: 6);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cannot parse Settings.json for admin check: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Fail Fast

    private static void ShowErrorMsgBoxAndFailFast(string message, Exception e)
    {
        // Firstly show users the message
        AppMessageBox.Show(e.ToString(), message, MessageBoxButton.OK, MessageBoxImage.Error);

        // Flow cannot construct its App instance, so ensure Flow crashes w/ the exception info.
        Environment.FailFast(message, e);
    }

    #endregion

    #region Register Events

    private void RegisterExitEvents()
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            _logger?.LogInformation("Process Exit");
            Dispose();
        };

        Current.Exit += (s, e) =>
        {
            _logger?.LogInformation("Application Exit");
            Dispose();
        };

        Current.SessionEnding += (s, e) =>
        {
            _logger?.LogInformation("Session Ending");
            Dispose();
        };
    }

    [Conditional("RELEASE")]
    private void RegisterDispatcherUnhandledException()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            var ex = e.Exception;
            _logger?.LogError(ex, "UI线程异常");
            e.Handled = true; //表示异常已处理，可以继续运行
        };
    }

    [Conditional("RELEASE")]
    private void RegisterAppDomainExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is not Exception ex) return;
            _logger?.LogError(ex, "非UI线程异常");
        };
    }

    private void RegisterTaskSchedulerUnhandledException()
    {
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            _logger?.LogError(e.Exception, "Task异常");
            e.SetObserved(); // 标记异常为已观察，防止应用程序崩溃
        };
    }

    #endregion

    #region ISingleInstanceApp

    public void OnSecondAppStarted() => _mainWindowViewModel?.Show();

    #endregion

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _logger?.LogInformation("Begin STranslate dispose ----------------------------------------------------");

        if (disposing)
        {
            // Dispose needs to be called on the main Windows thread,
            // since some resources owned by the thread need to be disposed.
            _autoUpdateCheckerService?.Dispose();
            _notification?.Uninstall();
            _mainWindowViewModel?.Dispose();
            _mainWindow?.Dispatcher.Invoke(_mainWindow.Dispose);
            _pluginManager?.Dispose();
        }

        _logger?.LogInformation("End STranslate dispose ----------------------------------------------------");
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
