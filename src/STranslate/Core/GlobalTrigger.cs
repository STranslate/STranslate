using System.Windows.Input;

namespace STranslate.Core;

public enum TriggerKind
{
    Chord,
    Sequence,
    ModifierDoubleTap,
    Hold
}

public enum SuppressionMode
{
    NeverSuppress,
    SuppressOnMatch,
    SuppressWhileHolding
}

public enum ModifierDoubleTapKey
{
    None,
    Ctrl,
    Alt,
    Shift,
    Win
}

public sealed class GlobalTriggerBinding
{
    public string Id { get; init; } = string.Empty;

    public TriggerKind Kind { get; init; } = TriggerKind.Chord;

    public HotkeyModel Hotkey { get; init; }

    public IReadOnlyList<HotkeyModel> Sequence { get; init; } = [];

    public ModifierDoubleTapKey ModifierKey { get; init; } = ModifierDoubleTapKey.None;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(500);

    public SuppressionMode SuppressionMode { get; init; } = SuppressionMode.SuppressOnMatch;

    public Action? OnTriggered { get; init; }

    public Action? OnPressed { get; init; }

    public Action? OnReleased { get; init; }

    public Key PrimaryKey => Kind switch
    {
        TriggerKind.Sequence => Sequence.Count > 0 ? Sequence[0].CharKey : Key.None,
        TriggerKind.ModifierDoubleTap => ModifierKey.ToRepresentativeKey(),
        _ => Hotkey.CharKey
    };
}

public static class ModifierDoubleTapKeyExtensions
{
    public static bool TryGetModifierDoubleTapKey(this Key key, out ModifierDoubleTapKey modifierKey)
    {
        modifierKey = key switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierDoubleTapKey.Ctrl,
            Key.LeftAlt or Key.RightAlt => ModifierDoubleTapKey.Alt,
            Key.LeftShift or Key.RightShift => ModifierDoubleTapKey.Shift,
            Key.LWin or Key.RWin => ModifierDoubleTapKey.Win,
            _ => ModifierDoubleTapKey.None
        };

        return modifierKey != ModifierDoubleTapKey.None;
    }

    public static bool IsModifierDoubleTapKey(this Key key)
        => key.TryGetModifierDoubleTapKey(out _);

    public static Key ToRepresentativeKey(this ModifierDoubleTapKey key) => key switch
    {
        ModifierDoubleTapKey.Ctrl => Key.LeftCtrl,
        ModifierDoubleTapKey.Alt => Key.LeftAlt,
        ModifierDoubleTapKey.Shift => Key.LeftShift,
        ModifierDoubleTapKey.Win => Key.LWin,
        _ => Key.None
    };
}
