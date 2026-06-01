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

    private sealed class HoldState
    {
        public required GlobalTriggerBinding Binding { get; init; }
        public int ReleaseVersion { get; set; }
        public bool IsPendingRelease { get; set; }
    }

    private sealed class ModifierTapState
    {
        public required ModifierDoubleTapKey ModifierKey { get; init; }
        public bool IsCleanTap { get; set; }
    }

    private static readonly ILogger<HotkeyMapper> _logger = Ioc.Default.GetRequiredService<ILogger<HotkeyMapper>>();
    private static readonly Lock _stateLock = new();
    private static readonly TimeSpan HoldReleaseConfirmationDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan HoldPhysicalStateCheckDelay = TimeSpan.FromMilliseconds(20);
    private static readonly Dictionary<string, GlobalTriggerBinding> _bindings = [];
    private static readonly Dictionary<string, SequenceState> _sequenceStates = [];
    private static readonly HashSet<Key> _pressedKeys = [];
    private static readonly HashSet<Key> _suppressedChordKeys = [];
    private static readonly Dictionary<string, HoldState> _activeHoldStates = [];
    private static readonly Dictionary<Key, ModifierTapState> _activeModifierTapStates = [];
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
                if (_bindings.TryGetValue(binding.Id, out var oldBinding))
                    ClearModifierDoubleTapStateCore(oldBinding);

                _activeHoldStates.Remove(binding.Id);
                _bindings[binding.Id] = binding;
                _sequenceStates.Remove(binding.Id);
                ClearModifierDoubleTapStateCore(binding);
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
            _activeHoldStates.Clear();
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

        if (left.Kind == TriggerKind.Hold && right.Kind == TriggerKind.Hold)
            return left.PrimaryKey == right.PrimaryKey;

        if (left.Kind == TriggerKind.Hold && right.Kind == TriggerKind.Chord)
            return left.PrimaryKey == right.PrimaryKey && HasNoModifiers(right.Hotkey);

        if (left.Kind == TriggerKind.Chord && right.Kind == TriggerKind.Hold)
            return left.PrimaryKey == right.PrimaryKey && HasNoModifiers(left.Hotkey);

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
            return _suppressedChordKeys.Contains(key) || ShouldSuppressRepeatedKey(key);

        if (shouldSkip)
        {
            ResetSequenceStates();
            ResetModifierTapStates();
            return false;
        }

        TrackModifierTapKeyDown(key);

        var shouldSuppress = false;
        var bindings = _bindings.Values.ToList();
        foreach (var binding in bindings)
        {
            switch (binding.Kind)
            {
                case TriggerKind.Hold:
                    if (MatchesHold(binding, key, actions))
                        shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressWhileHolding;
                    break;
                case TriggerKind.Chord:
                    if (MatchesChord(binding.Hotkey, key))
                    {
                        QueueAction(binding.OnTriggered, actions);
                        if (binding.SuppressionMode == SuppressionMode.SuppressOnMatch)
                        {
                            _suppressedChordKeys.Add(key);
                            shouldSuppress = true;
                        }
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
        var wasPressed = _pressedKeys.Remove(key);

        var shouldSuppress = _suppressedChordKeys.Remove(key);
        var bindings = _bindings.Values.ToList();

        foreach (var binding in bindings)
        {
            if (binding.Kind == TriggerKind.Hold &&
                MatchesHoldKey(binding, key) &&
                StartHoldReleaseConfirmation(binding))
            {
                shouldSuppress |= binding.SuppressionMode == SuppressionMode.SuppressWhileHolding;
            }
        }

        if (!shouldSkip &&
            wasPressed &&
            TryGetCleanModifierTap(key, out var modifierKey))
        {
            var modifierBindings = bindings
                .Where(x => x.Kind == TriggerKind.ModifierDoubleTap && x.ModifierKey == modifierKey)
                .ToList();

            if (modifierBindings.Count > 0 &&
                HandleModifierDoubleTap(modifierKey, modifierBindings[0].Timeout))
            {
                foreach (var binding in modifierBindings)
                {
                    QueueAction(binding.OnTriggered, actions);
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

    private static bool HandleModifierDoubleTap(ModifierDoubleTapKey modifierKey, TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        _lastModifierTapAt.TryGetValue(modifierKey, out var lastTapAt);
        _lastModifierTapAt[modifierKey] = now;

        if (lastTapAt == DateTimeOffset.MinValue || now - lastTapAt > timeout)
            return false;

        _lastModifierTapAt[modifierKey] = DateTimeOffset.MinValue;
        return true;
    }

    private static void TrackModifierTapKeyDown(Key key)
    {
        if (!TryGetModifierDoubleTapKey(key, out var modifierKey))
        {
            MarkActiveModifierTapsDirty();
            _lastModifierTapAt.Clear();
            return;
        }

        var hasOtherPressedKey = _pressedKeys.Any(x => x != key);
        if (hasOtherPressedKey)
        {
            MarkActiveModifierTapsDirty();
            _lastModifierTapAt.Clear();
        }

        _activeModifierTapStates[key] = new ModifierTapState
        {
            ModifierKey = modifierKey,
            IsCleanTap = !hasOtherPressedKey
        };
    }

    private static bool TryGetCleanModifierTap(Key key, out ModifierDoubleTapKey modifierKey)
    {
        modifierKey = ModifierDoubleTapKey.None;
        if (!_activeModifierTapStates.Remove(key, out var state))
            return false;

        if (!state.IsCleanTap)
        {
            _lastModifierTapAt.Remove(state.ModifierKey);
            return false;
        }

        modifierKey = state.ModifierKey;
        return true;
    }

    private static void MarkActiveModifierTapsDirty()
    {
        foreach (var state in _activeModifierTapStates.Values)
        {
            state.IsCleanTap = false;
        }
    }

    private static void ResetModifierTapStates()
    {
        _activeModifierTapStates.Clear();
        _lastModifierTapAt.Clear();
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

    private static bool MatchesHold(GlobalTriggerBinding binding, Key eventKey, List<Action> actions)
    {
        if (!MatchesHoldKey(binding, eventKey))
            return false;

        var modifiers = ReadModifiers();
        if (binding.Hotkey.Ctrl != modifiers.Ctrl ||
            binding.Hotkey.Alt != modifiers.Alt ||
            binding.Hotkey.Shift != modifiers.Shift ||
            binding.Hotkey.Win != modifiers.Win)
        {
            return false;
        }

        if (_activeHoldStates.TryGetValue(binding.Id, out var state))
        {
            state.IsPendingRelease = false;
            state.ReleaseVersion++;
            return true;
        }

        _activeHoldStates[binding.Id] = new HoldState
        {
            Binding = binding
        };
        QueueAction(binding.OnPressed, actions);
        return true;
    }

    private static bool MatchesHoldKey(GlobalTriggerBinding binding, Key eventKey)
        => binding.Hotkey.CharKey == eventKey;

    private static bool ShouldSuppressRepeatedKey(Key key)
    {
        foreach (var state in _activeHoldStates.Values)
        {
            var binding = state.Binding;
            if (binding.Kind == TriggerKind.Hold &&
                MatchesHoldKey(binding, key) &&
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

    private static bool StartHoldReleaseConfirmation(GlobalTriggerBinding binding)
    {
        if (!_activeHoldStates.TryGetValue(binding.Id, out var state))
            return false;

        state.IsPendingRelease = true;
        var releaseVersion = ++state.ReleaseVersion;

        _ = ConfirmHoldReleaseAsync(binding.Id, releaseVersion);
        return true;
    }

    private static async Task ConfirmHoldReleaseAsync(string id, int releaseVersion)
    {
        try
        {
            await Task.Delay(HoldReleaseConfirmationDelay);
            await Task.Delay(HoldPhysicalStateCheckDelay);

            var actions = new List<Action>();
            lock (_stateLock)
            {
                if (!_activeHoldStates.TryGetValue(id, out var state) ||
                    !state.IsPendingRelease ||
                    state.ReleaseVersion != releaseVersion)
                {
                    return;
                }

                if (IsHoldPhysicallyDown(state.Binding))
                {
                    state.IsPendingRelease = false;
                    state.ReleaseVersion++;
                    return;
                }

                _activeHoldStates.Remove(id);
                QueueAction(state.Binding.OnReleased, actions);
            }

            Dispatch(actions);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error confirming hold trigger release");
        }
    }

    private static bool IsHoldPhysicallyDown(GlobalTriggerBinding binding)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(binding.Hotkey.CharKey);
        return virtualKey != 0 && (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
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

    private static bool HasNoModifiers(HotkeyModel hotkey)
        => !hotkey.Ctrl && !hotkey.Alt && !hotkey.Shift && !hotkey.Win;

    private static bool TryGetModifierDoubleTapKey(Key key, out ModifierDoubleTapKey modifierKey)
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

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static void ClearPressedKeysCore()
    {
        _pressedKeys.Clear();
        _suppressedChordKeys.Clear();
        ResetModifierTapStates();
    }

    private static void RemoveBindingCore(string id)
    {
        if (_bindings.Remove(id, out var removedBinding))
            ClearModifierDoubleTapStateCore(removedBinding);

        _sequenceStates.Remove(id);
        _activeHoldStates.Remove(id);

        if (_bindings.Count == 0)
            ClearPressedKeysCore();
    }

    private static void ClearModifierDoubleTapStateCore(GlobalTriggerBinding binding)
    {
        if (binding.Kind == TriggerKind.ModifierDoubleTap)
            _lastModifierTapAt.Remove(binding.ModifierKey);
    }
}
