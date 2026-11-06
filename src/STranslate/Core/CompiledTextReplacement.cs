using System.Globalization;
using System.Text.RegularExpressions;

namespace STranslate.Core;

// 将替换模板拆成源文本切片，避免 Match.Result 在长度校验前展开巨大的捕获组。
internal sealed class CompiledTextReplacement
{
    internal Regex Regex { get; }
    internal string Replacement { get; }
    internal IReadOnlyList<ReplacementPart> Parts { get; }

    internal CompiledTextReplacement(Regex regex, string replacement)
    {
        Regex = regex;
        Replacement = replacement;
        var parts = new List<ReplacementPart>();
        var literalStart = 0;
        for (var index = 0; index < replacement.Length - 1; index++)
        {
            if (replacement[index] != '$')
                continue;
            var end = index + 2;
            var kind = replacement[index + 1];
            var group = -1;
            if (kind == '{')
            {
                var closing = replacement.IndexOf('}', index + 2);
                if (closing < 0)
                    continue;
                var name = replacement.Substring(index + 2, closing - index - 2);
                if (name.Length > 0 && name.All(char.IsAsciiDigit))
                {
                    if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out group))
                        throw new ArgumentException("捕获组编号超过范围。");
                    if (regex.GroupNameFromNumber(group).Length == 0)
                        continue;
                }
                else
                    group = regex.GroupNumberFromName(name);
                if (group < 0)
                    continue;
                kind = 'g';
                end = closing + 1;
            }
            else if (char.IsAsciiDigit(kind))
            {
                while (end < replacement.Length && char.IsAsciiDigit(replacement[end]))
                    end++;
                if (!int.TryParse(replacement.AsSpan(index + 1, end - index - 1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out group))
                    throw new ArgumentException("捕获组编号超过范围。");
                if (regex.GroupNameFromNumber(group).Length == 0)
                    continue;
                kind = 'g';
            }
            else if (kind == '+')
            {
                kind = 'g';
                group = regex.GetGroupNumbers()[^1];
            }
            else if (kind is not ('$' or '&' or '`' or '\'' or '_'))
                continue;

            if (index > literalStart)
                parts.Add(new('l', literalStart, index - literalStart));
            parts.Add(kind == '$' ? new('l', index, 1) : new(kind, group, 0));
            index = end - 1;
            literalStart = end;
        }
        if (literalStart < replacement.Length)
            parts.Add(new('l', literalStart, replacement.Length - literalStart));
        Parts = parts.AsReadOnly();
    }

    internal readonly record struct ReplacementPart(char Kind, int Index, int Length)
    {
        internal ReadOnlySpan<char> Resolve(string input, Match match, string replacement) => Kind switch
        {
            'l' => replacement.AsSpan(Index, Length),
            'g' => input.AsSpan(match.Groups[Index].Index, match.Groups[Index].Length),
            '&' => input.AsSpan(match.Index, match.Length),
            '`' => input.AsSpan(0, match.Index),
            '\'' => input.AsSpan(match.Index + match.Length),
            '_' => input.AsSpan(),
            _ => throw new InvalidOperationException()
        };
    }
}
