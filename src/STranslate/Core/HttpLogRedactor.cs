using STranslate.Plugin;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace STranslate.Core;

internal sealed partial class HttpLogRedactor
{
    private const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "api_key", "api-key", "apikey", "token", "access_token", "refresh_token", "client_id",
        "client_secret", "password", "authorization", "proxy-authorization", "x-goog-api-key", "x-api-key",
        "ocp-apim-subscription-key", "cookie", "set-cookie", "sign", "signature", "x-mt-signature"
    };

    private readonly HashSet<string> _credentials = new(StringComparer.Ordinal);
    private Regex? _credentialPattern;
    private bool _sseJsonFragment;

    public HttpLogRedactor(string url, Options? options)
    {
        if (TryGetHttpUri(url, out var uri))
        {
            foreach (var (name, value) in ReadParameters(uri.Query.TrimStart('?')))
            {
                if (SensitiveNames.Contains(name))
                    AddCredential(value);
            }

            if (uri.UserInfo.Length > 0)
            {
                foreach (var value in uri.UserInfo.Split(':', 2))
                    AddCredential(Uri.UnescapeDataString(value));
            }
        }

        if (options?.QueryParams is { } queryParams)
        {
            foreach (var (name, value) in queryParams)
            {
                if (SensitiveNames.Contains(name))
                    AddCredential(value);
            }
        }

        if (options?.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
            {
                if (!SensitiveNames.Contains(name) || string.IsNullOrEmpty(value))
                    continue;

                AddCredential(value);
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var separator = value.IndexOf(' ');
                    if (separator >= 0)
                        AddCredential(value[(separator + 1)..].Trim());
                }
            }
        }

        Url = FormatUrl(url);
    }

    public string Url { get; private set; }

    public void SetRequestUri(Uri? uri) => Url = FormatUrl(uri?.OriginalString);

    public void RegisterFormCredentials(Dictionary<string, string> formData)
    {
        foreach (var (name, value) in formData)
        {
            if (SensitiveNames.Contains(name))
                AddCredential(value);
        }
    }

    public string SanitizeText(string text) =>
        MaskCredentials(UrlPattern().Replace(text, match => FormatUrl(match.Value)));

    public string SanitizeBody(string? content, string? mediaType = null)
    {
        if (string.IsNullOrEmpty(content))
            return "<empty>";

        if (string.Equals(mediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return SanitizeForm(content);

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return string.Join('\n', content.Split('\n').Select(line => SanitizeStreamLine(line)));

        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("[") ||
            string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            return SanitizeJson(content);
        }

        return SanitizeText(content);
    }

    public string SanitizeStreamLine(string line, string? mediaType = null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            _sseJsonFragment = false;
            return string.Empty;
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return SanitizeBody(line, string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase) ? null : mediaType);

        var data = line[5..].TrimStart();
        if (_sseJsonFragment)
            return $"data: <SSE JSON fragment: {data.Length} characters>";

        if (data.TrimEnd() == "[DONE]")
            return "data: [DONE]";

        if (data.Length == 0)
            return "data:";

        if (data[0] is not ('{' or '[' or '"'))
            return $"data: {SanitizeText(data)}";

        // 独立解析失败后，直到空行才恢复记录，避免输出多行 JSON 的后续片段。
        var sanitized = SanitizeJson(data, out var parsed);
        _sseJsonFragment = !parsed;
        return $"data: {sanitized}";
    }

    public void SkipStreamLine(string line) => _sseJsonFragment = !string.IsNullOrWhiteSpace(line);

    private string FormatUrl(string? url)
    {
        if (!TryGetHttpUri(url, out var uri))
            return "<invalid URL>";

        var address = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        var path = MaskCredentials(uri.AbsolutePath);
        var usesSse = ReadParameters(uri.Query.TrimStart('?')).Any(parameter =>
            parameter.Name == "alt" && parameter.Value == "sse");

        return usesSse ? $"{address}{path}?alt=sse" : address + path;
    }

    private static bool TryGetHttpUri(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";

    private static IEnumerable<(string Name, string Value)> ReadParameters(string query)
    {
        foreach (var field in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator >= 0)
                yield return (HttpUtility.UrlDecode(field[..separator]), HttpUtility.UrlDecode(field[(separator + 1)..]));
        }
    }

    private string SanitizeForm(string content)
    {
        var fields = content.Split('&');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var separator = field.IndexOf('=');
            if (separator <= 0 || InvalidPercentEscape().IsMatch(field))
                return $"<invalid form: {content.Length} characters>";

            var name = field[..separator];
            var value = SensitiveNames.Contains(HttpUtility.UrlDecode(name))
                ? Redacted
                : SanitizeText(HttpUtility.UrlDecode(field[(separator + 1)..]));
            fields[i] = $"{MaskCredentials(name)}={value}";
        }

        return string.Join('&', fields);
    }

    private string SanitizeJson(string content) => SanitizeJson(content, out _);

    private string SanitizeJson(string content, out bool parsed)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                WriteJson(writer, document.RootElement);

            parsed = true;
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            parsed = false;
            return $"<invalid JSON: {content.Length} characters>";
        }
    }

    private void WriteJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(MaskCredentials(property.Name));
                    if (SensitiveNames.Contains(property.Name))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SanitizeText(element.GetString()!));
                break;
            default:
                var value = element.GetRawText();
                if (MaskCredentials(value) == value)
                    element.WriteTo(writer);
                else
                    writer.WriteStringValue(Redacted);
                break;
        }
    }

    private void AddCredential(string? value)
    {
        if (string.IsNullOrEmpty(value) || !_credentials.Add(value))
            return;

        _credentials.Add(Uri.EscapeDataString(value));
        _credentials.Add(HttpUtility.UrlEncode(value));
        _credentialPattern = null;
    }

    private string MaskCredentials(string text)
    {
        if (_credentials.Count == 0)
            return text;

        _credentialPattern ??= new Regex(string.Join('|', _credentials
            .OrderByDescending(value => value.Length)
            .Select(value => PercentEscape().Replace(Regex.Escape(value), match => $"(?i:{match.Value})"))),
            RegexOptions.CultureInvariant);
        return _credentialPattern.Replace(text, Redacted);
    }

    [GeneratedRegex("""https?://[^\s"'<>\\]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("%(?![0-9a-fA-F]{2})")]
    private static partial Regex InvalidPercentEscape();

    [GeneratedRegex("%[0-9a-fA-F]{2}")]
    private static partial Regex PercentEscape();
}
