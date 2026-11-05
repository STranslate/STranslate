using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace STranslate.Helpers;

public class ClipboardHelper
{
    private static readonly UiaTextReader _uiaTextReader = new();

    /// <summary>
    ///     使用 SendInput API 模拟 Ctrl+C 或 Ctrl+V 键盘输入。
    /// </summary>
    /// <param name="isCopy">如果为 true，则模拟 Ctrl+C；否则模拟 Ctrl+V。</param>
    public static void SendCtrlCV(bool isCopy = true) => SendCtrlCV(isCopy, SendKeyboardInputs);

    internal static void SendCtrlCV(bool isCopy, Func<INPUT[], uint> sendInput)
    {
        var key = isCopy ? VIRTUAL_KEY.VK_C : VIRTUAL_KEY.VK_V;
        // 清理修饰键、抑制 Alt/Win 菜单和复制/粘贴必须在同一批次中发送，
        // 避免用户松开 Ctrl 或其他模拟输入插入组合键中间。
        INPUT[] inputs =
        [
            CreateKeyboardInput(VIRTUAL_KEY.VK_CONTROL, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_LCONTROL, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_RCONTROL, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_SHIFT, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_LSHIFT, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_RSHIFT, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_CONTROL),
            CreateKeyboardInput(VIRTUAL_KEY.VK_MENU, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_LMENU, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_RMENU, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_LWIN, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_RWIN, keyUp: true),
            CreateKeyboardInput(key),
            CreateKeyboardInput(key, keyUp: true),
            CreateKeyboardInput(VIRTUAL_KEY.VK_CONTROL, keyUp: true)
        ];
        try
        {
            if (sendInput(inputs) != inputs.Length)
                throw new InvalidOperationException("模拟复制或粘贴的按键未能全部发送，请检查目标程序的权限级别。");
        }
        catch
        {
            // 部分发送可能留下按住的 C/V 或 Ctrl；尽力释放，并保留原始异常。
            try
            {
                sendInput([CreateKeyboardInput(key, keyUp: true), CreateKeyboardInput(VIRTUAL_KEY.VK_CONTROL, keyUp: true)]);
            }
            catch
            {
                // 目标权限等原因也可能阻止清理输入。
            }
            throw;
        }
    }

    private static INPUT CreateKeyboardInput(VIRTUAL_KEY key, bool keyUp = false)
    {
        var flags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        if (key is VIRTUAL_KEY.VK_RCONTROL or VIRTUAL_KEY.VK_RMENU or VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN)
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = key;
        input.Anonymous.ki.wScan = (ushort)(PInvoke.MapVirtualKey((uint)key, 0) & 0xFF);
        input.Anonymous.ki.dwFlags = flags;
        return input;
    }

    private static uint SendKeyboardInputs(INPUT[] inputs) => PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());

    /// <summary>
    ///     优先通过 UI Automation 获取选中文本，失败时回退到模拟复制。
    /// </summary>
    /// <param name="timeout">剪贴板取词超时（毫秒），默认500ms；UIA 额外最多等待250ms且不超过此值</param>
    /// <param name="cancellation">可以用来取消工作的取消标记</param>
    /// <returns>返回当前选中的文本。</returns>
    public static async Task<string?> GetSelectedTextAsync(int timeout = 500, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var foregroundWindow = Win32Helper.GetForegroundWindow();
        if (foregroundWindow == 0)
            return null;

        var text = await _uiaTextReader.TryGetSelectedTextAsync(timeout, cancellation);
        cancellation.ThrowIfCancellationRequested();

        // 等待 UIA 时用户可能切换窗口，不向新的前台窗口发送复制按键。
        if (Win32Helper.GetForegroundWindow() != foregroundWindow)
            return null;

        if (!string.IsNullOrEmpty(text))
            return text;

        return await GetSelectedTextFromClipboardAsync(timeout, cancellation);
    }

    /// <summary>
    ///     模拟复制并读取选中文本。
    /// </summary>
    /// <param name="timeout">超时时间（毫秒）</param>
    /// <returns>返回当前选中的文本</returns>
    private static async Task<string?> GetSelectedTextFromClipboardAsync(int timeout, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var originalText = GetText();
        uint originalSequence = PInvoke.GetClipboardSequenceNumber();

        // 发送复制命令
        cancellation.ThrowIfCancellationRequested();
        SendCtrlCV();

        var startTime = Environment.TickCount64;
        var hasSequenceChanged = false;

        while (Environment.TickCount64 - startTime < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            uint currentSequence = PInvoke.GetClipboardSequenceNumber();

            // 检查序列号是否变化
            if (currentSequence != originalSequence)
            {
                hasSequenceChanged = true;
                // 序列号变化后，等待一段时间确保内容完全更新
                await Task.Delay(30, cancellation);
                break;
            }

            await Task.Delay(10, cancellation);
        }

        cancellation.ThrowIfCancellationRequested();
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
