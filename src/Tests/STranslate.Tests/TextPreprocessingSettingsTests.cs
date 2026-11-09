using STranslate.Core;
using System.Text.Json;
using System.IO;

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

    [Fact]
    public async Task StorageRestartAndBackupRecoveryPreservePreprocessingSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "STranslate-preprocessing-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "Settings.json");
        try
        {
            var storage = new StorageBase<PreprocessingDocument>(path);
            var settings = await storage.LoadAsync();
            settings.TextPreprocessing = new() { IsEnabled = true, Rules =
                [new() { Id = "a", Pattern = "a", Replacement = "b\n$1" }, new() { Id = "b", Pattern = "b", IsEnabled = false }] };
            storage.Save();
            storage.Save();
            var restarted = await new StorageBase<PreprocessingDocument>(path).LoadAsync();
            Assert.True(restarted.TextPreprocessing.IsEnabled);
            Assert.Equal(["a", "b"], restarted.TextPreprocessing.Rules.Select(rule => rule.Id));
            await File.WriteAllTextAsync(path, "{invalid json");
            var recovered = await new StorageBase<PreprocessingDocument>(path).LoadAsync();
            Assert.Equal(JsonSerializer.Serialize(restarted.TextPreprocessing), JsonSerializer.Serialize(recovered.TextPreprocessing));
            Assert.Equal("b\n$1", recovered.TextPreprocessing.Rules[0].Replacement);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public sealed class PreprocessingDocument
    {
        public TextPreprocessingSettings TextPreprocessing { get; set; } = new();
    }
}
