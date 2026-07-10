using STranslate.Plugin.Translate.DeepLBuiltIn.View;
using STranslate.Plugin.Translate.DeepLBuiltIn.ViewModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.DeepLBuiltIn;

public class Main : TranslatePluginBase
{
    private const string URL = "https://oneshot-free.www.deepl.com/v1/translate";
    private const string ExtensionOrigin = "chrome-extension://cofdbpoegempjloogbagkncekinflcnj";
    private const int CacheLimit = 128;

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
    private static readonly string InstanceId = Guid.NewGuid().ToString();
    private static int _preconnectStarted;

    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private readonly Dictionary<string, string> _cache = [];
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel();
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh-Hans",
        LangEnum.ChineseTraditional => "zh-Hant",
        LangEnum.Cantonese => null,
        LangEnum.English => "en",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.Turkish => "tr",
        LangEnum.PortuguesePortugal => "pt-PT",
        LangEnum.PortugueseBrazil => "pt-BR",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => null,
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => "mn",
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => "nb",
        LangEnum.NorwegianNynorsk => "nb",
        LangEnum.Persian => null,
        LangEnum.Swedish => "sv",
        LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => "uk",
        LangEnum.Uzbek => "uz",
        _ => "auto"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "zh-Hans",
        LangEnum.ChineseSimplified => "zh-Hans",
        LangEnum.ChineseTraditional => "zh-Hant",
        LangEnum.Cantonese => null,
        LangEnum.English => "en-US",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.Turkish => "tr",
        LangEnum.PortuguesePortugal => "pt-PT",
        LangEnum.PortugueseBrazil => "pt-BR",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => null,
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => "mn",
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => "nb",
        LangEnum.NorwegianNynorsk => "nb",
        LangEnum.Persian => null,
        LangEnum.Swedish => "sv",
        LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => "uk",
        LangEnum.Uzbek => "uz",
        _ => "zh-Hans"
    };

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
        _ = Task.Run(PreconnectAsync);
    }

    public override void Dispose() { }

    public override async Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
    {
        if (GetSourceLanguage(request.SourceLang) is not string sourceStr)
        {
            result.Fail(Context.GetTranslation("UnsupportedSourceLang"));
            return;
        }
        if (GetTargetLanguage(request.TargetLang) is not string targetStr)
        {
            result.Fail(Context.GetTranslation("UnsupportedTargetLang"));
            return;
        }

        var cacheKey = $"{sourceStr}\u001f{targetStr}\u001f{request.Text}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cachedText))
            {
                result.Success(cachedText);
                return;
            }
        }

        var response = await SendTranslateRequestAsync(request.Text, sourceStr, targetStr, cancellationToken);

        var jsonDoc = JsonDocument.Parse(response);
        var translatedText = jsonDoc.RootElement
            .GetProperty("translations")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(translatedText))
            throw new Exception(response);

        lock (_cache)
        {
            if (_cache.Count >= CacheLimit)
                _cache.Clear();

            _cache[cacheKey] = translatedText;
        }

        result.Success(translatedText);
    }

    private static async Task<string> SendTranslateRequestAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        var payload = new DeepLRequest(
            [text],
            sourceLanguage,
            targetLanguage,
            "Translate",
            new DeepLAppInformation(
                "brex_macOS",
                "brex_chrome_120.0.0.0",
                "1.86.0",
                "chrome_web_store",
                InstanceId));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, URL)
        {
            Content = new StringContent(json, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        return responseText;
    }

    private static async Task PreconnectAsync()
    {
        if (Interlocked.Exchange(ref _preconnectStarted, 1) != 0)
            return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://oneshot-free.www.deepl.com/");
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        catch
        {
            // Best-effort connection warm-up; translation requests still handle real errors.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "None");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", ExtensionOrigin);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        return client;
    }

    private sealed record DeepLRequest(
        string[] Text,
        string SourceLang,
        string TargetLang,
        string UsageType,
        DeepLAppInformation AppInformation);

    private sealed record DeepLAppInformation(
        string Os,
        string OsVersion,
        string AppVersion,
        string AppBuild,
        string InstanceId);
}
