using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace STranslate.Helpers;

internal sealed class AcrobatCursorCompatibility
{
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { public int IsIcon; public uint X, Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("gdi32.dll", EntryPoint="GetObjectW")] static extern int GetObject(IntPtr bitmap, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] static extern int GetBitmapBits(IntPtr bitmap, int count, byte[] data);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    // 从 Acrobat 文字选择光标采集的已知位图指纹，避免依赖进程重启后会变化的句柄。
    private const string TextCursorFingerprint = "6:12:37x37x1-1207FBF437A6D60BAD608C9C4A7397194C4F3768142A32C7E5F3A1415452A992:37x37x32-8B594E3DDBF4F7ADCB4EA9DB36D5FE599C6C336AC46A4CFC4E166D5C862A1B60";
    // 同一光标在 PerMonitorV2 实际拖选中读到 56×56，需与 37×37 样本分别匹配。
    private const string PerMonitorTextCursorFingerprint = "9:18:56x56x1-5C55C8F4DB4010BA9203D83536D0609856AF8C847AC039E37E7DDE8FBD574B61:56x56x32-5600571C565EC241354317B795788BB262735B4026608F2F58B283C813B692AF";
    private const int CacheLifetimeMs = 2000;
    private readonly Func<uint> _getForegroundProcessId;
    private readonly Func<uint, string> _getProcessName;
    private readonly Func<IntPtr, string> _getFingerprint;
    private readonly Func<long> _getTimestamp;
    private IntPtr _lastCursor;
    private bool _lastCursorMatches;
    private uint _lastPid;
    private bool _lastProcessMatches;
    private long _lastProcessCheck;
    private long _lastCursorCheck;
    private bool _hasProcessCache;
    private bool _hasCursorCache;

    public AcrobatCursorCompatibility() : this(GetForegroundProcessId, GetProcessName, Fingerprint, () => Environment.TickCount64)
    {
    }

    internal AcrobatCursorCompatibility(Func<uint> getForegroundProcessId, Func<uint, string> getProcessName,
        Func<IntPtr, string> getFingerprint, Func<long> getTimestamp)
    {
        _getForegroundProcessId = getForegroundProcessId;
        _getProcessName = getProcessName;
        _getFingerprint = getFingerprint;
        _getTimestamp = getTimestamp;
    }

    // 仅由鼠标 Hook 线程调用；缓存属于当前服务实例，不跨实例共享。
    public bool IsTextCursor(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero)
            return false;

        try
        {
            var pid = _getForegroundProcessId();
            if (pid == 0)
                return false;

            var now = _getTimestamp();
            if (!_hasProcessCache || pid != _lastPid || now - _lastProcessCheck >= CacheLifetimeMs)
            {
                if (pid != _lastPid)
                    _hasCursorCache = false;
                _hasProcessCache = true;
                _lastProcessCheck = now;
                _lastPid = pid;
                _lastProcessMatches = false;
                _lastProcessMatches = IsAcrobatProcess(_getProcessName(pid));
            }

            if (!_lastProcessMatches)
                return false;

            if (!_hasCursorCache || cursor != _lastCursor || now - _lastCursorCheck >= CacheLifetimeMs)
            {
                _hasCursorCache = true;
                _lastCursorCheck = now;
                _lastCursor = cursor;
                _lastCursorMatches = false;
                _lastCursorMatches = MatchesFingerprint(_getFingerprint(cursor));
            }

            return _lastCursorMatches;
        }
        catch
        {
            // 进程退出、无权读取或 GDI 读取失败时不把普通拖动当作划词。
            return false;
        }
    }

    private static uint GetForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid;
    }

    private static string GetProcessName(uint pid)
    {
        using var process = Process.GetProcessById(checked((int)pid));
        return process.ProcessName;
    }
    internal static bool IsAcrobatProcess(string name) =>
        string.Equals(name, "Acrobat", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "AcroRd32", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesFingerprint(string fingerprint) =>
        string.Equals(fingerprint, TextCursorFingerprint, StringComparison.Ordinal) ||
        string.Equals(fingerprint, PerMonitorTextCursorFingerprint, StringComparison.Ordinal);
    public static string Fingerprint(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return "";
        try { return icon.X + ":" + icon.Y + ":" + BitmapFingerprint(icon.Mask) + ":" + BitmapFingerprint(icon.Color); }
        finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
    }

    static string BitmapFingerprint(IntPtr bitmap)
    {
        if (bitmap == IntPtr.Zero) return "none";
        if (GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), out var info) == 0) return "error";
        int length = checked(Math.Abs(info.Height) * info.WidthBytes);
        if (length <= 0 || length > 1048576) return "error";
        var data = new byte[length];
        if (GetBitmapBits(bitmap, length, data) != length) return "error";
        return info.Width + "x" + info.Height + "x" + info.BitsPixel + "-" + Convert.ToHexString(SHA256.HashData(data));
    }
}
