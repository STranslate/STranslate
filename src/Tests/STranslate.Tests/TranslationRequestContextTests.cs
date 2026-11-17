using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;

namespace STranslate.Tests;

public class TranslationRequestContextTests
{
    [Fact]
    public async Task MixedVisibleServicesKeepIndependentRequestsAcrossRuleChangesAndStaleCallbacks()
    {
        var settings = new Settings { TextPreprocessing = Rules("automatic") };
        var coordinator = new TranslationResultCoordinator(key => key, new(), settings);
        var a = Service("A");
        var b = Service("B");
        var automatic = coordinator.BeginAutomaticOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        automatic.TryPrepare((ITranslatePlugin)a.Plugin);
        automatic.TryPrepare((ITranslatePlugin)b.Plugin);
        await automatic.PrepareInputAsync();
        automatic.BindPreparedInput(((ITranslatePlugin)a.Plugin).TransResult);
        automatic.BindPreparedInput(((ITranslatePlugin)b.Plugin).TransResult);
        Assert.Equal(new TextReplacementRequest("automatic", "A / B"), Assert.Single(coordinator.GetPreparedRequests([a, b], "secret")));

        settings.TextPreprocessing = Rules("manual");
        var manual = coordinator.BeginOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        manual.TryPrepare((ITranslatePlugin)b.Plugin);
        await manual.PrepareInputAsync();
        manual.BindPreparedInput(((ITranslatePlugin)b.Plugin).TransResult);
        // 旧自动请求即使晚到，也不能把 B 的新请求快照改回去。
        automatic.BindPreparedInput(((ITranslatePlugin)b.Plugin).TransResult);
        Assert.Equal([new("automatic", "A"), new TextReplacementRequest("manual", "B")],
            coordinator.GetPreparedRequests([a, b], "secret"));
        Assert.Empty(coordinator.GetPreparedRequests([a, b], "changed input"));
    }

    [Fact]
    public async Task DisabledPinnedAndResetServicesDoNotLeaveStalePreviewSources()
    {
        var coordinator = new TranslationResultCoordinator(key => key, new(), new Settings { TextPreprocessing = Rules("prepared") });
        var service = Service("old name");
        var plugin = (ITranslatePlugin)service.Plugin;
        var first = coordinator.BeginOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        first.TryPrepare(plugin);
        await first.PrepareInputAsync();
        first.BindPreparedInput(plugin.TransResult);
        service.Options!.ExecMode = ExecutionMode.Pinned;
        Assert.Empty(coordinator.GetPreparedRequests([service], "secret"));
        service.Options.TemporaryDisplay = true;
        service.DisplayName = "new name";
        Assert.Equal("new name", Assert.Single(coordinator.GetPreparedRequests([service], "secret")).ServiceNames);
        service.IsEnabled = false;
        Assert.Empty(coordinator.GetPreparedRequests([service], "secret"));
        service.IsEnabled = true;
        var next = coordinator.BeginOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        next.TryPrepare(plugin);
        Assert.Empty(coordinator.GetPreparedRequests([service], "secret"));
        first.BindPreparedInput(plugin.TransResult);
        Assert.Empty(coordinator.GetPreparedRequests([service], "secret"));
    }

    [Fact]
    public async Task EveryDetectionSettingIsFrozenBeforeBackgroundPreparation()
    {
        var settings = new Settings { LanguageDetector = LanguageDetectorType.Yandex, LocalDetectorRate = 0.42,
            SourceLangIfAuto = LangEnum.French, FirstLanguage = LangEnum.ChineseSimplified, SecondLanguage = LangEnum.English };
        var coordinator = new TranslationResultCoordinator(key => key, new(), settings);
        var operation = coordinator.BeginOperation("secret", LangEnum.English, LangEnum.Auto, CancellationToken.None);
        settings.LanguageDetector = LanguageDetectorType.Local;
        settings.LocalDetectorRate = 0.9;
        settings.SourceLangIfAuto = settings.FirstLanguage = settings.SecondLanguage = LangEnum.Japanese;
        await operation.PrepareInputAsync();
        Assert.Equal(new LangDetectOptions(LanguageDetectorType.Yandex, 0.42, LangEnum.French,
            LangEnum.ChineseSimplified, LangEnum.English), operation.DetectionOptions);
    }

    [Fact]
    public void ImageFailuresShowOneLocalizedRuleCauseAlongWithTheTotalBlockCount()
    {
        var errors = new[] {
            new TextPreprocessingException("b", "B", "internal", errorKind: "OutputLimit"),
            new TextPreprocessingException("a", "A", "internal", errorKind: "Timeout") };
        string Translate(string key) => key switch
        {
            "TranslatePreprocessingBlocksFailed" => "{0} 个块保留原文",
            "TranslatePreprocessingTimeout" => "规则 {0} 超时",
            "TranslatePreprocessingOutputLimit" => "规则 {0} 超限",
            _ => key
        };
        var message = TextPreprocessingErrorText.FormatImageFailures(7, errors, Translate);
        Assert.Equal("7 个块保留原文" + Environment.NewLine + "规则 A 超时", message);
        Assert.Equal(message, TextPreprocessingErrorText.FormatImageFailures(7, errors.Reverse(), Translate));
        Assert.Equal("2 个块保留原文", TextPreprocessingErrorText.FormatImageFailures(2, [], Translate));
    }

    private static TextPreprocessingSettings Rules(string replacement) => new()
    { IsEnabled = true, Rules = [new() { Pattern = "secret", Replacement = replacement }] };
    private static Service Service(string name) => new()
    { ServiceID = name, DisplayName = name, MetaData = new() { PluginID = "test" }, Options = new(), Plugin = new StubPlugin() };
    private sealed class StubPlugin : TranslatePluginBase
    {
        public override Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default)
        { result.Success("translated"); return Task.CompletedTask; }
        public override string? GetSourceLanguage(LangEnum language) => language.ToString();
        public override string? GetTargetLanguage(LangEnum language) => language.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }
}
