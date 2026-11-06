using STranslate.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STranslate.Tests;

public class TextPreprocessingEdgeCaseTests
{
    [Fact]
    public void NullRulesFromPersistedConfigurationBlockProcessing()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [null!, new() { Pattern = "secret", Replacement = "public" }]
        };

        Assert.Equal("InvalidConfiguration", Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService().Prepare("secret", settings)).ErrorKind);
    }

    [Fact]
    public void OversizedLiteralReplacementFailsBeforeAppending()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Id = "large", Name = "大替换", Pattern = "x", Replacement = new string('a', 9) }]
        };

        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService(new TextPreprocessingLimits(TimeSpan.FromSeconds(1), 8))
                .Prepare("x", settings));

        Assert.Equal("large", exception.RuleId);
        Assert.Contains("长度限制", exception.Message);
    }

    [Theory]
    [InlineData("$1/${named}/$0/$&/$`/$'/$+/$test/$_/$$")]
    [InlineData("${missing}-$42-$01-${01}-${0}-${named}")]
    [InlineData("$$$1$$$$${named}$${named}$")]
    [InlineData("${bad${named}}-${}-${-1}-$00001-$00002")]
    [InlineData("$123-$012-$10-$2-${2}-${10}")]
    public void BoundedExpansionMatchesDotNetReplacementSyntax(string replacement)
    {
        const string pattern = "(?<named>a)|(b)";
        const string input = "xabcy";
        var expected = Regex.Replace(input, pattern, replacement, RegexOptions.CultureInvariant);
        var actual = new TextPreprocessingService().Prepare(input, new()
        {
            IsEnabled = true,
            Rules = [new() { Pattern = pattern, Replacement = replacement }]
        });
        Assert.Equal(expected, actual.RequestText);
    }

    [Theory]
    [InlineData("^", "X", "a\nb", true, false)]
    [InlineData("a.*b", "X", "a\nb", false, true)]
    [InlineData("(?=.)", "X", "中文😀", false, false)]
    [InlineData("(a)?(b)?", "$+", "ac", false, false)]
    public void OptionsAndZeroWidthMatchesUseDotNetSemantics(
        string pattern, string replacement, string input, bool multiline, bool singleline)
    {
        var options = RegexOptions.CultureInvariant |
            (multiline ? RegexOptions.Multiline : RegexOptions.None) |
            (singleline ? RegexOptions.Singleline : RegexOptions.None);
        var result = new TextPreprocessingService().Prepare(input, new()
        {
            IsEnabled = true,
            Rules = [new() { Pattern = pattern, Replacement = replacement, Multiline = multiline, Singleline = singleline }]
        });
        Assert.Equal(Regex.Replace(input, pattern, replacement, options), result.RequestText);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"IsEnabled\":true,\"Rules\":null}")]
    [InlineData("{\"IsEnabled\":true}")]
    [InlineData("{\"IsEnabled\":\"true\",\"Rules\":[]}")]
    [InlineData("{\"IsEnabled\":true,\"Rules\":[null]}")]
    public void CorruptJsonIsPreservedAndCannotSilentlyDisableProcessing(string json)
    {
        var settings = JsonSerializer.Deserialize<TextPreprocessingSettings>(json)!;
        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService().Prepare("secret", settings));
        Assert.Equal("InvalidConfiguration", exception.ErrorKind);
        Assert.Equal(json, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void CapturedOutputIsBoundedBeforeAllocatingExpansion()
    {
        var service = new TextPreprocessingService(new(TimeSpan.FromSeconds(2), 1_000, TimeSpan.FromSeconds(2)));
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Pattern = "(.*)", Replacement = string.Concat(Enumerable.Repeat("$1", 10_000)) }]
        };
        var input = new string('x', 100_000);
        service.Validate(settings);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<TextPreprocessingException>(() => service.Prepare(input, settings));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal("OutputLimit", exception.ErrorKind);
        Assert.True(allocated < 5_000_000, $"Allocated {allocated} bytes");
    }

    [Fact]
    public void NoMatchPreservesTextEvenWhenReplacementWouldExceedLimit()
    {
        var result = new TextPreprocessingService(new(TimeSpan.FromSeconds(1), 8)).Prepare("not matched", new()
        {
            IsEnabled = true,
            Rules = [new() { Pattern = "z", Replacement = new string('x', 9) }]
        });
        Assert.Equal("not matched", result.RequestText);
    }

    [Fact]
    public void CatastrophicBacktrackingStopsAtTimeout()
    {
        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService(new(TimeSpan.FromMilliseconds(10), 1_000_000)).Prepare(
                new string('a', 20_000) + "!", new()
                {
                    IsEnabled = true,
                    Rules = [new() { Pattern = "^(a+)+$", Replacement = "x" }]
                }));
        Assert.Equal("Timeout", exception.ErrorKind);
    }

    [Fact]
    public void WholeOperationBudgetIsEnforcedAcrossRules()
    {
        var exception = Assert.Throws<TextPreprocessingException>(() =>
            new TextPreprocessingService(new(TimeSpan.FromSeconds(1), 1000, TimeSpan.FromTicks(1)))
                .Prepare("input", new()
                {
                    IsEnabled = true,
                    Rules = [new() { Pattern = "x", Replacement = "y" }]
                }));
        Assert.Equal("Timeout", exception.ErrorKind);
    }

    [Fact]
    public async Task BackgroundPreparationCapturesRulesBeforeReturning()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Pattern = "x", Replacement = "old" }]
        };
        var pending = new TextPreprocessingService().PrepareAsync("x", settings);
        settings.Rules[0].Replacement = "new";
        Assert.Equal("old", (await pending).RequestText);
    }
}
