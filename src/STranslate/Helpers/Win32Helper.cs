using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Dwm;
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

    public static bool ActivateForegroundWindow(Window window) => ActivateForegroundWindow(GetWindowHandle(window));

    public static bool ActivateForegroundWindow(nint handle) => ActivateForegroundWindow(new HWND(handle));

    internal static bool ActivateForegroundWindow(HWND handle)
        => WindowActivationContext.Select(
            normal: () => SetForegroundWindow(handle),
            forceForeground: () => ForceSetForegroundWindow(handle));

    /// <summary>
    /// 强制将窗口带到前台，使用 AttachThreadInput 绕过系统限制。
    /// </summary>
    internal static bool ForceSetForegroundWindow(HWND handle)
    {
        var foregroundWnd = PInvoke.GetForegroundWindow();
        if (handle == foregroundWnd) return true;

        var currentThreadId = PInvoke.GetCurrentThreadId();
        var foregroundThreadId = PInvoke.GetWindowThreadProcessId(foregroundWnd, out _);
        var attached = false;

        try
        {
            if (foregroundThreadId != currentThreadId && foregroundThreadId != 0)
            {
                attached = PInvoke.AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }

            var result = PInvoke.SetForegroundWindow(handle);

            if (PInvoke.IsIconic(handle))
            {
                PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            if (!result)
            {
                PInvoke.BringWindowToTop(handle);
            }

            return result || PInvoke.GetForegroundWindow() == handle;
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(foregroundThreadId, currentThreadId, false);
            }
        }
    }

    public static bool IsForegroundWindow(Window window) => IsForegroundWindow(GetWindowHandle(window));

    public static bool IsForegroundWindow(nint handle) => IsForegroundWindow(new HWND(handle));

    internal static bool IsForegroundWindow(HWND handle) => handle.Equals(PInvoke.GetForegroundWindow());

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

    #region Window Position

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private enum MonitorDpiType
    {
        Effective = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    /// <summary>
    /// 使用物理像素设置窗口边界，并可选择是否同步显示窗口。
    /// </summary>
    /// <param name="window">需要定位的 WPF 窗口。</param>
    /// <param name="left">窗口左边缘的物理像素坐标。</param>
    /// <param name="top">窗口上边缘的物理像素坐标。</param>
    /// <param name="width">窗口宽度（物理像素）。</param>
    /// <param name="height">窗口高度（物理像素）。</param>
    /// <param name="showWindow">是否在定位时同步显示窗口。</param>
    public static void SetWindowPhysicalBounds(
        Window window,
        int left,
        int top,
        int width,
        int height,
        bool showWindow = true)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (showWindow)
            flags |= SWP_SHOWWINDOW;

        if (!SetWindowPos(
                hwnd,
                0,
                left,
                top,
                Math.Max(1, width),
                Math.Max(1, height),
                flags))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// 在同一个 DeferWindowPos batch 中更新两个 HWND 的物理矩形。
    /// 用于 Pinned 主内容窗与视觉 Chrome 伴随窗，避免快速拖动时两个窗口逐次 SetWindowPos 造成可见迟滞。
    /// </summary>
    internal static bool SetTwoWindowPhysicalBounds(
        Window firstWindow,
        System.Drawing.Rectangle firstBounds,
        Window secondWindow,
        System.Drawing.Rectangle secondBounds)
    {
        var first = new WindowInteropHelper(firstWindow).EnsureHandle();
        var second = new WindowInteropHelper(secondWindow).EnsureHandle();
        var deferred = BeginDeferWindowPos(2);
        if (deferred == 0)
            return false;

        deferred = DeferWindowPos(
            deferred,
            first,
            0,
            firstBounds.Left,
            firstBounds.Top,
            Math.Max(1, firstBounds.Width),
            Math.Max(1, firstBounds.Height),
            SWP_NOZORDER | SWP_NOACTIVATE);
        if (deferred == 0)
            return false;

        deferred = DeferWindowPos(
            deferred,
            second,
            0,
            secondBounds.Left,
            secondBounds.Top,
            Math.Max(1, secondBounds.Width),
            Math.Max(1, secondBounds.Height),
            SWP_NOZORDER | SWP_NOACTIVATE);
        return deferred != 0 && EndDeferWindowPos(deferred);
    }

    internal static bool PlaceWindowBehind(Window window, Window windowAbove)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var above = new WindowInteropHelper(windowAbove).EnsureHandle();
        return PlaceWindowBehind(hwnd, above);
    }

    internal static bool PlaceWindowBehind(nint hwnd, nint windowAbove)
    {
        if (hwnd == 0 || windowAbove == 0)
            return false;

        return SetWindowPos(
            hwnd,
            windowAbove,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    internal static void ConfigureClickThroughNoActivate(Window window)
    {
        var hwnd = GetWindowHandle(window, ensure: true);
        var exStyle = GetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE).ToInt64();
        exStyle |= (long)(uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        exStyle |= (long)(uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        exStyle |= (long)(uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        exStyle &= ~(long)(uint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        SetWindowStyle(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, new nint(exStyle));
    }

    internal static unsafe bool SetWindowCloaked(Window window, bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return PInvoke.DwmSetWindowAttribute(
            GetWindowHandle(window, ensure: true),
            DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
            &value,
            (uint)sizeof(int)).Succeeded;
    }

    internal static bool FlushDesktopComposition() => PInvoke.DwmFlush().Succeeded;

    public static DpiScale GetDpiScaleForPhysicalPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint(x, y), MONITOR_DEFAULTTONEAREST);
        var hr = GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out var dpiY);
        return hr == 0 && dpiX > 0 && dpiY > 0
            ? new DpiScale(dpiX / 96d, dpiY / 96d)
            : new DpiScale(1, 1);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint DeferWindowPos(
        nint hWinPosInfo,
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(nint hWinPosInfo);

    [DllImport("user32.dll")]
    private static extern HMONITOR MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        HMONITOR hmonitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

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
        var windowBounds = new Rect(
            appBounds.left,
            appBounds.top,
            appBounds.right - appBounds.left,
            appBounds.bottom - appBounds.top);
        return IsWindowBoundsFullscreen(windowBounds, monitorInfo.Bounds);
    }

    internal static bool IsWindowBoundsFullscreen(Rect windowBounds, Rect monitorBounds)
    {
        const double tolerance = 1;
        return Math.Abs(windowBounds.Left - monitorBounds.Left) <= tolerance &&
               Math.Abs(windowBounds.Top - monitorBounds.Top) <= tolerance &&
               Math.Abs(windowBounds.Right - monitorBounds.Right) <= tolerance &&
               Math.Abs(windowBounds.Bottom - monitorBounds.Bottom) <= tolerance;
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
