using STranslate.Core;
using STranslate.Plugin;

namespace STranslate.Tests;

public class TranslationOperationPreprocessingTests
{
    [Fact]
    public async Task TranslationPluginReceivesPreparedTextWhileOperationKeepsOriginalText()
    {
        var settings = new Settings
        {
            TextPreprocessing = new TextPreprocessingSettings
            {
                IsEnabled = true,
                Rules = [new() { Pattern = "secret", Replacement = "public" }]
            }
        };
        var receivedText = string.Empty;
        var plugin = new StubTranslatePlugin((request, result, _) =>
        {
            receivedText = request.Text;
            result.Success("translated");
            return Task.CompletedTask;
        });
        var coordinator = new TranslationResultCoordinator(
            static key => key,
            new TextPreprocessingService(),
            settings);
        var operation = coordinator.BeginOperation(
            "keep secret",
            LangEnum.English,
            LangEnum.ChineseSimplified,
            CancellationToken.None);

        Assert.Equal("keep secret", operation.Text);
        Assert.Equal("keep public", operation.RequestText);
        Assert.True(operation.IsPreprocessed);
        Assert.True(operation.TryPrepare(plugin));

        var result = await operation.TranslateAsync(
            plugin,
            LangEnum.English,
            LangEnum.ChineseSimplified);

        Assert.True(result.IsSuccess);
        Assert.Equal("keep public", receivedText);
    }

    [Fact]
    public async Task DictionaryPluginAlsoReceivesPreparedText()
    {
        var settings = new Settings
        {
            TextPreprocessing = new TextPreprocessingSettings
            {
                IsEnabled = true,
                Rules = [new() { Pattern = "secret", Replacement = "public" }]
            }
        };
        var receivedText = string.Empty;
        var plugin = new StubDictionaryPlugin((text, result, _) =>
        {
            receivedText = text;
            result.ResultType = DictionaryResultType.Success;
            result.Text = "translated";
            return Task.CompletedTask;
        });
        var coordinator = new TranslationResultCoordinator(
            static key => key,
            new TextPreprocessingService(),
            settings);
        var operation = coordinator.BeginOperation(
            "keep secret",
            LangEnum.English,
            LangEnum.ChineseSimplified,
            CancellationToken.None);

        Assert.True(operation.TryPrepare(plugin));
        var result = await operation.LookupAsync(plugin);

        Assert.Equal(DictionaryResultType.Success, result.ResultType);
        Assert.Equal("keep public", receivedText);
    }

    private sealed class StubTranslatePlugin(
        Func<TranslateRequest, TranslateResult, CancellationToken, Task> translateAsync)
        : TranslatePluginBase
    {
        public override Task TranslateAsync(
            TranslateRequest request,
            TranslateResult result,
            CancellationToken cancellationToken = default) =>
            translateAsync(request, result, cancellationToken);

        public override string? GetSourceLanguage(LangEnum langEnum) => langEnum.ToString();
        public override string? GetTargetLanguage(LangEnum langEnum) => langEnum.ToString();
        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context)
        {
        }
        public override void Dispose()
        {
        }
    }

    private sealed class StubDictionaryPlugin(
        Func<string, DictionaryResult, CancellationToken, Task> translateAsync)
        : DictionaryPluginBase
    {
        public override Task TranslateAsync(
            string content,
            DictionaryResult result,
            CancellationToken cancellationToken = default) =>
            translateAsync(content, result, cancellationToken);

        public override System.Windows.Controls.Control GetSettingUI() => null!;
        public override void Init(IPluginContext context)
        {
        }
        public override void Dispose()
        {
        }
    }
}
