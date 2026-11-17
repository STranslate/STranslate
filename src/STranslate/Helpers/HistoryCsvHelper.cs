using STranslate.Core;
using STranslate.Plugin;
using System.Text;

namespace STranslate.Helpers;

/// <summary>
/// 负责把历史记录转换为可读的宽表 CSV 文本。
/// </summary>
public static class HistoryCsvHelper
{
    private static readonly char[] CsvEscapeChars = [',', '"', '\r', '\n'];

    /// <summary>
    /// 统一的 CSV 输出编码（UTF-8 with BOM），用于兼容 Excel 打开中文。
    /// </summary>
    public static readonly Encoding Utf8BomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// 构建历史记录 CSV 文本。
    /// </summary>
    /// <param name="items">要导出的历史记录。</param>
    /// <param name="languageDisplayNameResolver">语言代码到显示名的解析委托。</param>
    /// <returns>完整 CSV 字符串。</returns>
    public static string BuildCsv(
        IReadOnlyList<HistoryModel> items,
        Func<string?, string> languageDisplayNameResolver,
        bool includeRequestDetails = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(languageDisplayNameResolver);

        var engineRows = items.Select(item => BuildExportEngineItems(item, languageDisplayNameResolver)).ToList();
        var maxEngineCount = engineRows.Count == 0 ? 0 : engineRows.Max(row => row.Count);

        var headers = new List<string>
        {
            "序号",
            "记录ID",
            "时间",
            "原文语言",
            "目标语言",
            "翻译原文"
        };

        for (var index = 1; index <= maxEngineCount; index++)
        {
            headers.Add($"翻译引擎{index}");
            headers.Add($"翻译结果{index}");
            if (includeRequestDetails)
            {
                headers.Add($"实际发送文本{index}");
                headers.Add($"实际源语言{index}");
                headers.Add($"实际目标语言{index}");
            }
        }

        var csvBuilder = new StringBuilder();
        AppendCsvRow(csvBuilder, headers);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = new List<string>
            {
                (index + 1).ToString(),
                item.Id.ToString(),
                item.Time.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                BuildLanguageDisplayForExport(item.SourceLang, item.EffectiveSourceLang, languageDisplayNameResolver),
                BuildLanguageDisplayForExport(item.TargetLang, item.EffectiveTargetLang, languageDisplayNameResolver),
                item.SourceText ?? string.Empty
            };

            var engineItems = engineRows[index];
            for (var engineIndex = 0; engineIndex < maxEngineCount; engineIndex++)
            {
                if (engineIndex < engineItems.Count)
                {
                    row.Add(engineItems[engineIndex].EngineName);
                    row.Add(engineItems[engineIndex].ResultText);
                    if (includeRequestDetails)
                    {
                        row.Add(engineItems[engineIndex].RequestText);
                        row.Add(engineItems[engineIndex].SourceLanguage);
                        row.Add(engineItems[engineIndex].TargetLanguage);
                    }
                }
                else
                {
                    row.Add(string.Empty);
                    row.Add(string.Empty);
                    if (includeRequestDetails) row.AddRange([string.Empty, string.Empty, string.Empty]);
                }
            }

            AppendCsvRow(csvBuilder, row);
        }

        return csvBuilder.ToString();
    }

    private static List<ExportEngineItem> BuildExportEngineItems(HistoryModel history, Func<string?, string> languageDisplayNameResolver)
    {
        var result = new List<ExportEngineItem>();

        foreach (var data in history.Data)
        {
            var engineName = ResolveEngineName(data);
            var resultText = BuildResultText(data);
            result.Add(new ExportEngineItem(engineName, resultText,
                data.RequestText ?? (string.IsNullOrEmpty(data.PreprocessingFingerprint) ? history.SourceText : string.Empty),
                languageDisplayNameResolver(data.EffectiveSourceLang ?? history.EffectiveSourceLang ?? history.SourceLang),
                languageDisplayNameResolver(data.EffectiveTargetLang ?? history.EffectiveTargetLang ?? history.TargetLang)));
        }

        return result;
    }

    private static string ResolveEngineName(HistoryData data)
    {
        if (!string.IsNullOrWhiteSpace(data.ServiceDisplayName))
            return data.ServiceDisplayName;

        return "Unknown";
    }

    private static string BuildResultText(HistoryData data)
    {
        if (data.DictResult != null)
            return BuildDictionarySummary(data.DictResult);

        return BuildTranslationText(data);
    }

    /// <summary>
    /// 失败时统一留空，避免把错误文案写入导出结果，方便后续筛选有效译文。
    /// </summary>
    private static string BuildTranslationText(HistoryData data)
    {
        if (data.TransResult is not { IsSuccess: true } transResult || string.IsNullOrWhiteSpace(transResult.Text))
            return string.Empty;

        var mainText = transResult.Text;
        if (data.TransBackResult is { IsSuccess: true } backResult && !string.IsNullOrWhiteSpace(backResult.Text))
            return $"{mainText}{Environment.NewLine}回译: {backResult.Text}";

        return mainText;
    }

    private static string BuildDictionarySummary(DictionaryResult dictResult)
    {
        if (dictResult.ResultType != DictionaryResultType.Success)
            return string.Empty;

        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(dictResult.Text))
            lines.Add($"词条: {dictResult.Text.Trim()}");

        var meanings = BuildDictionaryMeanText(dictResult);
        if (!string.IsNullOrWhiteSpace(meanings))
            lines.Add($"释义: {meanings}");

        var sentences = string.Join(
            Environment.NewLine,
            dictResult.Sentences.Where(sentence => !string.IsNullOrWhiteSpace(sentence))
        );
        if (!string.IsNullOrWhiteSpace(sentences))
            lines.Add($"例句: {sentences}");

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

    private static string BuildDictionaryMeanText(DictionaryResult dictResult)
    {
        var lines = dictResult.DictMeans
            .Select(mean =>
            {
                var partOfSpeech = mean.PartOfSpeech?.Trim() ?? string.Empty;
                var means = string.Join("；", mean.Means.Where(m => !string.IsNullOrWhiteSpace(m)));

                if (string.IsNullOrWhiteSpace(partOfSpeech) && string.IsNullOrWhiteSpace(means))
                    return string.Empty;
                if (string.IsNullOrWhiteSpace(partOfSpeech))
                    return means;
                if (string.IsNullOrWhiteSpace(means))
                    return partOfSpeech;

                return $"{partOfSpeech} {means}";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 导出语言显示与历史页保持一致：
    /// 原始语言为 Auto 且有效语言存在时显示“Auto(有效语言)”，否则按原始语言显示。
    /// </summary>
    private static string BuildLanguageDisplayForExport(
        string? rawLanguage,
        string? effectiveLanguage,
        Func<string?, string> languageDisplayNameResolver)
    {
        var raw = string.IsNullOrWhiteSpace(rawLanguage) ? null : rawLanguage;
        if (!string.Equals(raw, nameof(LangEnum.Auto), StringComparison.OrdinalIgnoreCase))
            return languageDisplayNameResolver(raw);

        var autoText = languageDisplayNameResolver(nameof(LangEnum.Auto));
        if (!Enum.TryParse<LangEnum>(effectiveLanguage, ignoreCase: true, out var effectiveEnum) ||
            effectiveEnum == LangEnum.Auto)
            return autoText;

        return $"{autoText}({languageDisplayNameResolver(effectiveEnum.ToString())})";
    }

    private static void AppendCsvRow(StringBuilder csvBuilder, IReadOnlyList<string> fields)
    {
        csvBuilder.AppendJoin(',', fields.Select(EscapeCsvField));
        csvBuilder.Append("\r\n");
    }

    private static string EscapeCsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.IndexOfAny(CsvEscapeChars) < 0)
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private readonly record struct ExportEngineItem(string EngineName, string ResultText,
        string RequestText, string SourceLanguage, string TargetLanguage);
}
