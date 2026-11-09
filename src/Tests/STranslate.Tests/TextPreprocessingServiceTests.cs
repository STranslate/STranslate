using STranslate.Core;

namespace STranslate.Tests;

public class TextPreprocessingServiceTests
{
    [Theory]
    [InlineData(false, true, "a", "b", "a", 0)]
    [InlineData(true, false, "a", "b", "a", 0)]
    [InlineData(true, true, "missing", "b", "a", 0)]
    [InlineData(true, true, "a", "a", "a", 0)]
    [InlineData(true, true, "a", "b", "a", 1)]
    public void ReportsOnlyEnabledRulesThatActuallyChangedText(
        bool enabled, bool ruleEnabled, string pattern, string replacement, string input, int appliedCount)
    {
        var result = new TextPreprocessingService().Prepare(input, new()
        {
            IsEnabled = enabled,
            Rules = [new() { Mode = TextReplacementMode.PlainText, IsEnabled = ruleEnabled,
                Pattern = pattern, Replacement = replacement }]
        });
        Assert.Equal(appliedCount, result.AppliedRules.Count);
    }

    [Fact]
    public void MarksChainedReplacementsEvenWhenFinalTextMatchesOriginal()
    {
        var settings = new TextPreprocessingSettings { IsEnabled = true, Rules =
            [new() { Mode = TextReplacementMode.PlainText, Pattern = "a", Replacement = "b" },
             new() { Pattern = "(b)", Replacement = "$1" },
             new() { Pattern = "missing", Replacement = "x" },
             new() { Pattern = "b", Replacement = "a" }] };
        var result = new TextPreprocessingService().Prepare("a", settings);
        Assert.Equal("a", result.RequestText);
        Assert.Equal([new AppliedTextReplacement("a", "b"), new("b", "a")], result.AppliedRules);
        settings.Rules[0].Replacement = "changed later";
        Assert.Equal("b", result.AppliedRules[0].Replacement);
    }

    [Fact]
    public void MultipleMatchesCountAsOneAppliedRule()
    {
        var result = new TextPreprocessingService().Prepare("a a a", new()
        {
            IsEnabled = true,
            Rules = [new() { Mode = TextReplacementMode.PlainText, Pattern = "a", Replacement = "b" }]
        });
        Assert.Equal("b b b", result.RequestText);
        Assert.Equal(new AppliedTextReplacement("a", "b"), Assert.Single(result.AppliedRules));
    }

    [Theory]
    [InlineData("a.b", "$1\\name", "a.b axb", "$1\\name axb")]
    [InlineData("[", "${name}", "[x[", "${name}x${name}")]
    public void PlainTextTreatsPatternsAndReplacementsLiterally(string pattern, string replacement, string input, string expected)
    {
        var result = new TextPreprocessingService().Prepare(input, new()
        {
            IsEnabled = true,
            Rules = [new() { Mode = TextReplacementMode.PlainText, Pattern = pattern, Replacement = replacement }]
        });
        Assert.Equal(expected, result.RequestText);
    }

    [Fact]
    public void PlainTextAndRegexCanBeChainedWithIgnoreCase()
    {
        var result = new TextPreprocessingService().Prepare("ACME.42", new()
        {
            IsEnabled = true,
            Rules = [new() { Mode = TextReplacementMode.PlainText, Pattern = "acme.", Replacement = "product-", IgnoreCase = true },
                new() { Pattern = "product-(\\d+)", Replacement = "item $1" }]
        });
        Assert.Equal("item 42", result.RequestText);
    }

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
