namespace STranslate.Core;

internal static class TextPreprocessingErrorText
{
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
        return exception.ErrorOffset is { } offset
            ? message + " " + string.Format(translate("Translate_TextPreprocessingErrorPosition"), offset + 1)
            : message;
    }
}
