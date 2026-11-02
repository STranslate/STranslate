using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using WindowsInput;

namespace STranslate.Helpers;

public class ClipboardHelper
{
    private static readonly InputSimulator _inputSimulator = new();

    /// <summary>
    ///     使用 SendInput API 模拟 Ctrl+C 或 Ctrl+V 键盘输入。
    /// </summary>
    /// <param name="isCopy">如果为 true，则模拟 Ctrl+C；否则模拟 Ctrl+V。</param>
    public static void SendCtrlCV(bool isCopy = true)
    {
        // 先清理可能存在的按键状态 ！！！很重要否则模拟复制会失败
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.CONTROL);
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.LCONTROL);
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.RCONTROL);
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.SHIFT);
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.LSHIFT);
        _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.RSHIFT);
        // 先释放 Shift，再按住 Ctrl 松开 Alt/Win，避免额外组成 Ctrl+Shift 或激活菜单。
        _inputSimulator.Keyboard.KeyDown(VirtualKeyCode.CONTROL);
        try
        {
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.MENU);
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.LMENU);
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.RMENU);
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.LWIN);
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.RWIN);

            _inputSimulator.Keyboard.KeyPress(isCopy ? VirtualKeyCode.VK_C : VirtualKeyCode.VK_V);
        }
        finally
        {
            _inputSimulator.Keyboard.KeyUp(VirtualKeyCode.CONTROL);
        }
    }

    /// <summary>
    ///     获取当前选中的文本。
    /// </summary>
    /// <param name="timeout">超时时间（以毫秒为单位），默认500ms</param>
    /// <param name="cancellation">可以用来取消工作的取消标记</param>
    /// <returns>返回当前选中的文本。</returns>
    public static async Task<string?> GetSelectedTextAsync(int timeout = 500, CancellationToken cancellation = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);

        try
        {
            return await GetSelectedTextImplAsync(timeout);
        }
        catch (OperationCanceledException)
        {
            return GetText()?.Trim(); // 超时时返回当前剪贴板内容
        }
    }

    /// <summary>
    ///     获取选中文本实现
    /// </summary>
    /// <param name="timeout">超时时间（毫秒）</param>
    /// <returns>返回当前选中的文本</returns>
    private static async Task<string?> GetSelectedTextImplAsync(int timeout = 500)
    {
        var originalText = GetText();
        uint originalSequence = PInvoke.GetClipboardSequenceNumber();

        // 发送复制命令
        SendCtrlCV();

        var startTime = Environment.TickCount;
        var hasSequenceChanged = false;

        while (Environment.TickCount - startTime < timeout)
        {
            uint currentSequence = PInvoke.GetClipboardSequenceNumber();

            // 检查序列号是否变化
            if (currentSequence != originalSequence)
            {
                hasSequenceChanged = true;
                // 序列号变化后，等待一段时间确保内容完全更新
                await Task.Delay(30);
                break;
            }

            await Task.Delay(10);
        }

        var currentText = GetText();

        // 如果序列号变化了，或者内容发生了变化，或者原本就没有内容
        if (hasSequenceChanged ||
            currentText != originalText ||
            string.IsNullOrEmpty(originalText))
        {
            return currentText?.Trim();
        }

        return null; // 没有检测到变化
    }

    #region TextCopy

    private static readonly uint[] SupportedFormats =
    [
        CF_UNICODETEXT,
        CF_TEXT,
        CF_OEMTEXT,
        CustomFormat1,
        CustomFormat2,
        CustomFormat3,
        CustomFormat4,
        CustomFormat5,
    ];

    private const uint CF_TEXT = 1; // ANSI 文本
    private const uint CF_UNICODETEXT = 13; // Unicode 文本
    private const uint CF_OEMTEXT = 7; // OEM 文本
    private const uint CF_DIB = 16; // 位图（保留常量但不参与文本读取）
    private const uint CustomFormat1 = 49499; // 自定义格式 1
    private const uint CustomFormat2 = 49290; // 自定义格式 2
    private const uint CustomFormat3 = 49504; // 自定义格式 3
    private const uint CustomFormat4 = 50103; // 自定义格式 4
    private const uint CustomFormat5 = 50104; // 自定义格式 5

    // https://github.com/CopyText/TextCopy/blob/main/src/TextCopy/WindowsClipboard.cs

    public static void SetText(string text)
    {
        TryOpenClipboard();

        InnerSet(text);
    }

    private static unsafe void InnerSet(string text)
    {
        PInvoke.EmptyClipboard();
        HGLOBAL hGlobal = default;
        try
        {
            var bytes = (text.Length + 1) * 2;
            hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes);

            if (hGlobal.IsNull) throw new Win32Exception(Marshal.GetLastWin32Error());

            var target = PInvoke.GlobalLock(hGlobal);

            if (target == null) throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var textBytes = Encoding.Unicode.GetBytes(text + '\0');
                Marshal.Copy(textBytes, 0, (IntPtr)target, textBytes.Length);
            }
            finally
            {
                PInvoke.GlobalUnlock(hGlobal);
            }

            // 修复：直接传递 hGlobal.Value（IntPtr）而不是 HGLOBAL
            if (PInvoke.SetClipboardData(CF_UNICODETEXT, new HANDLE(hGlobal.Value)).IsNull) throw new Win32Exception(Marshal.GetLastWin32Error());

            hGlobal = default;
        }
        finally
        {
            if (!hGlobal.IsNull) PInvoke.GlobalFree(hGlobal);

            PInvoke.CloseClipboard();
        }
    }

    private static void TryOpenClipboard()
    {
        var num = 10;
        while (true)
        {
            if (PInvoke.OpenClipboard(default)) break;

            if (--num == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

            Thread.Sleep(100);
        }
    }

    public static string? GetText()
    {
        // 先占有剪贴板，再检查可用格式，减少 TOCTTOU 竞态
        TryOpenClipboard();

        var support = SupportedFormats.Any(format => PInvoke.IsClipboardFormatAvailable(format));
        if (!support)
        {
            PInvoke.CloseClipboard();
            return null;
        }

        return InnerGet();
    }

    private static Encoding GetOemEncoding()
    {
        try
        {
            // 使用真实 OEM 代码页；不可用时回退到系统默认编码
            var cp = (int)PInvoke.GetOEMCP();
            return Encoding.GetEncoding(cp);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    private static unsafe string? InnerGet()
    {
        HANDLE handle = default;
        void* pointer = null;

        try
        {
            foreach (var format in SupportedFormats)
            {
                handle = PInvoke.GetClipboardData(format);
                if (handle.IsNull) continue;

                pointer = PInvoke.GlobalLock(new HGLOBAL(handle.Value));
                if (pointer == null) continue;

                var size = PInvoke.GlobalSize(new HGLOBAL(handle.Value));
                if (size <= 0)
                {
                    // 修复：避免锁泄漏
                    PInvoke.GlobalUnlock(new HGLOBAL(handle.Value));
                    pointer = null;
                    continue;
                }

                var buffer = new byte[size];
                Marshal.Copy((IntPtr)pointer, buffer, 0, (int)size);

                // 仅对文本/自定义文本格式做解码
                var encoding = format switch
                {
                    CF_UNICODETEXT => Encoding.Unicode, // UTF-16LE
                    CF_TEXT => Encoding.Default,        // ANSI（系统ACP）
                    CF_OEMTEXT => GetOemEncoding(),     // OEM（可进一步改为 OEM 代码页，见下备注）
                    _ => Encoding.UTF8                  // 自定义格式按 UTF-8 尝试
                };

                var result = encoding.GetString(buffer);
                var nullCharIndex = result.IndexOf('\0');
                return nullCharIndex == -1 ? result : result[..nullCharIndex];
            }
        }
        finally
        {
            if (pointer != null) PInvoke.GlobalUnlock(new HGLOBAL(handle.Value));
            PInvoke.CloseClipboard();
        }

        return null;
    }

    #endregion
}
