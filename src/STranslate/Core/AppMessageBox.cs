using STranslate.Helpers;
using System.Windows;
using MessageBox = iNKORE.UI.WPF.Modern.Controls.MessageBox;

namespace STranslate.Core;

/// <summary>
/// 提供带稳定 owner 的应用级 MessageBox 显示入口。
/// </summary>
public static class AppMessageBox
{
    private const double OwnerSize = 1;

    /// <summary>
    /// 显示仅包含消息内容的 MessageBox。
    /// </summary>
    /// <param name="messageBoxText">消息内容。</param>
    /// <returns>用户点击后的 MessageBox 结果。</returns>
    public static MessageBoxResult Show(string messageBoxText)
        => ShowOnDispatcher(() => ShowWithOwner(owner => MessageBox.Show(owner, messageBoxText)));

    /// <summary>
    /// 显示包含标题和消息内容的 MessageBox。
    /// </summary>
    /// <param name="messageBoxText">消息内容。</param>
    /// <param name="caption">窗口标题。</param>
    /// <returns>用户点击后的 MessageBox 结果。</returns>
    public static MessageBoxResult Show(string messageBoxText, string caption)
        => ShowOnDispatcher(() => ShowWithOwner(owner => MessageBox.Show(owner, messageBoxText, caption)));

    /// <summary>
    /// 显示包含标题、消息内容和按钮类型的 MessageBox。
    /// </summary>
    /// <param name="messageBoxText">消息内容。</param>
    /// <param name="caption">窗口标题。</param>
    /// <param name="button">按钮类型。</param>
    /// <returns>用户点击后的 MessageBox 结果。</returns>
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
        => ShowOnDispatcher(() => ShowWithOwner(owner => MessageBox.Show(owner, messageBoxText, caption, button)));

    /// <summary>
    /// 显示包含标题、消息内容、按钮类型和图标的 MessageBox。
    /// </summary>
    /// <param name="messageBoxText">消息内容。</param>
    /// <param name="caption">窗口标题。</param>
    /// <param name="button">按钮类型。</param>
    /// <param name="icon">图标类型。</param>
    /// <returns>用户点击后的 MessageBox 结果。</returns>
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => ShowOnDispatcher(() => ShowWithOwner(owner => MessageBox.Show(owner, messageBoxText, caption, button, icon)));

    /// <summary>
    /// 显示包含默认按钮结果的 MessageBox。
    /// </summary>
    /// <param name="messageBoxText">消息内容。</param>
    /// <param name="caption">窗口标题。</param>
    /// <param name="button">按钮类型。</param>
    /// <param name="icon">图标类型。</param>
    /// <param name="defaultResult">默认按钮结果。</param>
    /// <returns>用户点击后的 MessageBox 结果。</returns>
    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult? defaultResult)
        => ShowOnDispatcher(() => ShowWithOwner(owner => MessageBox.Show(owner, messageBoxText, caption, button, icon, defaultResult)));

    private static MessageBoxResult ShowOnDispatcher(Func<MessageBoxResult> show)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
            return show();

        return dispatcher.Invoke(show);
    }

    private static MessageBoxResult ShowWithOwner(Func<Window, MessageBoxResult> show)
    {
        using var ownerScope = CreateOwnerScope();
        return show(ownerScope.Owner);
    }

    private static MessageBoxOwnerScope CreateOwnerScope()
    {
        var activeOwner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive && window.IsVisible);

        if (activeOwner != null)
            return MessageBoxOwnerScope.ForExistingOwner(activeOwner);

        return MessageBoxOwnerScope.ForTemporaryOwner(CreateTransparentOwner());
    }

    private static Window CreateTransparentOwner()
    {
        var owner = new Window
        {
            Width = OwnerSize,
            Height = OwnerSize,
            Left = (SystemParameters.PrimaryScreenWidth - OwnerSize) / 2,
            Top = (SystemParameters.PrimaryScreenHeight - OwnerSize) / 2,
            Opacity = 0,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None
        };

        return owner;
    }

    private sealed class MessageBoxOwnerScope : IDisposable
    {
        private readonly bool _isTemporaryOwner;

        private MessageBoxOwnerScope(Window owner, bool isTemporaryOwner)
        {
            Owner = owner;
            _isTemporaryOwner = isTemporaryOwner;
        }

        public Window Owner { get; }

        public static MessageBoxOwnerScope ForExistingOwner(Window owner)
            => new(owner, false);

        public static MessageBoxOwnerScope ForTemporaryOwner(Window owner)
        {
            owner.Show();
            Win32Helper.ForceSetForegroundWindow(owner);
            owner.Activate();

            return new(owner, true);
        }

        public void Dispose()
        {
            if (_isTemporaryOwner && Owner.IsVisible)
                Owner.Close();
        }
    }
}
