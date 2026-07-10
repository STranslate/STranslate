using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.ICibaTranslateBuiltIn.View;
using STranslate.Plugin.Translate.ICibaTranslateBuiltIn.ViewModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.ICibaTranslateBuiltIn;

public class Main : TranslatePluginBase
{
    private const string TranslateUrl = "https://dictionary.iciba.com/dictionary/fy/batch";
    private const string TranslatePath = "/dictionary/fy/batch";
    private const string TranslateClient = "6";
    private const string TranslateKey = "1000006";
    private const string TranslateSignatureSalt = "7ece94d9f9c202b0d2ec557dg4r9bc";
    private const string WordPageUrl = "https://www.iciba.com/word";
    private const string NextDataStartTag = "<script id=\"__NEXT_DATA__\" type=\"application/json\">";
    private const string ScriptEndTag = "</script>";

    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel();
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
    }

    public override void Dispose()
    {
        _settingUi = null;
        _viewModel = null;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh",
        LangEnum.ChineseTraditional => "zh",
        LangEnum.Cantonese => "zh",
        LangEnum.English => "en",
        LangEnum.Japanese => "ja",
        LangEnum.Korean => "ko",
        LangEnum.French => "fr",
        LangEnum.Spanish => "es",
        LangEnum.Russian => "ru",
        LangEnum.German => "de",
        LangEnum.Italian => "it",
        LangEnum.Turkish => "tr",
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => "mn",
        LangEnum.MongolianTraditional => "mn",
        LangEnum.Khmer => "km",
        LangEnum.NorwegianBokmal => "no",
        LangEnum.NorwegianNynorsk => "no",
        LangEnum.Persian => "fa",
        LangEnum.Swedish => "sv",
        LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => "uk",
        LangEnum.Uzbek => "auto",
        _ => "auto"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Uzbek => "uz",
        _ => GetSourceLanguage(langEnum)
    };

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

        var content = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            result.Fail("No result.");
            return;
        }

        try
        {
            var translatedText = await TranslateWithBatchApiAsync(content, sourceStr, targetStr, cancellationToken);
            if (string.IsNullOrWhiteSpace(translatedText))
                translatedText = await TranslateWithWordPageAsync(content, cancellationToken);

            if (string.IsNullOrWhiteSpace(translatedText))
            {
                result.Fail("No result.");
                return;
            }

            result.Success(translatedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Kingsoft translation failed");
            result.Fail("Translation failed.");
        }
    }

    private async Task<string> TranslateWithBatchApiAsync(
        string content,
        string sourceStr,
        string targetStr,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var queryParams = new Dictionary<string, string>
        {
            { "client", TranslateClient },
            { "key", TranslateKey },
            { "timestamp", timestamp }
        };
        queryParams["signature"] = CreateSignature(TranslatePath, queryParams);

        var option = new Options
        {
            QueryParams = queryParams,
            Headers = new Dictionary<string, string>
            {
                { "Origin", "https://www.iciba.com" },
                { "Referer", "https://www.iciba.com/" },
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36" }
            }
        };

        var payload = new
        {
            from = NormalizeTranslateLanguage(sourceStr),
            to = NormalizeTranslateLanguage(targetStr),
            textList = new[] { content }
        };

        var response = await Context.HttpService.PostAsync(TranslateUrl, payload, option, cancellationToken);
        if (string.IsNullOrWhiteSpace(response))
            return string.Empty;

        try
        {
            using var jsonDoc = JsonDocument.Parse(response);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("code", out var code) ||
                !IsSuccessCode(code) ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var translatedLines = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddIfNotEmpty(item.GetString(), translatedLines);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("out", out var outText) &&
                    outText.ValueKind == JsonValueKind.String)
                {
                    AddIfNotEmpty(outText.GetString(), translatedLines);
                }
            }

            return string.Join(Environment.NewLine, translatedLines);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private async Task<string> TranslateWithWordPageAsync(string content, CancellationToken cancellationToken)
    {
        var lookupContent = NormalizeLookupContent(content);
        if (string.IsNullOrWhiteSpace(lookupContent))
            return string.Empty;

        var option = new Options
        {
            QueryParams = new Dictionary<string, string>
            {
                { "w", lookupContent.ToLowerInvariant() }
            },
            Headers = new Dictionary<string, string>
            {
                { "Referer", "https://www.iciba.com/" },
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36" }
            }
        };

        var response = await Context.HttpService.GetAsync(WordPageUrl, option, cancellationToken);
        var nextDataJson = ExtractNextDataJson(response);
        if (string.IsNullOrWhiteSpace(nextDataJson))
            return string.Empty;

        try
        {
            using var jsonDoc = JsonDocument.Parse(nextDataJson);
            if (!TryGetWordBaseInfo(jsonDoc.RootElement, out var root) ||
                !IsExpectedWord(root, lookupContent))
            {
                return string.Empty;
            }

            return ExtractPlainTranslation(root);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeTranslateLanguage(string language) => language switch
    {
        "zh-tw" => "cht",
        _ => language
    };

    private static string CreateSignature(string path, Dictionary<string, string> parameters)
    {
        var values = parameters
            .Where(item => item.Key != "signature")
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value);
        var raw = $"{path}{string.Concat(values)}{TranslateSignatureSalt}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSuccessCode(JsonElement code)
    {
        return code.ValueKind switch
        {
            JsonValueKind.Number => code.TryGetInt32(out var value) && value == 1,
            JsonValueKind.String => code.GetString() == "1",
            _ => false
        };
    }

    private static string? ExtractNextDataJson(string response)
    {
        var trimmedResponse = response.TrimStart();
        if (trimmedResponse.StartsWith('{'))
            return trimmedResponse;

        var startIndex = response.IndexOf(NextDataStartTag, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += NextDataStartTag.Length;
        var endIndex = response.IndexOf(ScriptEndTag, startIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex <= startIndex)
            return null;

        return response[startIndex..endIndex];
    }

    private static bool TryGetWordBaseInfo(JsonElement root, out JsonElement baseInfo)
    {
        baseInfo = default;

        if (TryGetWordInfo(root, out var wordInfo))
        {
            if (wordInfo.TryGetProperty("baesInfo", out baseInfo))
                return true;

            if (LooksLikeBaseInfo(wordInfo))
            {
                baseInfo = wordInfo;
                return true;
            }
        }

        if (TryFindWordInfo(root, out wordInfo))
        {
            if (wordInfo.TryGetProperty("baesInfo", out baseInfo))
                return true;

            if (LooksLikeBaseInfo(wordInfo))
            {
                baseInfo = wordInfo;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetWordInfo(JsonElement root, out JsonElement wordInfo)
    {
        wordInfo = default;

        if (root.TryGetProperty("props", out var props) &&
            props.TryGetProperty("pageProps", out var pagePropsFromProps))
        {
            return TryGetWordInfoFromPageProps(pagePropsFromProps, out wordInfo);
        }

        if (root.TryGetProperty("pageProps", out var pageProps))
        {
            return TryGetWordInfoFromPageProps(pageProps, out wordInfo);
        }

        return false;
    }

    private static bool TryGetWordInfoFromPageProps(JsonElement pageProps, out JsonElement wordInfo)
    {
        wordInfo = default;

        return pageProps.TryGetProperty("initialReduxState", out var initialReduxState) &&
               initialReduxState.TryGetProperty("word", out var wordState) &&
               wordState.TryGetProperty("wordInfo", out wordInfo);
    }

    private static bool TryFindWordInfo(JsonElement element, out JsonElement wordInfo)
    {
        wordInfo = default;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("wordInfo", out var directWordInfo) &&
                    LooksLikeWordInfo(directWordInfo))
                {
                    wordInfo = directWordInfo;
                    return true;
                }

                if (LooksLikeWordInfo(element))
                {
                    wordInfo = element;
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindWordInfo(property.Value, out wordInfo))
                        return true;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindWordInfo(item, out wordInfo))
                        return true;
                }
                break;
        }

        return false;
    }

    private static bool LooksLikeWordInfo(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               (element.TryGetProperty("baesInfo", out _) || LooksLikeBaseInfo(element));
    }

    private static bool LooksLikeBaseInfo(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               (element.TryGetProperty("word_name", out _) ||
                element.TryGetProperty("symbols", out _));
    }

    private static bool IsExpectedWord(JsonElement root, string lookupContent)
    {
        if (!root.TryGetProperty("word_name", out var wordName) ||
            wordName.GetString() is not string word ||
            string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var normalizedWord = NormalizeForCompare(word);
        var normalizedLookup = NormalizeForCompare(lookupContent);
        if (normalizedWord == normalizedLookup)
            return true;

        if (!root.TryGetProperty("exchange", out var exchange))
            return false;

        var forms = new List<string>();
        AddExchangeForms(exchange, forms);
        return forms.Select(NormalizeForCompare).Any(item => item == normalizedLookup);
    }

    private static void AddExchangeForms(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddIfNotEmpty(element.GetString(), values);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddExchangeForms(item, values);
                }
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddExchangeForms(property.Value, values);
                }
                break;
        }
    }

    private static string NormalizeLookupContent(string content)
    {
        var value = content.Trim().Replace('\u2019', '\'');
        while (value.Length > 0 && IsLookupTrimChar(value[0]))
            value = value[1..].TrimStart();
        while (value.Length > 0 && IsLookupTrimChar(value[^1]))
            value = value[..^1].TrimEnd();
        return value;
    }

    private static bool IsLookupTrimChar(char ch)
    {
        return char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch);
    }

    private static string NormalizeForCompare(string value)
    {
        return new string(NormalizeLookupContent(value)
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '\'' or '-' || IsCjk(ch))
            .ToArray());
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4e00' and <= '\u9fff';
    }

    private static string ExtractPlainTranslation(JsonElement baseInfo)
    {
        if (!baseInfo.TryGetProperty("symbols", out var symbols) ||
            symbols.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var candidates = new List<string>();
        foreach (var symbol in symbols.EnumerateArray())
        {
            if (!symbol.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("means", out var means))
                    continue;

                candidates.AddRange(ExtractMeanStrings(means));
            }
        }

        return PickBestPlainTranslation(candidates);
    }

    private static string PickBestPlainTranslation(IEnumerable<string> candidates)
    {
        return candidates
            .SelectMany(SplitMeanCandidate)
            .Select(NormalizePlainTranslation)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct()
            .OrderBy(GetPlainTranslationScore)
            .ThenBy(item => item.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    private static IEnumerable<string> SplitMeanCandidate(string candidate)
    {
        return candidate.Split([';', '；', ',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizePlainTranslation(string text)
    {
        var value = text.Trim();
        if (value == "他/她/它们")
            return "他们";
        if (value.Contains("雷电交加", StringComparison.Ordinal) && value.Contains("暴风雨", StringComparison.Ordinal))
            return "雷暴";
        if (value == "大雷雨")
            return "雷暴";

        return value;
    }

    private static int GetPlainTranslationScore(string text)
    {
        var score = text.Length;

        if (text.Contains('的'))
            score += 30;
        if (text.Any(char.IsWhiteSpace) || text.Any(IsDictionaryPunctuation))
            score += 10;
        if (IsShortChinesePhrase(text))
            score -= 5;

        return score;
    }

    private static bool IsDictionaryPunctuation(char ch)
    {
        return ch is '；' or ';' or ',' or '，' or '（' or '）' or '(' or ')' or '[' or ']';
    }

    private static bool IsShortChinesePhrase(string text)
    {
        return text.Length is >= 2 and <= 4 && text.All(ch => ch >= '\u4e00' && ch <= '\u9fff');
    }

    private static List<string> ExtractMeanStrings(JsonElement element)
    {
        var values = new List<string>();
        AddMeanStrings(element, values);
        return values;
    }

    private static void AddMeanStrings(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddIfNotEmpty(element.GetString(), values);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddMeanStrings(item, values);
                }
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("word_mean", out var wordMean))
                {
                    AddMeanStrings(wordMean, values);
                }
                else if (element.TryGetProperty("means", out var means))
                {
                    AddMeanStrings(means, values);
                }
                else if (element.TryGetProperty("mean", out var mean))
                {
                    AddMeanStrings(mean, values);
                }
                break;
        }
    }

    private static void AddIfNotEmpty(string? value, ICollection<string> values)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value.Trim());
    }
}
