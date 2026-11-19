using STranslate.Core;
using System.Text.Json;

namespace STranslate.Tests;

public class SettingsSelectedTextCaptureTests
{
    [Fact]
    public void LegacySettingsPreferUiaByDefault()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{}");

        Assert.NotNull(settings);
        Assert.True(settings.PreferUiaForSelectedText);
    }

    [Fact]
    public void DisabledPreferenceSurvivesSerialization()
    {
        var settings = new Settings { PreferUiaForSelectedText = false };
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        var preference = serialized.RootElement.GetProperty(nameof(Settings.PreferUiaForSelectedText));
        var loaded = JsonSerializer.Deserialize<Settings>(
            $$"""{"{{nameof(Settings.PreferUiaForSelectedText)}}":{{preference.GetRawText()}}}""");

        Assert.NotNull(loaded);
        Assert.False(loaded.PreferUiaForSelectedText);
    }
}
