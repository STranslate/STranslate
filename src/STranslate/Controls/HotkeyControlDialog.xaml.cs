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

    private const int MaxSequenceLength = 2;
    private static readonly TimeSpan SequenceTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ModifierDoubleTapTimeout = TimeSpan.FromMilliseconds(350);

    private readonly HotkeyType _type;
    private readonly Internationalization _i18n;
    private readonly HotkeySettings _hotkeySettings;
    private readonly TriggerKind _cacheKind;
    private readonly HotkeyModel _cacheHotkey;
    private readonly ModifierDoubleTapKey _cacheModifierKey;
    private readonly IReadOnlyList<string> _cacheSequence;
    private readonly bool _isHoldOnly;
    private readonly List<HotkeyModel> _currentSequence = [];
    private Action? _overwriteOtherHotkey;
    private TriggerKind _currentKind;
    private HotkeyModel _currentHotkey;
    private HotkeyModel? _pendingSequenceFirstHotkey;
    private DateTimeOffset _pendingSequenceDeadline = DateTimeOffset.MinValue;
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
    public IReadOnlyList<string> ResultSequence { get; private set; } = [];
    public string EmptyHotkey => _i18n.GetTranslation("None");

    public HotkeyControlDialog(HotkeyType type, string hotkey, string defaultHotkey, string windowTitle = "", bool singleKeyMode = false)
        : this(type, TriggerKind.Chord, hotkey, ModifierDoubleTapKey.None, [], defaultHotkey, windowTitle, singleKeyMode)
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
        : this(type, kind, hotkey, modifierKey, [], defaultHotkey, windowTitle, singleKeyMode)
    {
    }

    public HotkeyControlDialog(
        HotkeyType type,
        TriggerKind kind,
        string hotkey,
        ModifierDoubleTapKey modifierKey,
        IReadOnlyList<string>? sequence,
        string defaultHotkey,
        string windowTitle = "",
        bool singleKeyMode = false,
        bool holdOnly = false)
    {
        _type = type;
        _cacheKind = holdOnly ? TriggerKind.Hold : kind;
        _cacheHotkey = new HotkeyModel(hotkey);
        _cacheModifierKey = modifierKey;
        _cacheSequence = sequence?.ToList() ?? [];
        _isHoldOnly = holdOnly;
        _currentKind = _cacheKind;
        _currentHotkey = _cacheHotkey;
        _currentModifierKey = modifierKey;
        _currentSequence.AddRange(_cacheSequence.Select(x => new HotkeyModel(x)));
        SingleKeyMode = singleKeyMode;
        _i18n = Ioc.Default.GetRequiredService<Internationalization>();
        _hotkeySettings = Ioc.Default.GetRequiredService<HotkeySettings>();
        WindowTitle = windowTitle switch
        {
            "" or null => _i18n.GetTranslation("BindHotkey"),
            _ => windowTitle
        };
        DefaultHotkey = defaultHotkey;

        SetKeysToDisplay(_currentKind, _cacheHotkey, modifierKey, _currentSequence);

        InitializeComponent();
        if (_currentKind == TriggerKind.ModifierDoubleTap && _currentModifierKey == ModifierDoubleTapKey.Win)
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
        ResultSequence = _currentKind == TriggerKind.Sequence
            ? [.. _currentSequence.Select(x => x.ToString())]
            : [];
        ResultValue = _currentKind is TriggerKind.ModifierDoubleTap or TriggerKind.Sequence
            ? Constant.EmptyHotkey
            : _currentHotkey.ToString();
        Hide();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_isHoldOnly)
        {
            ResetAutoCaptureState();
            SetHoldToDisplay(new HotkeyModel(DefaultHotkey));
            return;
        }

        ResetAutoCaptureState();
        SetChordToDisplay(new HotkeyModel(DefaultHotkey));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        ResetAutoCaptureState();
        SetKeysToDisplay(_isHoldOnly ? TriggerKind.Hold : TriggerKind.Chord, new HotkeyModel(Constant.EmptyHotkey), ModifierDoubleTapKey.None, []);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ReturnType = HkReturnType.Cancel;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.IsRepeat)
            return;

        HandleKeyDown(NormalizeKey(e));
    }

    private void HandleKeyDown(Key key)
    {
        if (SingleKeyMode)
        {
            SetChordToDisplay(new HotkeyModel(false, false, false, false, key));
            return;
        }

        if (_isHoldOnly)
        {
            SetHoldToDisplay(new HotkeyModel(false, false, false, false, key));
            return;
        }

        HandleAutoTriggerKeyDown(key);
    }

    private void HandleAutoTriggerKeyDown(Key key)
    {
        if (key.TryGetModifierDoubleTapKey(out var modifierKey))
        {
            TrackModifierTapKeyDown(modifierKey);
            SetPureModifierPlaceholderToDisplay(key);
            return;
        }

        if (IsAnyPureModifierKey(key))
        {
            InvalidateModifierTapState();
            SetPureModifierPlaceholderToDisplay(key);
            return;
        }

        InvalidateModifierTapState();
        SetAutoChordToDisplay(BuildChordHotkey(key));
    }

    private void SetAutoChordToDisplay(HotkeyModel hotkey)
    {
        var now = DateTimeOffset.UtcNow;
        if (_pendingSequenceFirstHotkey is { } firstHotkey && now <= _pendingSequenceDeadline)
        {
            SetSequenceToDisplay([firstHotkey, hotkey]);
            ClearPendingSequence();
            return;
        }

        SetChordToDisplay(hotkey);
        _pendingSequenceFirstHotkey = hotkey;
        _pendingSequenceDeadline = now + SequenceTimeout;
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
        if (_isHoldOnly)
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

    private void ResetAutoCaptureState()
    {
        ClearPendingSequence();
        InvalidateModifierTapState();
    }

    private void ClearPendingSequence()
    {
        _pendingSequenceFirstHotkey = null;
        _pendingSequenceDeadline = DateTimeOffset.MinValue;
    }

    private void SetChordToDisplay(HotkeyModel hotkey)
        => SetKeysToDisplay(TriggerKind.Chord, hotkey, ModifierDoubleTapKey.None, []);

    private void SetPureModifierPlaceholderToDisplay(Key key)
        => SetChordToDisplay(new HotkeyModel(false, false, false, false, key));

    private void SetHoldToDisplay(HotkeyModel hotkey)
        => SetKeysToDisplay(TriggerKind.Hold, hotkey, ModifierDoubleTapKey.None, []);

    private void SetSequenceToDisplay(IReadOnlyList<HotkeyModel> sequence)
        => SetKeysToDisplay(TriggerKind.Sequence, default, ModifierDoubleTapKey.None, sequence);

    private void SetModifierDoubleTapToDisplay(ModifierDoubleTapKey modifierKey)
        => SetKeysToDisplay(TriggerKind.ModifierDoubleTap, default, modifierKey, []);

    private void SetKeysToDisplay(
        TriggerKind kind,
        HotkeyModel hotkey,
        ModifierDoubleTapKey modifierKey,
        IReadOnlyList<HotkeyModel> sequence)
    {
        _overwriteOtherHotkey = null;
        _currentKind = kind;
        _currentHotkey = hotkey;
        _currentModifierKey = modifierKey;
        _currentSequence.Clear();
        _currentSequence.AddRange(sequence);
        KeysToDisplay.Clear();
        if (PART_InfoBar != null)
            ResetUI();

        switch (kind)
        {
            case TriggerKind.ModifierDoubleTap:
                ClearPendingSequence();
                AddModifierDoubleTapDisplay(modifierKey);
                break;
            case TriggerKind.Sequence:
                ClearPendingSequence();
                AddSequenceDisplay(_currentSequence);
                break;
            case TriggerKind.Hold:
                ClearPendingSequence();
                AddHotkeyDisplay(hotkey);
                break;
            default:
                AddHotkeyDisplay(hotkey);
                break;
        }

        if (PART_InfoBar == null)
            return;

        UpdateUI();
    }

    private void AddHotkeyDisplay(HotkeyModel hotkey)
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

    private void AddModifierDoubleTapDisplay(ModifierDoubleTapKey modifierKey)
    {
        if (modifierKey == ModifierDoubleTapKey.None)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        KeysToDisplay.Add(_i18n.GetTranslation("Hotkey_DoubleTap"));
        KeysToDisplay.Add(modifierKey.ToString());
    }

    private void AddSequenceDisplay(IReadOnlyList<HotkeyModel> sequence)
    {
        if (sequence.Count == 0)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        for (var index = 0; index < sequence.Count; index++)
        {
            if (index > 0)
                KeysToDisplay.Add(",");

            foreach (var key in sequence[index].EnumerateDisplayKeys())
            {
                KeysToDisplay.Add(key);
            }
        }
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
                ShowUnavailable();
                return;
            }
        }
        else if (_currentKind == TriggerKind.Hold)
        {
            if (!HotkeyMapper.CheckHoldAvailability(_currentHotkey))
            {
                ShowUnavailable();
                return;
            }
        }
        else if (_currentKind == TriggerKind.Sequence)
        {
            if (!CheckSequenceAvailability(_currentSequence))
            {
                ShowUnavailable();
                return;
            }
        }
        else if (_currentModifierKey is ModifierDoubleTapKey.None or ModifierDoubleTapKey.Win)
        {
            ShowUnavailable();
            return;
        }

        var sequence = _currentSequence.Select(x => x.ToString()).ToList();
        var registeredHotkey = _hotkeySettings.RegisteredHotkeys
            .Where(x => x.Type.HasFlag(_type) || _type.HasFlag(x.Type))
            .Where(x => !x.Matches(_cacheKind, _cacheHotkey.ToString(), _cacheModifierKey, _cacheSequence))
            .FirstOrDefault(x => x.ConflictsWith(_currentKind, _currentHotkey.ToString(), _currentModifierKey, sequence));
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

    private void ShowUnavailable()
    {
        PART_InfoBar.Message = _i18n.GetTranslation("HotkeyUnavailable");
        PART_InfoBar.Visibility = Visibility.Visible;
        SaveBtn.IsEnabled = false;
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

    private static bool CheckSequenceAvailability(IReadOnlyList<HotkeyModel> sequence)
        => sequence.Count >= MaxSequenceLength && sequence.All(x => x.Validate(false));

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

    private static bool IsAnyPureModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or
                  Key.LeftAlt or Key.RightAlt or
                  Key.LeftShift or Key.RightShift or
                  Key.LWin or Key.RWin;

    private bool IsGlobalHotkeyType => _type == HotkeyType.Global;

    private bool CanTrackModifierDoubleTap => IsGlobalHotkeyType && !SingleKeyMode && !_isHoldOnly;
}
