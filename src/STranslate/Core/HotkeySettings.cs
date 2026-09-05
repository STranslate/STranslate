using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Helpers;
using STranslate.Resources;
using STranslate.ViewModels;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace STranslate.Core;

public partial class HotkeySettings : ObservableObject, IDisposable
{
    private AppStorage<HotkeySettings> Storage { get; set; } = null!;
    private MainWindowViewModel MainWindowViewModel { get; set; } = null!;
    private ForegroundFullscreenMonitor? _fullscreenMonitor;
    private static readonly TimeSpan[] HotkeyRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];
    private bool _globalHotkeysRegistered;
    private int _hotkeyRegistrationGeneration;

    [ObservableProperty] public partial bool CrosswordTranslateByCtrlSameC { get; set; } = false;

    [ObservableProperty] public partial Key IncrementalTranslateKey { get; set; } = Key.None;

    #region Setting Items

    public GlobalHotkey OpenWindowHotkey { get; set; } = new("Alt + G");
    public GlobalHotkey InputTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey CrosswordTranslateHotkey { get; set; } = new("Alt + D");
    public GlobalHotkey ScreenshotTranslateHotkey { get; set; } = new("Alt + S");
    public GlobalHotkey ImageTranslateHotkey { get; set; } = new("Alt + Shift + X");
    public GlobalHotkey ReplaceTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey MouseSelectionTranslationHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey SilentOcrHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey SilentTtsHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey OcrHotkey { get; set; } = new("Alt + Shift + S");
    public GlobalHotkey ClipboardMonitorHotkey { get; set; } = new(Constant.EmptyHotkey);

    #region Software Hotkeys - MainWindow

    public Hotkey OpenSettingsHotkey { get; set; } = new("Ctrl + OemComma");

    public Hotkey OpenHistoryHotkey { get; set; } = new("Ctrl + OemQuestion");

    public Hotkey HideInputHotkey { get; set; } = new("Ctrl + Shift + A");

    public Hotkey ToggleColorThemeHotkey { get; set; } = new("Ctrl + Shift + R");

    public Hotkey ToggleTopmostHotkey { get; set; } = new("Ctrl + Shift + T");

    public Hotkey SaveToVocabularyHotkey { get; set; } = new("Ctrl + Shift + S");

    public Hotkey HistoryNavigePreviousHotkey { get; set; } = new("Ctrl + P");

    public Hotkey HistoryNavigeNextHotkey { get; set; } = new("Ctrl + N");

    public Hotkey AutoTranslateHotkey { get; set; } = new("Ctrl + B");

    #endregion

    #region Software Hotkeys - OcrWindow / ImageTranslateWindow

    public Hotkey ReExecuteOcrHotkey { get; set; } = new("Ctrl + R");

    public Hotkey QrCodeHotkey { get; set; } = new("Ctrl + Shift + R");

    public Hotkey SwitchImageHotkey { get; set; } = new("Ctrl + OemQuestion");

    public Hotkey PinImageTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);

    #endregion

    [JsonIgnore]
    public List<RegisteredHotkeyData> RegisteredHotkeys =>
    [
        ..FixedHotkeys(),

        CreateGlobalHotkeyData(OpenWindowHotkey.Key, "Hotkey_OpenSTranslate", () => OpenWindowHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(InputTranslateHotkey.Key, "Hotkey_InputTranslate", () => InputTranslateHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(CrosswordTranslateHotkey.Key, "Hotkey_CrosswordTranslate", () => CrosswordTranslateHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(MouseSelectionTranslationHotkey.Key, "Hotkey_MouseSelectionTranslation", () => MouseSelectionTranslationHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(ReplaceTranslateHotkey.Key, "Hotkey_ReplaceTranslate", () => ReplaceTranslateHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(ScreenshotTranslateHotkey.Key, "Hotkey_ScreenshotTranslate", () => ScreenshotTranslateHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(ImageTranslateHotkey.Key, "Hotkey_ImageTranslate", () => ImageTranslateHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(SilentOcrHotkey.Key, "Hotkey_SilentOcr", () => SilentOcrHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(SilentTtsHotkey.Key, "Hotkey_SilentTts", () => SilentTtsHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(OcrHotkey.Key, "Hotkey_Ocr", () => OcrHotkey.Key = Constant.EmptyHotkey),
        CreateGlobalHotkeyData(ClipboardMonitorHotkey.Key, "Hotkey_ClipboardMonitor", () => ClipboardMonitorHotkey.Key = Constant.EmptyHotkey),

        // MainWindow
        new RegisteredHotkeyData(OpenSettingsHotkey.Key, "Hotkey_OpenSettings", HotkeyType.MainWindow, () => OpenSettingsHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(OpenHistoryHotkey.Key, "Hotkey_OpenHistory", HotkeyType.MainWindow, () => OpenHistoryHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(HideInputHotkey.Key, "Hotkey_ShowHideInputBox", HotkeyType.MainWindow, () => HideInputHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(ToggleColorThemeHotkey.Key, "Hotkey_ToggleColorTheme", HotkeyType.MainWindow, () => ToggleColorThemeHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(ToggleTopmostHotkey.Key, "Hotkey_ToggleTopmost", HotkeyType.MainWindow, () => ToggleTopmostHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(SaveToVocabularyHotkey.Key, "Hotkey_SaveToVocabulary", HotkeyType.MainWindow, () => SaveToVocabularyHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(HistoryNavigePreviousHotkey.Key, "Hotkey_HistoryNavigePrevious", HotkeyType.MainWindow, () => HistoryNavigePreviousHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(HistoryNavigeNextHotkey.Key, "Hotkey_HistoryNavigeNext", HotkeyType.MainWindow, () => HistoryNavigeNextHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(AutoTranslateHotkey.Key, "Hotkey_AutoTranslate", HotkeyType.MainWindow, () => AutoTranslateHotkey.Key = Constant.EmptyHotkey),

        // OcrWindow / ImageTranslateWindow
        new RegisteredHotkeyData(ReExecuteOcrHotkey.Key, "Hotkey_ReExecuteOcr", HotkeyType.OcrWindow | HotkeyType.ImageTransWindow, () => ReExecuteOcrHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(QrCodeHotkey.Key, "Hotkey_QrCode", HotkeyType.OcrWindow, () => QrCodeHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(SwitchImageHotkey.Key, "Hotkey_SwitchImage", HotkeyType.OcrWindow | HotkeyType.ImageTransWindow, () => SwitchImageHotkey.Key = Constant.EmptyHotkey),
        new RegisteredHotkeyData(PinImageTranslateHotkey.Key, "Hotkey_PinImageTranslate", HotkeyType.ImageTransWindow, () => PinImageTranslateHotkey.Key = Constant.EmptyHotkey),

        //TODO: Other Window
    ];

    private List<RegisteredHotkeyData> FixedHotkeys()
    {
        return
        [
            // MainWindow
            new RegisteredHotkeyData(Key.Escape.ToString(), "Hotkey_CancelOrHide", HotkeyType.Global | HotkeyType.MainWindow | HotkeyType.SettingsWindow | HotkeyType.OcrWindow | HotkeyType.ImageTransWindow),
            new RegisteredHotkeyData("Ctrl + Shift + Q", "Hotkey_Exit", HotkeyType.MainWindow),

            //TODO: Other Window
        ];
    }

    private RegisteredHotkeyData CreateGlobalHotkeyData(string hotkey, string resourceKey, Action onRemoved)
    {
        return new RegisteredHotkeyData(
            hotkey,
            resourceKey,
            // 直接注册所有所有类型快捷键 - 虽然只写Global也没问题，但是这样写更加符合设计
            HotkeyType.Global | HotkeyType.MainWindow | HotkeyType.SettingsWindow | HotkeyType.OcrWindow | HotkeyType.ImageTransWindow,
            () =>
            {
                if (HotkeyMapper.RemoveHotkey(hotkey))
                    onRemoved();
                else
                    AppMessageBox.Show(
                        Ioc.Default.GetRequiredService<Internationalization>().GetTranslation("HotkeyOverwriteFailed"),
                        Constant.AppName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        MessageBoxResult.OK);
            }
        );
    }

    #endregion

    public void SetStorage(AppStorage<HotkeySettings> storage)
    {
        Storage = storage;
        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IncrementalTranslateKey))
            {
                ApplyIncrementalTranslate();
                Save();
            }
            else if (e.PropertyName == nameof(CrosswordTranslateByCtrlSameC))
            {
                ApplyCtrlCc();
                Save();
            }
        };

        // 自动监听所有 GlobalHotkey 类型的属性
        foreach (var prop in GetType().GetProperties())
        {
            if (prop.PropertyType.IsSubclassOf(typeof(Hotkey)) || prop.PropertyType == typeof(Hotkey))
            {
                if (prop.GetValue(this) is not Hotkey hotkey)
                    continue;

                if (hotkey is GlobalHotkey)
                    SubscribeHotkeyPropertyChanged(hotkey, prop.Name);
                else
                    SubscribeHotkeyPropertyChanged(hotkey);
            }
        }
    }

    public void Save() => Storage?.Save();

    public void Initialize()
    {
        // 手动更新默认值
        var defaultHotkeys = new Dictionary<string, string>
        {
            // Global Hotkeys
            [nameof(OpenWindowHotkey)] = "Alt + G",
            [nameof(InputTranslateHotkey)] = "Alt + A",
            [nameof(CrosswordTranslateHotkey)] = "Alt + D",
            [nameof(ScreenshotTranslateHotkey)] = "Alt + S",
            [nameof(ImageTranslateHotkey)] = "Alt + Shift + X",
            [nameof(ReplaceTranslateHotkey)] = "Alt + F",
            [nameof(MouseSelectionTranslationHotkey)] = "Alt + Shift + D",
            [nameof(SilentOcrHotkey)] = "Alt + Shift + F",
            [nameof(SilentTtsHotkey)] = "Alt + Shift + G",
            [nameof(OcrHotkey)] = "Alt + Shift + S",
            [nameof(ClipboardMonitorHotkey)] = "Alt + Shift + A",
            // Software Hotkeys - MainWindow
            [nameof(OpenSettingsHotkey)] = "Ctrl + OemComma",
            [nameof(OpenHistoryHotkey)] = "Ctrl + OemQuestion",
            [nameof(HideInputHotkey)] = "Ctrl + Shift + A",
            [nameof(ToggleColorThemeHotkey)] = "Ctrl + Shift + R",
            [nameof(ToggleTopmostHotkey)] = "Ctrl + Shift + T",
            [nameof(HistoryNavigePreviousHotkey)] = "Ctrl + P",
            [nameof(HistoryNavigeNextHotkey)] = "Ctrl + N",
            [nameof(AutoTranslateHotkey)] = "Ctrl + B",
            // Software Hotkeys - OcrWindow / ImageTranslateWindow
            [nameof(ReExecuteOcrHotkey)] = "Ctrl + R",
            [nameof(QrCodeHotkey)] = "Ctrl + Shift + R",
            [nameof(SwitchImageHotkey)] = "Ctrl + OemQuestion",
            [nameof(PinImageTranslateHotkey)] = Constant.EmptyHotkey,
        };
        foreach (var prop in GetType().GetProperties())
        {
            if (prop.GetValue(this) is not Hotkey hotkey)
                continue;
            if (!defaultHotkeys.TryGetValue(prop.Name, out string? defaultKey))
                continue;

            hotkey.SetDefault(defaultKey);
        }
    }

    public void LazyInitialize()
    {
        MainWindowViewModel = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        _fullscreenMonitor = new ForegroundFullscreenMonitor(OnForegroundFullscreenChanged);

        ApplyCtrlCc(isInitial: true);
        ApplyIncrementalTranslate();
        ApplyGlobalHotkeyRegistrationPolicy();
        UpdateTrayIconWithPriority();
    }

    private void ApplyIncrementalTranslate()
    {
        if (IncrementalTranslateKey == Key.None)
        {
            HotkeyMapper.StopGlobalKeyboardMonitoring();
        }
        else
        {
            HotkeyMapper.RegisterHoldKey(
                IncrementalTranslateKey,
                MainWindowViewModel.OnIncKeyPressed,
                MainWindowViewModel.OnIncKeyReleased);
            HotkeyMapper.StartGlobalKeyboardMonitoring();
        }
    }

    private void ApplyCtrlCc(bool isInitial = false)
    {
        if (isInitial)
            CtrlSameCHelper.OnCtrlSameC +=
                MainWindowViewModel.CrosswordTranslateByCtrlSameCHandler;

        if (CrosswordTranslateByCtrlSameC)
            CtrlSameCHelper.Start();
        else
            CtrlSameCHelper.Stop();
    }

    public void ApplyGlobalHotkeys()
    {
        ApplyGlobalHotkeyRegistrationPolicy();
        UpdateTrayIconWithPriority();
    }

    public void ApplyIgnoreOnFullScreen()
    {
        ApplyGlobalHotkeyRegistrationPolicy();
        UpdateTrayIconWithPriority();
    }

    private void ApplyGlobalHotkeyRegistrationPolicy()
    {
        var settings = Ioc.Default.GetRequiredService<Settings>();
        if (settings.DisableGlobalHotkeys)
        {
            _fullscreenMonitor?.Stop();
            SetGlobalHotkeysRegistered(false);
            return;
        }

        if (settings.IgnoreHotkeysOnFullscreen)
        {
            _fullscreenMonitor?.Start();
            return;
        }

        _fullscreenMonitor?.Stop();
        SetGlobalHotkeysRegistered(true);
    }

    private void OnForegroundFullscreenChanged(bool isFullscreen)
    {
        var settings = Ioc.Default.GetRequiredService<Settings>();
        if (settings.DisableGlobalHotkeys || !settings.IgnoreHotkeysOnFullscreen)
            return;

        SetGlobalHotkeysRegistered(!isFullscreen);
    }

    private void SetGlobalHotkeysRegistered(bool shouldRegister)
    {
        if (_globalHotkeysRegistered == shouldRegister)
            return;

        // 先更新状态，避免 RegisterHotkeys() 通过 HandleGlobalLogic() 时被当成暂停状态。
        _globalHotkeysRegistered = shouldRegister;
        _hotkeyRegistrationGeneration++;
        if (shouldRegister)
            RegisterHotkeys();
        else
            UnregisterHotkeys();
    }

    /// <summary>
    /// 根据优先级更新托盘图标
    /// 优先级: NoHotkey > IgnoreOnFullScreen > Normal
    /// </summary>
    private void UpdateTrayIconWithPriority()
    {
        var settings = Ioc.Default.GetRequiredService<Settings>();

        // NoHotkey 优先级最高
        if (settings.DisableGlobalHotkeys)
        {
            UpdateTrayIcon(TrayIconType.NoHotkey);
            return;
        }

        // IgnoreOnFullScreen 优先级次之
        if (settings.IgnoreHotkeysOnFullscreen)
        {
            UpdateTrayIcon(TrayIconType.IgnoreOnFullScreen);
            return;
        }

        // 默认正常状态
        UpdateTrayIcon(TrayIconType.Normal);
    }

    private void UpdateTrayIcon(TrayIconType trayIconType)
    {
        Ioc.Default.GetRequiredService<MainWindowViewModel>().TrayIcon = trayIconType switch
        {
            TrayIconType.NoHotkey => BitmapImageLoc.NoHotkeyIcon,
            TrayIconType.IgnoreOnFullScreen => BitmapImageLoc.IgnoreOnFullScreenIcon,
#if DEBUG
            _ => BitmapImageLoc.DevIcon,
#else
            _ => BitmapImageLoc.AppIcon
#endif
        };
    }

    private void RegisterHotkeys()
    {
        HandleGlobalLogic(nameof(OpenWindowHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(InputTranslateHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(CrosswordTranslateHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(MouseSelectionTranslationHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(ReplaceTranslateHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(ScreenshotTranslateHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(ImageTranslateHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(SilentOcrHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(SilentTtsHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(OcrHotkey), retryOnConflict: true);
        HandleGlobalLogic(nameof(ClipboardMonitorHotkey), retryOnConflict: true);
    }

    private void UnregisterHotkeys()
    {
        HotkeyMapper.RemoveHotkey(OpenWindowHotkey.Key);
        HotkeyMapper.RemoveHotkey(InputTranslateHotkey.Key);
        HotkeyMapper.RemoveHotkey(CrosswordTranslateHotkey.Key);
        HotkeyMapper.RemoveHotkey(MouseSelectionTranslationHotkey.Key);
        HotkeyMapper.RemoveHotkey(ReplaceTranslateHotkey.Key);
        HotkeyMapper.RemoveHotkey(ScreenshotTranslateHotkey.Key);
        HotkeyMapper.RemoveHotkey(ImageTranslateHotkey.Key);
        HotkeyMapper.RemoveHotkey(SilentOcrHotkey.Key);
        HotkeyMapper.RemoveHotkey(SilentTtsHotkey.Key);
        HotkeyMapper.RemoveHotkey(OcrHotkey.Key);
        HotkeyMapper.RemoveHotkey(ClipboardMonitorHotkey.Key);
    }

    private void HandleGlobalLogic(string? propertyName, bool retryOnConflict = false)
    {
        if (!_globalHotkeysRegistered)
        {
            SetConflictState(propertyName, false);
            return;
        }

        switch (propertyName)
        {
            case nameof(OpenWindowHotkey):
                RegisterHotkey(OpenWindowHotkey, WithFullscreenCheck(() => MainWindowViewModel.ToggleAppCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(InputTranslateHotkey):
                RegisterHotkey(InputTranslateHotkey, WithFullscreenCheck(() => MainWindowViewModel.InputClearCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(CrosswordTranslateHotkey):
                RegisterHotkey(CrosswordTranslateHotkey, WithFullscreenCheck(() => MainWindowViewModel.CrosswordTranslateCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(MouseSelectionTranslationHotkey):
                RegisterHotkey(MouseSelectionTranslationHotkey, WithFullscreenCheck(() => MainWindowViewModel.ToggleMouseSelectionTranslationCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(ScreenshotTranslateHotkey):
                RegisterHotkey(ScreenshotTranslateHotkey, WithFullscreenCheck(() => MainWindowViewModel.ScreenshotTranslateCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(ImageTranslateHotkey):
                RegisterHotkey(ImageTranslateHotkey, WithFullscreenCheck(() => MainWindowViewModel.ImageTranslateCommand.Execute(null)), retryOnConflict);
                break;
            case nameof(OcrHotkey):
                RegisterHotkey(OcrHotkey, WithFullscreenCheck(() => MainWindowViewModel.OcrCommand.Execute(null)), retryOnConflict);
                break;

            // 静默操作
            case nameof(ReplaceTranslateHotkey):
                RegisterHotkey(ReplaceTranslateHotkey, WithFullscreenCheck(() =>
                {
                    if (MainWindowViewModel.ReplaceTranslateCommand.IsRunning)
                    {
                        MainWindowViewModel.ReplaceTranslateCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.ReplaceTranslateCommand.Execute(null);
                }), retryOnConflict);
                break;
            case nameof(SilentOcrHotkey):
                RegisterHotkey(SilentOcrHotkey, WithFullscreenCheck(() =>
                {
                    if (MainWindowViewModel.SilentOcrCommand.IsRunning)
                    {
                        MainWindowViewModel.SilentOcrCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.SilentOcrCommand.Execute(null);
                }), retryOnConflict);
                break;
            case nameof(SilentTtsHotkey):
                RegisterHotkey(SilentTtsHotkey, WithFullscreenCheck(() =>
                {
                    if (MainWindowViewModel.SilentTtsCommand.IsRunning)
                    {
                        MainWindowViewModel.SilentTtsCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.SilentTtsCommand.Execute(null);
                }), retryOnConflict);
                break;
            case nameof(ClipboardMonitorHotkey):
                RegisterHotkey(ClipboardMonitorHotkey, WithFullscreenCheck(() => MainWindowViewModel.ToggleClipboardMonitorCommand.Execute(null)), retryOnConflict);
                break;

        }
    }

    private void RegisterHotkey(GlobalHotkey hotkey, Action action, bool retryOnConflict)
    {
        var result = HotkeyMapper.SetHotkey(hotkey.Key, action, showConflictDialog: !retryOnConflict);
        if (result != HotkeyRegistrationResult.Conflict || !retryOnConflict)
        {
            hotkey.IsConflict = result != HotkeyRegistrationResult.Success;
            return;
        }

        hotkey.IsConflict = false;
        _ = RetryHotkeyRegistrationAsync(hotkey, hotkey.Key, action, _hotkeyRegistrationGeneration);
    }

    private async Task RetryHotkeyRegistrationAsync(
        GlobalHotkey hotkey,
        string expectedKey,
        Action action,
        int registrationGeneration)
    {
        for (var attempt = 0; attempt < HotkeyRetryDelays.Length; attempt++)
        {
            await Task.Delay(HotkeyRetryDelays[attempt]);

            if (!_globalHotkeysRegistered ||
                _hotkeyRegistrationGeneration != registrationGeneration ||
                hotkey.Key != expectedKey || hotkey.IsConflict)
                return;

            var isFinalAttempt = attempt == HotkeyRetryDelays.Length - 1;
            var result = HotkeyMapper.SetHotkey(hotkey.Key, action, showConflictDialog: isFinalAttempt);
            if (result == HotkeyRegistrationResult.Conflict)
            {
                if (isFinalAttempt)
                    hotkey.IsConflict = true;
                continue;
            }

            hotkey.IsConflict = result != HotkeyRegistrationResult.Success;
            return;
        }
    }

    private void SetConflictState(string? propertyName, bool isConflict)
    {
        switch (propertyName)
        {
            case nameof(OpenWindowHotkey): OpenWindowHotkey.IsConflict = isConflict; break;
            case nameof(InputTranslateHotkey): InputTranslateHotkey.IsConflict = isConflict; break;
            case nameof(CrosswordTranslateHotkey): CrosswordTranslateHotkey.IsConflict = isConflict; break;
            case nameof(MouseSelectionTranslationHotkey): MouseSelectionTranslationHotkey.IsConflict = isConflict; break;
            case nameof(ReplaceTranslateHotkey): ReplaceTranslateHotkey.IsConflict = isConflict; break;
            case nameof(ScreenshotTranslateHotkey): ScreenshotTranslateHotkey.IsConflict = isConflict; break;
            case nameof(ImageTranslateHotkey): ImageTranslateHotkey.IsConflict = isConflict; break;
            case nameof(SilentOcrHotkey): SilentOcrHotkey.IsConflict = isConflict; break;
            case nameof(SilentTtsHotkey): SilentTtsHotkey.IsConflict = isConflict; break;
            case nameof(OcrHotkey): OcrHotkey.IsConflict = isConflict; break;
            case nameof(ClipboardMonitorHotkey): ClipboardMonitorHotkey.IsConflict = isConflict; break;
        }
    }

    /// <summary>
    /// 订阅快捷键属性更改事件
    /// </summary>
    /// <param name="hotkey"></param>
    /// <param name="propertyName">默认表示软件内热键仅保存结果无需额外处理</param>
    private void SubscribeHotkeyPropertyChanged(Hotkey hotkey, string? propertyName = default)
    {
        hotkey.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != nameof(Hotkey.Key))
                return;
            if (!string.IsNullOrEmpty(propertyName))
                HandleGlobalLogic(propertyName);
            Save();
        };
    }

    /// <summary>
    /// 包装快捷键操作，添加全屏检查
    /// </summary>
    private Action WithFullscreenCheck(Action action)
    {
        return () =>
        {
            if (HotkeyExecutionGuard.ShouldSkipGlobalHotkey())
                return;

            action();
        };
    }

    public void Dispose()
    {
        _hotkeyRegistrationGeneration++;
        _fullscreenMonitor?.Dispose();
    }

    /// <summary>
    /// 验证快捷键字符串是否有效,无效则返回默认值
    /// </summary>
    /// <param name="hotkey">待验证的快捷键字符串</param>
    /// <param name="defaultHotkey">默认快捷键</param>
    /// <returns>验证通过返回原值,否则返回默认值</returns>
    internal string ValidateHotkey(string hotkey, string defaultHotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return defaultHotkey;

        try
        {
            var converter = new KeyGestureConverter();
            // 验证转换是否成功
            _ = converter.ConvertFromString(hotkey) as KeyGesture
                ?? throw new InvalidOperationException("转换结果为 null");
        }
        catch
        {
            return defaultHotkey;
        }

        return hotkey;
    }
}

public partial class Hotkey : ObservableObject
{
    private string? _defaultKey;

    [JsonConstructor]
    public Hotkey(string key) : this(key, null)
    {
    }

    protected Hotkey(string key, string? defaultKey)
    {
        Key = key;
        _defaultKey = defaultKey;
    }

    [JsonIgnore]
    public string Default => _defaultKey ?? Key;

    /* 不要新旧值检测，设置相同快捷键需要 */
    public string Key { get => field; set { field = value; OnPropertyChanged(); } }

    /// <summary>
    /// 内部方法：设置默认值
    /// </summary>
    internal void SetDefault(string defaultKey)
    {
        _defaultKey = defaultKey;
    }

    public override string ToString() => Key;
}

public partial class GlobalHotkey : Hotkey
{
    [JsonConstructor]
    public GlobalHotkey(string key) : this(key, false)
    {
    }

    public GlobalHotkey(string key, bool isConflict = false) : base(key)
    {
        IsConflict = isConflict;
    }

    [JsonIgnore]
    public bool IsConflict { get => field; set { field = value; OnPropertyChanged(); } }
}

public class RegisteredHotkeyData(string hotkey, string resourceKey, HotkeyType type = HotkeyType.Global, Action? action = default)
{
    public string Hotkey { get; } = hotkey;
    public string ResourceKey { get; } = resourceKey;
    public HotkeyType Type { get; } = type;
    public Action? OnRemovedHotkey { get; } = action;
}

[Flags]
public enum HotkeyType
{
    Global = 1,
    MainWindow = 2,
    SettingsWindow = 4,
    OcrWindow = 8,
    ImageTransWindow = 16
}

public enum TrayIconType
{
    Normal,
    NoHotkey,
    IgnoreOnFullScreen,
}
