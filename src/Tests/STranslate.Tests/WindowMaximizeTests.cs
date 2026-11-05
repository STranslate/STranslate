using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using STranslate.Helpers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace STranslate.Tests;

public class WindowMaximizeTests
{
    [Fact]
    public void DisablingMaximizePreservesResizeAcrossWindowVisibilityChanges()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new Window
                {
                    Width = 400,
                    Height = 200,
                    Opacity = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false
                };
                window.Resources.MergedDictionaries.Add(new ThemeResources());
                window.Resources.MergedDictionaries.Add(new XamlControlsResources());
                WindowHelper.SetUseModernWindowStyle(window, true);
                window.Loaded += (_, _) =>
                {
                    Win32Helper.AddWndProcHook(window, GuardMaximize);
                    Win32Helper.DisableMaximize(window);
                };
                window.Show();

                var handle = new WindowInteropHelper(window).Handle;
                const int styleIndex = -16;
                const int maximizeBox = 0x00010000;
                const int thickFrame = 0x00040000;
                Assert.Equal(0, GetWindowLong(handle, styleIndex) & maximizeBox);
                Assert.NotEqual(0, GetWindowLong(handle, styleIndex) & thickFrame);

                // 系统命令的低四位由系统使用，仍应识别为最大化请求。
                SendMessage(handle, 0x0112, 0xF032, 0);
                Assert.Equal(WindowState.Normal, window.WindowState);

                window.Hide();
                window.Topmost = true;
                window.Show();
                window.Width = 500;
                window.UpdateLayout();

                Assert.Equal(0, GetWindowLong(handle, styleIndex) & maximizeBox);
                Assert.NotEqual(0, GetWindowLong(handle, styleIndex) & thickFrame);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(500d, window.ActualWidth);
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);

    private static nint GuardMaximize(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        handled = Win32Helper.HandleMaximizeMessage(message, wParam, lParam);
        return 0;
    }
}
