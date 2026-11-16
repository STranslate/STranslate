using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace STranslate.Controls;

public static class SizeToContentBehavior
{
    private static readonly DependencyProperty SubscriptionProperty =
        DependencyProperty.RegisterAttached(
            "Subscription",
            typeof(SizeToContentSubscription),
            typeof(SizeToContentBehavior),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PersistentSizeToContentProperty =
        DependencyProperty.RegisterAttached(
            "PersistentSizeToContent",
            typeof(SizeToContent),
            typeof(SizeToContentBehavior),
            new PropertyMetadata(SizeToContent.Manual, OnPersistentSizeToContentChanged));

    public static void SetPersistentSizeToContent(Window window, SizeToContent value)
    {
        window.SetValue(PersistentSizeToContentProperty, value);
    }

    public static SizeToContent GetPersistentSizeToContent(Window window)
    {
        return (SizeToContent)window.GetValue(PersistentSizeToContentProperty);
    }

    private static void OnPersistentSizeToContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
            return;

        (window.GetValue(SubscriptionProperty) as SizeToContentSubscription)?.Detach();
        window.ClearValue(SubscriptionProperty);
        window.SizeToContent = (SizeToContent)e.NewValue;

        if ((SizeToContent)e.NewValue != SizeToContent.Manual)
            window.SetValue(SubscriptionProperty, new SizeToContentSubscription(window));
    }

    private sealed class SizeToContentSubscription
    {
        private readonly Window _window;
        private HwndSource? _source;
        private DispatcherOperation? _pendingRestore;

        public SizeToContentSubscription(Window window)
        {
            _window = window;
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += OnClosed;
            AttachSource();
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => AttachSource();

        private void AttachSource()
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (_source != null || handle == IntPtr.Zero)
                return;

            _source = HwndSource.FromHwnd(handle);
            if (_source != null)
                _source.SizeToContentChanged += OnSourceSizeToContentChanged;
        }

        private void OnSourceSizeToContentChanged(object? sender, EventArgs e)
        {
            if (_pendingRestore?.Status == DispatcherOperationStatus.Pending)
                return;

            // WPF 在原生缩放时先重新布局，再把 Window.SizeToContent 同步为 Manual。
            // SizeChanged 可能早于同步发生，也可能根本不触发；等当前消息处理完再恢复。
            _pendingRestore = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _pendingRestore = null;
                var sizeToContent = GetPersistentSizeToContent(_window);
                if (_source?.IsDisposed == false && sizeToContent != SizeToContent.Manual)
                    _window.SetCurrentValue(Window.SizeToContentProperty, sizeToContent);
            }));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Detach();
            _window.ClearValue(SubscriptionProperty);
        }

        public void Detach()
        {
            _pendingRestore?.Abort();
            _pendingRestore = null;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            if (_source != null)
                _source.SizeToContentChanged -= OnSourceSizeToContentChanged;
            _source = null;
        }
    }
}
