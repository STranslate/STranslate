using STranslate.Core;
using STranslate.Plugin;

namespace STranslate.Tests;

public class TranslationCacheContextTests
{
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
