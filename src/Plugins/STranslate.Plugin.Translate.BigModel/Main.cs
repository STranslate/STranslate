using STranslate.Plugin.Translate.BigModel.View;
using STranslate.Plugin.Translate.BigModel.ViewModel;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.BigModel;

public class Main : LlmTranslatePluginBase
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public override void SelectPrompt(Prompt? prompt)
    {
        base.SelectPrompt(prompt);

        // 保存到配置
        Settings.Prompts = [.. Prompts.Select(p => p.Clone())];
        Context.SaveSettingStorage<Settings>();
    }

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "Requires you to identify automatically",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese",
        LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        LangEnum.Uzbek => "Uzbek",
        LangEnum.Uyghur => "Uyghur",
        _ => "Requires you to identify automatically"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "Requires you to identify automatically",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese",
        LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        LangEnum.Uzbek => "Uzbek",
        LangEnum.Uyghur => "Uyghur",
        _ => "Requires you to identify automatically"
    };

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();

        Settings.Prompts.ForEach(Prompts.Add);
    }

    public override void Dispose() => _viewModel?.Dispose();

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

        var url = UrlHelper.BuildFinalUrl(Settings.Url, "/api/paas/v4/chat/completions", UrlPathMatchRule.ChatGLM);

        // 选择模型
        var model = Settings.Model.Trim();
        model = string.IsNullOrEmpty(model) ? "glm-4" : model;

        // 替换Prompt关键字
        var messages = (Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置"))
            .Clone()
            .Items;
        messages.ToList()
            .ForEach(item =>
                item.Content = item.Content
                .Replace("$source", sourceStr)
                .Replace("$target", targetStr)
                .Replace("$content", request.Text)
                );

        // 温度限定
        var temperature = Math.Clamp(Settings.Temperature, 0, 1);

        var content = new
        {
            model,
            messages,
            temperature,
            stream = true,
            thinking = new
            {
                type = Settings.Thinking ? "enabled" : "disabled"
            }
        };

        var key = Settings.ApiKey;
        var option = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer " + BigModelAuthenication.GenerateToken(key, 3600) }
            }
        };

        await Context.HttpService.StreamPostAsync(url, content, msg =>
        {
            if (string.IsNullOrEmpty(msg?.Trim()))
                return;

            var preprocessString = msg.Replace("data:", "").Trim();

            // 结束标记
            if (preprocessString.Equals("[DONE]"))
                return;

            try
            {
                /**
                 * 
                 * var parsedData = JsonDocument.Parse(preprocessString);

                if (parsedData is null)
                    return;

                var root = parsedData.RootElement;

                // 提取 content 的值
                var contentValue = root
                    .GetProperty("choices")[0]
                    .GetProperty("delta")
                    .GetProperty("content")
                    .GetString();
                * 
                 */
                // 解析JSON数据
                var parsedData = JsonNode.Parse(preprocessString);

                if (parsedData is null)
                    return;

                // 提取content的值
                var contentValue = parsedData["choices"]?[0]?["delta"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(contentValue))
                    return;

#if false
                /***********************************************************************
                 * 推理模型思考内容
                 * 1. content字段内：Groq（推理后带有换行）(兼容think标签还带有换行情况)
                 * 2. reasoning_content字段内：DeepSeek、硅基流动（推理后带有换行）、第三方服务商
                 ************************************************************************/

                #region 针对content内容中含有推理内容的优化

                if (contentValue.Trim() == "<think>")
                    isThink = true;
                if (contentValue.Trim() == "</think>")
                {
                    isThink = false;
                    // 跳过当前内容
                    return;
                }

                if (isThink)
                    return;

                #endregion

                #region 针对推理过后带有换行的情况进行优化

                // 优化推理模型思考结束后的\n\n符号
                if (string.IsNullOrWhiteSpace(sb.ToString()) && string.IsNullOrWhiteSpace(contentValue))
                    return;

                sb.Append(contentValue);

                #endregion
#endif
                result.Text += contentValue;
            }
            catch
            {
                // Ignore
                // * 适配OpenRouter等第三方服务流数据中包含与BigModel官方API中不同的数据
                // * 如 ": OPENROUTER PROCESSING"
            }
        },option, cancellationToken: cancellationToken);
    }
}
