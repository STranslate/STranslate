namespace STranslate.Plugin.Translate.OpenAI;

public class Settings
{
    public OpenAIApiMode ApiMode { get; set; } = OpenAIApiMode.ChatCompletions;
    public string ApiKey { get; set; } = string.Empty;
    public string Url { get; set; } = "https://api.openai.com/";
    public string Model { get; set; } = "gpt-4o";
    public List<string> Models { get; set; } =
    [
        "gpt-4o",
        "gpt-5",
    ];
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;
    public bool SendTemperature { get; set; } = false;
    public string AdditionalParametersJson { get; set; } = string.Empty;
    public int TopP { get; set; } = 1;
    public int N { get; set; } = 1;
    public bool Stream { get; set; } = true;
    public int? MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;

    public List<Prompt> Prompts { get; set; } =
    [
        new("翻译",
        [
            new PromptItem("system", "You are a professional, authentic translation engine. You only return the translated text, without any explanations."),
            new PromptItem("user", "Please translate  into $target (avoid explaining the original text):\r\n\r\n$content"),
        ], true),
        new("润色",
        [
            new PromptItem("system", "You are a professional, authentic text polishing engine. You only return the polished text, without any explanations."),
            new PromptItem("user", "Please polish the following text in $source (avoid explaining the original text):\r\n\r\n$content"),
        ]),
        new("总结",
        [
            new PromptItem("system", "You are a professional, authentic text summarization engine. You only return the summarized text, without any explanations."),
            new PromptItem("user", "Please summarize the following text in $source (avoid explaining the original text):\r\n\r\n$content"),
        ]),
    ];
}

public enum OpenAIApiMode
{
    ChatCompletions,
    Responses
}
