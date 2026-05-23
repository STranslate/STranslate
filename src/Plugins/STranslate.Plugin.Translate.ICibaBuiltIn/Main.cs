using STranslate.Plugin.Translate.ICibaBuiltIn.View;
using STranslate.Plugin.Translate.ICibaBuiltIn.ViewModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.ICibaBuiltIn;

public class Main : DictionaryPluginBase
{
    private const string WordPageUrl = "https://www.iciba.com/word";
    private const string ScriptStartTag = "<script";
    private const string ScriptEndTag = "</script>";
    private const string NextDataScriptId = "__NEXT_DATA__";

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

    public override void Dispose() { }

    public override async Task TranslateAsync(string content, DictionaryResult result, CancellationToken cancellationToken = default)
    {
        content = content.Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        var option = new Options
        {
            QueryParams = new Dictionary<string, string>
            {
                { "w", content.ToLowerInvariant() }
            }
        };

        var response = await Context.HttpService.GetAsync(WordPageUrl, option, cancellationToken);
        var nextDataJson = ExtractNextDataJson(response);
        if (string.IsNullOrWhiteSpace(nextDataJson))
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        using var jsonDoc = ParseNextDataJson(nextDataJson, content);
        if (jsonDoc is null)
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        if (!TryGetWordInfo(jsonDoc.RootElement, out var wordInfo) ||
            !wordInfo.TryGetProperty("baesInfo", out var root))
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        if (!root.TryGetProperty("word_name", out var wordName) ||
            wordName.GetString() is not string word ||
            string.IsNullOrWhiteSpace(word))
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        // 校验 iciba 返回的单词是否与用户输入一致
        // 当 iciba 无法识别输入时，会重定向到默认页（如"在线翻译"），需过滤此类结果
        if (!word.Equals(content, StringComparison.OrdinalIgnoreCase) &&
            !word.Equals(content.ToLowerInvariant(), StringComparison.Ordinal))
        {
            result.ResultType = DictionaryResultType.NoResult;
            return;
        }

        result.Text = word;
        result.ResultType = DictionaryResultType.Success;

        if (!root.TryGetProperty("symbols", out var symbols) ||
            symbols.ValueKind != JsonValueKind.Array ||
            symbols.GetArrayLength() == 0)
        {
            return;
        }

        var firstSymbol = symbols[0];
        if (Util.IsChinese(content))
        {
            ProcessChineseContent(firstSymbol, result);
            return;
        }

        ProcessEnglishContent(firstSymbol, result);
        ProcessWordExchange(root, result);
    }

    private static string? ExtractNextDataJson(string response)
    {
        var trimmedResponse = response.TrimStart();
        if (IsJsonPayload(trimmedResponse))
            return trimmedResponse;

        var searchIndex = 0;
        while (searchIndex < response.Length)
        {
            var scriptStartIndex = response.IndexOf(ScriptStartTag, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (scriptStartIndex < 0)
                return null;

            var startTagEndIndex = response.IndexOf('>', scriptStartIndex);
            if (startTagEndIndex < 0)
                return null;

            var startTag = response[scriptStartIndex..startTagEndIndex];
            if (!startTag.Contains(NextDataScriptId, StringComparison.OrdinalIgnoreCase))
            {
                searchIndex = startTagEndIndex + 1;
                continue;
            }

            var contentStartIndex = startTagEndIndex + 1;
            var scriptEndIndex = response.IndexOf(ScriptEndTag, contentStartIndex, StringComparison.OrdinalIgnoreCase);
            if (scriptEndIndex <= contentStartIndex)
                return null;

            var scriptContent = response[contentStartIndex..scriptEndIndex].Trim();
            return IsJsonPayload(scriptContent) ? scriptContent : null;
        }

        return null;
    }

    private JsonDocument? ParseNextDataJson(string nextDataJson, string content)
    {
        try
        {
            return JsonDocument.Parse(nextDataJson);
        }
        catch (JsonException ex)
        {
            Context.Logger.LogWarning(ex, "Failed to parse iCIBA __NEXT_DATA__ for content: {Content}", content);
            return null;
        }
    }

    private static bool IsJsonPayload(string value)
    {
        var trimmedValue = value.TrimStart();
        return trimmedValue.StartsWith('{') || trimmedValue.StartsWith('[');
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

    /// <summary>
    /// 处理中文内容的音标和释义
    /// </summary>
    /// <param name="firstSymbol">符号元素</param>
    /// <param name="result">字典结果</param>
    private void ProcessChineseContent(JsonElement firstSymbol, DictionaryResult result)
    {
        // 中文拼音
        if (firstSymbol.TryGetProperty("word_symbol", out var phZh))
        {
            var symbolZh = new Symbol
            {
                Label = "zh",
                Phonetic = phZh.GetString() ?? string.Empty,
                AudioUrl = firstSymbol.TryGetProperty("symbol_mp3", out var phZhMp3)
                    ? phZhMp3.GetString() ?? string.Empty
                    : string.Empty
            };
            if (!string.IsNullOrWhiteSpace(symbolZh.Phonetic))
                result.Symbols.Add(symbolZh);
        }

        // 处理中文释义（结构与英文不同）
        if (!firstSymbol.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("means", out var means))
                continue;

            if (TryAddClassifiedChineseMeans(means, result))
                continue;

            var partOfSpeech = GetStringProperty(part, "part") ??
                               GetStringProperty(part, "part_name") ??
                               Context.GetTranslation("Paraphrase");
            var meanValues = ExtractMeanStrings(means);
            if (meanValues.Count == 0)
                continue;

            result.DictMeans.Add(new DictMean
            {
                PartOfSpeech = string.IsNullOrWhiteSpace(partOfSpeech)
                    ? Context.GetTranslation("Paraphrase")
                    : partOfSpeech,
                Means = new ObservableCollection<string>(meanValues)
            });
        }
    }

    private bool TryAddClassifiedChineseMeans(JsonElement means, DictionaryResult result)
    {
        if (means.ValueKind != JsonValueKind.Array)
            return false;

        var hasClassifiedMeans = false;
        var validMeans = new List<string>();
        var invalidMeans = new List<string>();

        foreach (var mean in means.EnumerateArray())
        {
            if (mean.ValueKind != JsonValueKind.Object ||
                !mean.TryGetProperty("word_mean", out var wordMean))
            {
                continue;
            }

            var meaning = wordMean.GetString();
            if (string.IsNullOrEmpty(meaning))
                continue;

            hasClassifiedMeans = true;

            // 根据 has_mean 字段判断分类
            if (mean.TryGetProperty("has_mean", out var hasMean))
            {
                var isValid = GetHasMeanValue(hasMean);
                if (isValid.HasValue)
                {
                    if (isValid.Value)
                    {
                        validMeans.Add(meaning);
                    }
                    else
                    {
                        invalidMeans.Add(meaning);
                    }
                }
                // 如果 has_mean 存在但值无效，跳过该条目
            }
            else
            {
                // 如果没有 has_mean 字段，默认归为有效释义
                validMeans.Add(meaning);
            }
        }

        if (!hasClassifiedMeans)
            return false;

        // 添加有效释义
        if (validMeans.Count > 0)
        {
            var validDictMean = new DictMean
            {
                PartOfSpeech = Context.GetTranslation("Paraphrase"),
                Means = new ObservableCollection<string>(validMeans)
            };
            result.DictMeans.Add(validDictMean);
        }

        // 添加扩展释义（如电影名等）
        if (invalidMeans.Count > 0)
        {
            var invalidDictMean = new DictMean
            {
                PartOfSpeech = Context.GetTranslation("Expand"),
                Means = new ObservableCollection<string>(invalidMeans)
            };
            result.DictMeans.Add(invalidDictMean);
        }

        return true;
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
        {
            values.Add(value);
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    /// <summary>
    /// 处理英文内容的音标和释义
    /// </summary>
    /// <param name="firstSymbol">符号元素</param>
    /// <param name="result">字典结果</param>
    private void ProcessEnglishContent(JsonElement firstSymbol, DictionaryResult result)
    {
        // 美式发音
        if (firstSymbol.TryGetProperty("ph_am", out var phAm) &&
            !string.IsNullOrEmpty(phAm.GetString()))
        {
            var symbolAm = new Symbol
            {
                Label = "us",
                Phonetic = phAm.GetString() ?? string.Empty,
                AudioUrl = GetFirstStringProperty(firstSymbol, "ph_am_mp3", "ph_tts_mp3")
            };
            result.Symbols.Add(symbolAm);
        }

        // 英文词性和释义
        if (firstSymbol.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("part", out var partOfSpeech) ||
                    !part.TryGetProperty("means", out var means))
                    continue;

                var meanValues = ExtractMeanStrings(means);
                if (meanValues.Count == 0)
                    continue;

                var dictMean = new DictMean
                {
                    PartOfSpeech = partOfSpeech.GetString() ?? string.Empty,
                    Means = new ObservableCollection<string>(meanValues)
                };

                result.DictMeans.Add(dictMean);
            }
        }
    }

    private static string GetFirstStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetStringProperty(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    /// <summary>
    /// 处理词汇变形信息
    /// </summary>
    /// <param name="root">JSON根元素</param>
    /// <param name="result">字典结果</param>
    private void ProcessWordExchange(JsonElement root, DictionaryResult result)
    {
        if (!root.TryGetProperty("exchange", out var exchange))
            return;

        // 复数形式
        if (exchange.TryGetProperty("word_pl", out var wordPl))
        {
            AddWordForms(wordPl, result.Plurals);
        }

        // 过去式
        if (exchange.TryGetProperty("word_past", out var wordPast))
        {
            AddWordForms(wordPast, result.PastTense);
        }

        // 过去分词
        if (exchange.TryGetProperty("word_done", out var wordDone))
        {
            AddWordForms(wordDone, result.PastParticiple);
        }

        // 现在分词/动名词
        if (exchange.TryGetProperty("word_ing", out var wordIng))
        {
            AddWordForms(wordIng, result.PresentParticiple);
        }

        // 第三人称单数
        if (exchange.TryGetProperty("word_third", out var wordThird))
        {
            AddWordForms(wordThird, result.ThirdPersonSingular);
        }

        // 比较级
        if (exchange.TryGetProperty("word_er", out var wordEr))
        {
            AddWordForms(wordEr, result.Comparative);
        }

        // 最高级
        if (exchange.TryGetProperty("word_est", out var wordEst))
        {
            AddWordForms(wordEst, result.Superlative);
        }
    }

    /// <summary>
    /// 添加词汇变形到指定集合中
    /// </summary>
    /// <param name="jsonElement">JSON元素</param>
    /// <param name="collection">目标集合</param>
    private static void AddWordForms(JsonElement jsonElement, ObservableCollection<string> collection)
    {
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in jsonElement.EnumerateArray())
                {
                    var word = item.GetString();
                    if (!string.IsNullOrEmpty(word))
                    {
                        collection.Add(word);
                    }
                }
                break;
            case JsonValueKind.String:
                var singleWord = jsonElement.GetString();
                if (!string.IsNullOrEmpty(singleWord))
                {
                    collection.Add(singleWord);
                }
                break;
        }
    }

    /// <summary>
    /// 解析 has_mean 字段的值
    /// </summary>
    /// <param name="hasMean">has_mean 的 JsonElement</param>
    /// <returns>如果是有效值返回 bool，如果是无效值返回 null</returns>
    private static bool? GetHasMeanValue(JsonElement hasMean)
    {
        return hasMean.ValueKind switch
        {
            JsonValueKind.String => hasMean.GetString() switch
            {
                "1" => true,
                "0" => false,
                _ => null
            },
            JsonValueKind.Number => hasMean.GetInt32() switch
            {
                1 => true,
                0 => false,
                _ => null
            },
            _ => null
        };
    }
}
