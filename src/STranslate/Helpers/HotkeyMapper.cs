using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using System.Windows.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace STranslate.Helpers;

public class HotkeyMapper
{
    private static readonly ILogger<HotkeyMapper> _logger;
    private const string IncrementalTranslateTriggerId = "__incremental_translate_hold";
    private static readonly IReadOnlyDictionary<string, string> ReservedGlobalHotkeyResourceKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl + C"] = "HotkeyReservedClipboardCopy"
        };

    static HotkeyMapper()
    {
        _logger = Ioc.Default.GetRequiredService<ILogger<HotkeyMapper>>();
    }

    internal static bool SetHotkey(string hotkeyStr, Action action)
        => SetHotkey(hotkeyStr, hotkeyStr, action);

    internal static bool SetHotkey(
        string id,
        string hotkeyStr,
        Action action,
        SuppressionMode suppressionMode = SuppressionMode.SuppressOnMatch)
    {
        var hotkey = new HotkeyModel(hotkeyStr);
        if (string.IsNullOrEmpty(hotkeyStr) || hotkey.CharKey == Key.None)
        {
            GlobalInputEngine.Remove(id);
            return true;
        }

        if (IsReservedGlobalHotkey(hotkey))
        {
            _logger.LogWarning("Skipped reserved global hotkey: {HotkeyStr}", hotkeyStr);
            GlobalInputEngine.Remove(id);
            return false;
        }

        if (!hotkey.Validate(false) && hotkey.CharKey is not (Key.LWin or Key.RWin))
        {
            GlobalInputEngine.Remove(id);
            return false;
        }

        return GlobalInputEngine.AddOrReplace(new GlobalTriggerBinding
        {
            Id = id,
            Kind = TriggerKind.Chord,
            Hotkey = hotkey,
            SuppressionMode = suppressionMode,
            OnTriggered = action
        });
    }

    internal static bool SetGlobalTrigger(
        string id,
        GlobalHotkey hotkey,
        Action action,
        Action? onPressed = null,
        Action? onReleased = null)
    {
        return hotkey.Kind switch
        {
            TriggerKind.ModifierDoubleTap => RegisterModifierDoubleTap(
                id,
                hotkey.ModifierKey,
                TimeSpan.FromMilliseconds(350),
                action),
            TriggerKind.Sequence => RegisterSequence(
                id,
                hotkey.Sequence.Select(x => new HotkeyModel(x)).ToList(),
                TimeSpan.FromMilliseconds(500),
                action),
            TriggerKind.Hold => RegisterHoldKey(
                id,
                hotkey.Key,
                onPressed ?? action,
                onReleased),
            TriggerKind.Chord => SetHotkey(id, hotkey.Key, action),
            _ => RemoveGlobalTriggerAndReturnSuccess(id)
        };
    }

    internal static bool RemoveHotkey(string hotkeyStr)
    {
        try
        {
            GlobalInputEngine.Remove(hotkeyStr);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error removing hotkey: {HotkeyStr}", hotkeyStr);
            return false;
        }
    }

    internal static void RemoveGlobalTrigger(string id) => GlobalInputEngine.Remove(id);

    private static bool RemoveGlobalTriggerAndReturnSuccess(string id)
    {
        GlobalInputEngine.Remove(id);
        return true;
    }

    public static void RegisterHoldKey(Key key, Action onPress, Action onRelease)
        => RegisterHoldKey(IncrementalTranslateTriggerId, key, onPress, onRelease);

    public static bool RegisterHoldKey(string id, Key key, Action? onPress, Action? onRelease)
        => RegisterHoldKey(id, new HotkeyModel(false, false, false, false, key), onPress, onRelease);

    public static bool RegisterHoldKey(string id, string hotkeyStr, Action? onPress, Action? onRelease)
        => RegisterHoldKey(id, new HotkeyModel(hotkeyStr), onPress, onRelease);

    private static bool RegisterHoldKey(string id, HotkeyModel hotkey, Action? onPress, Action? onRelease)
    {
        if (!CheckHoldAvailability(hotkey))
        {
            GlobalInputEngine.Remove(id);
            return hotkey.CharKey == Key.None;
        }

        return GlobalInputEngine.AddOrReplace(new GlobalTriggerBinding
        {
            Id = id,
            Kind = TriggerKind.Hold,
            Hotkey = hotkey,
            SuppressionMode = SuppressionMode.SuppressWhileHolding,
            OnPressed = onPress,
            OnReleased = onRelease
        });
    }

    public static bool RegisterSequence(
        string id,
        IReadOnlyList<HotkeyModel> sequence,
        TimeSpan timeout,
        Action action,
        SuppressionMode suppressionMode = SuppressionMode.NeverSuppress)
    {
        if (sequence.Count == 0)
        {
            GlobalInputEngine.Remove(id);
            return true;
        }

        if (sequence.Any(x => !x.Validate(false)))
        {
            GlobalInputEngine.Remove(id);
            return false;
        }

        return GlobalInputEngine.AddOrReplace(new GlobalTriggerBinding
        {
            Id = id,
            Kind = TriggerKind.Sequence,
            Sequence = sequence,
            Timeout = timeout,
            SuppressionMode = suppressionMode,
            OnTriggered = action
        });
    }

    public static bool RegisterModifierDoubleTap(
        string id,
        ModifierDoubleTapKey modifierKey,
        TimeSpan timeout,
        Action action,
        SuppressionMode suppressionMode = SuppressionMode.NeverSuppress)
    {
        if (modifierKey is ModifierDoubleTapKey.None or ModifierDoubleTapKey.Win)
        {
            GlobalInputEngine.Remove(id);
            return modifierKey == ModifierDoubleTapKey.None;
        }

        return GlobalInputEngine.AddOrReplace(new GlobalTriggerBinding
        {
            Id = id,
            Kind = TriggerKind.ModifierDoubleTap,
            ModifierKey = modifierKey,
            Timeout = timeout,
            SuppressionMode = suppressionMode,
            OnTriggered = action
        });
    }

    public static void StartGlobalKeyboardMonitoring()
    {
        // 全局键盘监听由 GlobalInputEngine 根据已注册规则自动启停。
    }

    public static void StopGlobalKeyboardMonitoring() => GlobalInputEngine.Remove(IncrementalTranslateTriggerId);

    internal static bool CheckAvailability(HotkeyModel currentHotkey)
        => !IsReservedGlobalHotkey(currentHotkey);

    internal static bool CheckHoldAvailability(HotkeyModel hotkey)
    {
        return hotkey.CharKey is not (Key.None or
                                      Key.LeftAlt or Key.RightAlt or
                                      Key.LeftCtrl or Key.RightCtrl or
                                      Key.LeftShift or Key.RightShift or
                                      Key.LWin or Key.RWin) &&
               hotkey.ModifierKeys == ModifierKeys.None;
    }

    internal static SpecialKeyState CheckModifiers()
    {
        SpecialKeyState state = new SpecialKeyState();
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0)
        {
            state.ShiftPressed = true;
        }
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0)
        {
            state.CtrlPressed = true;
        }
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_MENU) & 0x8000) != 0)
        {
            state.AltPressed = true;
        }
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LWIN) & 0x8000) != 0 ||
            (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RWIN) & 0x8000) != 0)
        {
            state.WinPressed = true;
        }

        return state;
    }

    internal static bool IsReservedGlobalHotkey(HotkeyModel hotkey)
        => TryGetReservedGlobalHotkeyMessageKey(hotkey, out _);

    internal static bool TryGetReservedGlobalHotkeyMessageKey(HotkeyModel hotkey, out string resourceKey)
    {
        if (ReservedGlobalHotkeyResourceKeys.TryGetValue(hotkey.ToString(), out var foundResourceKey))
        {
            resourceKey = foundResourceKey;
            return true;
        }

        resourceKey = string.Empty;
        return false;
    }
}
