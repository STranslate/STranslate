using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using STranslate.Helpers;
using STranslate.Resources;
using STranslate.ViewModels;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace STranslate.Core;

public partial class HotkeySettings : ObservableObject
{
    private AppStorage<HotkeySettings> Storage { get; set; } = null!;
    private MainWindowViewModel MainWindowViewModel { get; set; } = null!;

    [ObservableProperty] public partial bool CrosswordTranslateByCtrlSameC { get; set; } = false;

    [ObservableProperty] public partial Key IncrementalTranslateKey { get; set; } = Key.None;

    private const string CtrlSameCTriggerId = "__ctrl_same_c_sequence";
    private const string IncrementalTranslateTriggerId = "__incremental_translate_hold";

    #region Setting Items

    public GlobalHotkey OpenWindowHotkey { get; set; } = new("Alt + G");
    public GlobalHotkey InputTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey CrosswordTranslateHotkey { get; set; } = new("Alt + D");
    public GlobalHotkey ScreenshotTranslateHotkey { get; set; } = new("Alt + S");
    public GlobalHotkey ImageTranslateHotkey { get; set; } = new("Alt + Shift + X");
    public GlobalHotkey ReplaceTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);
    public GlobalHotkey MouseHookTranslateHotkey { get; set; } = new(Constant.EmptyHotkey);
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

    #endregion

    [JsonIgnore]
    public List<RegisteredHotkeyData> RegisteredHotkeys =>
    [
        ..FixedHotkeys(),

        CreateGlobalHotkeyData(OpenWindowHotkey, "Hotkey_OpenSTranslate"),
        CreateGlobalHotkeyData(InputTranslateHotkey, "Hotkey_InputTranslate"),
        CreateGlobalHotkeyData(CrosswordTranslateHotkey, "Hotkey_CrosswordTranslate"),
        CreateGlobalHotkeyData(MouseHookTranslateHotkey, "Hotkey_MouseHookTranslate"),
        CreateGlobalHotkeyData(ReplaceTranslateHotkey, "Hotkey_ReplaceTranslate"),
        CreateGlobalHotkeyData(ScreenshotTranslateHotkey, "Hotkey_ScreenshotTranslate"),
        CreateGlobalHotkeyData(ImageTranslateHotkey, "Hotkey_ImageTranslate"),
        CreateGlobalHotkeyData(SilentOcrHotkey, "Hotkey_SilentOcr"),
        CreateGlobalHotkeyData(SilentTtsHotkey, "Hotkey_SilentTts"),
        CreateGlobalHotkeyData(OcrHotkey, "Hotkey_Ocr"),
        CreateGlobalHotkeyData(ClipboardMonitorHotkey, "Hotkey_ClipboardMonitor"),

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

    private RegisteredHotkeyData CreateGlobalHotkeyData(GlobalHotkey hotkey, string resourceKey)
    {
        return new RegisteredHotkeyData(
            hotkey,
            resourceKey,
            // 直接注册所有所有类型快捷键 - 虽然只写Global也没问题，但是这样写更加符合设计
            HotkeyType.Global | HotkeyType.MainWindow | HotkeyType.SettingsWindow | HotkeyType.OcrWindow | HotkeyType.ImageTransWindow,
            () =>
            {
                hotkey.Clear();
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
            [nameof(MouseHookTranslateHotkey)] = "Alt + Shift + D",
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

        ApplyCtrlCc();
        ApplyIncrementalTranslate();

        RegisterHotkeys();

        UpdateTrayIconWithPriority();
    }

    private void ApplyIncrementalTranslate()
    {
        if (IncrementalTranslateKey == Key.None)
        {
            HotkeyMapper.RemoveGlobalTrigger(IncrementalTranslateTriggerId);
        }
        else
        {
            var registered = HotkeyMapper.RegisterHoldKey(
                IncrementalTranslateTriggerId,
                IncrementalTranslateKey,
                MainWindowViewModel.OnIncKeyPressed,
                MainWindowViewModel.OnIncKeyReleased);

            if (!registered)
            {
                HotkeyMapper.RemoveGlobalTrigger(IncrementalTranslateTriggerId);
                Ioc.Default.GetRequiredService<ILogger<HotkeySettings>>()
                    .LogWarning("Failed to register incremental translate hold key: {Key}", IncrementalTranslateKey);
            }
        }
    }

    private void ApplyCtrlCc()
    {
        if (CrosswordTranslateByCtrlSameC)
            HotkeyMapper.RegisterSequence(
                CtrlSameCTriggerId,
                [new HotkeyModel("Ctrl + C"), new HotkeyModel("Ctrl + C")],
                TimeSpan.FromMilliseconds(500),
                MainWindowViewModel.CrosswordTranslateByCtrlSameCHandler);
        else
            HotkeyMapper.RemoveGlobalTrigger(CtrlSameCTriggerId);
    }

    public void ApplyGlobalHotkeys()
    {
        UpdateTrayIconWithPriority();
    }

    public void ApplyIgnoreOnFullScreen() => UpdateTrayIconWithPriority();

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
        HandleGlobalLogic(nameof(OpenWindowHotkey));
        HandleGlobalLogic(nameof(InputTranslateHotkey));
        HandleGlobalLogic(nameof(CrosswordTranslateHotkey));
        HandleGlobalLogic(nameof(MouseHookTranslateHotkey));
        HandleGlobalLogic(nameof(ReplaceTranslateHotkey));
        HandleGlobalLogic(nameof(ScreenshotTranslateHotkey));
        HandleGlobalLogic(nameof(ImageTranslateHotkey));
        HandleGlobalLogic(nameof(SilentOcrHotkey));
        HandleGlobalLogic(nameof(SilentTtsHotkey));
        HandleGlobalLogic(nameof(OcrHotkey));
        HandleGlobalLogic(nameof(ClipboardMonitorHotkey));
    }

    private void UnregisterHotkeys()
    {
        HotkeyMapper.RemoveGlobalTrigger(nameof(OpenWindowHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(InputTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(CrosswordTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(MouseHookTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(ReplaceTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(ScreenshotTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(ImageTranslateHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(SilentOcrHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(SilentTtsHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(OcrHotkey));
        HotkeyMapper.RemoveGlobalTrigger(nameof(ClipboardMonitorHotkey));
    }

    private void HandleGlobalLogic(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(OpenWindowHotkey):
                OpenWindowHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(OpenWindowHotkey), OpenWindowHotkey, () => MainWindowViewModel.ToggleAppCommand.Execute(null));
                break;
            case nameof(InputTranslateHotkey):
                InputTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(InputTranslateHotkey), InputTranslateHotkey, () => MainWindowViewModel.InputClearCommand.Execute(WindowActivationMode.Normal));
                break;
            case nameof(CrosswordTranslateHotkey):
                CrosswordTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(CrosswordTranslateHotkey), CrosswordTranslateHotkey, () => MainWindowViewModel.CrosswordTranslateCommand.Execute(null));
                break;
            case nameof(MouseHookTranslateHotkey):
                MouseHookTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(MouseHookTranslateHotkey), MouseHookTranslateHotkey, () => MainWindowViewModel.ToggleMouseHookTranslateCommand.Execute(null));
                break;
            case nameof(ScreenshotTranslateHotkey):
                ScreenshotTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(ScreenshotTranslateHotkey), ScreenshotTranslateHotkey, () => MainWindowViewModel.ScreenshotTranslateCommand.Execute(null));
                break;
            case nameof(ImageTranslateHotkey):
                ImageTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(ImageTranslateHotkey), ImageTranslateHotkey, () => MainWindowViewModel.ImageTranslateCommand.Execute(null));
                break;
            case nameof(OcrHotkey):
                OcrHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(OcrHotkey), OcrHotkey, () => MainWindowViewModel.OcrCommand.Execute(null));
                break;

            // 静默操作
            case nameof(ReplaceTranslateHotkey):
                ReplaceTranslateHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(ReplaceTranslateHotkey), ReplaceTranslateHotkey, () =>
                {
                    if (MainWindowViewModel.ReplaceTranslateCommand.IsRunning)
                    {
                        MainWindowViewModel.ReplaceTranslateCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.ReplaceTranslateCommand.Execute(null);
                });
                break;
            case nameof(SilentOcrHotkey):
                SilentOcrHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(SilentOcrHotkey), SilentOcrHotkey, () =>
                {
                    if (MainWindowViewModel.SilentOcrCommand.IsRunning)
                    {
                        MainWindowViewModel.SilentOcrCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.SilentOcrCommand.Execute(null);
                });
                break;
            case nameof(SilentTtsHotkey):
                SilentTtsHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(SilentTtsHotkey), SilentTtsHotkey, () =>
                {
                    if (MainWindowViewModel.SilentTtsCommand.IsRunning)
                    {
                        MainWindowViewModel.SilentTtsCancelCommand.Execute(null);
                        return;
                    }

                    MainWindowViewModel.SilentTtsCommand.Execute(null);
                });
                break;
            case nameof(ClipboardMonitorHotkey):
                ClipboardMonitorHotkey.IsConflict = !HotkeyMapper.SetGlobalTrigger(nameof(ClipboardMonitorHotkey), ClipboardMonitorHotkey, () => MainWindowViewModel.ToggleClipboardMonitorCommand.Execute(null));
                break;

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
            var isGlobalHotkeyChange = hotkey is GlobalHotkey &&
                e.PropertyName is nameof(Hotkey.Key) or nameof(GlobalHotkey.Kind) or nameof(GlobalHotkey.ModifierKey);
            var isSoftwareHotkeyChange = hotkey is not GlobalHotkey && e.PropertyName == nameof(Hotkey.Key);

            if (!isGlobalHotkeyChange && !isSoftwareHotkeyChange)
                return;
            if (!string.IsNullOrEmpty(propertyName))
                RegisterHotkeys();
            Save();
        };
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

    public TriggerKind Kind { get => field; set { field = value; OnPropertyChanged(); } } = TriggerKind.Chord;

    public ModifierDoubleTapKey ModifierKey { get => field; set { field = value; OnPropertyChanged(); } } = ModifierDoubleTapKey.None;

    public void Clear()
    {
        Kind = TriggerKind.Chord;
        ModifierKey = ModifierDoubleTapKey.None;
        Key = Constant.EmptyHotkey;
    }
}

public class RegisteredHotkeyData
{
    public RegisteredHotkeyData(string hotkey, string resourceKey, HotkeyType type = HotkeyType.Global, Action? action = default)
    {
        Hotkey = hotkey;
        ResourceKey = resourceKey;
        Type = type;
        OnRemovedHotkey = action;
    }

    public RegisteredHotkeyData(GlobalHotkey hotkey, string resourceKey, HotkeyType type = HotkeyType.Global, Action? action = default)
    {
        Hotkey = hotkey.Key;
        Kind = hotkey.Kind;
        ModifierKey = hotkey.ModifierKey;
        ResourceKey = resourceKey;
        Type = type;
        OnRemovedHotkey = action;
    }

    public string Hotkey { get; }
    public TriggerKind Kind { get; } = TriggerKind.Chord;
    public ModifierDoubleTapKey ModifierKey { get; } = ModifierDoubleTapKey.None;
    public string ResourceKey { get; }
    public HotkeyType Type { get; }
    public Action? OnRemovedHotkey { get; }

    public bool Matches(TriggerKind kind, string hotkey, ModifierDoubleTapKey modifierKey)
    {
        if (Kind != kind)
            return false;

        return Kind switch
        {
            TriggerKind.ModifierDoubleTap => ModifierKey == modifierKey && modifierKey != ModifierDoubleTapKey.None,
            _ => string.Equals(Hotkey, hotkey, StringComparison.OrdinalIgnoreCase)
        };
    }
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
