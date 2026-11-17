namespace STranslate.Core;

/// <summary>服务实际使用的请求快照；null 表示原始记录缺失，不能重新推断。</summary>
public sealed record TextReplacementRequest(string? RequestText, string ServiceNames);
