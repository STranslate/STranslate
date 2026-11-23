using STranslate.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STranslate.Tests;

public class BackupTransferStateTests
{
    [Theory]
    [InlineData("S3|C:\\backup.zip", BackupType.S3, "C:\\backup.zip")]
    [InlineData("WebDav|C:\\backup.zip", BackupType.WebDav, "C:\\backup.zip")]
    [InlineData("webdav|C:\\backup.zip", BackupType.WebDav, "C:\\backup.zip")]
    [InlineData("C:\\legacy\\backup.zip", BackupType.WebDav, "C:\\legacy\\backup.zip")]
    [InlineData("Local|C:\\backup.zip", BackupType.WebDav, "Local|C:\\backup.zip")]
    public void ParseReadsTypeAndPath(string content, BackupType expectedType, string expectedPath)
    {
        var (type, path) = BackupTransferState.Parse(content);

        Assert.Equal(expectedType, type);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void FormatRoundTrips()
    {
        var content = BackupTransferState.Format(BackupType.S3, "C:\\backup.zip");

        Assert.Equal("S3|C:\\backup.zip", content);

        var (type, path) = BackupTransferState.Parse(content);
        Assert.Equal(BackupType.S3, type);
        Assert.Equal("C:\\backup.zip", path);
    }

    [Fact]
    public void CloudflareR2PresetUsesAutoRegion()
    {
        var settings = new BackupSettings { S3Provider = S3ProviderType.CloudflareR2 };
        settings.ApplyS3ProviderPreset();

        Assert.Equal("auto", settings.S3Region);
        Assert.False(settings.S3ForcePathStyle);
    }

    [Fact]
    public void MinioPresetEnablesPathStyle()
    {
        var settings = new BackupSettings { S3Provider = S3ProviderType.Minio };
        settings.ApplyS3ProviderPreset();

        Assert.Equal("us-east-1", settings.S3Region);
        Assert.True(settings.S3ForcePathStyle);
    }

    [Fact]
    public void S3SettingsRoundTripKeepsPersistedValues()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var original = new BackupSettings
        {
            Type = BackupType.S3,
            S3Provider = S3ProviderType.CloudflareR2,
            S3Endpoint = "https://example.com",
            S3Region = "custom-region",
            S3Bucket = "bucket",
            S3AccessKey = "access",
            S3SecretKey = "secret",
            S3ForcePathStyle = true,
        };

        var restored = JsonSerializer.Deserialize<BackupSettings>(
            JsonSerializer.Serialize(original, options), options)!;

        Assert.Equal(BackupType.S3, restored.Type);
        Assert.Equal(S3ProviderType.CloudflareR2, restored.S3Provider);
        Assert.Equal("https://example.com", restored.S3Endpoint);
        Assert.Equal("custom-region", restored.S3Region);
        Assert.Equal("bucket", restored.S3Bucket);
        Assert.Equal("access", restored.S3AccessKey);
        Assert.Equal("secret", restored.S3SecretKey);
        Assert.True(restored.S3ForcePathStyle);
    }
}
