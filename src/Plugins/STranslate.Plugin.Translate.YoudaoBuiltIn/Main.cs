using STranslate.Plugin.Translate.YoudaoBuiltIn.View;
using STranslate.Plugin.Translate.YoudaoBuiltIn.ViewModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.YoudaoBuiltIn;

public class Main : TranslatePluginBase
{
    private const string KeyUrl = "https://dict-trans.youdao.com/translate/key";
    private const string SseUrl = "https://dict-trans.youdao.com/webtranslate/sse";
    private const string WebOrigin = "https://fanyi.youdao.com";
    private const string Product = "webfanyi";
    private const string KeyFrom = "webfanyi.webmain";
    private const string KeyGetterId = "translate-webmain-key-getter";
    private const string TranslateKeyId = "translate-webfanyi-webmain";
    private const string KeyGetterSecret = "kSy5gtKA4yRUxAVPJPrdYKZ0jBKyd3t1";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;
    private string YdUuid { get; } = Guid.NewGuid().ToString("N");

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel();
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh-CHS",
        LangEnum.ChineseTraditional => "zh-CHT",
        LangEnum.Cantonese => null,
        LangEnum.English => "en",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.Turkish => null,
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => "km",
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => null,
        LangEnum.Swedish => "sv",
        LangEnum.Polish => null,
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => null,
        LangEnum.Uzbek => null,
        _ => "auto"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh-CHS",
        LangEnum.ChineseTraditional => "zh-CHT",
        LangEnum.Cantonese => null,
        LangEnum.English => "en",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.Turkish => null,
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => "km",
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => null,
        LangEnum.Swedish => "sv",
        LangEnum.Polish => null,
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => null,
        LangEnum.Uzbek => null,
        _ => "auto"
    };

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
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

        if (targetStr == "auto")
            targetStr = IsProbablyChinese(request.Text) ? "en" : "zh-CHS";

        var auth = await GetAuthAsync(cancellationToken);
        var formData = CreateSseParams(request.Text, sourceStr, targetStr, auth);
        var response = await PostMultipartAsync(SseUrl, formData, cancellationToken);
        var translatedText = ParseSseTranslation(response);

        if (string.IsNullOrWhiteSpace(translatedText))
            throw new Exception(response);

        result.Success(translatedText);
    }

    private async Task<YoudaoAuth> GetAuthAsync(CancellationToken cancellationToken)
    {
        var keyParams = CreateKeyParams();
        var url = $"{KeyUrl}?{BuildQueryString(keyParams)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddCommonHeaders(request);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var jsonDoc = JsonDocument.Parse(responseText);
        var data = jsonDoc.RootElement.GetProperty("data");
        return new YoudaoAuth(
            data.GetProperty("secretKey").GetString() ?? throw new Exception(responseText),
            data.GetProperty("token").GetString() ?? throw new Exception(responseText));
    }

    private Dictionary<string, string> CreateKeyParams()
    {
        var parameters = new Dictionary<string, string>
        {
            ["product"] = Product,
            ["appVersion"] = "12.0.0",
            ["client"] = "webmain",
            ["mid"] = "1",
            ["vendor"] = "web",
            ["screen"] = "1",
            ["model"] = "1",
            ["imei"] = "1",
            ["network"] = "wifi",
            ["keyfrom"] = KeyFrom,
            ["keyid"] = KeyGetterId,
            ["mysticTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["yduuid"] = YdUuid,
            ["abtest"] = "0",
            ["targetKeyid"] = TranslateKeyId
        };

        AddSignedFields(parameters, KeyGetterSecret);
        return parameters;
    }

    private Dictionary<string, string> CreateSseParams(string text, string sourceLanguage, string targetLanguage, YoudaoAuth auth)
    {
        var parameters = new Dictionary<string, string>
        {
            ["product"] = Product,
            ["appVersion"] = "1",
            ["client"] = "webmain",
            ["mid"] = "1",
            ["vendor"] = "web",
            ["screen"] = "1",
            ["model"] = "1",
            ["imei"] = "1",
            ["network"] = "wifi",
            ["keyfrom"] = KeyFrom,
            ["keyid"] = TranslateKeyId,
            ["mysticTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["yduuid"] = YdUuid,
            ["modelName"] = "llmLite",
            ["useTerm"] = "false",
            ["i"] = Uri.EscapeDataString(text),
            ["from"] = sourceLanguage,
            ["to"] = targetLanguage,
            ["signSecretKey"] = auth.SecretKey,
            ["keyId"] = TranslateKeyId,
            ["token"] = auth.Token,
            ["source"] = "webmain"
        };

        AddSignedFields(parameters, auth.SecretKey);
        return parameters;
    }

    private static void AddSignedFields(Dictionary<string, string> parameters, string key)
    {
        var names = parameters
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

        names.Add("key");

        var rawSignText = string.Join("&", names.Select(name => $"{name}={(name == "key" ? key : parameters[name])}"));
        var signBytes = MD5.HashData(Encoding.UTF8.GetBytes(rawSignText));
        parameters["sign"] = string.Concat(signBytes.Select(item => item.ToString("x2")));
        parameters["pointParam"] = string.Join(",", names);
    }

    private static async Task<string> PostMultipartAsync(string url, Dictionary<string, string> formData, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent("----WebKitFormBoundary" + Guid.NewGuid().ToString("N")[..16]);
        foreach (var (key, value) in formData)
        {
            var stringContent = new StringContent(value, Encoding.UTF8);
            stringContent.Headers.ContentType = null;
            content.Add(stringContent, key);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        AddCommonHeaders(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return responseText;
    }

    private static string ParseSseTranslation(string response)
    {
        var builder = new StringBuilder();
        var blocks = response.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var eventName = "";
            var dataLines = new List<string>();

            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line["event:".Length..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                    dataLines.Add(line["data:".Length..]);
            }

            if (dataLines.Count == 0)
                continue;

            var data = string.Join("\n", dataLines);
            if (eventName == "message")
            {
                using var jsonDoc = JsonDocument.Parse(data);
                if (jsonDoc.RootElement.TryGetProperty("transIncre", out var transIncre))
                    builder.Append(transIncre.GetString());
            }
            else if (eventName == "error")
            {
                throw new Exception(data);
            }
        }

        return builder.ToString();
    }

    private static string BuildQueryString(Dictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    }

    private static void AddCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.Referrer = new Uri(WebOrigin);
        request.Headers.TryAddWithoutValidation("Origin", WebOrigin);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    private static bool IsProbablyChinese(string text)
    {
        return text.Any(ch => ch is >= '\u3400' and <= '\u9fff');
    }

    private sealed record YoudaoAuth(string SecretKey, string Token);
}
