using STranslate.Plugin;

namespace STranslate.Core;

internal static class DirectTranslationRunner
{
    internal static async Task<TranslateResult> TranslateAsync(
        string text,
        TextPreprocessingSettings? settings,
        TextPreprocessingService preprocessing,
        ITranslatePlugin plugin,
        Func<string, CancellationToken, Task<(bool IsSuccess, LangEnum Source, LangEnum Target)>> detectLanguage,
        CancellationToken cancellationToken)
    {
        var prepared = await preprocessing.PrepareAsync(text, settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(prepared.RequestText))
            throw new TextPreprocessingException(string.Empty, string.Empty,
                "替换后没有可翻译内容。", errorKind: "EmptyResult");
        cancellationToken.ThrowIfCancellationRequested();
        // 检测失败时返回的是配置的回退语种，仍应继续翻译。
        var (_, source, target) = await detectLanguage(prepared.RequestText, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TranslateResult();
        await plugin.TranslateAsync(new(prepared.RequestText, source, target), result, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
