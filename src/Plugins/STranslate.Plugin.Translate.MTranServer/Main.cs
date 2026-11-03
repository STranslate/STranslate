using STranslate.Plugin.Translate.MTranServer.View;
using STranslate.Plugin.Translate.MTranServer.ViewModel;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.MTranServer;

public class Main : TranslatePluginBase
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public override Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => null,
        LangEnum.ChineseSimplified => "zh",
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
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => null,
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => "fa",
        LangEnum.Swedish => "sv",
        LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => "uk",
        LangEnum.Uzbek => "uz",
        LangEnum.Uyghur => null,
        _ => "auto"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => null,
        LangEnum.ChineseSimplified => "zh",
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
        LangEnum.PortuguesePortugal => "pt",
        LangEnum.PortugueseBrazil => "pt",
        LangEnum.Vietnamese => "vi",
        LangEnum.Indonesian => "id",
        LangEnum.Thai => null,
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => "hi",
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => "fa",
        LangEnum.Swedish => "sv",
        LangEnum.Polish => "pl",
        LangEnum.Dutch => "nl",
        LangEnum.Ukrainian => "uk",
        LangEnum.Uzbek => "uz",
        LangEnum.Uyghur => null,
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

        var content = new
        {
            from = sourceStr,
            to = targetStr,
            text = request.Text
        };

        var response = await Context.HttpService.PostAsync(Settings.Url, content, cancellationToken: cancellationToken);

        // 解析Google翻译返回的JSON
        var jsonDoc = JsonDocument.Parse(response);
        var translatedText = jsonDoc.RootElement.GetProperty("result").GetString() ?? throw new Exception(response);

        result.Success(translatedText);
    }
}
