using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using STranslate.Controls;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace STranslate.Tests;

[Collection("WPF UI")]
public class SizeToContentBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeResizeWithoutSizeChangePreservesAutomaticHeight(bool modernStyle)
    {
        RunOnStaThread(() =>
        {
            var content = new Border { Height = 100 };
            var window = CreateWindow(content, modernStyle);
            try
            {
                var originalHeight = window.ActualHeight;
                BeginNativeResize(window);
                PumpDispatcher();

                Assert.Equal(SizeToContent.Height, window.SizeToContent);
                content.Height = 240;
                PumpDispatcher();
                Assert.Equal(originalHeight + 140, window.ActualHeight, 1);

                content.Height = 60;
                PumpDispatcher();
                Assert.Equal(originalHeight - 40, window.ActualHeight, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HorizontalResizeReflowsContentAndManualModeCancelsPendingRestore(bool modernStyle)
    {
        RunOnStaThread(() =>
        {
            var content = new TextBlock
            {
                Text = string.Join(" ", Enumerable.Repeat("translation", 80)),
                TextWrapping = TextWrapping.Wrap
            };
            var window = CreateWindow(content, modernStyle);
            try
            {
                var originalHeight = window.ActualHeight;
                BeginNativeResize(window);
                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(GetWindowRect(handle, out var bounds));
                Assert.True(SetWindowPos(handle, 0, 0, 0, bounds.Right - bounds.Left + 250,
                    bounds.Bottom - bounds.Top, 0x0016)); // 保留位置、Z 顺序与激活状态。
                PumpDispatcher();

                Assert.Equal(SizeToContent.Height, window.SizeToContent);
                Assert.True(window.ActualHeight < originalHeight,
                    $"横向缩放后高度应减小：原高度 {originalHeight}，当前高度 {window.ActualHeight}，窗口宽度 {window.ActualWidth}，文本宽度 {content.ActualWidth}，文本高度 {content.ActualHeight}。");

                BeginNativeResize(window);
                SizeToContentBehavior.SetPersistentSizeToContent(window, SizeToContent.Manual);
                PumpDispatcher();
                Assert.Equal(SizeToContent.Manual, window.SizeToContent);

                SizeToContentBehavior.SetPersistentSizeToContent(window, SizeToContent.Height);
                BeginNativeResize(window);
                PumpDispatcher();
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Window CreateWindow(UIElement content, bool modernStyle)
    {
        var window = new Window
        {
            Width = 400,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            }
        };
        if (modernStyle)
        {
            window.Resources.MergedDictionaries.Add(new ThemeResources());
            window.Resources.MergedDictionaries.Add(new XamlControlsResources());
            WindowHelper.SetUseModernWindowStyle(window, true);
        }
        SizeToContentBehavior.SetPersistentSizeToContent(window, SizeToContent.Height);
        window.Show();
        PumpDispatcher();
        return window;
    }

    private static void BeginNativeResize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Assert.True(GetWindowRect(handle, out var bounds));
        SendMessage(handle, 0x0214, 2, ref bounds); // WM_SIZING / WMSZ_RIGHT
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
        => WpfUiCollection.Run(action);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint handle, uint message, nint wParam, ref NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
