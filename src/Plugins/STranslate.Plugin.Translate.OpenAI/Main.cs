using STranslate.Plugin.Translate.OpenAI.View;
using STranslate.Plugin.Translate.OpenAI.ViewModel;
using System.Text;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.OpenAI;

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

        var messages = BuildMessages(sourceStr, targetStr, request.Text);
        await ExecuteStreamingAsync(messages, text => result.Text = text, cancellationToken);
    }

    internal async Task ValidateApiAsync(CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages("en-US", "zh-CN", "Hello world");
        await ExecuteStreamingAsync(messages, null, cancellationToken);
    }

    private List<PromptItem> BuildMessages(string source, string target, string text)
    {
        var messages = (Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置"))
            .Clone()
            .Items
            .ToList();

        foreach (var item in messages)
        {
            item.Content = item.Content
                .Replace("$source", source)
                .Replace("$target", target)
                .Replace("$content", text);
        }

        return messages;
    }

    private async Task<string> ExecuteStreamingAsync(
        IReadOnlyCollection<PromptItem> messages,
        Action<string>? onTextUpdated,
        CancellationToken cancellationToken)
    {
        var apiMode = Settings.ApiMode;
        var url = OpenAIProtocol.BuildFinalUrl(Settings.Url, apiMode);
        var model = string.IsNullOrWhiteSpace(Settings.Model) ? "gpt-4o" : Settings.Model.Trim();
        var temperature = Math.Clamp(Settings.Temperature, 0, 2);
        var content = OpenAIProtocol.CreateRequest(
            apiMode,
            model,
            messages,
            temperature,
            Settings.AdditionalParametersJson,
            Settings.SendTemperature);

        var option = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + Settings.ApiKey }
            }
        };

        StringBuilder sb = new();
        var isThink = false;

        await foreach (var msg in Context.HttpService.StreamPostAsyncEnumerable(
            url,
            content,
            option,
            cancellationToken))
        {
            var streamEvent = OpenAIProtocol.ParseStreamLine(apiMode, msg);
            if (!string.IsNullOrWhiteSpace(streamEvent.ErrorMessage))
                throw new InvalidOperationException(streamEvent.ErrorMessage);

            var contentValue = streamEvent.TextDelta;
            if (string.IsNullOrEmpty(contentValue))
                continue;

            if (contentValue.Trim() == "<think>")
            {
                isThink = true;
                continue;
            }

            if (contentValue.Trim() == "</think>")
            {
                isThink = false;
                continue;
            }

            if (isThink)
                continue;

            // 优化推理内容结束后的前导空白。
            if (sb.Length == 0 && string.IsNullOrWhiteSpace(contentValue))
                continue;

            sb.Append(contentValue);
            onTextUpdated?.Invoke(sb.ToString());
        }

        if (sb.Length == 0)
            throw new InvalidOperationException(Context.GetTranslation("STranslate_Plugin_Translate_OpenAI_NoTextOutput"));

        return sb.ToString();
    }
}
