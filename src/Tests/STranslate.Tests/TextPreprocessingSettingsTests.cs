using STranslate.Core;
using System.Text.Json;

namespace STranslate.Tests;

public class TextPreprocessingSettingsTests
{
    [Fact]
    public void DefaultSettingsKeepPreprocessingDisabledAndEmpty()
    {
        var settings = new Settings();

        Assert.False(settings.TextPreprocessing.IsEnabled);
        Assert.Empty(settings.TextPreprocessing.Rules);
    }

    [Fact]
    public void SettingsRoundTripPreservesRuleOrderAndOptions()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules =
            [
                new()
                {
                    Id = "first",
                    Name = "标识符",
                    Pattern = "ACME-(\\d+)",
                    Replacement = "产品$1",
                    IsEnabled = true,
                    IgnoreCase = true,
                    Multiline = true,
                    Singleline = false
                },
                new()
                {
                    Id = "second",
                    Pattern = "secret",
                    Replacement = string.Empty,
                    IsEnabled = false
                }
            ]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<TextPreprocessingSettings>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.IsEnabled);
        Assert.Equal(["first", "second"], restored.Rules.Select(rule => rule.Id));
        Assert.Equal("产品$1", restored.Rules[0].Replacement);
        Assert.True(restored.Rules[0].IgnoreCase);
        Assert.True(restored.Rules[0].Multiline);
        Assert.False(restored.Rules[1].IsEnabled);
    }

    [Fact]
    public void PreparedInputSnapshotsSettingsBeforeLaterMutation()
    {
        var settings = new TextPreprocessingSettings
        {
            IsEnabled = true,
            Rules = [new() { Pattern = "old", Replacement = "new" }]
        };
        var service = new TextPreprocessingService();

        var prepared = service.Prepare("old", settings);
        settings.Rules[0].Replacement = "changed";

        Assert.Equal("new", prepared.RequestText);
    }
}
