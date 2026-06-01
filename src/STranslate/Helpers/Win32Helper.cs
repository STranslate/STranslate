using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Helpers;

public static class Win32Helper
{
    #region Task Switching

    /// <summary>
    /// Hide windows in the Alt+Tab window list
    /// </summary>
    /// <param name="window">To hide a window</param>
    public static void HideFromAltTab(Window window)
    {
        var hwnd = GetWindowHandle(window);

        var exStyle = GetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        // Add TOOLWINDOW style, remove APPWINDOW style
        var newExStyle = ((uint)exStyle | (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) & ~(uint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;

        SetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)newExStyle);
    }

    /// <summary>
    /// Restore window display in the Alt+Tab window list.
    /// </summary>
    /// <param name="window">To restore the displayed window</param>
    public static void ShowInAltTab(Window window)
    {
        var hwnd = GetWindowHandle(window);

        var exStyle = GetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        // Remove the TOOLWINDOW style and add the APPWINDOW style.
        var newExStyle = ((uint)exStyle & ~(uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) | (uint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;

        SetWindowStyle(GetWindowHandle(window), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)newExStyle);
    }

    /// <summary>
    /// Disable windows toolbar's control box
    /// This will also disable system menu with Alt+Space hotkey
    /// </summary>
    public static void DisableControlBox(Window window)
    {
        var hwnd = GetWindowHandle(window);

        var style = GetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);

        style &= ~(int)WINDOW_STYLE.WS_SYSMENU;

        SetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);
    }

    private static nint GetWindowStyle(HWND hWnd, WINDOW_LONG_PTR_INDEX nIndex)
    {
        var style = PInvoke.GetWindowLongPtr(hWnd, nIndex);
        return style == 0 && Marshal.GetLastPInvokeError() != 0 ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : style;
    }

    private static nint SetWindowStyle(HWND hWnd, WINDOW_LONG_PTR_INDEX nIndex, nint dwNewLong)
    {
        PInvoke.SetLastError(WIN32_ERROR.NO_ERROR); // Clear any existing error

        var result = PInvoke.SetWindowLongPtr(hWnd, nIndex, dwNewLong);
        return result == 0 && Marshal.GetLastPInvokeError() != 0 ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : result;
    }

    #endregion

    #region Window Foreground

    public static unsafe nint GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public static bool SetForegroundWindow(Window window) => SetForegroundWindow(GetWindowHandle(window));

    public static bool SetForegroundWindow(nint handle) => SetForegroundWindow(new HWND(handle));

    internal static bool SetForegroundWindow(HWND handle) => PInvoke.SetForegroundWindow(handle);

    public static bool ForceSetForegroundWindow(Window window) => ForceSetForegroundWindow(GetWindowHandle(window));

    public static bool ForceSetForegroundWindow(nint handle) => ForceSetForegroundWindow(new HWND(handle));

    /// <summary>
    /// 外部调用从后台唤起窗口时使用 AttachThreadInput 绕过 Foreground Lockout。
    /// </summary>
    internal static bool ForceSetForegroundWindow(HWND handle)
    {
        var foregroundWnd = PInvoke.GetForegroundWindow();
        if (handle == foregroundWnd) return true;

        var currentThreadId = PInvoke.GetCurrentThreadId();
        var foregroundThreadId = PInvoke.GetWindowThreadProcessId(foregroundWnd, out _);

        // 如果前台窗口属于不同的线程，尝试挂接输入
        bool needDetach = false;
        if (foregroundThreadId != currentThreadId && foregroundThreadId != 0)
        {
            // 挂接当前线程到前台窗口线程
            needDetach = PInvoke.AttachThreadInput(foregroundThreadId, currentThreadId, true);
        }

        // 尝试设置前台窗口
        // 注意：在挂接状态下，这通常会成功
        var result = PInvoke.SetForegroundWindow(handle);

        // 尝试恢复窗口（如果是最小化）
        if (PInvoke.IsIconic(handle))
        {
            PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        if (needDetach)
        {
            // 解除挂接
            PInvoke.AttachThreadInput(foregroundThreadId, currentThreadId, false);
        }

        // 再次尝试 BringWindowToTop 作为兜底
        if (!result)
        {
            PInvoke.BringWindowToTop(handle);
        }

        return result || PInvoke.GetForegroundWindow() == handle;
    }

    public static bool IsForegroundWindow(Window window) => IsForegroundWindow(GetWindowHandle(window));

    public static bool IsForegroundWindow(nint handle) => IsForegroundWindow(new HWND(handle));

    internal static bool IsForegroundWindow(HWND handle) => handle.Equals(PInvoke.GetForegroundWindow());

    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = PInvoke.GetForegroundWindow();
            if (hwnd == HWND.Null)
                return null;

            PInvoke.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
                return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Window Handle

    internal static HWND GetWindowHandle(Window window, bool ensure = false)
    {
        var windowHelper = new WindowInteropHelper(window);
        if (ensure)
        {
            windowHelper.EnsureHandle();
        }
        return new(windowHelper.Handle);
    }

    internal static HWND GetMainWindowHandle()
    {
        // When application is exiting, the Application.Current will be null
        if (Application.Current == null) return HWND.Null;

        // Get the FL main window
        var hwnd = GetWindowHandle(Application.Current.MainWindow, true);
        return hwnd;
    }

    #endregion

    #region Window WndProc

    internal static HwndSource? AddWndProcHook(Window window, HwndSourceHook hook)
    {
        var hwnd = GetWindowHandle(window, true);
        var hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(hook);
        return hwndSource;
    }
    private static uint? _taskbarCreatedMessage;
    internal static uint TaskbarCreatedMessage =>
        _taskbarCreatedMessage ??= PInvoke.RegisterWindowMessage("TaskbarCreated");

    #endregion

    #region Window Fullscreen

    /* Flow-Launcher
     * https://github.com/Flow-Launcher/Flow.Launcher
     */

    private const string WINDOW_CLASS_CONSOLE = "ConsoleWindowClass";
    private const string WINDOW_CLASS_WINTAB = "Flip3D";
    private const string WINDOW_CLASS_PROGMAN = "Progman";
    private const string WINDOW_CLASS_WORKERW = "WorkerW";

    private static HWND _hwnd_shell;
    private static HWND HWND_SHELL =>
        _hwnd_shell != HWND.Null ? _hwnd_shell : _hwnd_shell = PInvoke.GetShellWindow();

    private static HWND _hwnd_desktop;
    private static HWND HWND_DESKTOP =>
        _hwnd_desktop != HWND.Null ? _hwnd_desktop : _hwnd_desktop = PInvoke.GetDesktopWindow();

    public static unsafe bool IsForegroundWindowFullscreen()
    {
        // Get current active window
        var hWnd = PInvoke.GetForegroundWindow();
        if (hWnd.Equals(HWND.Null))
        {
            return false;
        }

        // If current active window is desktop or shell, exit early
        if (hWnd.Equals(HWND_DESKTOP) || hWnd.Equals(HWND_SHELL))
        {
            return false;
        }

        string windowClass;
        const int capacity = 256;
        Span<char> buffer = stackalloc char[capacity];
        int validLength;
        fixed (char* pBuffer = buffer)
        {
            validLength = PInvoke.GetClassName(hWnd, pBuffer, capacity);
        }

        windowClass = buffer[..validLength].ToString();

        // For Win+Tab (Flip3D)
        if (windowClass == WINDOW_CLASS_WINTAB)
        {
            return false;
        }

        PInvoke.GetWindowRect(hWnd, out var appBounds);

        // For console (ConsoleWindowClass), we have to check for negative dimensions
        if (windowClass == WINDOW_CLASS_CONSOLE)
        {
            return appBounds.top < 0 && appBounds.bottom < 0;
        }

        // For desktop (Progman or WorkerW, depends on the system), we have to check
        if (windowClass is WINDOW_CLASS_PROGMAN or WINDOW_CLASS_WORKERW)
        {
            var hWndDesktop = PInvoke.FindWindowEx(hWnd, HWND.Null, "SHELLDLL_DefView", null);
            hWndDesktop = PInvoke.FindWindowEx(hWndDesktop, HWND.Null, "SysListView32", "FolderView");
            if (hWndDesktop != HWND.Null)
            {
                return false;
            }
        }

        var monitorInfo = MonitorInfo.GetNearestDisplayMonitor(hWnd);
        return (appBounds.bottom - appBounds.top) == monitorInfo.Bounds.Height &&
               (appBounds.right - appBounds.left) == monitorInfo.Bounds.Width;
    }

    #endregion

    #region Pixel to DIP

    /// <summary>
    /// Transforms pixels to Device Independent Pixels used by WPF
    /// </summary>
    /// <param name="visual">current window, required to get presentation source</param>
    /// <param name="unitX">horizontal position in pixels</param>
    /// <param name="unitY">vertical position in pixels</param>
    /// <returns>point containing device independent pixels</returns>
    public static Point TransformPixelsToDIP(Visual visual, double unitX, double unitY)
    {
        Matrix matrix;
        var source = PresentationSource.FromVisual(visual);
        if (source is not null)
        {
            matrix = source.CompositionTarget.TransformFromDevice;
        }
        else
        {
            using var src = new HwndSource(new HwndSourceParameters());
            matrix = src.CompositionTarget.TransformFromDevice;
        }

        return new Point((int)(matrix.M11 * unitX), (int)(matrix.M22 * unitY));
    }

    #endregion

    #region Notification

    /// <summary>
    /// Notifications only supported on Windows 10 19041+(official is 17763+)
    /// <see href="https://learn.microsoft.com/zh-cn/windows/apps/develop/notifications/app-notifications/send-local-toast?tabs=desktop#step-1-install-nuget-package"/>
    /// </summary>
    /// <returns></returns>
    public static bool IsNotificationSupported() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            Environment.OSVersion.Version.Build >= 19041;

    #endregion

    #region System Font

    private static readonly Dictionary<string, string> _languageToNotoSans = new()
    {
        { "ko", "Noto Sans KR" },
        { "ja", "Noto Sans JP" },
        { "zh-CN", "Noto Sans SC" },
        { "zh-SG", "Noto Sans SC" },
        { "zh-Hans", "Noto Sans SC" },
        { "zh-TW", "Noto Sans TC" },
        { "zh-HK", "Noto Sans TC" },
        { "zh-MO", "Noto Sans TC" },
        { "zh-Hant", "Noto Sans TC" },
        { "th", "Noto Sans Thai" },
        { "ar", "Noto Sans Arabic" },
        { "he", "Noto Sans Hebrew" },
        { "hi", "Noto Sans Devanagari" },
        { "bn", "Noto Sans Bengali" },
        { "ta", "Noto Sans Tamil" },
        { "el", "Noto Sans Greek" },
        { "ru", "Noto Sans" },
        { "en", "Noto Sans" },
        { "fr", "Noto Sans" },
        { "de", "Noto Sans" },
        { "es", "Noto Sans" },
        { "pt", "Noto Sans" }
    };

    /// <summary>
    /// Gets the system default font.
    /// </summary>
    /// <param name="useNoto">
    /// If true, it will try to find the Noto font for the current culture.
    /// </param>
    /// <returns>
    /// The name of the system default font.
    /// </returns>
    public static string GetSystemDefaultFont(bool useNoto = true)
    {
        try
        {
            if (useNoto)
            {
                var culture = CultureInfo.CurrentCulture;
                var language = culture.Name; // e.g., "zh-TW"
                var langPrefix = language.Split('-')[0]; // e.g., "zh"

                // First, try to find by full name, and if not found, fallback to prefix
                if (TryGetNotoFont(language, out var notoFont) || TryGetNotoFont(langPrefix, out notoFont))
                {
                    // If the font is installed, return it
                    if (Fonts.SystemFontFamilies.Any(f => f.Source.Equals(notoFont)))
                    {
                        return notoFont;
                    }
                }
            }

            // If Noto font is not found, fallback to the system default font
            var font = SystemFonts.MessageFontFamily;
            if (font.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-US"), out var englishName))
            {
                return englishName;
            }

            return font.Source ?? "Segoe UI";
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private static bool TryGetNotoFont(string langKey, out string notoFont)
    {
        return _languageToNotoSans.TryGetValue(langKey, out notoFont!);
    }

#endregion
}
