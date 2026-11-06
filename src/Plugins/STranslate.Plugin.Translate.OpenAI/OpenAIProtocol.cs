using System.Text.Json;
using System.Text.Json.Nodes;

namespace STranslate.Plugin.Translate.OpenAI;

internal static class OpenAIProtocol
{
    private const string ChatCompletionsPath = "/v1/chat/completions";
    private const string ResponsesPath = "/v1/responses";

    internal static string BuildFinalUrl(string url, OpenAIApiMode apiMode)
    {
        var path = apiMode == OpenAIApiMode.Responses ? ResponsesPath : ChatCompletionsPath;
        return UrlHelper.BuildFinalUrl(url, path);
    }

    internal static object CreateRequest(
        OpenAIApiMode apiMode,
        string model,
        IReadOnlyCollection<PromptItem> messages,
        double temperature,
        string? additionalParametersJson = null,
        bool sendTemperature = false)
    {
        JsonObject request = apiMode switch
        {
            OpenAIApiMode.Responses => new JsonObject
            {
                ["model"] = model,
                ["input"] = JsonSerializer.SerializeToNode(messages),
                ["stream"] = true,
                ["store"] = false
            },
            _ => new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonSerializer.SerializeToNode(messages),
                ["stream"] = true
            }
        };

        if (sendTemperature)
            request["temperature"] = temperature;

        AppendAdditionalParameters(request, additionalParametersJson);
        return request;
    }

    private static void AppendAdditionalParameters(JsonObject request, string? additionalParametersJson)
    {
        if (string.IsNullOrWhiteSpace(additionalParametersJson))
            return;

        JsonObject additionalParameters;
        try
        {
            additionalParameters = JsonNode.Parse(additionalParametersJson) as JsonObject
                ?? throw new FormatException("附加请求参数的根节点必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new FormatException("附加请求参数不是有效的 JSON。", ex);
        }

        foreach (var (name, value) in additionalParameters)
        {
            if (string.Equals(name, "temperature", StringComparison.OrdinalIgnoreCase) ||
                request.Any(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"附加请求参数不能覆盖内置字段“{name}”。");

            request[name] = value?.DeepClone();
        }
    }

    internal static OpenAIStreamEvent ParseStreamLine(OpenAIApiMode apiMode, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return default;

        var payload = line.StartsWith("data:", StringComparison.Ordinal)
            ? line["data:".Length..].Trim()
            : line.Trim();

        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.Ordinal))
            return default;

        if (!payload.StartsWith('{'))
            return default;

        JsonNode? parsedData;
        try
        {
            parsedData = JsonNode.Parse(payload);
        }
        catch
        {
            // 部分 OpenAI-compatible 服务会在 SSE 中混入非 JSON 状态行。
            return default;
        }

        if (parsedData is null)
            return default;

        var errorMessage = GetErrorMessage(parsedData);
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return new OpenAIStreamEvent(null, errorMessage);

        var choices = parsedData["choices"] as JsonArray;
        var textDelta = apiMode switch
        {
            OpenAIApiMode.Responses when parsedData["type"]?.ToString() == "response.output_text.delta"
                => parsedData["delta"]?.ToString(),
            OpenAIApiMode.ChatCompletions
                => choices is { Count: > 0 }
                    ? choices[0]?["delta"]?["content"]?.ToString()
                    : null,
            _ => null
        };

        return string.IsNullOrEmpty(textDelta)
            ? default
            : new OpenAIStreamEvent(textDelta, null);
    }

    private static string? GetErrorMessage(JsonNode parsedData)
    {
        if (parsedData["type"]?.ToString() == "error")
            return parsedData["message"]?.ToString();

        if (parsedData["type"]?.ToString() == "response.failed")
            return parsedData["response"]?["error"]?["message"]?.ToString();

        return parsedData["error"]?["message"]?.ToString();
    }
}

internal readonly record struct OpenAIStreamEvent(string? TextDelta, string? ErrorMessage);
