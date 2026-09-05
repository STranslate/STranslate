using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace STranslate.Core;

internal static class OcrWordBuilder
{
    internal static ObservableCollection<OcrWord> CreateFromLayoutBlocks(IReadOnlyList<OcrLayoutBlock> blocks) =>
        CreateIndexedCollectionFromGroups(blocks.Select(block => CreateFromOcrContents(block.SourceContents)),
            separateParagraphs: true);

    public static ObservableCollection<OcrWord> CreateFromOcrContents(IEnumerable<OcrContent>? contents)
    {
        if (contents == null)
            return [];

        var blocks = contents
            .Where(content => !string.IsNullOrEmpty(content.Text) &&
                              content.BoxPoints != null &&
                              content.BoxPoints.Count > 0)
            .Select(content => new OcrTextBlock(content.Text, CalculateBoundingBox(content.BoxPoints!)))
            .Where(block => IsSelectableBounds(block.BoundingBox))
            .OrderBy(block => block.BoundingBox.Top)
            .ThenBy(block => block.BoundingBox.Left)
            .ToList();

        var words = new List<OcrWord>();
        OcrTextBlock? previousBlock = null;
        foreach (var block in blocks)
        {
            if (previousBlock != null)
            {
                var separator = GetSeparator(previousBlock, block);
                if (!string.IsNullOrEmpty(separator))
                    words.Add(CreateTextOnlyWord(separator));
            }

            var avgCharWidth = block.BoundingBox.Width / Math.Max(block.Text.Length, 1);
            for (var i = 0; i < block.Text.Length; i++)
            {
                words.Add(new OcrWord
                {
                    Text = block.Text[i].ToString(),
                    BoundingBox = new Rect(
                        block.BoundingBox.Left + avgCharWidth * i,
                        block.BoundingBox.Top,
                        avgCharWidth,
                        block.BoundingBox.Height)
                });
            }

            previousBlock = block;
        }

        return CreateIndexedCollection(words, preserveOrder: true);
    }

    public static IReadOnlyList<OcrWord> CreateFromFormattedText(
        string text,
        FormattedText formattedText,
        Point origin,
        Rect clipRect,
        double scaleFactor, bool preserveClippedText = false)
    {
        if (string.IsNullOrEmpty(text) ||
            clipRect.IsEmpty ||
            clipRect.Width <= 0 ||
            clipRect.Height <= 0 ||
            scaleFactor <= 0)
        {
            return [];
        }

        var words = new List<OcrWord>();
        for (var i = 0; i < text.Length; i++)
        {
            var geometry = formattedText.BuildHighlightGeometry(origin, i, 1);
            var bounds = geometry?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                // 裁剪/省略只影响命中框，不能删掉复制全文或整段所需的逻辑字符。
                if (preserveClippedText || char.IsWhiteSpace(text[i]))
                    words.Add(CreateTextOnlyWord(text[i].ToString()));
                continue;
            }

            var clippedBounds = Rect.Intersect(bounds, clipRect);
            if (clippedBounds.IsEmpty || clippedBounds.Width <= 0 || clippedBounds.Height <= 0)
            {
                if (preserveClippedText)
                    words.Add(CreateTextOnlyWord(text[i].ToString()));
                continue;
            }

            words.Add(new OcrWord
            {
                Text = text[i].ToString(),
                BoundingBox = ScaleRect(clippedBounds, scaleFactor)
            });
        }

        return words;
    }

    public static ObservableCollection<OcrWord> CreateIndexedCollection(IEnumerable<OcrWord> words, bool preserveOrder = false)
    {
        var indexedWords = words
            .Where(word => !string.IsNullOrEmpty(word.Text) &&
                           (IsSelectableBounds(word.BoundingBox) || word.BoundingBox.IsEmpty))
            .ToList();

        if (!preserveOrder)
        {
            indexedWords = indexedWords
                .OrderBy(word => IsSelectableBounds(word.BoundingBox) ? word.BoundingBox.Top : double.MaxValue)
                .ThenBy(word => IsSelectableBounds(word.BoundingBox) ? word.BoundingBox.Left : double.MaxValue)
                .ToList();
        }

        var nextVisualLineIndex = 0;
        AssignVisualLineIndexes(indexedWords, ref nextVisualLineIndex);
        AssignTextIndexes(indexedWords);

        return new ObservableCollection<OcrWord>(indexedWords);
    }

    public static ObservableCollection<OcrWord> CreateIndexedCollectionFromGroups(
        IEnumerable<IEnumerable<OcrWord>> wordGroups, bool separateParagraphs = false)
    {
        var indexedWords = new List<OcrWord>();
        var nextVisualLineIndex = 0;
        var paragraphIndex = 0;
        foreach (var wordGroup in wordGroups)
        {
            var groupWords = wordGroup
                .Where(word => !string.IsNullOrEmpty(word.Text) &&
                               (IsSelectableBounds(word.BoundingBox) || word.BoundingBox.IsEmpty))
                .ToList();

            if (groupWords.Count == 0)
                continue;
            if (separateParagraphs && indexedWords.Count > 0)
                indexedWords.Add(CreateTextOnlyWord(Environment.NewLine));
            foreach (var word in groupWords)
                word.ParagraphIndex = paragraphIndex;
            paragraphIndex++;
            AssignVisualLineIndexes(groupWords, ref nextVisualLineIndex);
            indexedWords.AddRange(groupWords);
        }

        AssignTextIndexes(indexedWords);
        return new ObservableCollection<OcrWord>(indexedWords);
    }

    private static Rect CalculateBoundingBox(IReadOnlyCollection<BoxPoint> boxPoints)
    {
        var minX = boxPoints.Min(point => point.X);
        var minY = boxPoints.Min(point => point.Y);
        var maxX = boxPoints.Max(point => point.X);
        var maxY = boxPoints.Max(point => point.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rect ScaleRect(Rect rect, double scaleFactor) =>
        new(
            rect.Left * scaleFactor,
            rect.Top * scaleFactor,
            rect.Width * scaleFactor,
            rect.Height * scaleFactor);

    private static OcrWord CreateTextOnlyWord(string text) =>
        new()
        {
            Text = text,
            BoundingBox = Rect.Empty
        };

    private static string GetSeparator(OcrTextBlock previous, OcrTextBlock current)
    {
        if (IsNextLine(previous.BoundingBox, current.BoundingBox))
            return Environment.NewLine;

        if (NeedsSpaceBetween(previous.Text, current.Text) &&
            current.BoundingBox.Left > previous.BoundingBox.Right)
        {
            return " ";
        }

        return string.Empty;
    }

    private static bool IsNextLine(Rect previous, Rect current)
    {
        var previousCenterY = previous.Top + previous.Height / 2;
        var currentCenterY = current.Top + current.Height / 2;
        var sameLineTolerance = Math.Max(previous.Height, current.Height) * 0.6;
        return Math.Abs(currentCenterY - previousCenterY) > sameLineTolerance;
    }

    private static bool NeedsSpaceBetween(string previous, string current) =>
        previous.Length > 0 &&
        current.Length > 0 &&
        !char.IsWhiteSpace(previous[^1]) &&
        !char.IsWhiteSpace(current[0]);

    private static bool IsSelectableBounds(Rect rect) =>
        !rect.IsEmpty && rect.Width > 0 && rect.Height > 0;

    private static void AssignVisualLineIndexes(
        IReadOnlyList<OcrWord> words,
        ref int nextVisualLineIndex)
    {
        OcrWord? previousSelectableWord = null;
        var currentVisualLineIndex = -1;
        foreach (var word in words)
        {
            word.VisualLineIndex = -1;
            if (!IsSelectableBounds(word.BoundingBox))
                continue;

            if (previousSelectableWord == null ||
                IsNextLine(previousSelectableWord.BoundingBox, word.BoundingBox))
            {
                currentVisualLineIndex = nextVisualLineIndex++;
            }

            word.VisualLineIndex = currentVisualLineIndex;
            previousSelectableWord = word;
        }
    }

    private static void AssignTextIndexes(IReadOnlyList<OcrWord> words)
    {
        var currentIndex = 0;
        foreach (var word in words)
        {
            word.StartIndexInFullText = currentIndex;
            currentIndex += word.Text.Length;
        }
    }

    private sealed record OcrTextBlock(string Text, Rect BoundingBox);
}
