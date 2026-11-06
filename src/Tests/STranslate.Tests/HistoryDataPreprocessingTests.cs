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
    public async Task ApplyingOperationContextPersistsActualRequestDetails()
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

        await operation.PrepareInputAsync();
        data.ApplyPreprocessingContext(operation);

        Assert.Equal("public", data.RequestText);
        Assert.Equal(operation.RulesFingerprint, data.PreprocessingFingerprint);
    }

    [Fact]
    public void NewMainResultCannotInheritOldBackTranslation()
    {
        var existing = new HistoryData { RequestText = "old", PreprocessingFingerprint = "old",
            TransResult = new() { Text = "old main" }, TransBackResult = new() { Text = "old back" } };
        existing.MergeFrom(new() { RequestText = "new", PreprocessingFingerprint = "new", TransResult = new() { Text = "new main" } });
        Assert.Equal("new main", existing.TransResult!.Text);
        Assert.Null(existing.TransBackResult);
    }

    [Theory]
    [InlineData("different main", "request", "fingerprint", false)]
    [InlineData("main", "other request", "fingerprint", false)]
    [InlineData("main", "request", "different fingerprint", false)]
    [InlineData("main", "request", "fingerprint", true)]
    public void BackOnlyMergeMustMatchTheStoredMain(string main, string request, string fingerprint, bool matches)
    {
        var existing = new HistoryData { RequestText = "request", PreprocessingFingerprint = "fingerprint", TransResult = new() { Text = "main" } };
        existing.MergeFrom(new() { RequestText = request, PreprocessingFingerprint = fingerprint,
            TransBackResult = new() { Text = "back" }, BackTranslationSourceText = main });
        Assert.Equal(matches, existing.HasSuccessfulBackResult);
    }
}
