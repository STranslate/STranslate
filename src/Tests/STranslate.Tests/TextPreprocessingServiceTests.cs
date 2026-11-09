using STranslate.Core;

namespace STranslate.Tests;

public class TextPreprocessingServiceTests
{
    [Fact]
    public void AppliesEnabledRulesInOrderAndReportsMatches()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new() { Id = "first", Name = "名称替换", Pattern = "ACME-\\d+", Replacement = "产品A" },
                new() { Id = "second", Name = "产品替换", Pattern = "产品A", Replacement = "Product A" }
            ]
        };

        var result = new TextPreprocessingService().Prepare("Review ACME-123 today.", settings);

        Assert.Equal("Review ACME-123 today.", result.OriginalText);
        Assert.Equal("Review Product A today.", result.RequestText);
        Assert.Equal(2, result.TotalMatchCount);
        Assert.Equal([1, 1], result.MatchSummaries.Select(summary => summary.MatchCount));
    }

    [Fact]
    public void SupportsCaptureGroupsAndMatchingOptions()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new()
                {
                    Id = "capture",
                    Pattern = "(?<name>acme)-(?<number>\\d+)",
                    Replacement = "${name} product $2",
                    IgnoreCase = true
                }
            ]
        };

        var result = new TextPreprocessingService().Prepare("ACME-42", settings);

        Assert.Equal("ACME product 42", result.RequestText);
    }

    [Fact]
    public void DisabledRulesAndGlobalSwitchPreserveInput()
    {
        var disabledRuleSettings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new() { Id = "disabled", Pattern = "secret", Replacement = "hidden", IsEnabled = false }
            ]
        };
        var enabledResult = new TextPreprocessingService().Prepare("secret", disabledRuleSettings);

        var disabledSettings = new TextPreprocessingSettings
        {
            IsEnabled = false,
            Rules =
            [
                new() { Id = "enabled", Pattern = "secret", Replacement = "hidden" }
            ]
        };
        var disabledResult = new TextPreprocessingService().Prepare("secret", disabledSettings);

        Assert.Equal("secret", enabledResult.RequestText);
        Assert.Equal("secret", disabledResult.RequestText);
        Assert.False(enabledResult.IsChanged);
        Assert.False(disabledResult.IsChanged);
        Assert.Equal(enabledResult.RulesFingerprint, new TextPreprocessingService()
            .Prepare("other", disabledRuleSettings).RulesFingerprint);
    }

    [Fact]
    public void InvalidPatternFailsBeforeReturningPartiallyProcessedText()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new() { Id = "bad", Name = "错误规则", Pattern = "(", Replacement = "x" }
            ]
        };

        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService().Prepare("input", settings));

        Assert.Equal("bad", exception.RuleId);
        Assert.Equal("错误规则", exception.RuleName);
        Assert.Contains("无效", exception.Message);
    }

    [Fact]
    public void EmptyReplacementDeletesMatchesAndLiteralDollarIsSupported()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new() { Pattern = "secret", Replacement = string.Empty },
                new() { Pattern = "value", Replacement = "$$1" }
            ]
        };

        var result = new TextPreprocessingService().Prepare("secret value", settings);

        Assert.Equal(" $1", result.RequestText);
    }

    [Fact]
    public void CancellingBeforeProcessingStopsWithoutResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Pattern = "input", Replacement = "output" }]
        };

        Assert.Throws<OperationCanceledException>(() =>
            new TextPreprocessingService().Prepare("input", settings, cancellation.Token));
    }

    [Fact]
    public void OutputLimitStopsExpansion()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Id = "large", Name = "扩展", Pattern = "x", Replacement = "12345" }]
        };

        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService(new TextPreprocessingLimits(TimeSpan.FromSeconds(1), 8))
                .Prepare("xx", settings));

        Assert.Equal("large", exception.RuleId);
        Assert.Contains("长度限制", exception.Message);
    }
}
