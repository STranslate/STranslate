using System.Text.Json;
using System.Text.Json.Serialization;

namespace STranslate.Core;

public sealed class TextPreprocessingSettingsJsonConverter : JsonConverter<TextPreprocessingSettings>
{
    public override bool HandleNull => true;

    public override TextPreprocessingSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        try
        {
            var data = element.Deserialize<ConfigurationData>(options);
            if (data is null || data.Rules is null || data.Rules.Any(rule => rule is null))
                return Invalid(element);
            return new() { IsEnabled = data.IsEnabled, Rules = data.Rules };
        }
        catch (JsonException)
        {
            return Invalid(element);
        }
    }

    public override void Write(Utf8JsonWriter writer, TextPreprocessingSettings value, JsonSerializerOptions options)
    {
        if (value?.InvalidConfiguration is { } invalid)
        {
            invalid.WriteTo(writer);
            return;
        }
        JsonSerializer.Serialize(writer, value is null ? null : new ConfigurationData
        {
            IsEnabled = value.IsEnabled,
            Rules = value.Rules
        }, options);
    }

    private static TextPreprocessingSettings Invalid(JsonElement element) =>
        new() { InvalidConfiguration = element.Clone() };

    private sealed class ConfigurationData
    {
        public bool IsEnabled { get; set; }
        public List<TextReplacementRule> Rules { get; set; } = null!;
    }
}
