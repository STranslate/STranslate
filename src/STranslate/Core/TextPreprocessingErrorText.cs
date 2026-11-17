namespace STranslate.Core;

internal static class TextPreprocessingErrorText
{
    internal static string FormatImageFailures(int failedBlocks, IEnumerable<TextPreprocessingException> errors,
        Func<string, string> translate)
    {
        var summary = string.Format(translate("TranslatePreprocessingBlocksFailed"), failedBlocks);
        // 并行块只汇总提示一次，并稳定选取一条具体规则错误，避免每个块各弹一次通知。
        var example = errors.OrderBy(error => error.RuleName, StringComparer.Ordinal)
            .ThenBy(error => error.RuleId, StringComparer.Ordinal).ThenBy(error => error.ErrorKind, StringComparer.Ordinal).FirstOrDefault();
        return example is null ? summary : summary + Environment.NewLine + Format(example, translate);
    }

    internal static string Format(TextPreprocessingException exception, Func<string, string> translate)
    {
        var key = exception.ErrorKind switch
        {
            "EmptyResult" => "TranslatePreprocessingEmpty",
            "InvalidConfiguration" => "TranslatePreprocessingInvalidConfiguration",
            "Timeout" => "TranslatePreprocessingTimeout",
            "OutputLimit" => "TranslatePreprocessingOutputLimit",
            _ => "TranslatePreprocessingFailed"
        };
        var message = string.Format(translate(key), exception.RuleName);
        if (exception.InnerException is System.Text.RegularExpressions.RegexParseException parse)
            message += " " + translate(parse.Error.ToString() switch
            {
                "InsufficientClosingParentheses" => "General_TextPreprocessingRegexParenthesis",
                "UnterminatedBracket" => "General_TextPreprocessingRegexBracket",
                "UnrecognizedEscape" => "General_TextPreprocessingRegexEscape",
                _ => "General_TextPreprocessingRegexHint"
            });
        return exception.ErrorOffset is { } offset
            ? message + " " + string.Format(translate("Translate_TextPreprocessingErrorPosition"), offset + 1)
            : message;
    }
}
