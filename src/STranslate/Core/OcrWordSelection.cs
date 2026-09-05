using System.Globalization;

namespace STranslate.Core;

internal static class OcrWordSelection
{
    internal static bool TryGetParagraphRange(IReadOnlyList<OcrWord>? words, OcrWord anchor,
        out int start, out int end)
    {
        start = int.MaxValue;
        end = -1;
        if (words == null || anchor.ParagraphIndex < 0)
            return false;
        foreach (var word in words)
        {
            if (word.ParagraphIndex != anchor.ParagraphIndex || word.Text.Length == 0)
                continue;
            start = Math.Min(start, word.StartIndexInFullText);
            end = Math.Max(end, word.EndIndexInFullText - 1);
        }
        return end >= start;
    }

    internal static bool TryGetWordRange(string text, int index, out int start, out int end)
    {
        start = 0;
        end = -1;
        if (index < 0 || index >= text.Length)
            return false;

        // 按 Unicode 文本元素取边界，避免切断代理对、emoji 和组合字符。
        var elements = StringInfo.ParseCombiningCharacters(text);
        var element = Array.BinarySearch(elements, index);
        if (element < 0)
            element = ~element - 1;
        var first = element;
        var last = element;
        var kind = Classify(elements[element]);
        while (first > 0 && Classify(elements[first - 1]) == kind)
            first--;
        while (last + 1 < elements.Length && Classify(elements[last + 1]) == kind)
            last++;
        start = elements[first];
        end = (last + 1 < elements.Length ? elements[last + 1] : text.Length) - 1;
        return true;

        int Classify(int position)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(text, position);
            if (char.IsWhiteSpace(text, position))
                return text[position] is '\r' or '\n' ? 3 : 0;
            return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.ConnectorPunctuation
                ? 1 : 2;
        }
    }

    internal static bool TryGetVisualLineRange(
        IReadOnlyList<OcrWord>? words,
        OcrWord? anchorWord,
        out int startIndex,
        out int endIndex)
    {
        startIndex = 0;
        endIndex = -1;
        if (words == null || anchorWord == null || anchorWord.VisualLineIndex < 0)
            return false;

        var hasLineWord = false;
        foreach (var word in words)
        {
            if (word.VisualLineIndex != anchorWord.VisualLineIndex || string.IsNullOrEmpty(word.Text))
                continue;

            var wordEndIndex = word.EndIndexInFullText - 1;
            if (!hasLineWord)
            {
                startIndex = word.StartIndexInFullText;
                endIndex = wordEndIndex;
                hasLineWord = true;
                continue;
            }

            startIndex = Math.Min(startIndex, word.StartIndexInFullText);
            endIndex = Math.Max(endIndex, wordEndIndex);
        }

        return hasLineWord;
    }
}
