using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.ViewModels;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace STranslate.Tests;

public class PreprocessingFlowRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualDictionaryOrInvalidPreparationCannotStealAnActiveLanguageDetection(bool invalidRule)
    {
        var settings = new Settings { TextPreprocessing = new() { IsEnabled = true,
            Rules = [new() { Pattern = "secret", Replacement = "public" }] } };
        var coordinator = new TranslationResultCoordinator(static key => key, new(), settings);
        // 只运行真实的单服务执行方法；词典返回 Error，在历史和通知等无关依赖之前退出。
        var model = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("<Settings>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, settings);
        var automatic = coordinator.BeginAutomaticOperation("secret", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        automatic.PublishIdentifiedLanguage(() => model.IsIdentifyProcessing = true);
        if (invalidRule) settings.TextPreprocessing.Rules[0].Pattern = "(";
        var manual = coordinator.BeginOperation("secret", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        var service = CreateService(new ErrorDictionary());
        var method = typeof(MainWindowViewModel).GetMethod("ExecuteSingleTranslateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var execution = (Task)method.Invoke(model, [service, manual])!;
        if (invalidRule) await Assert.ThrowsAsync<TextPreprocessingException>(() => execution);
        else await execution;
        automatic.PublishIdentifiedLanguage(() => model.IsIdentifyProcessing = false);
        Assert.False(model.IsIdentifyProcessing);
    }

    [Fact]
    public void ResettingLanguageStateAlsoEndsTheSupersededProgress()
    {
        var coordinator = new TranslationResultCoordinator(static key => key);
        var model = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("_translationCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, coordinator);
        var automatic = coordinator.BeginAutomaticOperation("text", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        automatic.PublishIdentifiedLanguage(() => model.IsIdentifyProcessing = true);
        typeof(MainWindowViewModel).GetMethod("ResetTranslationLanguageState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, null);
        Assert.False(model.IsIdentifyProcessing);
    }

    [Fact]
    public void CachedMainCanSkipDetectionOnlyWhenItsActualLanguagesAreKnown()
    {
        var service = CreateService(new UnusedTranslatePlugin());
        var history = new HistoryModel { Data = [new(service) { TransResult = new() { Text = "main" },
            EffectiveSourceLang = "English", EffectiveTargetLang = "ChineseSimplified" }] };
        Assert.True(history.CanCompleteFromStoredContexts([service]));
        history.Data[0].EffectiveSourceLang = "Auto";
        Assert.False(history.CanCompleteFromStoredContexts([service]));
        history.Data.Clear();
        Assert.False(history.CanCompleteFromStoredContexts([service]));
    }

    [Fact]
    public void ManualExecutionWaitsForBothMainAndBackRequests()
    {
        var plugin = new UnusedTranslatePlugin();
        var service = CreateService(plugin);
        Assert.False(MainWindowViewModel.IsServiceProcessing(service));
        plugin.TransResult.IsProcessing = true;
        Assert.True(MainWindowViewModel.IsServiceProcessing(service));
        plugin.TransResult.IsProcessing = false;
        plugin.TransBackResult.IsProcessing = true;
        Assert.True(MainWindowViewModel.IsServiceProcessing(service));
    }

    [Fact]
    public void FailedImageBlocksRemainSelectableWithoutPaintingOverTheirOriginals()
    {
        var translated = Block("translated", 0);
        var failed = Block("failed original", 50);
        var document = ImageTranslateRenderer.CreateTranslationResult([translated, failed],
            new HashSet<OcrLayoutBlock> { translated }, ImageTranslateOverlayTheme.Light);
        Assert.Equal("translated", Assert.Single(document.Items).Text);
        Assert.Equal("translated" + Environment.NewLine + "failed original", string.Concat(document.SelectableWords.Select(word => word.Text)));
        Assert.Contains(document.SelectableWords, word => word.BoundingBox.Top >= 50 && !word.BoundingBox.IsEmpty);
        var allFailed = ImageTranslateRenderer.CreateTranslationResult([failed], new HashSet<OcrLayoutBlock>(), ImageTranslateOverlayTheme.Light);
        Assert.True(allFailed.IsEmpty);
        Assert.Equal("failed original", string.Concat(allFailed.SelectableWords.Select(word => word.Text)));
    }

    [Fact]
    public void HistoryKeepsRequestSnapshotsForOneOriginalAndNeverRecalculatesLegacyRecords()
    {
        var history = new HistoryModel { SourceText = "secret", Data = [
            new() { ServiceDisplayName = "A", RequestText = "public" },
            new() { ServiceDisplayName = "B", RequestText = "public" }] };
        var shared = Assert.Single(history.ReplacementRequests);
        Assert.Equal("public", shared.RequestText);
        Assert.Equal("A / B", shared.ServiceNames);
        history.Data[1].RequestText = "other";
        Assert.Equal(["public", "other"], history.ReplacementRequests.Select(request => request.RequestText));
        history.Data[1].RequestText = null;
        Assert.Contains(history.ReplacementRequests, request => request.RequestText == "secret");
        history.Data = [new()];
        Assert.Empty(history.ReplacementRequests);
    }

    [Fact]
    public void RequestDetailsAreOptInAndUseThePerServiceLanguageSnapshot()
    {
        var history = new HistoryModel { SourceText = "secret", EffectiveSourceLang = "French", Data = [
            new() { ServiceDisplayName = "A", RequestText = "public, value", EffectiveSourceLang = "English",
                EffectiveTargetLang = "ChineseSimplified", TransResult = new() { Text = "result" } }] };
        var originalCsv = HistoryCsvHelper.BuildCsv([history], static value => value ?? "");
        Assert.DoesNotContain("实际发送文本", originalCsv);
        var detailedCsv = HistoryCsvHelper.BuildCsv([history], static value => value ?? "", includeRequestDetails: true);
        Assert.Contains("实际发送文本1,实际源语言1,实际目标语言1", detailedCsv);
        Assert.Contains("\"public, value\",English,ChineseSimplified", detailedCsv);
    }

    private static Service CreateService(IPlugin plugin) => new()
    {
        ServiceID = Guid.NewGuid().ToString("N"), MetaData = new() { PluginID = "test" }, Plugin = plugin, Options = new()
    };

    private static OcrLayoutBlock Block(string text, int top) => new()
    {
        Text = text, BoxPoints = [new(0, top), new(200, top), new(200, top + 30), new(0, top + 30)]
    };

    private sealed class ErrorDictionary : DictionaryPluginBase
    {
        public override Task TranslateAsync(string text, DictionaryResult result, CancellationToken cancellationToken = default)
        { result.ResultType = DictionaryResultType.Error; return Task.CompletedTask; }
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }

    private sealed class UnusedTranslatePlugin : TranslatePluginBase
    {
        public override Task TranslateAsync(TranslateRequest request, TranslateResult result, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public override string? GetSourceLanguage(LangEnum langEnum) => langEnum.ToString();
        public override string? GetTargetLanguage(LangEnum langEnum) => langEnum.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context) { }
        public override void Dispose() { }
    }
}
