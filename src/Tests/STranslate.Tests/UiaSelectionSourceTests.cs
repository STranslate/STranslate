using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using STranslate.Helpers;

namespace STranslate.Tests;

[Collection("WPF UI")]
public class UiaSelectionSourceTests
{
    private sealed record Node(string Name, Node? Parent = null, bool Text = false, bool OwnSelection = false);

    private static Node? FindSource(Node start) => UiaTextReader.FindSelectionSource(
        start, node => node.Parent, node => node.Text, node => node.OwnSelection);

    [Fact]
    public void WebContainerOrButtonUsesEnclosingDocument()
    {
        // Chromium 网页中焦点常停在帖子容器或按钮上，选区由外层 Document 提供。
        var document = new Node("Document", Text: true);
        var post = new Node("Group", new Node("Group", document));
        var button = new Node("Button", post);

        Assert.Same(document, FindSource(post));
        Assert.Same(document, FindSource(button));
    }

    [Fact]
    public void FocusedTextElementUsesItsOwnSelection()
    {
        var edit = new Node("Edit", new Node("Document", Text: true), Text: true);

        Assert.Same(edit, FindSource(edit));
    }

    [Fact]
    public void ControlWithOwnSelectionStopsLookup()
    {
        // 焦点在 RichTextBox 内嵌表格的单元格上时，复制的是表格选区，不能读取外层残留的文字选区。
        var richTextBox = new Node("RichTextBox", Text: true);
        var dataGrid = new Node("DataGrid", richTextBox, OwnSelection: true);
        var cell = new Node("Cell", new Node("Row", dataGrid));

        Assert.Null(FindSource(cell));
    }

    [Fact]
    public void NoTextPatternReturnsNull()
    {
        Assert.Null(FindSource(new Node("Button", new Node("Window"))));
    }

    [Fact]
    public void RichTextBoxSelectionIsReadFromEmbeddedButtonButNotFromDataGridCell()
    {
        Window? window = null;
        nint hwnd = 0;
        WpfUiCollection.Run(() =>
        {
            var paragraph = new Paragraph(new Run("outer selection"));
            var dataGrid = new DataGrid
            {
                ItemsSource = new[] { new { Name = "cell" } },
                HeadersVisibility = DataGridHeadersVisibility.Column
            };
            var richTextBox = new RichTextBox(new FlowDocument(paragraph))
            {
                IsDocumentEnabled = true
            };
            richTextBox.Document.Blocks.Add(new BlockUIContainer(new Button { Content = "button" }));
            richTextBox.Document.Blocks.Add(new BlockUIContainer(dataGrid));
            window = new Window
            {
                Content = richTextBox,
                Width = 400,
                Height = 300,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            window.Show();
            richTextBox.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
            hwnd = new WindowInteropHelper(window).Handle;
        });

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            // 只在 RichTextBox 内查找，避免找到窗口标题栏上的按钮。
            var document = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            Assert.NotNull(document);
            var button = document.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var row = document.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            Assert.NotNull(button);
            Assert.NotNull(row);
            var cell = TreeWalker.ControlViewWalker.GetFirstChild(row);
            Assert.NotNull(cell);

            Assert.Equal("outer selection", UiaTextReader.ReadSelection(button));
            Assert.Null(UiaTextReader.ReadSelection(cell));
        }
        finally
        {
            WpfUiCollection.Run(() => window?.Close());
        }
    }
}
