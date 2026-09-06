using STranslate.Plugin;

namespace STranslate.Core;

/// <summary>
/// 保存一次翻译执行的不可变输入，并提供仅对本次操作生效的插件执行和结果发布能力。
/// </summary>
internal sealed class TranslationOperation
{
    private readonly TranslationResultCoordinator _coordinator;
    private readonly Func<string, string> _getTranslation;

    internal TranslationOperation(
        TranslationResultCoordinator coordinator,
        Func<string, string> getTranslation,
        long operationId,
        string text,
        LangEnum sourceLang,
        LangEnum targetLang,
        CancellationToken cancellationToken,
        PreparedTranslationInput preparedInput)
    {
        _coordinator = coordinator;
        _getTranslation = getTranslation;
        OperationId = operationId;
        Text = text;
        RequestText = preparedInput.RequestText;
        RulesFingerprint = preparedInput.RulesFingerprint;
        SourceLang = sourceLang;
        TargetLang = targetLang;
        CancellationToken = cancellationToken;
    }

    internal long OperationId { get; }
    internal string Text { get; }
    internal string RequestText { get; }
    internal string RulesFingerprint { get; }
    internal bool IsPreprocessed => !string.IsNullOrEmpty(RulesFingerprint);
    internal LangEnum SourceLang { get; }
    internal LangEnum TargetLang { get; }
    internal CancellationToken CancellationToken { get; }
    internal bool IsLatestAutomatic => _coordinator.IsLatestAutomatic(OperationId);

    internal bool TryPrepare(ITranslatePlugin plugin)
    {
        var canPublishMainResult = TryReset(plugin.TransResult);
        TryReset(plugin.TransBackResult);
        return canPublishMainResult;
    }

    internal bool TryPrepareBack(ITranslatePlugin plugin) =>
        TryReset(plugin.TransBackResult);

    internal bool TryPrepare(IDictionaryPlugin plugin) =>
        _coordinator.TryResetDictionary(plugin.DictionaryResult, OperationId);

    internal bool IsCurrent(object channel) =>
        _coordinator.IsCurrent(channel, OperationId);

    internal bool TryPublish(object channel, Action publish) =>
        _coordinator.TryPublish(channel, OperationId, publish);

    internal bool TryPublishAutomatic(Action publish) =>
        _coordinator.TryPublishAutomatic(OperationId, publish);

    internal void ActivateIdentifiedLanguage() =>
        _coordinator.ActivateIdentifiedLanguage(OperationId);

    internal void PublishIdentifiedLanguage(Action publish) =>
        _coordinator.PublishIdentifiedLanguage(OperationId, publish);

    internal void PublishDictionary(DictionaryResult source, DictionaryResult target) =>
        _coordinator.PublishDictionary(source, target, OperationId);

    internal Task<TranslateResult> TranslateAsync(
        ITranslatePlugin plugin,
        LangEnum source,
        LangEnum target) =>
        ExecuteTranslateAsync(
            plugin,
            new TranslateRequest(RequestText, source, target),
            plugin.TransResult);

    internal Task<TranslateResult> BackTranslateAsync(
        ITranslatePlugin plugin,
        string text,
        LangEnum source,
        LangEnum target) =>
        ExecuteTranslateAsync(
            plugin,
            new TranslateRequest(text, source, target),
            plugin.TransBackResult);

    internal async Task<DictionaryResult> LookupAsync(IDictionaryPlugin plugin)
    {
        var result = new DictionaryResult();
        if (!IsCurrent(plugin.DictionaryResult))
        {
            result.ResultType = DictionaryResultType.Error;
            return result;
        }

        var startTime = DateTime.Now;
        TryPublish(
            plugin.DictionaryResult,
            () => plugin.DictionaryResult.IsProcessing = true);
        result.IsProcessing = true;
        try
        {
            await plugin.TranslateAsync(RequestText, result, CancellationToken).ConfigureAwait(false);
            CancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            result.ResultType = DictionaryResultType.Error;
            result.Text = _getTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            result.ResultType = DictionaryResultType.Error;
            result.Text = $"{_getTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            result.Duration = result.ResultType == DictionaryResultType.NoResult
                ? TimeSpan.Zero
                : DateTime.Now - startTime;

            result.IsProcessing = false;
            PublishDictionary(result, plugin.DictionaryResult);
        }

        return result;
    }

    private async Task<TranslateResult> ExecuteTranslateAsync(
        ITranslatePlugin plugin,
        TranslateRequest request,
        TranslateResult visibleResult)
    {
        var result = new TranslateResult();
        if (!IsCurrent(visibleResult))
        {
            result.IsSuccess = false;
            return result;
        }

        var startTime = DateTime.Now;
        using var forwarding = Forward(result, visibleResult);
        result.IsProcessing = true;
        try
        {
            await plugin.TranslateAsync(request, result, CancellationToken).ConfigureAwait(false);
            CancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            result.IsSuccess = false;
            result.Text = _getTranslation("TranslateCancel");
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.Text = $"{_getTranslation("TranslateFail")}: {ex.Message}";
        }
        finally
        {
            result.Duration = DateTime.Now - startTime;
            result.IsProcessing = false;
        }

        return result;
    }

    private bool TryReset(TranslateResult result) =>
        _coordinator.TryReset(result, OperationId);

    private IDisposable Forward(TranslateResult source, TranslateResult target) =>
        _coordinator.Forward(source, target, OperationId);
}
