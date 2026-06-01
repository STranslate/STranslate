using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Helpers;

public static class GlobalInputEngine
{
    private sealed class SequenceState
    {
        public int Index { get; set; }
        public DateTimeOffset Deadline { get; set; }
    }

    private static readonly ILogger<HotkeyMapper> _logger = Ioc.Default.GetRequiredService<ILogger<HotkeyMapper>>();
    private static readonly Lock _stateLock = new();
    private static readonly Dictionary<string, GlobalTriggerBinding> _bindings = [];
    private static readonly Dictionary<string, SequenceState> _sequenceStates = [];
    private static readonly HashSet<Key> _pressedKeys = [];
    private static readonly HashSet<string> _activeHoldBindings = [];
    private static readonly Dictionary<ModifierDoubleTapKey, DateTimeOffset> _lastModifierTapAt = [];
    private static UnhookWindowsHookExSafeHandle? _hookHandle;
    private static HOOKPROC? _hookProc;

    public static bool AddOrReplace(GlobalTriggerBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.Id))
            return false;

        if (!Validate(binding))
            return false;

        var added = false;
        lock (_stateLock)
        {
            if (HasConflictCore(binding, binding.Id))
            {
                RemoveBindingCore(binding.Id);
            }
            else
            {
                _bindings[binding.Id] = binding;
                _sequenceStates.Remove(binding.Id);
                added = true;
            }
        }

        if (!added)
        {
            StopHookIfUnused();
            return false;
        }

        EnsureHook();
        return true;
    }

    public static void Remove(string id)
    {
        lock (_stateLock)
        {
            RemoveBindingCore(id);
        }

        StopHookIfUnused();
    }

    public static void Clear()
    {
        lock (_stateLock)
        {
            _bindings.Clear();
            _sequenceStates.Clear();
            _activeHoldBindings.Clear();
            ClearPressedKeysCore();
        }

        StopHook();
    }

    public static bool HasConflict(GlobalTriggerBinding binding, string? excludingId = null)
    {
        lock (_stateLock)
        {
            return HasConflictCore(binding, excludingId);
        }
    }

    public static bool HasBindings
    {
        get
        {
            lock (_stateLock)
            {
                return _bindings.Count > 0;
            }
        }
    }

    private static bool Validate(GlobalTriggerBinding binding)
    {
        return binding.Kind switch
        {
            TriggerKind.Chord => binding.Hotkey.CharKey != Key.None,
            TriggerKind.Sequence => binding.Sequence.Count > 0 && binding.Sequence.All(x => x.CharKey != Key.None),
            TriggerKind.ModifierDoubleTap => binding.ModifierKey != ModifierDoubleTapKey.None,
            TriggerKind.Hold => binding.Hotkey.CharKey != Key.None,
            _ => false
        };
    }

    private static bool HasConflictCore(GlobalTriggerBinding binding, string? excludingId)
    {
        foreach (var existing in _bindings.Values)
        {
            if (existing.Id == excludingId)
                continue;

            if (Conflicts(existing, binding))
                return true;
        }

        return false;
    }

    private static bool Conflicts(GlobalTriggerBinding left, GlobalTriggerBinding right)
    {
        if (left.Kind == TriggerKind.Chord && right.Kind == TriggerKind.Chord)
            return left.Hotkey.Equals(right.Hotkey);

        if (left.Kind == TriggerKind.Hold || right.Kind == TriggerKind.Hold)
            return left.PrimaryKey == right.PrimaryKey;

        if (left.Kind == TriggerKind.ModifierDoubleTap && right.Kind == TriggerKind.ModifierDoubleTap)
            return left.ModifierKey == right.ModifierKey;

        if (left.Kind == TriggerKind.Sequence && right.Kind == TriggerKind.Sequence)
            return left.Sequence.SequenceEqual(right.Sequence);

        return false;
    }

    private static void EnsureHook()
    {
        if (_hookHandle != null && !_hookHandle.IsInvalid)
            return;

        try
        {
            _hookProc = HookCallback;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            var hModule = PInvoke.GetModuleHandle(curModule?.ModuleName);

            _hookHandle = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
                _hookProc,
                hModule,
                0);

            if (_hookHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("Failed to set keyboard hook. Error code: {Error}", error);
                _hookHandle = null;
                _hookProc = null;
                return;
            }

            _logger.LogInformation("Global input engine started");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to start global input engine");
            _hookHandle?.Dispose();
            _hookHandle = null;
            _hookProc = null;
        }
    }

    private static void StopHookIfUnused()
    {
        if (HasBindings)
            return;

        StopHook();
    }

    private static void StopHook()
    {
        try
        {
            _hookHandle?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to stop global input engine");
        }
        finally
        {
            _hookHandle = null;
            _hookProc = null;
        }
    }

    private static LRESULT HookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode < 0)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var kbdStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((((uint)kbdStruct.flags) & 0x10) != 0)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var key = KeyInterop.KeyFromVirtualKey((int)kbdStruct.vkCode);
        var message = (uint)wParam;
        var isKeyDown = message == PInvoke.WM_KEYDOWN || message == PInvoke.WM_SYSKEYDOWN;
        var isKeyUp = message == PInvoke.WM_KEYUP || message == PInvoke.WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var shouldSkip = GlobalTriggerGuard.ShouldSkipGlobalTrigger();
        var shouldSuppress = false;
        var actions = new List<Action>();

        lock (_stateLock)
        {
            if (isKeyDown)
                shouldSuppress = HandleKeyDown(key, shouldSkip, actions);
            else
                shouldSuppress = HandleKeyUp(key, shouldSkip, actions);
        }

        Dispatch(actions);

        return shouldSuppress
            ? new LRESULT(1)
            : PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private static bool HandleKeyDown(Key key, bool shouldSkip, List<Action> actions)
    {
        var isRepeated = !_pressedKeys.Add(key);
        if (isRepeated)
            return !shouldSkip && ShouldSuppressRepeatedKey(key);

        if (shouldSkip)
        {
            ResetSequenceStates();
            return false;
        }

        var shouldSuppress = false;
        var bindings = _bindings.Values.ToList();
        foreach (var binding in bindings)
        {
            switch (binding.Kind)
            {
                case TriggerKind.Hold:
                    if (binding.Hotkey.CharKey == key)
                    {
                        _activeHoldBindings.Add(binding.Id);
                        QueueAction(binding.OnPressed, actions);
                        shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressWhileHolding;
                    }
                    break;
                case TriggerKind.Chord:
                    if (MatchesChord(binding.Hotkey, key))
                    {
                        QueueAction(binding.OnTriggered, actions);
                        shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressOnMatch;
                    }
                    break;
                case TriggerKind.Sequence:
                    if (HandleSequence(binding, key, actions))
                        shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressOnMatch;
                    break;
            }
        }

        return shouldSuppress;
    }

    private static bool HandleKeyUp(Key key, bool shouldSkip, List<Action> actions)
    {
        _pressedKeys.Remove(key);

        var shouldSuppress = false;
        var bindings = _bindings.Values.ToList();

        foreach (var binding in bindings)
        {
            if (binding.Kind == TriggerKind.Hold &&
                binding.Hotkey.CharKey == key &&
                _activeHoldBindings.Remove(binding.Id))
            {
                QueueAction(binding.OnReleased, actions);
                shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressWhileHolding;
            }
        }

        if (!shouldSkip)
        {
            foreach (var binding in bindings.Where(x => x.Kind == TriggerKind.ModifierDoubleTap))
            {
                if (MatchesModifier(binding.ModifierKey, key) &&
                    HandleModifierDoubleTap(binding, actions))
                {
                    shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressOnMatch;
                }
            }
        }

        return shouldSuppress;
    }

    private static bool HandleSequence(GlobalTriggerBinding binding, Key key, List<Action> actions)
    {
        if (IsModifierKey(key))
            return false;

        var now = DateTimeOffset.UtcNow;
        if (!_sequenceStates.TryGetValue(binding.Id, out var state) || now > state.Deadline)
        {
            state = new SequenceState();
            _sequenceStates[binding.Id] = state;
        }

        var expected = binding.Sequence[state.Index];
        if (MatchesChord(expected, key))
        {
            state.Index++;
            state.Deadline = now + binding.Timeout;

            if (state.Index >= binding.Sequence.Count)
            {
                state.Index = 0;
                state.Deadline = DateTimeOffset.MinValue;
                QueueAction(binding.OnTriggered, actions);
                return true;
            }

            return false;
        }

        state.Index = 0;
        state.Deadline = DateTimeOffset.MinValue;

        if (MatchesChord(binding.Sequence[0], key))
        {
            state.Index = 1;
            state.Deadline = now + binding.Timeout;
        }

        return false;
    }

    private static bool HandleModifierDoubleTap(GlobalTriggerBinding binding, List<Action> actions)
    {
        var now = DateTimeOffset.UtcNow;
        _lastModifierTapAt.TryGetValue(binding.ModifierKey, out var lastTapAt);
        _lastModifierTapAt[binding.ModifierKey] = now;

        if (lastTapAt == DateTimeOffset.MinValue || now - lastTapAt > binding.Timeout)
            return false;

        _lastModifierTapAt[binding.ModifierKey] = DateTimeOffset.MinValue;
        QueueAction(binding.OnTriggered, actions);
        return true;
    }

    private static bool MatchesChord(HotkeyModel hotkey, Key eventKey)
    {
        if (hotkey.CharKey != eventKey)
            return false;

        var modifiers = ReadModifiers();
        if (IsModifierKey(hotkey.CharKey))
        {
            return !HasAdditionalModifier(hotkey.CharKey, modifiers);
        }

        return hotkey.Ctrl == modifiers.Ctrl &&
               hotkey.Alt == modifiers.Alt &&
               hotkey.Shift == modifiers.Shift &&
               hotkey.Win == modifiers.Win;
    }

    private static bool ShouldSuppressRepeatedKey(Key key)
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Kind == TriggerKind.Hold &&
                binding.Hotkey.CharKey == key &&
                _activeHoldBindings.Contains(binding.Id) &&
                binding.SuppressionMode == SuppressionMode.SuppressWhileHolding)
            {
                return true;
            }
        }

        return false;
    }

    private static void ResetSequenceStates()
    {
        foreach (var state in _sequenceStates.Values)
        {
            state.Index = 0;
            state.Deadline = DateTimeOffset.MinValue;
        }
    }

    private static void QueueAction(Action? action, List<Action> actions)
    {
        if (action != null)
            actions.Add(action);
    }

    private static void Dispatch(List<Action> actions)
    {
        if (actions.Count == 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        foreach (var action in actions)
        {
            if (dispatcher == null)
            {
                Task.Run(action);
                continue;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error executing global trigger action");
                }
            }));
        }
    }

    private static (bool Ctrl, bool Alt, bool Shift, bool Win) ReadModifiers()
    {
        return (
            IsVirtualKeyDown(VIRTUAL_KEY.VK_CONTROL) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_LCONTROL) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_RCONTROL),
            IsVirtualKeyDown(VIRTUAL_KEY.VK_MENU) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_LMENU) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_RMENU),
            IsVirtualKeyDown(VIRTUAL_KEY.VK_SHIFT) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_LSHIFT) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_RSHIFT),
            IsVirtualKeyDown(VIRTUAL_KEY.VK_LWIN) ||
            IsVirtualKeyDown(VIRTUAL_KEY.VK_RWIN));
    }

    private static bool IsVirtualKeyDown(VIRTUAL_KEY key)
        => (PInvoke.GetKeyState((int)key) & 0x8000) != 0;

    private static bool HasAdditionalModifier(Key primaryKey, (bool Ctrl, bool Alt, bool Shift, bool Win) modifiers)
    {
        return primaryKey switch
        {
            Key.LeftCtrl or Key.RightCtrl => modifiers.Alt || modifiers.Shift || modifiers.Win,
            Key.LeftAlt or Key.RightAlt => modifiers.Ctrl || modifiers.Shift || modifiers.Win,
            Key.LeftShift or Key.RightShift => modifiers.Ctrl || modifiers.Alt || modifiers.Win,
            Key.LWin or Key.RWin => modifiers.Ctrl || modifiers.Alt || modifiers.Shift,
            _ => modifiers.Ctrl || modifiers.Alt || modifiers.Shift || modifiers.Win
        };
    }

    private static bool MatchesModifier(ModifierDoubleTapKey modifier, Key key) => modifier switch
    {
        ModifierDoubleTapKey.Ctrl => key is Key.LeftCtrl or Key.RightCtrl,
        ModifierDoubleTapKey.Alt => key is Key.LeftAlt or Key.RightAlt,
        ModifierDoubleTapKey.Shift => key is Key.LeftShift or Key.RightShift,
        ModifierDoubleTapKey.Win => key is Key.LWin or Key.RWin,
        _ => false
    };

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static void ClearPressedKeysCore() => _pressedKeys.Clear();

    private static void RemoveBindingCore(string id)
    {
        _bindings.Remove(id);
        _sequenceStates.Remove(id);
        _activeHoldBindings.Remove(id);

        if (_bindings.Count == 0)
            ClearPressedKeysCore();
    }
}
