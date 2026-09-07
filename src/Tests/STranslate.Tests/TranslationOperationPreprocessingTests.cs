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

        await operation.PrepareInputAsync();
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
    public async Task NewRequestOwnsChannelsBeforePreparationAndFailureCannotReviveOldResults()
    {
        var settings = new Settings { TextPreprocessing = new() { IsEnabled = true,
            Rules = [new() { Pattern = "x", Replacement = "xx" }] } };
        var coordinator = new TranslationResultCoordinator(static key => key, new TextPreprocessingService(), settings);
        var plugin = new StubTranslatePlugin((_, result, _) => { result.Success("done"); return Task.CompletedTask; });
        var first = coordinator.BeginAutomaticOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        first.TryPrepare(plugin);
        await first.TranslateAsync(plugin, LangEnum.English, LangEnum.ChineseSimplified);
        settings.TextPreprocessing.Rules[0].Pattern = "(";
        var second = coordinator.BeginAutomaticOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        second.TryPrepare(plugin);

        Assert.False(first.IsLatestAutomatic);
        Assert.False(first.TryPublish(plugin.TransResult, () => plugin.TransResult.Text = "late"));
        await Assert.ThrowsAsync<TextPreprocessingException>(() => second.PrepareInputAsync());
        Assert.Empty(plugin.TransResult.Text);
        Assert.Null(coordinator.GetResultContext(plugin.TransResult));
    }

    [Fact]
    public async Task PreparationRunsOncePerOperationAndRetryStartsFromOriginal()
    {
        var settings = new Settings { TextPreprocessing = new() { IsEnabled = true,
            Rules = [new() { Pattern = "x", Replacement = "xx" }] } };
        var coordinator = new TranslationResultCoordinator(static key => key, new TextPreprocessingService(), settings);
        var operation = coordinator.BeginOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        settings.TextPreprocessing.Rules[0].Replacement = "xxx";
        var prepared = await operation.PrepareInputAsync();
        Assert.Equal("xx", prepared.RequestText);
        Assert.Same(prepared, await operation.PrepareInputAsync());
        var retry = coordinator.BeginOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        Assert.Equal("xxx", (await retry.PrepareInputAsync()).RequestText);
    }

    [Fact]
    public async Task BackTranslationUsesMainContextEvenAfterRulesBecomeInvalid()
    {
        var settings = new Settings { TextPreprocessing = new() { IsEnabled = true,
            Rules = [new() { Pattern = "x", Replacement = "prepared" }] } };
        var coordinator = new TranslationResultCoordinator(static key => key, new TextPreprocessingService(), settings);
        var requests = new List<TranslateRequest>();
        var plugin = new StubTranslatePlugin((request, result, _) =>
        {
            requests.Add(request);
            result.Success("main translation");
            return Task.CompletedTask;
        });
        var main = coordinator.BeginOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        main.TryPrepare(plugin);
        await main.TranslateAsync(plugin, LangEnum.English, LangEnum.ChineseSimplified);
        settings.TextPreprocessing.Rules[0].Pattern = "(";
        var bound = coordinator.GetResultContext(plugin.TransResult)!;
        var back = coordinator.BeginBackOperation(bound, CancellationToken.None);
        back.TryPrepareBack(plugin);
        await back.BackTranslateAsync(plugin, bound.TranslatedText, bound.EffectiveTarget, bound.EffectiveSource);
        Assert.Equal(["prepared", "main translation"], requests.Select(request => request.Text));
        Assert.Equal("prepared", back.RequestText);
    }

    [Fact]
    public async Task EmptyAndInvalidInputsNeverReachProviders()
    {
        var count = 0;
        var settings = new Settings { TextPreprocessing = new() { IsEnabled = true,
            Rules = [new() { Pattern = ".+", Replacement = "" }] } };
        var coordinator = new TranslationResultCoordinator(static key => key, new TextPreprocessingService(), settings);
        var plugin = new StubTranslatePlugin((_, _, _) => { count++; return Task.CompletedTask; });
        var operation = coordinator.BeginOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        operation.TryPrepare(plugin);
        Assert.Equal("EmptyResult", (await Assert.ThrowsAsync<TextPreprocessingException>(() =>
            operation.TranslateAsync(plugin, LangEnum.English, LangEnum.ChineseSimplified))).ErrorKind);
        settings.TextPreprocessing.Rules[0].Pattern = "(";
        operation = coordinator.BeginOperation("x", LangEnum.Auto, LangEnum.Auto, CancellationToken.None);
        operation.TryPrepare(plugin);
        await Assert.ThrowsAsync<TextPreprocessingException>(() => operation.TranslateAsync(plugin, LangEnum.English, LangEnum.ChineseSimplified));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DirectEntryPointsUseSamePreparedTextForDetectionAndTranslation()
    {
        var detected = new List<string>();
        var translated = new List<string>();
        var settings = new TextPreprocessingSettings { IsEnabled = true,
            Rules = [new() { Pattern = "secret", Replacement = "public" }] };
        var plugin = new StubTranslatePlugin((request, result, _) =>
        {
            translated.Add(request.Text);
            result.Success("done");
            return Task.CompletedTask;
        });
        await DirectTranslationRunner.TranslateAsync("secret", settings, new(), plugin,
            (text, _) => { detected.Add(text); return Task.FromResult((true, LangEnum.English, LangEnum.ChineseSimplified)); }, CancellationToken.None);
        Assert.Equal(["public"], detected);
        Assert.Equal(detected, translated);

        settings.Rules[0].Replacement = "";
        await Assert.ThrowsAsync<TextPreprocessingException>(() => DirectTranslationRunner.TranslateAsync(
            "secret", settings, new(), plugin,
            (text, _) => { detected.Add(text); return Task.FromResult((true, LangEnum.English, LangEnum.ChineseSimplified)); }, CancellationToken.None));
        Assert.Single(detected);
        Assert.Single(translated);
    }

    [Fact]
    public async Task ImageBlocksKeepOneSnapshotAndCannotMatchAcrossBlocks()
    {
        var settings = new TextPreprocessingSettings { IsEnabled = true,
            Rules = [new() { Pattern = "firstsecond", Replacement = "joined" }, new() { Pattern = "first", Replacement = "one" }] };
        var snapshot = settings.Clone();
        var received = new List<string>();
        var plugin = new StubTranslatePlugin((request, result, _) =>
        {
            received.Add(request.Text);
            settings.Rules[1].Replacement = "changed";
            result.Success("done");
            return Task.CompletedTask;
        });
        foreach (var block in new[] { "first", "second", "first" })
            await DirectTranslationRunner.TranslateAsync(block, snapshot, new(), plugin,
                (_, _) => Task.FromResult((true, LangEnum.English, LangEnum.ChineseSimplified)), CancellationToken.None);
        Assert.Equal(["one", "second", "one"], received);
    }

    [Fact]
    public async Task CancellationDuringLanguageDetectionPreventsPluginCall()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var plugin = new StubTranslatePlugin((_, _, _) => { count++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DirectTranslationRunner.TranslateAsync(
            "input", new(), new(), plugin, (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult((true, LangEnum.English, LangEnum.ChineseSimplified));
            }, cancellation.Token));
        Assert.Equal(0, count);
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
