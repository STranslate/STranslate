using STranslate.Controls;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace STranslate.Tests;

[Collection("WPF UI")]
public class ReplacementTextBoxTests
{
    [Fact]
    public void OneOriginalShowsServiceNamesOnlyWhereTheirReplacementValuesDiffer()
    {
        RunOnSta(() =>
        {
            var input = HoverInput("aa KEEP bb", "aa KEEP bb", readOnly: true);
            input.Requests = [new("xx KEEP yy", "A"), new("zz KEEP yy", "B")];
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.Changes.Count == 2);
            Render(root);
            var tooltip = (ToolTip)input.ToolTip;
            input.UpdateHover(CharacterPoint(input, 0));
            PumpUntil(() => tooltip.IsOpen);
            var rows = Assert.IsType<StackPanel>(tooltip.Content).Children.OfType<TextBlock>()
                .Select(row => new TextRange(row.ContentStart, row.ContentEnd).Text).ToArray();
            Assert.Equal(["A: xx", "B: zz"], rows);
            input.UpdateHover(CharacterPoint(input, 8));
            Assert.Equal("yy", Assert.IsType<TextBlock>(tooltip.Content).Text);
            input.SelectAll();
            Assert.Equal("aa KEEP bb", input.SelectedText);
            input.UpdateHover(null);
        });
    }

    [Fact]
    public void DifferentHistoryRequestsProvideCopyChoicesWithoutDuplicatingTheOriginal()
    {
        RunOnSta(() =>
        {
            var input = new ReplacementTextBox { Text = "aa", IsReadOnly = true,
                Requests = [new("xx", "A"), new("yy", "B"), new(null, "C")] };
            input.PrepareRequestContextMenu();
            var copyRequest = Assert.IsType<MenuItem>(input.ContextMenu.Items[^1]);
            Assert.Equal(["A", "B", "C"], copyRequest.Items.OfType<MenuItem>().Select(item => item.Header));
            Assert.False(((MenuItem)copyRequest.Items[2]).IsEnabled);
            input.Requests = [new("xx", "A / B")];
            Assert.Empty(copyRequest.Items.Cast<object>());
            Assert.True(copyRequest.IsEnabled);
            Assert.Equal("aa", input.Text);
            input.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedTextCanShowHoverAfterOrdinaryTextAndShowItAgainAfterLeaving(bool readOnly)
    {
        RunOnSta(() =>
        {
            var input = HoverInput("keep aa today", "keep xx today", readOnly);
            var root = new AdornerDecorator { Child = input, Width = 400, Height = 120 };
            using var window = new FocusTestWindow
            {
                Content = root, Width = 400, Height = 120, Left = -30000, Top = -30000,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
            };
            window.Show();
            PumpUntil(() => input.Changes.Count > 0);
            Render(root);
            FocusManager.SetIsFocusScope(root, true);
            Keyboard.Focus(input);
            Assert.True(input.IsKeyboardFocused);
            var tooltip = (ToolTip)input.ToolTip;
            input.UpdateHover(CharacterPoint(input, 0));
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.False(tooltip.IsOpen);
            Assert.True(input.IsKeyboardFocused);
            input.Select(5, 2);
            var highlight = CharacterPoint(input, 5);
            input.UpdateHover(highlight);
            Assert.False(tooltip.IsOpen);
            PumpUntil(() => tooltip.IsOpen);
            Assert.Equal("xx", ((TextBlock)tooltip.Content).Text);
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal("aa", input.SelectedText);
            input.UpdateHover(null);
            Assert.False(tooltip.IsOpen);
            input.UpdateHover(highlight);
            PumpUntil(() => tooltip.IsOpen);
            window.Hide();
            Assert.False(tooltip.IsOpen);
            input.UpdateHover(null);
        });
    }

    [Fact]
    public void MovingInsideTheSameHighlightDoesNotRestartTheHoverDelay()
    {
        RunOnSta(() =>
        {
            var input = HoverInput("aa KEEP bb", "xx KEEP yy");
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.Changes.Count == 2);
            Render(root);
            var tooltip = (ToolTip)input.ToolTip;
            input.UpdateHover(CharacterPoint(input, 0));
            for (var move = 0; move < 8; move++)
            {
                input.UpdateHover(CharacterPoint(input, move % 2));
                PumpFor(TimeSpan.FromMilliseconds(25));
            }
            Assert.True(tooltip.IsOpen);
            Assert.Equal("xx", ((TextBlock)tooltip.Content).Text);
            input.UpdateHover(CharacterPoint(input, 8));
            Assert.True(tooltip.IsOpen);
            Assert.Equal("yy", ((TextBlock)tooltip.Content).Text);
            input.UpdateHover(null);
        });
    }

    [Fact]
    public void SelectingTextSuppressesHoverAndReleasingItAllowsHoverAgain()
    {
        RunOnSta(() =>
        {
            var input = HoverInput("aa", "xx");
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.Changes.Count > 0);
            Render(root);
            var tooltip = (ToolTip)input.ToolTip;
            var highlight = CharacterPoint(input, 0);
            input.UpdateHover(highlight, selecting: true);
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.False(tooltip.IsOpen);
            input.UpdateHover(highlight);
            PumpUntil(() => tooltip.IsOpen);
            input.UpdateHover(highlight, selecting: true);
            Assert.False(tooltip.IsOpen);
            input.UpdateHover(highlight);
            PumpUntil(() => tooltip.IsOpen);
            input.RequestText = null;
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.False(tooltip.IsOpen);
            input.UpdateHover(null);
        });
    }

    [Fact]
    public void LeavingBeforeTheDelayCancelsThePendingHover()
    {
        RunOnSta(() =>
        {
            var input = HoverInput("aa", "xx");
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.Changes.Count > 0);
            Render(root);
            input.UpdateHover(CharacterPoint(input, 0));
            input.UpdateHover(null);
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert.False(((ToolTip)input.ToolTip).IsOpen);
        });
    }

    [Fact]
    public void RequestCopyExtendsAnExistingMenuWithoutChangingOrDuplicatingItsItems()
    {
        RunOnSta(() =>
        {
            var originalItem = new MenuItem { Header = "Original item" };
            var menu = new ContextMenu();
            menu.Items.Add(originalItem);
            var input = new ReplacementTextBox { ContextMenu = menu, Text = "secret", RequestText = "public" };
            input.PrepareRequestContextMenu();
            input.PrepareRequestContextMenu();
            Assert.Equal(3, menu.Items.Count);
            Assert.Same(originalItem, menu.Items[0]);
            Assert.True(((MenuItem)menu.Items[2]).IsEnabled);
            input.RequestText = null;
            Assert.False(((MenuItem)menu.Items[2]).IsEnabled);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
            Assert.Same(originalItem, Assert.Single(menu.Items.Cast<object>()));
        });
    }

    [Fact]
    public void HighlightsPreserveOriginalTextSelectionAndUndo()
    {
        RunOnSta(() =>
        {
            var input = new ReplacementTextBox { Text = "keep secret today", FontSize = 20,
                TextWrapping = TextWrapping.Wrap, Background = Brushes.White, Foreground = Brushes.Black };
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.IsLoaded);
            var plain = Render(root);
            input.Select(5, 6);
            input.RequestText = "keep public today";
            PumpUntil(() => input.Changes.Count > 0);
            Assert.Equal("keep secret today", input.Text);
            Assert.Equal(5, input.SelectionStart);
            Assert.Equal("secret", input.SelectedText);
            Assert.NotEqual(plain, Render(root));
            input.SelectedText = "edited";
            input.RequestText = null;
            Assert.Empty(input.Changes);
            Assert.True(input.CanUndo);
            input.Undo();
            Assert.Equal("keep secret today", input.Text);
        });
    }

    [Fact]
    public void HighlightsSkipRenderingWhileTextLayoutIsUnavailable()
    {
        RunOnSta(() =>
        {
            var input = new LayoutTestTextBox { Text = "original", RequestText = "replacement",
                TextWrapping = TextWrapping.Wrap, FontSize = 20, Background = Brushes.White, Foreground = Brushes.Black };
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.Changes.Count > 0);
            Render(root);
            var adorner = Assert.Single(AdornerLayer.GetAdornerLayer(input).GetAdorners(input));
            var renderedBeforeTextLayout = false;
            input.BeforeArrange = () =>
            {
                Assert.Equal(0, input.LineCount);
                Assert.Equal(0, input.GetFirstVisibleLineIndex());
                adorner.InvalidateVisual();
                adorner.Arrange(new Rect(0, 0, 400, 120));
                renderedBeforeTextLayout = true;
            };

            input.FontSize = 22;
            root.UpdateLayout();
            Assert.True(renderedBeforeTextLayout);
            var highlighted = Render(root);
            input.RequestText = null;
            Assert.NotEqual(highlighted, Render(root));
        });
    }

    [Fact]
    public void SupersededComparisonCannotRestoreOldHighlights()
    {
        RunOnSta(() =>
        {
            var input = new ReplacementTextBox { Text = new string('x', 900), FontSize = 16 };
            using var source = CreateSource(input, out _);
            PumpUntil(() => input.IsLoaded);
            input.RequestText = new string('b', 900);
            input.RequestText = "latest";
            PumpUntil(() => input.Changes.Count == 1 && input.Changes[0].Replacement == "latest");
            input.RequestText = null;
            PumpDispatcher();
            Assert.Empty(input.Changes);
            Assert.Equal(new string('x', 900), input.Text);
        });
    }

    [Theory]
    [InlineData("", "inserted")]
    [InlineData("ab", "+ab")]
    [InlineData("ab", "ab+")]
    [InlineData("ab", "a+b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("remove this", "remove ")]
    [InlineData("😀 e\u0301", "😁 e")]
    [InlineData("مرحبا العالم", "أهلا العالم")]
    public void ReadOnlyHighlightsRenderWithoutChangingTheirCopyableText(string original, string request)
    {
        RunOnSta(() =>
        {
            var input = new ReplacementTextBox { Text = original, IsReadOnly = true, FontSize = 22,
                FlowDirection = original.StartsWith('م') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                TextWrapping = TextWrapping.Wrap, Background = Brushes.White, Foreground = Brushes.Black };
            using var source = CreateSource(input, out var root);
            PumpUntil(() => input.IsLoaded);
            var plain = Render(root);
            input.RequestText = request;
            PumpUntil(() => input.Changes.Count > 0);
            Assert.NotEqual(plain, Render(root));
            input.SelectAll();
            Assert.Equal(original, input.SelectedText);
        });
    }

    private static HwndSource CreateSource(ReplacementTextBox input, out AdornerDecorator root)
    {
        root = new AdornerDecorator { Child = input, Width = 400, Height = 120 };
        var source = new HwndSource(new HwndSourceParameters("STranslate replacement rendering test")
        { Width = 400, Height = 120, PositionX = -30000, PositionY = -30000, WindowStyle = 0 });
        source.RootVisual = root;
        root.Measure(new Size(400, 120));
        root.Arrange(new Rect(0, 0, 400, 120));
        root.UpdateLayout();
        return source;
    }

    private static ReplacementTextBox HoverInput(string original, string request, bool readOnly = false)
    {
        var input = new ReplacementTextBox { Text = original, RequestText = request, IsReadOnly = readOnly,
            FontSize = 20, TextWrapping = TextWrapping.Wrap, Background = Brushes.White, Foreground = Brushes.Black };
        // 运行真实 Popup 和计时器，但不在用户桌面显示测试提示。
        ((ToolTip)input.ToolTip).Opacity = 0;
        return input;
    }

    private sealed class FocusTestWindow : Window, IDisposable
    {
        public void Dispose() => Close();
    }

    private sealed class LayoutTestTextBox : ReplacementTextBox
    {
        public Action? BeforeArrange { get; set; }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var beforeArrange = BeforeArrange;
            BeforeArrange = null;
            beforeArrange?.Invoke();
            return base.ArrangeOverride(arrangeBounds);
        }
    }

    private static Point CharacterPoint(ReplacementTextBox input, int index)
    {
        var leading = input.GetRectFromCharacterIndex(index);
        var trailing = input.GetRectFromCharacterIndex(index, true);
        return new Point((leading.X + trailing.X) / 2, leading.Y + leading.Height / 2);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static byte[] Render(FrameworkElement root)
    {
        root.UpdateLayout();
        PumpDispatcher();
        var bitmap = new RenderTargetBitmap(400, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[400 * 120 * 4];
        bitmap.CopyPixels(pixels, 400 * 4, 0);
        return pixels;
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var timer = Stopwatch.StartNew();
        while (!completed())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("高亮未在预期时间内更新。");
            PumpDispatcher();
            Thread.Sleep(1);
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
        => WpfUiCollection.Run(action);
}
