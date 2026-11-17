using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Controls.Primitives;
using STranslate.Helpers;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace STranslate.Tests;

[Collection("WPF UI")]
public class ModernWindowLifecycleTests
{
    [Fact]
    public void DetachVisualTreeClearsWindowOwnedReferences()
    {
        RunOnStaThread(() =>
        {
            var dataContext = new object();
            var child = new Border { DataContext = dataContext };
            var panel = new Grid();
            panel.Children.Add(child);
            var window = new Window
            {
                Content = panel,
                DataContext = dataContext
            };

            ModernWindowLifecycle.DetachVisualTree(window);

            Assert.Null(window.Content);
            Assert.Null(window.DataContext);
            Assert.Null(child.DataContext);
            Assert.Empty(panel.Children);
        });
    }

    [Fact]
    public void ClosingModernWindowRemovesPropertyDescriptorTrackers()
    {
        RunOnStaThread(() =>
        {
            var window = CreateModernWindow();
            Assert.True(IsTrackedByDescriptor(window, Window.WindowStyleProperty));
            Assert.True(IsTrackedByDescriptor(window, Window.ResizeModeProperty));

            window.Close();

            Assert.False(IsTrackedByDescriptor(window, Window.WindowStyleProperty));
            Assert.False(IsTrackedByDescriptor(window, Window.ResizeModeProperty));
        });
    }

    private static LifecycleTestWindow CreateModernWindow()
    {
        var window = new LifecycleTestWindow
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            ShowInTaskbar = false,
            Content = new Grid()
        };
        window.Resources.MergedDictionaries.Add(new ThemeResources());
        window.Resources.MergedDictionaries.Add(new XamlControlsResources());
        WindowHelper.SetUseModernWindowStyle(window, true);

        window.Show();
        window.ApplyTemplate();
        window.UpdateLayout();

        Assert.NotNull(FindVisualChild<TitleBarControl>(window));
        return window;
    }

    private static bool IsTrackedByDescriptor(Window window, DependencyProperty property)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(Window));
        // Property 会按需初始化底层描述符；直接读取 _property 可能尚未初始化。
        var descriptorProperty = typeof(DependencyPropertyDescriptor)
            .GetProperty("Property", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var innerDescriptor = descriptorProperty.GetValue(descriptor)!;
        var trackersField = innerDescriptor.GetType()
            .GetField("_trackers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var trackers = (IDictionary)trackersField.GetValue(innerDescriptor)!;
        return trackers.Contains(window);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private static void RunOnStaThread(Action action)
        => WpfUiCollection.Run(action);

    private sealed class LifecycleTestWindow : Window
    {
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (!e.Cancel)
                ModernWindowLifecycle.DetachModernWindowStyle(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            ModernWindowLifecycle.DetachVisualTree(this);
            base.OnClosed(e);
        }
    }
}
