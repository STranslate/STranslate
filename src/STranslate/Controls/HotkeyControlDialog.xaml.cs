using CommunityToolkit.Mvvm.DependencyInjection;
using iNKORE.UI.WPF.Modern.Controls;
using STranslate.Core;
using STranslate.Helpers;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace STranslate.Controls;

public partial class HotkeyControlDialog : ContentDialog
{
    public enum HkReturnType
    {
        Save,
        Delete,
        Cancel
    }

    private readonly HotkeyType _type;
    private readonly Internationalization _i18n;
    private readonly HotkeySettings _hotkeySettings;
    private readonly HotkeyModel _cacheHotkey;
    private Action? _overwriteOtherHotkey;

    private string DefaultHotkey { get; }
    public string WindowTitle { get; }
    public bool SingleKeyMode { get; }
    public ObservableCollection<string> KeysToDisplay { get; } = [];
    public HkReturnType ReturnType { get; private set; } = HkReturnType.Cancel;
    public string ResultValue { get; private set; } = string.Empty;
    public string EmptyHotkey => _i18n.GetTranslation("None");

    public HotkeyControlDialog(HotkeyType type, string hotkey, string defaultHotkey, string windowTitle = "", bool singleKeyMode = false)
    {
        _type = type;
        SingleKeyMode = singleKeyMode;
        _i18n = Ioc.Default.GetRequiredService<Internationalization>();
        _hotkeySettings = Ioc.Default.GetRequiredService<HotkeySettings>();
        WindowTitle = windowTitle switch
        {
            "" or null => _i18n.GetTranslation("BindHotkey"),
            _ => windowTitle
        };
        DefaultHotkey = defaultHotkey;
        _cacheHotkey = new HotkeyModel(hotkey);
        SetKeysToDisplay(_cacheHotkey);

        InitializeComponent();
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        _overwriteOtherHotkey?.Invoke();
        OnSaveClick(sender, e);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 空热键状态，重定向到删除结果
        if (KeysToDisplay.Count == 1 && KeysToDisplay[0] == EmptyHotkey)
        {
            ReturnType = HkReturnType.Delete;
            Hide();
            return;
        }
        ReturnType = HkReturnType.Save;
        ResultValue = string.Join("+", KeysToDisplay);
        Hide();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
        => SetKeysToDisplay(new HotkeyModel(DefaultHotkey));

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        KeysToDisplay.Clear();
        KeysToDisplay.Add(EmptyHotkey);
        ResetUI();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ReturnType = HkReturnType.Cancel;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        //when alt is pressed, the real key should be e.SystemKey
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单键模式处理
        if (SingleKeyMode)
        {
            // 忽略修饰键本身
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            // 创建不带修饰键的单键模型
            var singleHotkeyModel = new HotkeyModel(false, false, false, false, key);
            SetKeysToDisplay(singleHotkeyModel);
            return;
        }

        // 原有的组合键处理逻辑
        SpecialKeyState specialKeyState = HotkeyMapper.CheckModifiers();

        var hotkeyModel = new HotkeyModel(
            specialKeyState.AltPressed,
            specialKeyState.ShiftPressed,
            specialKeyState.WinPressed,
            specialKeyState.CtrlPressed,
            key);

        SetKeysToDisplay(hotkeyModel);
    }

    private void SetKeysToDisplay(HotkeyModel? hotkey)
    {
        _overwriteOtherHotkey = null;
        KeysToDisplay.Clear();

        if (hotkey == null || hotkey == default(HotkeyModel) || hotkey.Value.ToString() == Constant.EmptyHotkey)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        foreach (var key in hotkey.Value.EnumerateDisplayKeys()!)
        {
            KeysToDisplay.Add(key);
        }

        if (PART_InfoBar == null)
            return;

        UpdateUI(hotkey.Value);
    }

    private void UpdateUI(HotkeyModel hotkey)
    {
        ResetUI();

        if (_type.HasFlag(HotkeyType.Global) &&
            HotkeyMapper.TryGetReservedGlobalHotkeyMessageKey(hotkey, out var resourceKey))
        {
            PART_InfoBar.Message = _i18n.GetTranslation(resourceKey);
            PART_InfoBar.Visibility = Visibility.Visible;
            SaveBtn.IsEnabled = false;
            return;
        }

        var registeredHotkey = _hotkeySettings.RegisteredHotkeys
            .Where(x => x.Type.HasFlag(_type) || _type.HasFlag(x.Type))
            .Where(x => x.Hotkey != _cacheHotkey.ToString())
            .FirstOrDefault(x => x.Hotkey == hotkey.ToString());
        if (registeredHotkey != null)
        {
            PART_InfoBar.Visibility = Visibility.Visible;
            if (registeredHotkey.OnRemovedHotkey != null)
            {
                PART_InfoBar.Message = string.Format(_i18n.GetTranslation("HotkeyUnavailableEditable"), _i18n.GetTranslation(registeredHotkey.ResourceKey));
                SaveBtn.IsEnabled = false;
                SaveBtn.Visibility = Visibility.Collapsed;
                OverwriteBtn.Visibility = Visibility.Visible;
                _overwriteOtherHotkey = registeredHotkey.OnRemovedHotkey;
            }
            else
            {
                PART_InfoBar.Message = string.Format(_i18n.GetTranslation("HotkeyUnavailableUneditable"), _i18n.GetTranslation(registeredHotkey.ResourceKey));
                SaveBtn.IsEnabled = false;
                SaveBtn.Visibility = Visibility.Visible;
                OverwriteBtn.Visibility = Visibility.Collapsed;
            }
        }
        else if (!CheckHotkeyAvailability(hotkey, !SingleKeyMode)) // 单键模式下不验证 KeyGesture
        {
            PART_InfoBar.Message = _i18n.GetTranslation("HotkeyUnavailable");
            PART_InfoBar.Visibility = Visibility.Visible;
            SaveBtn.IsEnabled = false;
        }
    }

    private void ResetUI()
    {
        PART_InfoBar.Visibility = Visibility.Collapsed;
        SaveBtn.IsEnabled = true;
        SaveBtn.Visibility = Visibility.Visible;
        OverwriteBtn.Visibility = Visibility.Collapsed;
    }

    private bool CheckHotkeyAvailability(HotkeyModel hotkey, bool validateKeyGesture)
    {
        if (_type.HasFlag(HotkeyType.Global) && HotkeyMapper.IsReservedGlobalHotkey(hotkey))
            return false;

        return hotkey.ToString() is "LWin" or "RWin" ||
               (hotkey.Validate(validateKeyGesture) && HotkeyMapper.CheckAvailability(hotkey));
    }
}
