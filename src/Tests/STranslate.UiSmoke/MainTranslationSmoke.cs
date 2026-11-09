using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using STranslate.Core;
using STranslate.Plugin;
using STranslate.Services;
using STranslate.ViewModels;
using STranslate.Views;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

internal static class MainTranslationSmoke
{
    internal static async Task RunAsync()
    {
        if (!DataLocation.DataDirectory().StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("冒烟数据库必须位于测试输出目录。");
        Directory.CreateDirectory(DataLocation.CacheDirectory);
        var settings = new Settings { AutoTranslate = false, SourceLang = LangEnum.Auto, TargetLang = LangEnum.Auto,
            LanguageDetector = LanguageDetectorType.Local, HistoryLimit = (HistoryLimit)100,
            CopyAfterTranslation = CopyAfterTranslation.NoAction, CopyAfterTranslationNotAutomatic = false,
            TextPreprocessing = Rules("public") };
        var serviceSettings = new ServiceSettings();
        serviceSettings.SetStorage(new NoOpStorage());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(settings);
        services.AddSingleton(serviceSettings);
        services.AddSingleton<HotkeySettings>();
        services.AddSingleton<PluginManager>();
        services.AddSingleton<ServiceManager>();
        services.AddSingleton<PluginService>();
        services.AddSingleton<Internationalization>();
        services.AddSingleton<DataProvider>();
        services.AddSingleton<TranslateService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<TtsService>();
        services.AddSingleton<VocabularyService>();
        services.AddSingleton<SqlService>();
        services.AddSingleton<TextPreprocessingService>();
        services.AddSingleton<MouseHookService>();
        services.AddSingleton<MouseSelectionService>();
        services.AddSingleton<MouseSelectionIconWindow>();
        services.AddSingleton<IHttpService>(DispatchProxy.Create<IHttpService, UnexpectedCall>());
        services.AddSingleton<IAudioPlayer>(DispatchProxy.Create<IAudioPlayer, UnexpectedCall>());
        services.AddSingleton<IScreenshot>(DispatchProxy.Create<IScreenshot, UnexpectedCall>());
        services.AddSingleton<INotification>(DispatchProxy.Create<INotification, UnexpectedCall>());
        services.AddSingleton<ISnackbar, RecordingSnackbar>();
        services.AddSingleton<MainWindowViewModel>();
        using var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);
        var sql = provider.GetRequiredService<SqlService>();
        await sql.InitializeDBAsync();
        await sql.DeleteAllDataAsync();
        var translation = provider.GetRequiredService<TranslateService>();
        var first = new FakeTranslation();
        var second = new FakeTranslation();
        var dictionary = new FakeDictionary();
        var firstService = Service("first", first);
        translation.Services.Add(firstService);
        translation.Services.Add(Service("second", second));
        translation.Services.Add(Service("dictionary", dictionary));
        var main = provider.GetRequiredService<MainWindowViewModel>();
        main.InputText = "secret";
        await main.TranslateCommand.ExecuteAsync(null);
        Require(first.Requests.Single().Text == "public" && second.Requests.Single().Text == "public", "Main providers bypassed preprocessing");
        Require(dictionary.Requests.Single() == "public", "Dictionary bypassed preprocessing");
        Require(main.InputText == "secret" && main.HasReplacements, "Main input or replacement indicator mismatch");
        var cached = await sql.GetDataAsync("secret", "Auto", "Auto");
        Require(cached?.Data.Count == 3 && cached.Data.All(data => data.RequestText == "public"), "History request metadata mismatch");
        await main.TranslateCommand.ExecuteAsync(null);
        Require(first.Requests.Count == 1 && second.Requests.Count == 1, "Compatible cache was not reused");
        settings.TextPreprocessing = Rules("公开");
        await main.TranslateCommand.ExecuteAsync(null);
        Require(first.Requests.Last().Text == "公开" && second.Requests.Last().Text == "公开", "Changed rules reused stale cache");
        Require(first.Requests.Last().SourceLang == LangEnum.ChineseSimplified, "Language detector did not see preprocessed text");
        await main.SingleTranslateCommand.ExecuteAsync(firstService);
        Require(first.Requests.Last().Text == "公开", "Single-service retry used the wrong input");
        var primaryText = first.TransResult.Text;
        settings.TextPreprocessing = new() { IsEnabled = true, Rules = [new() { Pattern = "(", Replacement = "invalid" }] };
        await main.SingleTransBackCommand.ExecuteAsync(firstService);
        Require(first.Requests.Last().Text == primaryText, "Manual back translation reapplied current rules");
        var count = first.Requests.Count + second.Requests.Count + dictionary.Requests.Count;
        await main.TranslateCommand.ExecuteAsync(null);
        Require(count == first.Requests.Count + second.Requests.Count + dictionary.Requests.Count, "Invalid preprocessing reached a provider");
        settings.TextPreprocessing = Rules("");
        await main.TranslateCommand.ExecuteAsync(null);
        Require(count == first.Requests.Count + second.Requests.Count + dictionary.Requests.Count, "Empty preprocessing reached a provider");
        Require(!main.HasReplacements && string.IsNullOrEmpty(first.TransResult.Text), "Failed request retained old visible results");
        Require(!provider.GetRequiredService<MouseHookService>().IsRunning, "Smoke test activated a global mouse hook");
        Console.WriteLine("PASS: main translation, streaming providers, dictionary, detection, cache, rule changes, single retry, bound back translation, invalid/empty input and SQL history.");
    }

    private static TextPreprocessingSettings Rules(string replacement) => new()
        { IsEnabled = true, Rules = [new() { Pattern = "secret", Replacement = replacement }] };
    private static Service Service(string id, IPlugin plugin) => new()
        { ServiceID = id, DisplayName = id, IsEnabled = true, Plugin = plugin,
          MetaData = new() { PluginID = id, Name = id }, Options = new() { ExecMode = ExecutionMode.Automatic } };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public class UnexpectedCall : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Unexpected external call: " + targetMethod?.Name);
    }

    private sealed class NoOpStorage : AppStorage<ServiceSettings>
    {
        public override void Save() { }
        public override Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class RecordingSnackbar : ISnackbar
    {
        public void Show(string message, Severity severity = Severity.Informational, int durationMs = 3000, string? actionText = null, Action? actionCallback = null) { }
        public void ShowSuccess(string message, int durationMs = 3000) { }
        public void ShowWarning(string message, int durationMs = 3000) { }
        public void ShowError(string message, int durationMs = 4000) { }
        public void ShowInfo(string message, int durationMs = 3000) { }
    }

    private sealed class FakeTranslation : TranslatePluginBase
    {
        internal ConcurrentQueue<TranslateRequest> Requests { get; } = new();
        public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            result.Text = "partial";
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            result.Success("translated: " + request.Text);
        }
        public override string? GetSourceLanguage(LangEnum language) => language.ToString();
        public override string? GetTargetLanguage(LangEnum language) => language.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }

    private sealed class FakeDictionary : DictionaryPluginBase
    {
        internal ConcurrentQueue<string> Requests { get; } = new();
        public override Task TranslateAsync(string text, DictionaryResult result, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(text);
            result.Text = "dictionary: " + text;
            result.ResultType = DictionaryResultType.Success;
            return Task.CompletedTask;
        }
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }
}
