using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STranslate.Controls;
using STranslate.Core;
using STranslate.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".artifacts/preprocessing-ui-smoke");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, Constant.PortableFolderName));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                System.Reflection.Assembly.Load("iNKORE.UI.WPF.Modern");
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
                    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:ui="http://schemas.inkore.net/lib/ui/wpf/modern">
                        <ResourceDictionary.MergedDictionaries>
                            <ui:ThemeResources AccentColor="#0078D4" RequestedTheme="Light" />
                            <ui:XamlControlsResources />
                            <ResourceDictionary Source="pack://application:,,,/XamlFlair.WPF;component/DefaultAnimations.xaml" />
                            <ResourceDictionary Source="pack://application:,,,/STranslate;component/Resources/CustomStyles.xaml" />
                            <ResourceDictionary Source="pack://application:,,,/STranslate;component/Themes/Generic.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                    """));
                var trace = new BindingErrorListener();
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
                foreach (var language in new[] { "zh-cn", "en" })
                {
                    var resources = new ResourceDictionary { Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Languages", language + ".xaml")) };
                    app.Resources.MergedDictionaries.Add(resources);
                    foreach (var size in new[] { new Size(1000, 780), new Size(700, 600) })
                        await VerifyDialogAsync(language, size, output);
                    app.Resources.MergedDictionaries.Remove(resources);
                }
                await VerifyStorageAsync(output);
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Languages", "en.xaml")) });
                await MainTranslationSmoke.RunAsync();
                if (trace.Errors.Count > 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, trace.Errors));
                Console.WriteLine("PASS: Chinese/English dialogs, owner, draft/apply/cancel, preview, request details, layout pixels, Settings restart and ZIP/backup recovery.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exitCode;
    }

    private static async Task VerifyDialogAsync(string language, Size size, string output)
    {
        var owner = new Window { Width = size.Width, Height = size.Height, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, Content = new Grid() };
        iNKORE.UI.WPF.Modern.ThemeManager.SetRequestedTheme(owner, iNKORE.UI.WPF.Modern.ElementTheme.Light);
        owner.Show();
        try
        {
            var settings = new Settings { TextPreprocessing = new() { IsEnabled = true, Rules =
                [new() { Name = "Product identifiers", Pattern = @"\bACME-\d+\b", Replacement = "Product A" },
                 new() { Name = "A deliberately long rule name to verify list clipping", Pattern = "today", Replacement = "this week" }] } };
            var original = settings.TextPreprocessing;
            using var editor = new TextPreprocessingEditViewModel(settings, new(), Translate);
            var dialog = new TextPreprocessingDialog(editor);
            var shown = dialog.ShowAsync(owner);
            await Task.Delay(400);
            Require(editor.PreviewText == "Please review Product A this week.", "Preview mismatch");
            var boxes = Descendants(dialog).OfType<TextBox>().ToArray();
            foreach (var label in Descendants(dialog).OfType<TextBlock>().Where(label => label.Text.Contains("deliberately")))
                Require(label.ActualHeight < 30, "Rule name changed the row height");
            var primary = Descendants(dialog).OfType<Button>().First(button => button.Name == "PrimaryButton");
            Require(primary.Background is SolidColorBrush { Color.A: > 0 }, "Primary button accent is missing");
            Require(boxes.Any(box => box.Text == editor.PreviewText), "Preview binding missing");
            Require(boxes.Any(box => box.Text == original.Rules[0].Pattern), "Rule editor binding missing");
            Require(dialog.ActualWidth <= owner.ActualWidth && dialog.ActualHeight <= owner.ActualHeight, "Dialog exceeds owner");
            Capture(dialog, Path.Combine(output, $"{language}-{size.Width}x{size.Height}.png"));
            editor.Rules[0].Replacement = "Discarded";
            Require(ReferenceEquals(original, settings.TextPreprocessing), "Draft leaked into settings");
            dialog.Hide();
            await shown;
            Require(settings.TextPreprocessing.Rules[0].Replacement == "Product A", "Cancel changed saved configuration");

            using var savedEditor = new TextPreprocessingEditViewModel(settings, new(), Translate);
            var saveDialog = new TextPreprocessingDialog(savedEditor);
            var saveShown = saveDialog.ShowAsync(owner);
            await Task.Delay(200);
            savedEditor.Rules[0].Replacement = "Saved";
            var saveButton = Descendants(saveDialog).OfType<Button>().First(button => button.Name == "PrimaryButton");
            saveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await saveShown.WaitAsync(TimeSpan.FromSeconds(5));
            Require(settings.TextPreprocessing.Rules[0].Replacement == "Saved", "Save button did not apply draft");

            var request = new TranslationRequestDialog("Please review Product A this week.", "English > Chinese");
            var requestShown = request.ShowAsync(owner);
            await Task.Delay(200);
            Require(Descendants(request).OfType<TextBox>().Any(box => box.Text == "Please review Product A this week."), "Request details binding missing");
            Capture(request, Path.Combine(output, $"request-{language}-{size.Width}x{size.Height}.png"));
            request.Hide();
            await requestShown;
        }
        finally { owner.Close(); }
    }

    private static async Task VerifyStorageAsync(string output)
    {
        Application.Current.MainWindow = null;
        var settingsDirectory = Path.Combine(output, "settings");
        Directory.CreateDirectory(settingsDirectory);
        var file = Path.Combine(settingsDirectory, "Settings.json");
        var storage = new StorageBase<Settings>(file);
        var settings = storage.Load();
        settings.TextPreprocessing = new() { IsEnabled = true, Rules =
            [new() { Id = "first", Pattern = "(secret)", Replacement = "$1\nmasked", IgnoreCase = true },
             new() { Id = "second", Pattern = "secret", Replacement = "", IsEnabled = false }] };
        storage.Save();
        storage.Save();
        var expected = JsonSerializer.Serialize(settings.TextPreprocessing);
        Require(JsonSerializer.Serialize(new StorageBase<Settings>(file).Load().TextPreprocessing) == expected, "Settings restart mismatch");
        var archive = Path.Combine(output, "backup-" + Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(settingsDirectory, archive);
        var restore = Path.Combine(output, "restored-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(archive, restore);
        Require(JsonSerializer.Serialize(new StorageBase<Settings>(Path.Combine(restore, "Settings.json")).Load().TextPreprocessing) == expected, "ZIP restore mismatch");
        await File.WriteAllTextAsync(file, "{broken");
        Require(JsonSerializer.Serialize(new StorageBase<Settings>(file).Load().TextPreprocessing) == expected, "Backup recovery mismatch");
    }

    private static string Translate(string key) => Application.Current.TryFindResource(key) as string ?? key;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(FrameworkElement element, string file)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        Require(width > 200 && height > 100, "Dialog was not laid out");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Require(pixels.Where((_, index) => index % 4 == 3).Count(value => value != 0) > width * height / 4, "Dialog pixels are blank");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        internal List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message is not null) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
