namespace STranslate.Plugin.Tts.Qwen3;

public class Settings
{
    /// <summary>
    /// 本地 Qwen3-TTS 服务基础地址（不含尾斜杠）。对应 qwen3-tts-server 默认 http://127.0.0.1:9880
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:9880";

    /// <summary>
    /// 说话人。CustomVoice 模型下全小写，如 ryan / aiden / vivian / uncle_fu 等。
    /// 可通过 GET {BaseUrl}/speakers 获取完整列表。
    /// 默认 ryan —— 英文男声，兼顾中文与英文翻译播报；纯中文场景可换 uncle_fu。
    /// </summary>
    public string Speaker { get; set; } = "ryan";

    /// <summary>
    /// 语言。Auto 为自动识别；也可显式指定 Chinese / English / Japanese 等。
    /// </summary>
    public string Language { get; set; } = "Auto";

    /// <summary>
    /// 自然语言风格指令（可选）。例如："用轻快愉悦的语气说" / "Speak calmly"。
    /// </summary>
    public string Instruct { get; set; } = string.Empty;

    /// <summary>
    /// 请求超时（秒）。Qwen3-TTS 首次推理稍慢，建议 60 秒起。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}
