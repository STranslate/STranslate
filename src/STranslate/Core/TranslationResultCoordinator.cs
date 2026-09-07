using STranslate.Plugin;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace STranslate.Core;

/// <summary>
/// 创建彼此隔离的翻译操作，并确保每个结果通道只接受最新操作的更新。
/// </summary>
internal sealed class TranslationResultCoordinator
{
    private readonly ConditionalWeakTable<object, OperationChannel> _channels = new();
    private readonly object _automaticSideEffectsChannel = new();
    private readonly object _identifiedLanguageChannel = new();
    private readonly object _inputPreviewChannel = new();
    private readonly Func<string, string> _getTranslation;
    private readonly TextPreprocessingService? _preprocessingService;
    private readonly Settings? _settings;
    private long _lastOperationId;

    internal TranslationResultCoordinator(Func<string, string> getTranslation)
    {
        _getTranslation = getTranslation;
    }

    internal TranslationResultCoordinator(
        Func<string, string> getTranslation,
        TextPreprocessingService preprocessingService,
        Settings settings)
        : this(getTranslation)
    {
        _preprocessingService = preprocessingService;
        _settings = settings;
    }

    internal TranslationOperation BeginOperation(
        string text,
        LangEnum sourceLang,
        LangEnum targetLang,
        CancellationToken cancellationToken)
    {
        var operationId = Interlocked.Increment(ref _lastOperationId);
        TryActivate(_inputPreviewChannel, operationId);
        var snapshot = _settings?.TextPreprocessing?.Clone();
        return new TranslationOperation(
            this,
            _getTranslation,
            operationId,
            text,
            sourceLang,
            targetLang,
            cancellationToken,
            prepareInput: _preprocessingService is null ? null :
                () => _preprocessingService.PrepareAsync(text, snapshot, cancellationToken));
    }

    internal TranslationOperation BeginBackOperation(TranslationResultContext context, CancellationToken cancellationToken) =>
        new(this, _getTranslation, Interlocked.Increment(ref _lastOperationId),
            context.Input.OriginalText, context.SourceLang, context.TargetLang, cancellationToken, context.Input);

    internal TranslationResultContext? GetResultContext(TranslateResult result)
    {
        var state = _channels.GetOrCreateValue(result);
        lock (state.SyncRoot)
            return state.Context;
    }

    internal void BindResultContext(TranslateResult result, long operationId, TranslationResultContext context) =>
        TryPublish(result, operationId, () => _channels.GetOrCreateValue(result).Context = context);

    internal TranslationOperation BeginAutomaticOperation(
        string text,
        LangEnum sourceLang,
        LangEnum targetLang,
        CancellationToken cancellationToken)
    {
        var operation = BeginOperation(text, sourceLang, targetLang, cancellationToken);
        TryActivate(_automaticSideEffectsChannel, operation.OperationId);
        TryActivate(_identifiedLanguageChannel, operation.OperationId);
        return operation;
    }

    internal void ResetIdentifiedLanguage(Action reset)
    {
        var operationId = Interlocked.Increment(ref _lastOperationId);
        TryActivate(_identifiedLanguageChannel, operationId);
        TryActivate(_inputPreviewChannel, operationId);
        TryPublish(_identifiedLanguageChannel, operationId, reset);
    }

    internal bool TryResetDictionary(DictionaryResult result, long operationId) =>
        InvokeOnDispatcher(() => TryReset(result, operationId));

    internal bool IsLatestAutomatic(long operationId) =>
        IsCurrent(_automaticSideEffectsChannel, operationId);

    internal bool TryPublishAutomatic(long operationId, Action publish) =>
        TryPublish(_automaticSideEffectsChannel, operationId, publish);

    internal void ActivateIdentifiedLanguage(long operationId) =>
        TryActivate(_identifiedLanguageChannel, operationId);

    internal void PublishIdentifiedLanguage(long operationId, Action publish) =>
        TryPublish(_identifiedLanguageChannel, operationId, publish);

    internal bool TryActivate(object channel, long operationId) =>
        TryActivate(channel, operationId, static () => { });

    internal bool TryReset(TranslateResult result, long operationId) =>
        TryActivate(result, operationId, () =>
        {
            _channels.GetOrCreateValue(result).Context = null;
            Reset(result);
        });

    internal bool TryReset(DictionaryResult result, long operationId) =>
        TryActivate(result, operationId, () => Reset(result));

    internal IDisposable Forward(
        TranslateResult source,
        TranslateResult target,
        long operationId)
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            TryPublish(
                target,
                operationId,
                () => CopyProperty(source, target, args.PropertyName));
        };

        source.PropertyChanged += handler;
        return new PropertyChangedSubscription(source, handler);
    }

    internal bool TryPublish(
        DictionaryResult source,
        DictionaryResult target,
        long operationId) =>
        TryPublish(target, operationId, () => Copy(source, target));

    internal void PublishDictionary(
        DictionaryResult source,
        DictionaryResult target,
        long operationId) =>
        InvokeOnDispatcher(() => TryPublish(source, target, operationId));

    private static T InvokeOnDispatcher<T>(Func<T> callback)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? callback()
            : dispatcher.Invoke(callback);
    }

    internal bool TryPublishInputPreview(long operationId, Action publish) =>
        TryPublish(_inputPreviewChannel, operationId, publish);

    internal bool TryPublish(object channel, long operationId, Action publish)
    {
        var state = _channels.GetOrCreateValue(channel);
        lock (state.SyncRoot)
        {
            if (state.OperationId != operationId)
                return false;

            // 检查与写入必须位于同一临界区，否则旧请求可能在新请求重置后回写。
            publish();
            return true;
        }
    }

    internal bool IsCurrent(object channel, long operationId)
    {
        var state = _channels.GetOrCreateValue(channel);
        lock (state.SyncRoot)
        {
            return state.OperationId == operationId;
        }
    }

    private bool TryActivate(object channel, long operationId, Action activate)
    {
        var state = _channels.GetOrCreateValue(channel);
        lock (state.SyncRoot)
        {
            if (operationId < state.OperationId)
                return false;

            state.OperationId = operationId;
            activate();
            return true;
        }
    }

    private static void Reset(TranslateResult result)
    {
        result.Text = string.Empty;
        result.Duration = TimeSpan.Zero;
        result.IsProcessing = false;
        result.IsSuccess = true;
    }

    private static void Reset(DictionaryResult result)
    {
        result.Text = string.Empty;
        result.Symbols.Clear();
        result.DictMeans.Clear();
        result.Plurals.Clear();
        result.PastTense.Clear();
        result.PastParticiple.Clear();
        result.PresentParticiple.Clear();
        result.ThirdPersonSingular.Clear();
        result.Comparative.Clear();
        result.Superlative.Clear();
        result.Tags.Clear();
        result.Sentences.Clear();
        result.Duration = TimeSpan.Zero;
        result.IsProcessing = false;
        result.ResultType = DictionaryResultType.None;
    }

    private static void CopyProperty(TranslateResult source, TranslateResult target, string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(TranslateResult.Text):
                target.Text = source.Text;
                break;
            case nameof(TranslateResult.IsSuccess):
                target.IsSuccess = source.IsSuccess;
                break;
            case nameof(TranslateResult.Duration):
                target.Duration = source.Duration;
                break;
            case nameof(TranslateResult.IsProcessing):
                target.IsProcessing = source.IsProcessing;
                break;
        }
    }

    private static void Copy(DictionaryResult source, DictionaryResult target)
    {
        Reset(target);
        target.Text = source.Text;
        target.ResultType = source.ResultType;
        CopyCollection(source.Symbols, target.Symbols);
        CopyCollection(source.DictMeans, target.DictMeans);
        CopyCollection(source.Plurals, target.Plurals);
        CopyCollection(source.PastTense, target.PastTense);
        CopyCollection(source.PastParticiple, target.PastParticiple);
        CopyCollection(source.PresentParticiple, target.PresentParticiple);
        CopyCollection(source.ThirdPersonSingular, target.ThirdPersonSingular);
        CopyCollection(source.Comparative, target.Comparative);
        CopyCollection(source.Superlative, target.Superlative);
        CopyCollection(source.Tags, target.Tags);
        CopyCollection(source.Sentences, target.Sentences);
        target.Duration = source.ResultType == DictionaryResultType.NoResult
            ? TimeSpan.Zero
            : source.Duration;
        target.IsProcessing = source.IsProcessing;
    }

    private static void CopyCollection<T>(IEnumerable<T> source, ICollection<T> target)
    {
        foreach (var item in source)
            target.Add(item);
    }

    private sealed class OperationChannel
    {
        internal object SyncRoot { get; } = new();
        internal long OperationId { get; set; }
        internal TranslationResultContext? Context { get; set; }
    }

    private sealed class PropertyChangedSubscription(
        TranslateResult source,
        PropertyChangedEventHandler handler) : IDisposable
    {
        private TranslateResult? _source = source;
        private PropertyChangedEventHandler? _handler = handler;

        public void Dispose()
        {
            var currentSource = Interlocked.Exchange(ref _source, null);
            var currentHandler = Interlocked.Exchange(ref _handler, null);
            if (currentSource != null && currentHandler != null)
                currentSource.PropertyChanged -= currentHandler;
        }
    }
}
