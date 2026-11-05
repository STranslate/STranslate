using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using STranslate.Helpers;

namespace STranslate.Tests;

public class WindowShowAnimationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeWindowFadesAndRestoresStyleOnCompletionOrCancellation(bool cancel)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.Opacity = 1;
            byte? firstAlpha = null;
            byte? latestAlpha = null;
            EventHandler observe = (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (IsCloaked(window) || !GetLayeredWindowAttributes(hwnd, out _, out var alpha, out _)) return;
                firstAlpha ??= alpha;
                latestAlpha = alpha;
            };
            try
            {
                window.Show();
                var hwnd = new WindowInteropHelper(window).Handle;
                var originalStyle = GetWindowLong(hwnd, -20);
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                CompositionTarget.Rendering += observe;
                animation.Start();
                PumpUntil(() => firstAlpha.HasValue);
                Assert.InRange(firstAlpha ?? byte.MaxValue, (byte)0, (byte)254);
                Assert.True(window.Topmost);
                Assert.Equal(1, window.Opacity);
                Assert.False(window.AllowsTransparency);
                if (cancel)
                    window.Hide();
                else
                {
                    PumpUntil(() => latestAlpha > firstAlpha);
                    PumpUntil(() => !animation.IsActive);
                }
                Assert.False(animation.IsActive);
                Assert.Equal(originalStyle, GetWindowLong(hwnd, -20));
                Assert.Equal(!cancel, window.IsVisible);
                Assert.Equal(1, window.Opacity);
            }
            finally { CompositionTarget.Rendering -= observe; window.Close(); }
        });
    }

    [Fact]
    public void FadeFinishesBeforeTheWindowStopsRising()
    {
        Assert.Equal(0, WindowShowAnimation.GetAlpha(0));
        Assert.InRange(WindowShowAnimation.GetAlpha(70), (byte)1, (byte)254);
        Assert.Equal(255, WindowShowAnimation.GetAlpha(140));
        Assert.Equal(255, WindowShowAnimation.GetAlpha(180));
    }

    [Fact]
    public void RealWindowReturnsToOriginalBoundsOnEachMonitor()
    {
        RunOnSta(() =>
        {
            var previousDpiContext = SetThreadDpiAwarenessContext(new nint(-4));
            try
            {
                foreach (var monitor in MonitorInfo.GetDisplayMonitors())
                {
                    var window = CreateWindow();
                    try
                    {
                        window.Show();
                        Win32Helper.SetWindowPhysicalBounds(window,
                            (int)monitor.WorkingArea.Left + 60, (int)monitor.WorkingArea.Top + 60, 400, 250);
                        PumpFor(TimeSpan.FromMilliseconds(60));
                        Assert.Equal(monitor.Name, MonitorInfo.GetNearestDisplayMonitor(new WindowInteropHelper(window).Handle).Name);
                        window.Hide();
                        var bounds = GetBounds(window);
                        using var animation = WindowShowAnimation.TryCreate(window);
                        Assert.NotNull(animation);
                        window.Show();
                        Assert.Equal(bounds, GetBounds(window));
                        animation.Start();
                        PumpUntil(() => !IsCloaked(window));
                        Assert.NotEqual(bounds.Top, GetBounds(window).Top);
                        var hwnd = new WindowInteropHelper(window).Handle;
                        Assert.True(monitor.Name == MonitorInfo.GetNearestDisplayMonitor(hwnd).Name,
                            $"屏幕 {monitor.Name}：起点 {bounds.Left},{bounds.Top}；当前 {GetBounds(window).Left},{GetBounds(window).Top}；DIP {window.Left},{window.Top}");
                        PumpUntil(() => !animation.IsActive);
                        Assert.Equal(bounds, GetBounds(window));
                    }
                    finally { window.Close(); }
                }
            }
            finally { if (previousDpiContext != 0) SetThreadDpiAwarenessContext(previousDpiContext); }
        });
    }

    [Fact]
    public void FrameRisesToTargetWithoutOvershoot()
    {
        Assert.Equal(18, WindowShowAnimation.GetOffset(0), precision: 3);
        var previous = WindowShowAnimation.GetOffset(0);
        for (var elapsed = 1; elapsed <= 180; elapsed++)
        {
            var offset = WindowShowAnimation.GetOffset(elapsed);
            Assert.InRange(offset, 0, previous);
            previous = offset;
        }
        Assert.Equal(0, WindowShowAnimation.GetOffset(180), precision: 3);
        Assert.Equal(0, WindowShowAnimation.GetOffset(260), precision: 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealWindowMovesWithoutWritingAnimationPositionsToBindings(bool topmost)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            var source = new Placement { Left = 160, Top = 180, Topmost = topmost };
            var leftBinding = Bind(window, Window.LeftProperty, source, nameof(Placement.Left));
            var topBinding = Bind(window, Window.TopProperty, source, nameof(Placement.Top));
            var topmostBinding = Bind(window, Window.TopmostProperty, source, nameof(Placement.Topmost));
            try
            {
                window.Show();
                window.Hide();
                var originalBounds = GetBounds(window);
                var root = (UIElement)window.Content;
                var transform = root.RenderTransform;
                var hwnd = new WindowInteropHelper(window).Handle;
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                window.Show();
                var completed = 0;
                animation.Start(() =>
                {
                    Assert.True(window.Topmost);
                    Assert.NotEqual(0, GetWindowLong(hwnd, -20) & 0x00000008); // WS_EX_TOPMOST
                    Assert.Equal(originalBounds.Top, GetBounds(window).Top);
                    completed++;
                });
                animation.Start();
                PumpUntil(() => !IsCloaked(window));
                Assert.True(animation.IsActive);
                Assert.NotEqual(originalBounds.Top, GetBounds(window).Top);
                Assert.Equal(hwnd, new WindowInteropHelper(window).Handle);
                Assert.Equal(180, source.Top);
                Assert.Equal(160, source.Left);
                Assert.True(window.Topmost);
                Assert.NotEqual(0, GetWindowLong(hwnd, -20) & 0x00000008);
                Assert.Equal(topmost, source.Topmost);
                Assert.Same(transform, root.RenderTransform);
                window.Height += 60;
                window.UpdateLayout();
                PumpUntil(() => !animation.IsActive);
                Assert.Equal(originalBounds.Top, GetBounds(window).Top);
                Assert.Equal(originalBounds.Left, GetBounds(window).Left);
                Assert.True(GetBounds(window).Height > originalBounds.Height);
                Assert.Same(leftBinding, BindingOperations.GetBindingBase(window, Window.LeftProperty));
                Assert.Same(topBinding, BindingOperations.GetBindingBase(window, Window.TopProperty));
                Assert.Same(topmostBinding, BindingOperations.GetBindingBase(window, Window.TopmostProperty));
                Assert.Equal(180, source.Top);
                Assert.Equal(topmost, source.Topmost);
                Assert.Equal(topmost, window.Topmost);
                Assert.Equal(topmost, (GetWindowLong(hwnd, -20) & 0x00000008) != 0);
                Assert.Equal(1, completed);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void FirstShowIsCloakedOnlyUntilRealWindowStartsMoving()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            WindowShowAnimation? animation = null;
            var cloakedAtLoad = false;
            window.SourceInitialized += (_, _) => animation = WindowShowAnimation.TryCreate(window);
            window.Loaded += (_, _) => cloakedAtLoad = IsCloaked(window);
            try
            {
                window.Show();
                Assert.NotNull(animation);
                Assert.True(cloakedAtLoad);
                var bounds = GetBounds(window);
                animation.Start();
                PumpUntil(() => !IsCloaked(window));
                Assert.True(animation.IsActive);
                Assert.True(window.IsVisible);
                PumpUntil(() => !animation.IsActive);
                Assert.Equal(bounds, GetBounds(window));
            }
            finally { animation?.Dispose(); window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationRestoresLatestTopmostBinding(bool latestTopmost)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            var source = new Placement { Topmost = false };
            var binding = Bind(window, Window.TopmostProperty, source, nameof(Placement.Topmost));
            try
            {
                window.Show();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                var foreground = Win32Helper.GetForegroundWindow();
                var completed = false;
                animation.Start(() => completed = true);
                PumpUntil(() => !IsCloaked(window));
                Assert.True(window.Topmost);
                Assert.False(source.Topmost);
                Assert.Equal(foreground, Win32Helper.GetForegroundWindow());
                source.Topmost = latestTopmost;
                window.Hide();
                Assert.False(animation.IsActive);
                Assert.False(completed);
                Assert.False(window.IsVisible);
                Assert.Same(binding, BindingOperations.GetBindingBase(window, Window.TopmostProperty));
                Assert.Equal(latestTopmost, window.Topmost);
                Assert.Equal(latestTopmost, source.Topmost);
                Assert.Equal(latestTopmost,
                    (GetWindowLong(new WindowInteropHelper(window).Handle, -20) & 0x00000008) != 0);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HidingCancelsPendingOrRunningAnimationWithoutReopeningWindow(bool running)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                var bounds = GetBounds(window);
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                var completed = false;
                animation.Start(() => completed = true);
                if (running) PumpUntil(() => !IsCloaked(window));
                window.Visibility = Visibility.Collapsed;
                Assert.False(animation.IsActive);
                Assert.False(IsCloaked(window));
                Assert.Equal(bounds, GetBounds(window));
                Assert.False(window.Topmost);
                PumpFor(TimeSpan.FromMilliseconds(320));
                Assert.False(window.IsVisible);
                Assert.False(completed);
                using var next = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(next);
                animation.Dispose();
                Assert.True(IsCloaked(window));
                window.Show();
                next.Start();
                PumpUntil(() => !next.IsActive);
                Assert.True(window.IsVisible);
                Assert.Equal(bounds, GetBounds(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HidingInsidePositionUpdateCancelsWithoutCompletingOrReopening()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                var bounds = GetBounds(window);
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                window.LocationChanged += (_, _) => window.Visibility = Visibility.Collapsed;
                var completed = false;
                animation.Start(() => completed = true);
                PumpUntil(() => !animation.IsActive);
                Assert.False(window.IsVisible);
                Assert.False(IsCloaked(window));
                Assert.Equal(bounds, GetBounds(window));
                Assert.False(completed);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KeyboardInputRestoresLatestBoundPositionWithoutSwallowingInput()
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            var source = new Placement { Top = 180 };
            var binding = Bind(window, Window.TopProperty, source, nameof(Placement.Top));
            try
            {
                window.Show();
                using var animation = WindowShowAnimation.TryCreate(window);
                Assert.NotNull(animation);
                animation.Start();
                PumpUntil(() => !IsCloaked(window));
                source.Top = 200;
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key);
                Assert.False(key.Handled);
                Assert.False(animation.IsActive);
                Assert.Equal(200, window.Top);
                Assert.Equal(200, source.Top);
                Assert.Same(binding, BindingOperations.GetBindingBase(window, Window.TopProperty));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingCancelsPendingOrRunningAnimation(bool running)
    {
        RunOnSta(() =>
        {
            var window = CreateWindow();
            window.Show();
            using var animation = WindowShowAnimation.TryCreate(window);
            Assert.NotNull(animation);
            var completed = false;
            animation.Start(() => completed = true);
            if (running) PumpUntil(() => !IsCloaked(window));
            window.Close();
            Assert.False(animation.IsActive);
            Assert.False(completed);
            PumpFor(TimeSpan.FromMilliseconds(320));
        });
    }
    private static Window CreateWindow() => new()
    {
        Style = null,
        Width = 320,
        Height = 200,
        Left = 160,
        Top = 180,
        Opacity = 0,
        ShowActivated = false,
        ShowInTaskbar = false,
        Content = new Border { Background = Brushes.White }
    };

    private static Binding Bind(Window window, DependencyProperty property, object source, string path)
    {
        var binding = new Binding(path) { Source = source, Mode = BindingMode.TwoWay };
        window.SetBinding(property, binding);
        return binding;
    }

    private static NativeRect GetBounds(Window window)
    {
        Assert.True(GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds));
        return bounds;
    }

    private static bool IsCloaked(Window window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle,
            14, out var cloaked, sizeof(int)));
        return (cloaked & 1) != 0;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(TimeSpan.FromMilliseconds(5));
        Assert.True(condition(), "等待窗口显示动画超时");
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

    public sealed class Placement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Topmost { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
