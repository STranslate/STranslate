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

    private static readonly TimeSpan ModifierDoubleTapTimeout = TimeSpan.FromMilliseconds(350);

    private readonly HotkeyType _type;
    private readonly Internationalization _i18n;
    private readonly HotkeySettings _hotkeySettings;
    private readonly TriggerKind _cacheKind;
    private readonly HotkeyModel _cacheHotkey;
    private readonly ModifierDoubleTapKey _cacheModifierKey;
    private Action? _overwriteOtherHotkey;
    private TriggerKind _currentKind;
    private HotkeyModel _currentHotkey;
    private ModifierDoubleTapKey _currentModifierKey;
    private ModifierDoubleTapKey _activeModifierTapKey = ModifierDoubleTapKey.None;
    private ModifierDoubleTapKey _lastModifierTapKey = ModifierDoubleTapKey.None;
    private DateTimeOffset _lastModifierTapAt = DateTimeOffset.MinValue;
    private int _hotkeyCaptureId;
    private bool _isActiveModifierTapClean;

    private string DefaultHotkey { get; }
    public string WindowTitle { get; }
    public bool SingleKeyMode { get; }
    public ObservableCollection<string> KeysToDisplay { get; } = [];
    public HkReturnType ReturnType { get; private set; } = HkReturnType.Cancel;
    public TriggerKind ResultKind { get; private set; } = TriggerKind.Chord;
    public string ResultValue { get; private set; } = string.Empty;
    public ModifierDoubleTapKey ResultModifierKey { get; private set; } = ModifierDoubleTapKey.None;
    public string EmptyHotkey => _i18n.GetTranslation("None");

    public HotkeyControlDialog(HotkeyType type, string hotkey, string defaultHotkey, string windowTitle = "", bool singleKeyMode = false)
        : this(type, TriggerKind.Chord, hotkey, ModifierDoubleTapKey.None, defaultHotkey, windowTitle, singleKeyMode)
    {
    }

    public HotkeyControlDialog(
        HotkeyType type,
        TriggerKind kind,
        string hotkey,
        ModifierDoubleTapKey modifierKey,
        string defaultHotkey,
        string windowTitle = "",
        bool singleKeyMode = false)
    {
        _type = type;
        _cacheKind = kind;
        _cacheHotkey = new HotkeyModel(hotkey);
        _cacheModifierKey = modifierKey;
        _currentKind = kind;
        _currentHotkey = _cacheHotkey;
        _currentModifierKey = modifierKey;
        SingleKeyMode = singleKeyMode;
        _i18n = Ioc.Default.GetRequiredService<Internationalization>();
        _hotkeySettings = Ioc.Default.GetRequiredService<HotkeySettings>();
        WindowTitle = windowTitle switch
        {
            "" or null => _i18n.GetTranslation("BindHotkey"),
            _ => windowTitle
        };
        DefaultHotkey = defaultHotkey;

        SetKeysToDisplay(kind, _cacheHotkey, modifierKey);

        InitializeComponent();
        if (kind == TriggerKind.ModifierDoubleTap && modifierKey == ModifierDoubleTapKey.Win)
            UpdateUI();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hotkeyCaptureId != 0)
            return;

        _hotkeyCaptureId = GlobalInputEngine.BeginHotkeyCapture(OnCapturedSystemKey);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_hotkeyCaptureId == 0)
            return;

        GlobalInputEngine.EndHotkeyCapture(_hotkeyCaptureId);
        _hotkeyCaptureId = 0;
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        _overwriteOtherHotkey?.Invoke();
        OnSaveClick(sender, e);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (KeysToDisplay.Count == 1 && KeysToDisplay[0] == EmptyHotkey)
        {
            ReturnType = HkReturnType.Delete;
            Hide();
            return;
        }

        ReturnType = HkReturnType.Save;
        ResultKind = _currentKind;
        ResultModifierKey = _currentKind == TriggerKind.ModifierDoubleTap
            ? _currentModifierKey
            : ModifierDoubleTapKey.None;
        ResultValue = _currentKind == TriggerKind.ModifierDoubleTap
            ? Constant.EmptyHotkey
            : _currentHotkey.ToString();
        Hide();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
        => SetChordToDisplay(new HotkeyModel(DefaultHotkey));

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _currentKind = TriggerKind.Chord;
        _currentHotkey = new HotkeyModel(Constant.EmptyHotkey);
        _currentModifierKey = ModifierDoubleTapKey.None;
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

        HandleKeyDown(NormalizeKey(e));
    }

    private void HandleKeyDown(Key key)
    {
        if (SingleKeyMode)
        {
            SetChordToDisplay(new HotkeyModel(false, false, false, false, key));
            return;
        }

        if (IsGlobalHotkeyType && key.TryGetModifierDoubleTapKey(out var modifierKey))
        {
            TrackModifierTapKeyDown(modifierKey);
            SetChordToDisplay(new HotkeyModel(false, false, false, false, key));
            return;
        }

        InvalidateModifierTapState();
        SetChordToDisplay(BuildChordHotkey(key));
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!CanTrackModifierDoubleTap)
            return;

        e.Handled = true;

        HandleKeyUp(NormalizeKey(e));
    }

    private void HandleKeyUp(Key key)
    {
        if (!CanTrackModifierDoubleTap)
            return;

        if (!key.TryGetModifierDoubleTapKey(out var modifierKey))
            return;

        if (!TryConsumeModifierTap(modifierKey))
            return;

        var now = DateTimeOffset.UtcNow;
        if (_lastModifierTapKey == modifierKey && now - _lastModifierTapAt <= ModifierDoubleTapTimeout)
        {
            _lastModifierTapKey = ModifierDoubleTapKey.None;
            _lastModifierTapAt = DateTimeOffset.MinValue;
            SetModifierDoubleTapToDisplay(modifierKey);
            return;
        }

        _lastModifierTapKey = modifierKey;
        _lastModifierTapAt = now;
    }

    private void OnCapturedSystemKey(Key key, bool isKeyDown)
    {
        if (isKeyDown)
            HandleKeyDown(key);
        else
            HandleKeyUp(key);
    }

    private void TrackModifierTapKeyDown(ModifierDoubleTapKey modifierKey)
    {
        if (_activeModifierTapKey == ModifierDoubleTapKey.None)
        {
            _activeModifierTapKey = modifierKey;
            _isActiveModifierTapClean = !HasAdditionalModifier(modifierKey);
            if (!_isActiveModifierTapClean)
            {
                _lastModifierTapKey = ModifierDoubleTapKey.None;
                _lastModifierTapAt = DateTimeOffset.MinValue;
            }
            return;
        }

        if (_activeModifierTapKey != modifierKey || HasAdditionalModifier(modifierKey))
            InvalidateModifierTapState();
    }

    private bool TryConsumeModifierTap(ModifierDoubleTapKey modifierKey)
    {
        var isCleanTap = _activeModifierTapKey == modifierKey && _isActiveModifierTapClean;
        ResetActiveModifierTapState();
        return isCleanTap;
    }

    private void InvalidateModifierTapState()
    {
        ResetActiveModifierTapState();
        _lastModifierTapKey = ModifierDoubleTapKey.None;
        _lastModifierTapAt = DateTimeOffset.MinValue;
    }

    private void ResetActiveModifierTapState()
    {
        _activeModifierTapKey = ModifierDoubleTapKey.None;
        _isActiveModifierTapClean = false;
    }

    private void SetChordToDisplay(HotkeyModel hotkey)
        => SetKeysToDisplay(TriggerKind.Chord, hotkey, ModifierDoubleTapKey.None);

    private void SetModifierDoubleTapToDisplay(ModifierDoubleTapKey modifierKey)
        => SetKeysToDisplay(TriggerKind.ModifierDoubleTap, default, modifierKey);

    private void SetKeysToDisplay(TriggerKind kind, HotkeyModel hotkey, ModifierDoubleTapKey modifierKey)
    {
        _overwriteOtherHotkey = null;
        _currentKind = kind;
        _currentHotkey = hotkey;
        _currentModifierKey = modifierKey;
        KeysToDisplay.Clear();
        if (PART_InfoBar != null)
            ResetUI();

        if (kind == TriggerKind.ModifierDoubleTap)
        {
            if (modifierKey == ModifierDoubleTapKey.None)
            {
                KeysToDisplay.Add(EmptyHotkey);
                return;
            }

            KeysToDisplay.Add(_i18n.GetTranslation("Hotkey_DoubleTap"));
            KeysToDisplay.Add(modifierKey.ToString());
        }
        else
        {
            if (hotkey == default(HotkeyModel) || hotkey.ToString() == Constant.EmptyHotkey)
            {
                KeysToDisplay.Add(EmptyHotkey);
                return;
            }

            foreach (var key in hotkey.EnumerateDisplayKeys())
            {
                KeysToDisplay.Add(key);
            }
        }

        if (PART_InfoBar == null)
            return;

        UpdateUI();
    }

    private void UpdateUI()
    {
        ResetUI();

        if (_currentKind == TriggerKind.Chord)
        {
            if (_type.HasFlag(HotkeyType.Global) &&
                HotkeyMapper.TryGetReservedGlobalHotkeyMessageKey(_currentHotkey, out var resourceKey))
            {
                PART_InfoBar.Message = _i18n.GetTranslation(resourceKey);
                PART_InfoBar.Visibility = Visibility.Visible;
                SaveBtn.IsEnabled = false;
                return;
            }

            if (!CheckHotkeyAvailability(_currentHotkey, !SingleKeyMode))
            {
                PART_InfoBar.Message = _i18n.GetTranslation("HotkeyUnavailable");
                PART_InfoBar.Visibility = Visibility.Visible;
                SaveBtn.IsEnabled = false;
                return;
            }
        }
        else if (_currentModifierKey is ModifierDoubleTapKey.None or ModifierDoubleTapKey.Win)
        {
            PART_InfoBar.Message = _i18n.GetTranslation("HotkeyUnavailable");
            PART_InfoBar.Visibility = Visibility.Visible;
            SaveBtn.IsEnabled = false;
            return;
        }

        var registeredHotkey = _hotkeySettings.RegisteredHotkeys
            .Where(x => x.Type.HasFlag(_type) || _type.HasFlag(x.Type))
            .Where(x => !x.Matches(_cacheKind, _cacheHotkey.ToString(), _cacheModifierKey))
            .FirstOrDefault(x => x.Matches(_currentKind, _currentHotkey.ToString(), _currentModifierKey));
        if (registeredHotkey == null)
            return;

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

        return hotkey.Validate(validateKeyGesture) && HotkeyMapper.CheckAvailability(hotkey);
    }

    private static HotkeyModel BuildChordHotkey(Key key)
    {
        var specialKeyState = HotkeyMapper.CheckModifiers();
        return new HotkeyModel(
            specialKeyState.AltPressed,
            specialKeyState.ShiftPressed,
            specialKeyState.WinPressed || GlobalInputEngine.IsHotkeyCaptureWinPressed,
            specialKeyState.CtrlPressed,
            key);
    }

    private static Key NormalizeKey(KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey != Key.None)
            return e.SystemKey;

        if (e.Key == Key.ImeProcessed && e.ImeProcessedKey != Key.None)
            return e.ImeProcessedKey;

        return e.Key;
    }

    private static bool HasAdditionalModifier(ModifierDoubleTapKey modifierKey)
    {
        var modifiers = Keyboard.Modifiers;
        return modifierKey switch
        {
            ModifierDoubleTapKey.Ctrl => (modifiers & ~ModifierKeys.Control) != ModifierKeys.None,
            ModifierDoubleTapKey.Alt => (modifiers & ~ModifierKeys.Alt) != ModifierKeys.None,
            ModifierDoubleTapKey.Shift => (modifiers & ~ModifierKeys.Shift) != ModifierKeys.None,
            ModifierDoubleTapKey.Win => (modifiers & ~ModifierKeys.Windows) != ModifierKeys.None,
            _ => modifiers != ModifierKeys.None
        };
    }

    private bool IsGlobalHotkeyType => _type == HotkeyType.Global;

    private bool CanTrackModifierDoubleTap => IsGlobalHotkeyType && !SingleKeyMode;
}
