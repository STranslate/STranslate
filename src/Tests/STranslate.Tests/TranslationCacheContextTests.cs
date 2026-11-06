using STranslate.Core;
using STranslate.Plugin;
using STranslate.ViewModels;

namespace STranslate.Tests;

public class TranslationCacheContextTests
{
    [Fact]
    public async Task FailedRetranslationKeepsOldHistoryWithoutPublishingOrRecachingIt()
    {
        var coordinator = CreateCoordinator("new request");
        var operation = coordinator.BeginOperation("secret", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        await operation.PrepareInputAsync();
        var failed = CreateService("failed");
        var successful = CreateService("successful");
        var oldFailed = new HistoryData(failed) { RequestText = "old request", PreprocessingFingerprint = "old",
            TransResult = new() { Text = "old successful translation" } };
        var oldSuccessful = new HistoryData(successful) { RequestText = "old request", PreprocessingFingerprint = "old",
            TransResult = new() { Text = "old result" } };
        var history = new HistoryModel { SourceText = "secret", Data = [oldFailed, oldSuccessful] };
        operation.TryPrepare((ITranslatePlugin)failed.Plugin);
        operation.TryPrepare((ITranslatePlugin)successful.Plugin);
        Assert.Equal(2, MainWindowViewModel.PopulateResultsFromCache(history, [failed, successful], operation).Count);
        Assert.Empty(((ITranslatePlugin)failed.Plugin).TransResult.Text);
        var incoming = new HistoryData(successful) { TransResult = new() { Text = "new result" } };
        incoming.ApplyPreprocessingContext(operation);
        history.Data.Add(incoming);
        history.RestoreUntranslatedResults();
        Assert.Same(oldFailed, history.GetData(failed));
        Assert.Same(incoming, history.GetData(successful));
        Assert.False(oldFailed.IsCompatible(operation.Text, operation.RequestText, operation.RulesFingerprint));
    }

    [Fact]
    public async Task SameOriginalTextWithChangedRulesIsNotCompatible()
    {
        var firstCoordinator = CreateCoordinator("public");
        var secondCoordinator = CreateCoordinator("private");
        var firstOperation = firstCoordinator.BeginOperation(
            "secret",
            LangEnum.English,
            LangEnum.ChineseSimplified,
            CancellationToken.None);
        var secondOperation = secondCoordinator.BeginOperation(
            "secret",
            LangEnum.English,
            LangEnum.ChineseSimplified,
            CancellationToken.None);
        var data = new HistoryData();
        await firstOperation.PrepareInputAsync();
        await secondOperation.PrepareInputAsync();
        data.ApplyPreprocessingContext(firstOperation);

        Assert.False(data.IsCompatible(
            secondOperation.Text,
            secondOperation.RequestText,
            secondOperation.RulesFingerprint));
    }

    [Fact]
    public async Task MixedCachePublishesOnlyCompatibleSuccessfulResults()
    {
        var coordinator = CreateCoordinator("public");
        var operation = coordinator.BeginAutomaticOperation("secret", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        await operation.PrepareInputAsync();
        var valid = CreateService("valid");
        var stale = CreateService("stale");
        var failed = CreateService("failed");
        var services = new[] { valid, stale, failed };
        var history = new HistoryModel { SourceText = "secret", SourceLang = "Auto", TargetLang = "Auto" };
        foreach (var service in services)
        {
            var data = new HistoryData(service) { TransResult = new() { Text = service.ServiceID } };
            data.ApplyPreprocessingContext(operation, LangEnum.English, LangEnum.ChineseSimplified);
            history.Data.Add(data);
            operation.TryPrepare((ITranslatePlugin)service.Plugin);
        }
        history.Data[1].PreprocessingFingerprint = "obsolete";
        history.Data[2].TransResult!.IsSuccess = false;
        var stalePublications = 0;
        ((ITranslatePlugin)stale.Plugin).TransResult.PropertyChanged += (_, _) => stalePublications++;

        var pending = MainWindowViewModel.PopulateResultsFromCache(history, services.ToList(), operation);

        Assert.Equal([stale, failed], pending);
        Assert.Equal("valid", ((ITranslatePlugin)valid.Plugin).TransResult.Text);
        Assert.Empty(((ITranslatePlugin)stale.Plugin).TransResult.Text);
        Assert.Empty(((ITranslatePlugin)failed.Plugin).TransResult.Text);
        Assert.Equal(0, stalePublications);
        Assert.Single(history.Data);
        Assert.Equal(LangEnum.English, coordinator.GetResultContext(((ITranslatePlugin)valid.Plugin).TransResult)!.EffectiveSource);
    }

    [Fact]
    public void LegacyJsonAndBackOnlyCacheKeepTheMainLanguages()
    {
        var service = CreateService("legacy");
        service.Options!.AutoBackTranslation = true;
        var history = new HistoryModel { SourceText = "original", SourceLang = "Auto", TargetLang = "Auto",
            EffectiveSourceLang = "English", EffectiveTargetLang = "ChineseSimplified",
            RawData = """[{"PluginID":"test","ServiceID":"legacy","TransResult":{"Text":"main","IsSuccess":true},"TransBackResult":{"Text":"failed","IsSuccess":false}}]""" };
        var coordinator = new TranslationResultCoordinator(static key => key);
        var operation = coordinator.BeginOperation("original", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        operation.TryPrepare((ITranslatePlugin)service.Plugin);

        Assert.Equal([service], MainWindowViewModel.PopulateResultsFromCache(history, [service], operation));
        Assert.Equal("main", ((ITranslatePlugin)service.Plugin).TransResult.Text);
        Assert.Empty(((ITranslatePlugin)service.Plugin).TransBackResult.Text);
        var data = Assert.Single(history.Data);
        Assert.Equal("English", data.EffectiveSourceLang);
        history.EffectiveSourceLang = "French";
        Assert.Equal("English", data.EffectiveSourceLang);
        data.MergeFrom(new() { RequestText = "original", PreprocessingFingerprint = "",
            EffectiveSourceLang = "English", EffectiveTargetLang = "ChineseSimplified",
            BackTranslationSourceText = "main", TransBackResult = new() { Text = "back" } });
        Assert.True(history.HasData(service));
    }

    [Fact]
    public async Task RuleReorderAndSwitchChangesInvalidateCacheButRenameDoesNot()
    {
        var settings = new TextPreprocessingSettings { IsEnabled = true, Rules =
            [new() { Pattern = "a", Replacement = "b" }, new() { Pattern = "b", Replacement = "c" }] };
        var engine = new TextPreprocessingService();
        var initial = await engine.PrepareAsync("a", settings);
        var data = new HistoryData { RequestText = initial.RequestText, PreprocessingFingerprint = initial.RulesFingerprint };
        settings.Rules[0].Name = "Renamed";
        var renamed = engine.Prepare("a", settings);
        Assert.True(data.IsCompatible("a", renamed.RequestText, renamed.RulesFingerprint));
        settings.Rules.Reverse();
        var reordered = engine.Prepare("a", settings);
        Assert.False(data.IsCompatible("a", reordered.RequestText, reordered.RulesFingerprint));
        settings.IsEnabled = false;
        var disabled = engine.Prepare("a", settings);
        Assert.False(data.IsCompatible("a", disabled.RequestText, disabled.RulesFingerprint));
        Assert.True(new HistoryData().IsCompatible("a", disabled.RequestText, disabled.RulesFingerprint));
    }

    [Fact]
    public void PartialPreprocessingMetadataCannotReuseOriginalAsRequestText()
    {
        Assert.False(new HistoryData { PreprocessingFingerprint = "rules" }.IsCompatible("text", "text", "rules"));
    }

    private static Service CreateService(string id) => new()
    {
        ServiceID = id,
        MetaData = new() { PluginID = "test" },
        Plugin = new CachePlugin(),
        Options = new()
    };

    private sealed class CachePlugin : TranslatePluginBase
    {
        public override Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("缓存填充不应调用提供商。");
        public override string? GetSourceLanguage(LangEnum language) => language.ToString();
        public override string? GetTargetLanguage(LangEnum language) => language.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }

    private static TranslationResultCoordinator CreateCoordinator(string replacement)
    {
        var settings = new Settings
        {
            TextPreprocessing = new TextPreprocessingSettings
            {
                IsEnabled = true,
                Rules = [new() { Pattern = "secret", Replacement = replacement }]
            }
        };
        return new TranslationResultCoordinator(
            static key => key,
            new TextPreprocessingService(),
            settings);
    }
}
