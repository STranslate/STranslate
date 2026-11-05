using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using STranslate.Helpers;

namespace STranslate.Tests;

public class TopEdgeAutoHideTests
{
    [Fact]
    public void ShowAnimationDefersDockingUntilItReleasesTheWindow()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            var showing = true;
            using var controller = new TopEdgeAutoHideController(window, () => true, isShowing: () => showing);
            try
            {
                controller.Update();
                Assert.False(controller.IsDocked);
                Assert.False(window.Topmost);
                showing = false;
                controller.Update();
                Assert.True(controller.IsDocked);
                window.Visibility = Visibility.Collapsed;
                showing = true;
                controller.Update();
                Assert.False(controller.IsDocked);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(iNKORE.UI.WPF.Modern.ElementTheme.Default)]
    [InlineData(iNKORE.UI.WPF.Modern.ElementTheme.Light)]
    [InlineData(iNKORE.UI.WPF.Modern.ElementTheme.Dark)]
    public void CollapsedStripHasVisibleContentWithApplicationTheme(iNKORE.UI.WPF.Modern.ElementTheme theme)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            iNKORE.UI.WPF.Modern.ThemeManager.SetRequestedTheme(window, theme);
            using var controller = new TopEdgeAutoHideController(window, () => true);
            try
            {
                controller.Update();
                controller.SetMoving(true);
                controller.Collapse(animate: false);
                var strip = (Window)typeof(TopEdgeAutoHideController)
                    .GetField("_strip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(controller)!;
                strip.Resources.MergedDictionaries.Add(new iNKORE.UI.WPF.Modern.Controls.XamlControlsResources());
                PumpFor(TimeSpan.FromMilliseconds(150));
                strip.UpdateLayout();
                var indicator = (System.Windows.Controls.Border)strip.Content;
                Assert.True(strip.IsVisible);
                Assert.True(indicator.ActualHeight > 0, $"感应条内容高度为 {indicator.ActualHeight}，窗口高度为 {strip.ActualHeight}");
                Assert.NotNull(indicator.Background);
                Assert.NotNull(indicator.BorderBrush);
                var contentBounds = indicator.TransformToAncestor(strip).TransformBounds(new Rect(indicator.RenderSize));
                Assert.True(new Rect(strip.RenderSize).Contains(contentBounds), $"内容区域 {contentBounds} 不在窗口 {strip.RenderSize} 内");
                // 测试宿主不创建 Application，系统调色板不会初始化；用固定画刷单独验证布局裁剪。
                strip.Resources[iNKORE.UI.WPF.Modern.ThemeKeys.AccentAAFillColorDefaultBrushKey] = Brushes.CornflowerBlue;
                PumpFor(TimeSpan.FromMilliseconds(50));
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)strip.ActualWidth,
                    (int)strip.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render((Visual)VisualTreeHelper.GetChild(strip, 0));
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] > 128), "感应条内容被裁剪，未渲染出可见像素");
                Assert.Equal(iNKORE.UI.WPF.Modern.ThemeManager.GetActualTheme(window),
                    iNKORE.UI.WPF.Modern.ThemeManager.GetActualTheme(strip));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CollapsePreservesPositionAndDisablingRestoresWindowAndTopmostBinding()
    {
        RunOnSta(() =>
        {
            var enabled = true;
            var source = new TopmostSource();
            var window = CreateWindow();
            window.SetBinding(Window.TopmostProperty, new Binding(nameof(TopmostSource.Value))
            {
                Source = source,
                Mode = BindingMode.TwoWay
            });
            using var controller = new TopEdgeAutoHideController(window, () => enabled);
            try
            {
                var top = window.Top;
                controller.Update();
                Assert.True(controller.IsDocked);
                Assert.True(window.Topmost);
                Assert.False(source.Value);

                controller.Collapse(animate: false);
                Assert.True(controller.IsCollapsed);
                Assert.False(window.IsVisible);
                Assert.Equal(top, window.Top);

                Assert.True(controller.Expand(animate: false));
                Assert.True(window.IsVisible);
                Assert.False(controller.IsCollapsed);
                controller.Collapse(animate: false);

                enabled = false;
                controller.Update();
                Assert.True(window.IsVisible);
                Assert.False(controller.IsDocked);
                Assert.False(window.Topmost);
                Assert.NotNull(BindingOperations.GetBindingBase(window, Window.TopmostProperty));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExplicitHideAndDraggingAwayCancelDocking()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            using var controller = new TopEdgeAutoHideController(window, () => true);
            try
            {
                controller.Update();
                Assert.True(controller.IsDocked);
                controller.Collapse(animate: false);
                window.Visibility = Visibility.Collapsed;
                controller.Update();
                Assert.False(controller.IsDocked);
                Assert.False(controller.IsCollapsed);
                Assert.False(window.IsVisible);

                window.Show();
                controller.Update();
                Assert.True(controller.IsDocked);
                controller.SetMoving(true);
                window.Top += 100;
                controller.SetMoving(false);
                Assert.False(controller.IsDocked);
                Assert.True(window.IsVisible);
                Assert.False(window.Topmost);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExpandInvokesCallbackAfterRestoringTheWindow()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            using var controller = new TopEdgeAutoHideController(window, () => true);
            try
            {
                controller.Update();
                controller.Collapse(animate: false);
                var expanded = false;

                controller.Expand(animate: false, expanded: () => expanded = true);

                Assert.True(expanded);
                Assert.True(window.IsVisible);
                Assert.False(controller.IsCollapsed);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SlideCloaksSourceWithoutChangingItsBoundsOrTransformAndReversesFromCurrentFrame()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow(withChrome: true);
            try
            {
                var source = new PositionSource { Top = window.Top };
                var binding = new Binding(nameof(PositionSource.Top)) { Source = source, Mode = BindingMode.TwoWay };
                window.SetBinding(Window.TopProperty, binding);
                var originalTop = window.Top;
                var root = (UIElement)VisualTreeHelper.GetChild(window, 0);
                var originalTransform = root.RenderTransform;
                var hwnd = Win32Helper.GetWindowHandle(window);
                Assert.True(GetWindowRect(hwnd, out var originalBounds));

                using var animation = TopEdgeSlideAnimation.TryCreate(window, initiallyExpanded: true);
                Assert.NotNull(animation);
                Assert.False(IsCloaked(window));
                var collapsed = false;
                animation.Start(expand: false, () => collapsed = true);
                Assert.False(IsCloaked(window));
                PumpUntil(() => animation.VisibleFraction < 0.9);
                Assert.InRange(animation.VisibleFraction, 0.001, 0.999);
                Assert.False(collapsed);
                Assert.True(window.IsVisible);
                Assert.True(IsCloaked(window));
                Assert.Same(originalTransform, root.RenderTransform);
                Assert.Equal(originalTop, window.Top);
                Assert.Equal(originalTop, source.Top);
                Assert.Same(binding, BindingOperations.GetBindingBase(window, Window.TopProperty));
                Assert.True(GetWindowRect(hwnd, out var animatedBounds));
                Assert.Equal(originalBounds, animatedBounds);
                var fractionBeforeReversal = animation.VisibleFraction;
                var expanded = false;
                animation.Start(expand: true, () => expanded = true);
                Assert.Equal(fractionBeforeReversal, animation.VisibleFraction);
                PumpUntil(() => expanded);
                Assert.False(collapsed);
                Assert.Equal(1, animation.VisibleFraction);
                Assert.True(IsCloaked(window));
                animation.Dispose();
                Assert.False(IsCloaked(window));
                Assert.Same(originalTransform, root.RenderTransform);
                Assert.Equal(originalTop, source.Top);
                Assert.True(GetWindowRect(hwnd, out var restoredBounds));
                Assert.Equal(originalBounds, restoredBounds);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AnimatedCollapseAndExpandCompleteAndDisablingCancelsPendingHide()
    {
        RunOnSta(() =>
        {
            var enabled = true;
            var window = CreateWindow();
            using var controller = new TopEdgeAutoHideController(window, () => enabled);
            try
            {
                controller.Update();
                // 本测试直接驱动收展，暂停鼠标轮询以免用户的真实鼠标位置干扰。
                controller.SetMoving(true);
                controller.Collapse(animate: true);
                Assert.True(controller.IsAnimating);
                Assert.True(window.IsVisible);
                PumpUntil(() => !controller.IsAnimating);
                Assert.True(controller.IsCollapsed);
                Assert.False(window.IsVisible);
                Assert.False(IsCloaked(window));

                controller.Expand(animate: true);
                Assert.True(controller.IsAnimating);
                Assert.True(window.IsVisible);
                Assert.True(IsCloaked(window));
                PumpUntil(() => !controller.IsAnimating);
                Assert.False(controller.IsCollapsed);
                Assert.True(window.IsVisible);
                Assert.False(IsCloaked(window));

                controller.Collapse(animate: true);
                controller.Expand(animate: true);
                PumpUntil(() => !controller.IsAnimating);
                Assert.False(controller.IsCollapsed);
                Assert.True(window.IsVisible);
                Assert.False(IsCloaked(window));

                controller.Collapse(animate: true);
                enabled = false;
                controller.Update();
                Assert.False(controller.IsAnimating);
                Assert.False(controller.IsDocked);
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.True(window.IsVisible);
                Assert.False(window.Topmost);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExplicitHideDuringSlideCancelsAnimationAndDoesNotReopenWindow()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            using var controller = new TopEdgeAutoHideController(window, () => true);
            try
            {
                controller.Update();
                controller.Collapse(animate: true);
                window.Visibility = Visibility.Collapsed;
                Assert.False(controller.IsAnimating);
                Assert.False(controller.IsDocked);
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.False(window.IsVisible);
                Assert.False(controller.IsCollapsed);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExpandingCloaksHiddenSourceBeforeItIsShown()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow(withChrome: true);
            try
            {
                window.Hide();
                using var animation = TopEdgeSlideAnimation.TryCreate(window, initiallyExpanded: false);
                Assert.NotNull(animation);
                Assert.False(window.IsVisible);
                Assert.True(IsCloaked(window));

                window.Show();
                Assert.True(window.IsVisible);
                Assert.True(IsCloaked(window));
                var completed = false;
                animation.Start(expand: true, () => completed = true);
                PumpUntil(() => completed);
                animation.Dispose();
                Assert.True(window.IsVisible);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposingBeforePreparationDoesNotRecloakOrShowSource(bool initiallyExpanded)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                if (!initiallyExpanded) window.Hide();
                using var animation = TopEdgeSlideAnimation.TryCreate(window, initiallyExpanded);
                Assert.NotNull(animation);
                var started = false;
                var completed = false;
                animation.Start(expand: !initiallyExpanded,
                    () => completed = true, () => started = true);
                animation.Dispose();
                Assert.False(IsCloaked(window));
                window.Hide();
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.False(started);
                Assert.False(completed);
                Assert.False(window.IsVisible);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DisposingPreparedAnimationCancelsCompletionAndUncloaksSource()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                var root = (UIElement)VisualTreeHelper.GetChild(window, 0);
                var originalTransform = root.RenderTransform;
                var completed = false;
                using var animation = TopEdgeSlideAnimation.TryCreate(window, initiallyExpanded: false);
                Assert.NotNull(animation);
                animation.Start(expand: true, () => completed = true);
                PumpUntil(() => animation.VisibleFraction > 0.1);
                Assert.True(IsCloaked(window));
                Assert.Same(originalTransform, root.RenderTransform);
                animation.Dispose();
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.False(completed);
                Assert.Same(originalTransform, root.RenderTransform);
                Assert.True(window.IsVisible);
                Assert.False(IsCloaked(window));
            }
            finally { window.Close(); }
        });
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(TimeSpan.FromMilliseconds(10));
        Assert.True(condition(), "等待动画帧超时");
    }

    private static bool IsCloaked(Window window)
    {
        const int dwmwaCloaked = 14;
        const int dwmCloakedApp = 1;
        Assert.Equal(0, DwmGetWindowAttribute(Win32Helper.GetWindowHandle(window),
            dwmwaCloaked, out var cloaked, sizeof(int)));
        return (cloaked & dwmCloakedApp) != 0;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static Window CreateWindow(bool withChrome = false)
    {
        _ = new iNKORE.UI.WPF.Modern.ThemeResources();
        var window = new Window
        {
            Width = 300, Height = 200, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        if (withChrome)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 32,
                CornerRadius = new CornerRadius(8),
                GlassFrameThickness = new Thickness(0)
            });
        }
        window.Show();
        var monitor = MonitorInfo.GetNearestDisplayMonitor(Win32Helper.GetWindowHandle(window));
        var position = Win32Helper.TransformPixelsToDIP(window, monitor.WorkingArea.Left, monitor.WorkingArea.Top);
        window.Left = position.X + 100;
        window.Top = position.Y;
        return window;
    }

    // 主题库缓存包含 Dispatcher 对象，所有窗口测试共用同一个 UI 线程。
    private static readonly Lazy<Dispatcher> TestDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    private static void RunOnSta(Action action) => TestDispatcher.Value.Invoke(action);

    public sealed class TopmostSource
    {
        public bool Value { get; set; }
    }

    public sealed class PositionSource
    {
        public double Top { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
