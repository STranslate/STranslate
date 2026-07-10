using Microsoft.Extensions.Logging;
using STranslate.Plugin.Translate.TransmartBuiltIn.View;
using STranslate.Plugin.Translate.TransmartBuiltIn.ViewModel;
using System.Text.Json;
using System.Windows.Controls;

namespace STranslate.Plugin.Translate.TransmartBuiltIn;

public class Main : TranslatePluginBase
{
    private const string URL = "https://transmart.qq.com/api/imt";
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

    public override string? GetSourceLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh",
        LangEnum.ChineseTraditional => "zh-TW",
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
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => null,
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => null,
        LangEnum.Swedish => null,
        LangEnum.Polish => null,
        LangEnum.Dutch => null,
        LangEnum.Ukrainian => null,
        LangEnum.Uzbek => null,
        _ => "auto"
    };

    public override string? GetTargetLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "auto",
        LangEnum.ChineseSimplified => "zh",
        LangEnum.ChineseTraditional => "zh-TW",
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
        LangEnum.Thai => "th",
        LangEnum.Malay => "ms",
        LangEnum.Arabic => "ar",
        LangEnum.Hindi => null,
        LangEnum.MongolianCyrillic => null,
        LangEnum.MongolianTraditional => null,
        LangEnum.Khmer => null,
        LangEnum.NorwegianBokmal => null,
        LangEnum.NorwegianNynorsk => null,
        LangEnum.Persian => null,
        LangEnum.Swedish => null,
        LangEnum.Polish => null,
        LangEnum.Dutch => null,
        LangEnum.Ukrainian => null,
        LangEnum.Uzbek => null,
        _ => "auto"
    };

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

        var option = new Options
        {
            Headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36" },
                { "Referer", "https://yi.qq.com/zh-CN/index" }
            }
        };

        var content = new
        {
            header = new
            {
                fn = "auto_translation_block",
                client_key = "browser-chrome-110.0.0-Mac OS-df4bd4c5-a65d-44b2-a40f-42f34f3535f2-1677486696487"
            },
            type = "plain",
            model_category = "normal",
            source = new
            {
                lang = sourceStr,
                text_block = request.Text
            },
            target = new
            {
                lang = targetStr
            }
        };

        try
        {
            var response = await Context.HttpService.PostAsync(
                URL,
                content,
                option,
                cancellationToken: cancellationToken);
            using var jsonDoc = JsonDocument.Parse(response);
            if (!jsonDoc.RootElement.TryGetProperty("auto_translation", out var translation) ||
                string.IsNullOrWhiteSpace(translation.GetString()))
            {
                result.Fail("No result.");
                return;
            }

            result.Success(translation.GetString()!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(ex, "Tencent translation failed");
            result.Fail("Translation failed.");
        }
    }
}
