using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace STranslate.Core;

/// <summary>
/// 翻译请求的文本预处理配置。
/// </summary>
[JsonConverter(typeof(TextPreprocessingSettingsJsonConverter))]
public sealed class TextPreprocessingSettings
{
    /// <summary>
    /// 是否启用翻译前替换。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 按列表顺序执行的替换规则。
    /// </summary>
    public List<TextReplacementRule> Rules { get; set; } = [];

    internal JsonElement? InvalidConfiguration { get; set; }

    internal TextPreprocessingSettings Clone()
    {
        return new TextPreprocessingSettings
        {
            IsEnabled = IsEnabled,
            Rules = Rules?.Select(rule => rule?.Clone()!).ToList()!,
            InvalidConfiguration = InvalidConfiguration
        };
    }
}

/// <summary>
/// 一条翻译前正则替换规则。
/// </summary>
public partial class TextReplacementRule : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _pattern = string.Empty;
    private string _replacement = string.Empty;
    private bool _isEnabled = true;
    private bool _ignoreCase;
    private bool _multiline;
    private bool _singleline;

    /// <summary>
    /// 用于诊断和后续编辑定位的稳定标识。
    /// </summary>
    public string Id { get => _id; set => SetProperty(ref _id, value); }

    /// <summary>
    /// 规则显示名称。
    /// </summary>
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>
    /// .NET 正则表达式。
    /// </summary>
    public string Pattern { get => _pattern; set => SetProperty(ref _pattern, value); }

    /// <summary>
    /// 替换文本，支持 .NET 捕获组替换语法。
    /// </summary>
    public string Replacement { get => _replacement; set => SetProperty(ref _replacement, value); }

    /// <summary>
    /// 是否执行此规则。
    /// </summary>
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    /// <summary>
    /// 是否忽略大小写。
    /// </summary>
    public bool IgnoreCase { get => _ignoreCase; set => SetProperty(ref _ignoreCase, value); }

    /// <summary>
    /// 是否启用多行模式。
    /// </summary>
    public bool Multiline { get => _multiline; set => SetProperty(ref _multiline, value); }

    /// <summary>
    /// 是否启用单行模式。
    /// </summary>
    public bool Singleline { get => _singleline; set => SetProperty(ref _singleline, value); }

    internal TextReplacementRule Clone()
    {
        return new TextReplacementRule
        {
            Id = Id,
            Name = Name,
            Pattern = Pattern,
            Replacement = Replacement,
            IsEnabled = IsEnabled,
            IgnoreCase = IgnoreCase,
            Multiline = Multiline,
            Singleline = Singleline
        };
    }
}

/// <summary>
/// 正则处理的资源限制。
/// </summary>
internal readonly record struct TextPreprocessingLimits(
    TimeSpan RegexTimeout,
    int MaxOutputLength,
    TimeSpan? TotalTimeout = null)
{
    internal TimeSpan OperationTimeout => TotalTimeout ?? TimeSpan.FromMilliseconds(500);
    internal static TextPreprocessingLimits Default => new(
        TimeSpan.FromMilliseconds(100),
        1_000_000);

    internal void Validate()
    {
        if (RegexTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RegexTimeout));
        if (MaxOutputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputLength));
        if (OperationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TotalTimeout));
    }
}

/// <summary>
/// 单条规则的命中摘要。
/// </summary>
internal sealed record TextReplacementMatchSummary(
    string RuleId,
    string RuleName,
    int MatchCount);

/// <summary>
/// 本次翻译使用的原文和处理后请求文本。
/// </summary>
internal sealed record PreparedTranslationInput(
    string OriginalText,
    string RequestText,
    string RulesFingerprint,
    IReadOnlyList<TextReplacementMatchSummary> MatchSummaries)
{
    internal bool IsChanged => !string.Equals(OriginalText, RequestText, StringComparison.Ordinal);
    internal bool IsPreprocessed => !string.IsNullOrEmpty(RulesFingerprint);
    internal int TotalMatchCount => MatchSummaries.Sum(summary => summary.MatchCount);
}

/// <summary>
/// 翻译前替换失败时抛出的错误。
/// </summary>
internal sealed class TextPreprocessingException : Exception
{
    internal TextPreprocessingException(
        string ruleId,
        string ruleName,
        string message,
        Exception? innerException = null,
        string errorKind = "InvalidPattern")
        : base(message, innerException)
    {
        RuleId = ruleId;
        RuleName = ruleName;
        ErrorKind = errorKind;
    }

    internal string RuleId { get; }
    internal string RuleName { get; }
    internal string ErrorKind { get; }
    internal int? ErrorOffset => (InnerException as RegexParseException)?.Offset;
}

/// <summary>
/// 在翻译请求发出前执行规则快照和正则替换。
/// </summary>
public sealed class TextPreprocessingService
{
    private const string FingerprintVersion = "regex-v1";
    private readonly TextPreprocessingLimits _limits;
    private readonly Dictionary<(string Pattern, string Replacement, RegexOptions Options), CompiledTextReplacement> _cache = [];
    private readonly object _cacheLock = new();

    public TextPreprocessingService()
        : this(null)
    {
    }

    internal TextPreprocessingService(TextPreprocessingLimits? limits)
    {
        _limits = limits ?? TextPreprocessingLimits.Default;
        _limits.Validate();
    }

    internal PreparedTranslationInput Prepare(
        string text,
        TextPreprocessingSettings? settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var snapshot = Capture(settings);
        var enabledRules = snapshot.IsEnabled
            ? snapshot.Rules.Where(rule => rule.IsEnabled).ToList()
            : [];
        foreach (var rule in enabledRules)
            ValidateRuleFields(rule);
        var fingerprint = CreateFingerprint(snapshot.IsEnabled, enabledRules);

        cancellationToken.ThrowIfCancellationRequested();
        if (enabledRules.Count == 0)
        {
            return new PreparedTranslationInput(text, text, fingerprint, []);
        }

        var currentText = text;
        var summaries = new List<TextReplacementMatchSummary>(enabledRules.Count);
        foreach (var rule in enabledRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentText = ApplyRule(currentText, rule, summaries, started, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedTranslationInput(text, currentText, fingerprint, summaries.AsReadOnly());
    }

    internal static TextPreprocessingSettings Capture(TextPreprocessingSettings? settings)
    {
        if (settings is null || settings.InvalidConfiguration.HasValue ||
            settings.Rules is null || settings.Rules.Any(rule => rule is null))
            throw new TextPreprocessingException(string.Empty, string.Empty,
                "翻译前替换配置损坏，请在设置中修正后重试。", errorKind: "InvalidConfiguration");
        return settings.Clone();
    }

    internal Task<PreparedTranslationInput> PrepareAsync(
        string text, TextPreprocessingSettings? settings, CancellationToken cancellationToken = default)
    {
        var snapshot = Capture(settings);
        return Task.Run(() => Prepare(text, snapshot, cancellationToken), cancellationToken);
    }

    internal void Validate(TextPreprocessingSettings settings)
    {
        var snapshot = Capture(settings);
        foreach (var rule in snapshot.Rules)
            GetCompiledRule(rule);
    }

    private static void ValidateRuleFields(TextReplacementRule rule)
    {
        if (string.IsNullOrEmpty(rule.Pattern))
            throw new TextPreprocessingException(rule.Id, RuleName(rule),
                $"翻译前替换规则“{RuleName(rule)}”的正则表达式不能为空。");
        if (rule.Replacement is null || string.IsNullOrWhiteSpace(rule.Id))
            throw new TextPreprocessingException(rule.Id, RuleName(rule),
                $"翻译前替换规则“{RuleName(rule)}”的配置无效。", errorKind: "InvalidConfiguration");
    }

    private static string RuleName(TextReplacementRule rule) =>
        string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name;

    private CompiledTextReplacement GetCompiledRule(TextReplacementRule rule)
    {
        ValidateRuleFields(rule);
        var key = (rule.Pattern, rule.Replacement, CreateRegexOptions(rule));
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var compiled))
                return compiled;
            try
            {
                compiled = new CompiledTextReplacement(new Regex(key.Pattern, key.Item3, _limits.RegexTimeout), key.Replacement);
            }
            catch (ArgumentException ex)
            {
                throw new TextPreprocessingException(rule.Id, RuleName(rule),
                    $"翻译前替换规则“{RuleName(rule)}”的正则表达式无效。", ex);
            }
            if (_cache.Count >= 128)
                _cache.Clear();
            _cache.Add(key, compiled);
            return compiled;
        }
    }

    private string ApplyRule(
        string text,
        TextReplacementRule rule,
        ICollection<TextReplacementMatchSummary> summaries,
        long started,
        CancellationToken cancellationToken)
    {
        var ruleName = RuleName(rule);
        var ruleStarted = Stopwatch.GetTimestamp();
        var compiled = GetCompiledRule(rule);
        var builder = new StringBuilder(Math.Min(text.Length, _limits.MaxOutputLength));
        var lastIndex = 0;
        var matchCount = 0;
        try
        {
            CheckBudget();
            for (var match = compiled.Regex.Match(text); match.Success; match = match.NextMatch())
            {
                CheckBudget();
                AppendWithLimit(builder, text.AsSpan(lastIndex, match.Index - lastIndex), rule, ruleName);
                foreach (var part in compiled.Parts)
                {
                    CheckBudget();
                    AppendWithLimit(builder, part.Resolve(text, match, compiled.Replacement), rule, ruleName);
                }
                lastIndex = match.Index + match.Length;
                matchCount++;
            }

            CheckBudget();
            if (matchCount > 0)
                AppendWithLimit(builder, text.AsSpan(lastIndex), rule, ruleName);
        }
        catch (TextPreprocessingException)
        {
            throw;
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new TextPreprocessingException(
                rule.Id,
                ruleName,
                $"翻译前替换规则“{ruleName}”执行超时。",
                ex, "Timeout");
        }
        catch (ArgumentException ex)
        {
            throw new TextPreprocessingException(
                rule.Id,
                ruleName,
                $"翻译前替换规则“{ruleName}”的替换文本无效。",
                ex);
        }

        summaries.Add(new TextReplacementMatchSummary(rule.Id, ruleName, matchCount));
        return matchCount == 0 ? text : builder.ToString();

        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > _limits.OperationTimeout ||
                Stopwatch.GetElapsedTime(ruleStarted) > _limits.RegexTimeout)
                throw new TextPreprocessingException(rule.Id, ruleName,
                    $"翻译前替换规则“{ruleName}”执行超时。", errorKind: "Timeout");
        }
    }

    private void AppendWithLimit(
        StringBuilder builder,
        ReadOnlySpan<char> value,
        TextReplacementRule rule,
        string ruleName)
    {
        if (value.Length > _limits.MaxOutputLength - builder.Length)
        {
            throw new TextPreprocessingException(
                rule.Id,
                ruleName,
                $"翻译前替换规则“{ruleName}”的结果超过长度限制。", errorKind: "OutputLimit");
        }

        builder.Append(value);
    }

    private static RegexOptions CreateRegexOptions(TextReplacementRule rule)
    {
        var options = RegexOptions.CultureInvariant;
        if (rule.IgnoreCase) options |= RegexOptions.IgnoreCase;
        if (rule.Multiline) options |= RegexOptions.Multiline;
        if (rule.Singleline) options |= RegexOptions.Singleline;
        return options;
    }

    private static string CreateFingerprint(
        bool isEnabled,
        IReadOnlyList<TextReplacementRule> enabledRules)
    {
        if (!isEnabled || enabledRules.Count == 0)
            return string.Empty;

        var builder = new StringBuilder(FingerprintVersion);
        builder.Append('\n').Append(isEnabled ? "enabled" : "disabled");
        foreach (var rule in enabledRules)
        {
            builder.Append('\n').Append(rule.Pattern.Length).Append(':').Append(rule.Pattern);
            builder.Append('\n').Append(rule.Replacement.Length).Append(':').Append(rule.Replacement);
            builder.Append('\n').Append(rule.IgnoreCase ? '1' : '0');
            builder.Append(rule.Multiline ? '1' : '0');
            builder.Append(rule.Singleline ? '1' : '0');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
