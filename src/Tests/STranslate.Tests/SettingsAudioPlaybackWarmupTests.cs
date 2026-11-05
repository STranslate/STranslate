using STranslate.Core;
using System.Text.Json;

namespace STranslate.Tests;

public class SettingsAudioPlaybackWarmupTests
{
    [Fact]
    public void LegacySettingsDisableWarmupByDefault()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{}");

        Assert.NotNull(settings);
        Assert.Equal(0, settings.AudioPlaybackWarmupMs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1500, 1500)]
    [InlineData(5000, 5000)]
    [InlineData(int.MaxValue, 5000)]
    public void SettingAndDeserializationClampWarmup(int input, int expected)
    {
        var settings = new Settings { AudioPlaybackWarmupMs = input };
        Assert.Equal(expected, settings.AudioPlaybackWarmupMs);

        var loaded = JsonSerializer.Deserialize<Settings>($$"""{"AudioPlaybackWarmupMs":{{input}}}""");
        Assert.NotNull(loaded);
        Assert.Equal(expected, loaded.AudioPlaybackWarmupMs);
    }

    [Fact]
    public void WarmupSurvivesSerializationRoundTrip()
    {
        var settings = new Settings { AudioPlaybackWarmupMs = 1500 };
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        var warmup = serialized.RootElement.GetProperty(nameof(Settings.AudioPlaybackWarmupMs));
        Assert.Equal(1500, warmup.GetInt32());
        // 只回读本次字段，避免无 WPF 应用的单元测试触发其他窗口设置的行为。
        var loaded = JsonSerializer.Deserialize<Settings>($$"""{"AudioPlaybackWarmupMs":{{warmup.GetRawText()}}}""");

        Assert.NotNull(loaded);
        Assert.Equal(1500, loaded.AudioPlaybackWarmupMs);
    }
}
