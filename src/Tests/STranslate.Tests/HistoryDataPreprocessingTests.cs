using STranslate.Core;
using STranslate.Plugin;

namespace STranslate.Tests;

public class HistoryDataPreprocessingTests
{
    [Fact]
    public void OldHistoryWithoutMetadataMatchesUnprocessedRequest()
    {
        var data = new HistoryData();

        Assert.True(data.IsCompatible("original", "original", string.Empty));
        Assert.False(data.IsCompatible("original", "processed", "fingerprint"));
    }

    [Fact]
    public void HistoryMetadataMustMatchRequestTextAndFingerprint()
    {
        var data = new HistoryData
        {
            RequestText = "processed",
            PreprocessingFingerprint = "fingerprint"
        };

        Assert.True(data.IsCompatible("original", "processed", "fingerprint"));
        Assert.False(data.IsCompatible("original", "processed", "other"));
        Assert.False(data.IsCompatible("original", "different", "fingerprint"));
    }

    [Fact]
    public void ApplyingOperationContextPersistsActualRequestDetails()
    {
        var settings = new Settings
        {
            TextPreprocessing = new TextPreprocessingSettings
            {
                IsEnabled = true,
                Rules = [new() { Pattern = "secret", Replacement = "public" }]
            }
        };
        var coordinator = new TranslationResultCoordinator(
            static key => key,
            new TextPreprocessingService(),
            settings);
        var operation = coordinator.BeginOperation(
            "secret",
            LangEnum.English,
            LangEnum.ChineseSimplified,
            CancellationToken.None);
        var data = new HistoryData();

        data.ApplyPreprocessingContext(operation);

        Assert.Equal("public", data.RequestText);
        Assert.Equal(operation.RulesFingerprint, data.PreprocessingFingerprint);
    }
}
