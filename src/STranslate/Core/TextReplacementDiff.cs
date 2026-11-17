using System.Globalization;
using System.Text;

namespace STranslate.Core;

internal sealed record TextReplacementChange(int Start, int Length, string Replacement)
{
    internal IReadOnlyList<TextReplacementVariant>? Variants { get; init; }
    internal bool IsDeletion => Replacement.Length == 0 &&
        (Variants is null || Variants.All(variant => variant.Replacement == string.Empty && variant.IsChanged));
}

internal sealed record TextReplacementVariant(string ServiceNames, string? Replacement, bool IsChanged);

/// <summary>比较原文与实际发送文本，不重新执行规则；索引始终落在文本元素边界。</summary>
internal static class TextReplacementDiff
{
    private const long MaxComparisonCells = 1_000_000;

    /// <summary>在一份原文上合并所有服务的变化范围，并将各请求投影到相同范围。</summary>
    internal static IReadOnlyList<TextReplacementChange> CreateForRequests(
        string original, IReadOnlyList<TextReplacementRequest> requests, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0) return [];
        if (requests.All(request => request.RequestText == requests[0].RequestText))
            return requests[0].RequestText is { } shared ? Create(original, shared, cancellationToken) : [];

        var sources = requests.Select(request =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (Request: request, Changes: request.RequestText is { } text
                ? Create(original, text, cancellationToken) : []);
        }).ToArray();
        var ranges = new List<(int Start, int End)>();
        foreach (var change in sources.SelectMany(source => source.Changes).OrderBy(change => change.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = change.Start + change.Length;
            // 相交或相邻变化合成一个范围，插入点不会在两个 Tooltip 中重复出现。
            if (ranges.Count > 0 && change.Start <= ranges[^1].End)
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            else ranges.Add((change.Start, end));
        }

        return ranges.Select(range =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variants = sources.Select(source =>
            {
                if (source.Request.RequestText is null)
                    return new TextReplacementVariant(source.Request.ServiceNames, null, false);
                var builder = new StringBuilder();
                var cursor = range.Start;
                var changed = false;
                foreach (var change in source.Changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (change.Start < range.Start || change.Start + change.Length > range.End) continue;
                    builder.Append(original.AsSpan(cursor, change.Start - cursor)).Append(change.Replacement);
                    cursor = change.Start + change.Length;
                    changed = true;
                }
                builder.Append(original.AsSpan(cursor, range.End - cursor));
                return new TextReplacementVariant(source.Request.ServiceNames, builder.ToString(), changed);
            }).ToArray();
            return new TextReplacementChange(range.Start, range.End - range.Start,
                variants.First(variant => variant.Replacement is not null).Replacement!) { Variants = variants };
        }).ToArray();
    }

    internal static IReadOnlyList<TextReplacementChange> Create(
        string original, string request, CancellationToken cancellationToken = default)
    {
        if (original == request) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var left = StringInfo.ParseCombiningCharacters(original).Append(original.Length).ToArray();
        var right = StringInfo.ParseCombiningCharacters(request).Append(request.Length).ToArray();
        var prefix = 0;
        var leftEnd = left.Length - 1;
        var rightEnd = right.Length - 1;
        while (prefix < leftEnd && prefix < rightEnd && Equal(prefix, prefix)) prefix++;
        while (leftEnd > prefix && rightEnd > prefix && Equal(leftEnd - 1, rightEnd - 1))
        {
            leftEnd--;
            rightEnd--;
        }

        var rows = leftEnd - prefix;
        var columns = rightEnd - prefix;
        // 大段文本采用一个准确的变化区间，避免展示层发生无界二次方运算。
        if (rows == 0 || columns == 0 || (long)(rows + 1) * (columns + 1) > MaxComparisonCells)
            return [Change(prefix, leftEnd, prefix, rightEnd)];

        var lengths = new int[rows + 1, columns + 1];
        for (var i = rows - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var j = columns - 1; j >= 0; j--)
                lengths[i, j] = Equal(prefix + i, prefix + j)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        }

        var changes = new List<TextReplacementChange>();
        var x = 0;
        var y = 0;
        while (x < rows || y < columns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (x < rows && y < columns && Equal(prefix + x, prefix + y))
            {
                x++;
                y++;
                continue;
            }
            var startX = x;
            var startY = y;
            do
            {
                if (x < rows && (y == columns || lengths[x + 1, y] >= lengths[x, y + 1])) x++;
                else y++;
            } while ((x < rows || y < columns) &&
                     !(x < rows && y < columns && Equal(prefix + x, prefix + y)));
            changes.Add(Change(prefix + startX, prefix + x, prefix + startY, prefix + y));
        }
        return changes;

        bool Equal(int i, int j) => original.AsSpan(left[i], left[i + 1] - left[i])
            .SequenceEqual(request.AsSpan(right[j], right[j + 1] - right[j]));

        TextReplacementChange Change(int from, int to, int replacementFrom, int replacementTo) =>
            new(left[from], left[to] - left[from], request[right[replacementFrom]..right[replacementTo]]);
    }
}
