using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace STranslate.Controls;

public class HotkeyControl : Button
{
    private readonly Internationalization _i18n;

    static HotkeyControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyControl),
            new FrameworkPropertyMetadata(typeof(HotkeyControl)));
    }

    public HotkeyControl()
    {
        _i18n = Ioc.Default.GetRequiredService<Internationalization>();
    }

    public string WindowTitle
    {
        get => (string)GetValue(WindowTitleProperty);
        set => SetValue(WindowTitleProperty, value);
    }
    public static readonly DependencyProperty WindowTitleProperty = DependencyProperty.Register(
        nameof(WindowTitle),
        typeof(string),
        typeof(HotkeyControl),
        new PropertyMetadata(string.Empty)
    );

    public bool ValidateKeyGesture
    {
        get => (bool)GetValue(ValidateKeyGestureProperty);
        set => SetValue(ValidateKeyGestureProperty, value);
    }
    public static readonly DependencyProperty ValidateKeyGestureProperty = DependencyProperty.Register(
        nameof(ValidateKeyGesture),
        typeof(bool),
        typeof(HotkeyControl),
        new PropertyMetadata(true)
    );

    public string DefaultHotkey
    {
        get => (string)GetValue(DefaultHotkeyProperty);
        set => SetValue(DefaultHotkeyProperty, value);
    }

    public static readonly DependencyProperty DefaultHotkeyProperty = DependencyProperty.Register(
        nameof(DefaultHotkey),
        typeof(string),
        typeof(HotkeyControl),
        new PropertyMetadata(string.Empty)
    );

    public ICommand? ChangeHotkey
    {
        get => (ICommand)GetValue(ChangeHotkeyProperty);
        set => SetValue(ChangeHotkeyProperty, value);
    }
    public static readonly DependencyProperty ChangeHotkeyProperty = DependencyProperty.Register(
        nameof(ChangeHotkey),
        typeof(ICommand),
        typeof(HotkeyControl),
        new PropertyMetadata(default(ICommand))
    );

    public bool IsRegistered
    {
        get => (bool)GetValue(IsRegisteredProperty);
        set => SetValue(IsRegisteredProperty, value);
    }
    public static readonly DependencyProperty IsRegisteredProperty = DependencyProperty.Register(
        nameof(IsRegistered),
        typeof(bool),
        typeof(HotkeyControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault)
    );

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(string),
        typeof(HotkeyControl),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged)
    );

    public TriggerKind Kind
    {
        get => (TriggerKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(TriggerKind),
        typeof(HotkeyControl),
        new FrameworkPropertyMetadata(TriggerKind.Chord, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged)
    );

    public ModifierDoubleTapKey ModifierKey
    {
        get => (ModifierDoubleTapKey)GetValue(ModifierKeyProperty);
        set => SetValue(ModifierKeyProperty, value);
    }
    public static readonly DependencyProperty ModifierKeyProperty = DependencyProperty.Register(
        nameof(ModifierKey),
        typeof(ModifierDoubleTapKey),
        typeof(HotkeyControl),
        new FrameworkPropertyMetadata(ModifierDoubleTapKey.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged)
    );

    public ObservableCollection<string> Sequence
    {
        get => (ObservableCollection<string>)GetValue(SequenceProperty);
        set => SetValue(SequenceProperty, value);
    }
    public static readonly DependencyProperty SequenceProperty = DependencyProperty.Register(
        nameof(Sequence),
        typeof(ObservableCollection<string>),
        typeof(HotkeyControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged)
    );

    public bool HoldOnly
    {
        get => (bool)GetValue(HoldOnlyProperty);
        set => SetValue(HoldOnlyProperty, value);
    }
    public static readonly DependencyProperty HoldOnlyProperty = DependencyProperty.Register(
        nameof(HoldOnly),
        typeof(bool),
        typeof(HotkeyControl),
        new PropertyMetadata(false)
    );

    public HotkeyType Type
    {
        get => (HotkeyType)GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    public static readonly DependencyProperty TypeProperty =
        DependencyProperty.Register(
            nameof(Type),
            typeof(HotkeyType),
            typeof(HotkeyControl),
            new PropertyMetadata(HotkeyType.Global));


    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HotkeyControl hotkey)
        {
            return;
        }

        hotkey.RefreshHotkeyInterface(hotkey.Hotkey);
    }

    private void RefreshHotkeyInterface(string hotkey)
    {
        if (IsGlobalTriggerControl && Kind == TriggerKind.ModifierDoubleTap)
        {
            SetModifierDoubleTapToDisplay(ModifierKey);
            CurrentHotkey = new HotkeyModel(false, false, false, false, ModifierKey.ToRepresentativeKey());
            return;
        }

        if (IsGlobalTriggerControl && Kind == TriggerKind.Sequence)
        {
            SetSequenceToDisplay(Sequence);
            CurrentHotkey = Sequence?.Count > 0
                ? new HotkeyModel(Sequence[0])
                : new HotkeyModel(false, false, false, false, Key.None);
            return;
        }

        SetKeysToDisplay(new HotkeyModel(hotkey));
        CurrentHotkey = new HotkeyModel(hotkey);
    }

    private bool CheckHotkeyAvailability(HotkeyModel hotkey, bool validateKeyGesture) =>
        (!Type.HasFlag(HotkeyType.Global) || !HotkeyMapper.IsReservedGlobalHotkey(hotkey)) &&
        hotkey.Validate(validateKeyGesture) &&
        HotkeyMapper.CheckAvailability(hotkey);

    public string EmptyHotkey => _i18n.GetTranslation("None");

    public ObservableCollection<string> KeysToDisplay { get; set; } = [];

    public HotkeyModel CurrentHotkey { get; private set; } = new(false, false, false, false, Key.None);

    protected override void OnClick() => _ = OpenHotkeyDialogAsync();

    private async Task OpenHotkeyDialogAsync()
    {
        var dialog = new HotkeyControlDialog(Type, Kind, Hotkey, ModifierKey, Sequence, DefaultHotkey, WindowTitle, holdOnly: HoldOnly);
        await dialog.ShowAsync();
        switch (dialog.ReturnType)
        {
            case HotkeyControlDialog.HkReturnType.Save:
                if (IsGlobalTriggerControl)
                    SetGlobalTrigger(dialog.ResultKind, dialog.ResultValue, dialog.ResultModifierKey, dialog.ResultSequence);
                else
                    SetHotkey(dialog.ResultValue);
                break;
            case HotkeyControlDialog.HkReturnType.Cancel:
                RefreshHotkeyInterface(Hotkey);
                break;
            case HotkeyControlDialog.HkReturnType.Delete:
                Delete();
                break;
            default:
                break;
        }
    }

    public void SetHotkey(string keyStr, bool triggerValidate = true) => SetHotkey(new HotkeyModel(keyStr), triggerValidate);

    private void SetHotkey(HotkeyModel keyModel, bool triggerValidate = true)
    {
        var hotkeyString = keyModel.ToString();
        if (triggerValidate)
        {
            // TODO: This is a temporary way to enforce changing only the open flow hotkey to Win, and will be removed by PR #3157
            var isWinKey = hotkeyString is "LWin" or "RWin";

            if (!isWinKey && !CheckHotkeyAvailability(keyModel, ValidateKeyGesture))
            {
                return;
            }

            Hotkey = hotkeyString;
            SetKeysToDisplay(CurrentHotkey);
            ChangeHotkey?.Execute(keyModel);
        }
        else
        {
            Hotkey = hotkeyString;
            ChangeHotkey?.Execute(keyModel);
        }
    }

    private void Delete()
    {
        if (IsGlobalTriggerControl)
        {
            ModifierKey = ModifierDoubleTapKey.None;
            Sequence = [];
            Kind = TriggerKind.Chord;
        }

        Hotkey = Constant.EmptyHotkey;
        SetKeysToDisplay(new HotkeyModel(false, false, false, false, Key.None));
    }

    private void SetGlobalTrigger(TriggerKind kind, string keyStr, ModifierDoubleTapKey modifierKey, IReadOnlyList<string> sequence)
    {
        if (kind == TriggerKind.ModifierDoubleTap)
        {
            Hotkey = Constant.EmptyHotkey;
            ModifierKey = modifierKey;
            Sequence = [];
            Kind = TriggerKind.ModifierDoubleTap;
            SetModifierDoubleTapToDisplay(modifierKey);
            return;
        }

        if (kind == TriggerKind.Sequence)
        {
            Hotkey = Constant.EmptyHotkey;
            ModifierKey = ModifierDoubleTapKey.None;
            Sequence = [.. sequence];
            Kind = TriggerKind.Sequence;
            SetSequenceToDisplay(Sequence);
            return;
        }

        ModifierKey = ModifierDoubleTapKey.None;
        Sequence = [];
        Kind = kind == TriggerKind.Hold ? TriggerKind.Hold : TriggerKind.Chord;
        SetHotkey(keyStr);
    }

    private void SetKeysToDisplay(HotkeyModel? hotkey)
    {
        KeysToDisplay.Clear();

        if (hotkey == null || hotkey == default(HotkeyModel) || hotkey.ToString() == Constant.EmptyHotkey)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        foreach (var key in hotkey.Value.EnumerateDisplayKeys()!)
        {
            KeysToDisplay.Add(key);
        }
    }

    private void SetModifierDoubleTapToDisplay(ModifierDoubleTapKey modifierKey)
    {
        KeysToDisplay.Clear();

        if (modifierKey == ModifierDoubleTapKey.None)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        KeysToDisplay.Add(_i18n.GetTranslation("Hotkey_DoubleTap"));
        KeysToDisplay.Add(modifierKey.ToString());
    }

    private void SetSequenceToDisplay(IReadOnlyList<string>? sequence)
    {
        KeysToDisplay.Clear();
        if (sequence == null || sequence.Count == 0)
        {
            KeysToDisplay.Add(EmptyHotkey);
            return;
        }

        for (var index = 0; index < sequence.Count; index++)
        {
            if (index > 0)
                KeysToDisplay.Add(",");

            foreach (var key in new HotkeyModel(sequence[index]).EnumerateDisplayKeys())
            {
                KeysToDisplay.Add(key);
            }
        }
    }

    private bool IsGlobalTriggerControl => Type == HotkeyType.Global;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var itemsControl = GetTemplateChild("PART_ItemsHost") as ItemsControl;
        itemsControl?.ItemsSource = KeysToDisplay;
    }
}
