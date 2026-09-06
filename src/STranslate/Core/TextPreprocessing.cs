using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace STranslate.Core;

/// <summary>
/// 翻译请求的文本预处理配置。
/// </summary>
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

    internal TextPreprocessingSettings Clone()
    {
        return new TextPreprocessingSettings
        {
            IsEnabled = IsEnabled,
            Rules = Rules?.Select(rule => rule.Clone()).ToList() ?? []
        };
    }
}

/// <summary>
/// 一条翻译前正则替换规则。
/// </summary>
public sealed class TextReplacementRule
{
    /// <summary>
    /// 用于诊断和后续编辑定位的稳定标识。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 规则显示名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// .NET 正则表达式。
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// 替换文本，支持 .NET 捕获组替换语法。
    /// </summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>
    /// 是否执行此规则。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 是否忽略大小写。
    /// </summary>
    public bool IgnoreCase { get; set; }

    /// <summary>
    /// 是否启用多行模式。
    /// </summary>
    public bool Multiline { get; set; }

    /// <summary>
    /// 是否启用单行模式。
    /// </summary>
    public bool Singleline { get; set; }

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
    int MaxOutputLength)
{
    internal static TextPreprocessingLimits Default => new(
        TimeSpan.FromMilliseconds(100),
        1_000_000);

    internal void Validate()
    {
        if (RegexTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RegexTimeout));
        if (MaxOutputLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputLength));
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
        Exception? innerException = null)
        : base(message, innerException)
    {
        RuleId = ruleId;
        RuleName = ruleName;
    }

    internal string RuleId { get; }
    internal string RuleName { get; }
}

/// <summary>
/// 在翻译请求发出前执行规则快照和正则替换。
/// </summary>
public sealed class TextPreprocessingService
{
    private const string FingerprintVersion = "regex-v1";
    private readonly TextPreprocessingLimits _limits;

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

        var snapshot = (settings ?? new TextPreprocessingSettings()).Clone();
        var enabledRules = snapshot.IsEnabled
            ? snapshot.Rules.Where(rule => rule is not null && rule.IsEnabled).ToList()
            : [];
        var fingerprint = CreateFingerprint(snapshot.IsEnabled, enabledRules);

        cancellationToken.ThrowIfCancellationRequested();
        if (enabledRules.Count == 0 || text.Length == 0)
        {
            return new PreparedTranslationInput(text, text, fingerprint, []);
        }

        var currentText = text;
        var summaries = new List<TextReplacementMatchSummary>(enabledRules.Count);
        foreach (var rule in enabledRules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentText = ApplyRule(currentText, rule, summaries, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedTranslationInput(text, currentText, fingerprint, summaries);
    }

    private string ApplyRule(
        string text,
        TextReplacementRule rule,
        ICollection<TextReplacementMatchSummary> summaries,
        CancellationToken cancellationToken)
    {
        var ruleName = string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name;
        if (string.IsNullOrEmpty(rule.Pattern))
        {
            throw new TextPreprocessingException(
                rule.Id,
                ruleName,
                $"翻译前替换规则“{ruleName}”的正则表达式不能为空。");
        }

        Regex regex;
        try
        {
            regex = new Regex(
                rule.Pattern,
                CreateRegexOptions(rule),
                _limits.RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new TextPreprocessingException(
                rule.Id,
                ruleName,
                $"翻译前替换规则“{ruleName}”的正则表达式无效。",
                ex);
        }

        var replacement = rule.Replacement ?? string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, _limits.MaxOutputLength));
        var lastIndex = 0;
        var matchCount = 0;
        try
        {
            foreach (Match match in regex.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendWithLimit(builder, text.AsSpan(lastIndex, match.Index - lastIndex), rule, ruleName);
                AppendWithLimit(builder, match.Result(replacement), rule, ruleName);
                lastIndex = match.Index + match.Length;
                matchCount++;
            }

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
                ex);
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
                $"翻译前替换规则“{ruleName}”的结果超过长度限制。");
        }

        builder.Append(value);
    }

    private void AppendWithLimit(
        StringBuilder builder,
        string value,
        TextReplacementRule rule,
        string ruleName)
        => AppendWithLimit(builder, value.AsSpan(), rule, ruleName);

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
