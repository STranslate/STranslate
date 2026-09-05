using STranslate.Plugin;

namespace STranslate.Core;

internal static class OcrLayoutAnalyzer
{
    private const double MinSmartMergeConfidence = 0.48;

    internal static void Apply(OcrResult ocrResult, LayoutAnalysisMode mode)
    {
        if ((ocrResult.OcrContents.Count == 0 && ocrResult.Regions.Count == 0) || !Utilities.HasBoxPoints(ocrResult))
            return;

        var contents = AnalyzeBlocks(ocrResult, mode).Select(x => x.ToOcrContent()).ToList();
        ocrResult.OcrContents.Clear();
        ocrResult.OcrContents.AddRange(contents);
    }

    internal static List<OcrContent> Analyze(IReadOnlyList<OcrContent> contents, LayoutAnalysisMode mode)
        => AnalyzeBlocks(contents, mode).Select(x => x.ToOcrContent()).ToList();

    internal static List<OcrLayoutBlock> AnalyzeBlocks(OcrResult ocrResult, LayoutAnalysisMode mode)
    {
        return mode switch
        {
            LayoutAnalysisMode.NoMerge => CreateNoMergeBlocks(GetFlatContents(ocrResult)),
            LayoutAnalysisMode.Provider => HasProviderLayout(ocrResult)
                ? CreateProviderBlocks(ocrResult)
                : CreateProviderFallbackBlocks(ocrResult),
            LayoutAnalysisMode.Auto => HasProviderLayout(ocrResult)
                ? CreateProviderBlocks(ocrResult)
                : AnalyzeSmart(GetSmartSourceItems(ocrResult)),
            _ => AnalyzeSmart(GetSmartSourceItems(ocrResult))
        };
    }

    private static List<OcrLayoutBlock> CreateProviderFallbackBlocks(OcrResult ocrResult)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine("Provider layout requested but OCR result has no structured layout. Falling back to NoMerge.");
#endif
        return CreateNoMergeBlocks(GetFlatContents(ocrResult));
    }

    internal static List<OcrLayoutBlock> AnalyzeBlocks(IReadOnlyList<OcrContent> contents, LayoutAnalysisMode mode)
    {
        if (mode is LayoutAnalysisMode.NoMerge or LayoutAnalysisMode.Provider)
            return CreateNoMergeBlocks(contents);

        var items = CreateLayoutItems(contents);
        if (items.Count == 0)
            return CreateNoMergeBlocks(contents);

        return AnalyzeSmart(items);
    }

    private static List<OcrLayoutBlock> AnalyzeSmart(List<LayoutItem> items)
    {
        var lineSegments = BuildLineSegments(items);
        if (lineSegments.Count == 0)
            return [];

        var metrics = LayoutMetrics.From(lineSegments);
        var regions = BuildLayoutRegions(lineSegments, metrics);

        return OrderRegionsForReading(regions, metrics)
            .SelectMany(region => AnalyzeRegion(region, metrics))
            .ToList();
    }

    private static List<OcrLayoutBlock> AnalyzeRegion(LayoutRegion region, LayoutMetrics metrics)
    {
        var regionMetrics = metrics.WithNormalLineGapFrom(region.Lines);
        var tableContext = TableLikeRegion.From(region.Lines, regionMetrics);
        var paragraphs = new List<ParagraphGroup>();

        foreach (var line in region.Lines.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var target = FindBestParagraph(line, paragraphs, regionMetrics, tableContext);
            if (target == null)
                paragraphs.Add(new ParagraphGroup(line));
            else
                target.Paragraph.Add(line, target.Confidence);
        }

        return paragraphs
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .Select(x => x.ToLayoutBlock())
            .ToList();
    }

    private static bool HasProviderLayout(OcrResult ocrResult) =>
        ocrResult.Regions.Any(region => region.Paragraphs.Any(paragraph => paragraph.Lines.Count > 0));

    private static List<OcrContent> GetFlatContents(OcrResult ocrResult)
    {
        if (ocrResult.OcrContents.Count > 0)
            return ocrResult.OcrContents;

        return ocrResult.Regions
            .SelectMany(region => region.Paragraphs)
            .SelectMany(paragraph => paragraph.Lines)
            .ToList();
    }

    private static List<LayoutItem> GetSmartSourceItems(OcrResult ocrResult)
    {
        var contents = GetFlatContents(ocrResult);
        return CreateLayoutItems(contents);
    }

    private static List<OcrLayoutBlock> CreateProviderBlocks(OcrResult ocrResult)
    {
        var blocks = new List<OcrLayoutBlock>();

        foreach (var region in ocrResult.Regions)
        {
            foreach (var paragraph in region.Paragraphs)
            {
                var lines = paragraph.Lines
                    .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                    .ToList();
                if (lines.Count == 0)
                    continue;

                var text = JoinProviderParagraphText(lines);
                var lineBoxPoints = lines
                    .Where(line => line.BoxPoints.Count > 0)
                    .Select(line => CloneBoxPoints(line.BoxPoints))
                    .ToList();
                var boxPoints = paragraph.BoxPoints.Count > 0
                    ? CloneBoxPoints(paragraph.BoxPoints)
                    : CreateUnionBoxPoints(lineBoxPoints);

                blocks.Add(new OcrLayoutBlock
                {
                    Text = text,
                    BoxPoints = boxPoints,
                    LineBoxPoints = lineBoxPoints,
                    SourceContents = lines.ToArray(),
                    Source = OcrLayoutSource.Provider,
                    Confidence = 1
                });
            }
        }

        return blocks;
    }

    private static List<OcrLayoutBlock> CreateNoMergeBlocks(IReadOnlyList<OcrContent> contents) =>
        contents
            .Where(content => !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => new OcrLayoutBlock
            {
                Text = content.Text,
                BoxPoints = CloneBoxPoints(content.BoxPoints),
                LineBoxPoints = content.BoxPoints.Count > 0 ? [CloneBoxPoints(content.BoxPoints)] : [],
                SourceContents = [content],
                Source = OcrLayoutSource.NoMerge,
                Confidence = 1
            })
            .ToList();

    private static List<LayoutRegion> BuildLayoutRegions(List<LineSegment> lineSegments, LayoutMetrics metrics)
    {
        var regions = new List<LayoutRegion>();

        foreach (var line in lineSegments.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
        {
            var target = FindBestRegion(line, regions, metrics);
            if (target == null)
                regions.Add(new LayoutRegion(line));
            else
                target.Add(line);
        }

        return regions;
    }

    private static LayoutRegion? FindBestRegion(
        LineSegment line,
        List<LayoutRegion> regions,
        LayoutMetrics metrics)
    {
        LayoutRegion? bestRegion = null;
        var bestScore = double.NegativeInfinity;

        foreach (var region in regions)
        {
            if (!TryGetRegionAffinity(region, line, metrics, out var score))
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                bestRegion = region;
            }
        }

        return bestRegion;
    }

    private static bool TryGetRegionAffinity(
        LayoutRegion region,
        LineSegment line,
        LayoutMetrics metrics,
        out double score)
    {
        score = double.NegativeInfinity;

        var bestReferenceScore = double.NegativeInfinity;
        foreach (var reference in region.Lines.TakeLast(4))
        {
            if (line.Bounds.Top < reference.Bounds.Top - metrics.LineHeight * 0.35)
                continue;

            var verticalGap = VerticalGap(reference.Bounds, line.Bounds);
            if (verticalGap > metrics.LineHeight * 3.2)
                continue;

            var horizontalOverlap = HorizontalOverlapRatio(reference.Bounds, line.Bounds);
            var leftDelta = Math.Abs(reference.Bounds.Left - line.Bounds.Left);
            var centerDelta = Math.Abs(reference.Bounds.CenterX - line.Bounds.CenterX);
            var hasRegionAffinity =
                horizontalOverlap >= 0.30 ||
                leftDelta <= metrics.LineHeight * 1.8 ||
                centerDelta <= Math.Max(reference.Bounds.Width, line.Bounds.Width) * 0.35;

            if (!hasRegionAffinity)
                continue;

            var leftScore = 1 - Math.Min(1, leftDelta / Math.Max(metrics.LineHeight * 2, 1));
            var centerScore = 1 - Math.Min(1, centerDelta / Math.Max(Math.Max(reference.Bounds.Width, line.Bounds.Width), 1));
            var gapScore = 1 - Math.Min(1, verticalGap / Math.Max(metrics.LineHeight * 3.2, 1));
            var widthDelta = Math.Abs(reference.Bounds.Width - line.Bounds.Width);
            var widthScore = 1 - Math.Min(1, widthDelta / Math.Max(Math.Max(reference.Bounds.Width, line.Bounds.Width), 1));
            var referenceScore = horizontalOverlap * 4 + leftScore * 2 + centerScore + gapScore + widthScore;

            if (referenceScore > bestReferenceScore)
                bestReferenceScore = referenceScore;
        }

        if (double.IsNegativeInfinity(bestReferenceScore))
            return false;

        score = bestReferenceScore;
        return true;
    }

    private static IEnumerable<LayoutRegion> OrderRegionsForReading(
        List<LayoutRegion> regions,
        LayoutMetrics metrics)
    {
        var pending = regions
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .ToList();

        while (pending.Count > 0)
        {
            var band = new List<LayoutRegion> { pending[0] };
            var bandBounds = pending[0].Bounds;
            pending.RemoveAt(0);

            for (var i = 0; i < pending.Count;)
            {
                var candidate = pending[i];
                if (IsSameReadingBand(bandBounds, candidate.Bounds, metrics))
                {
                    band.Add(candidate);
                    bandBounds = Bounds.Union(bandBounds, candidate.Bounds);
                    pending.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            foreach (var region in band.OrderBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top))
                yield return region;
        }
    }

    private static bool IsSameReadingBand(Bounds bandBounds, Bounds candidateBounds, LayoutMetrics metrics)
    {
        if (VerticalOverlapRatio(bandBounds, candidateBounds) >= 0.25)
            return true;

        var topDelta = Math.Abs(bandBounds.Top - candidateBounds.Top);
        var hasVerticalContact = candidateBounds.Top <= bandBounds.Bottom + metrics.LineHeight * 1.5;
        return topDelta <= metrics.LineHeight * 2 && hasVerticalContact;
    }

    private static ParagraphCandidate? FindBestParagraph(
        LineSegment line,
        List<ParagraphGroup> paragraphs,
        LayoutMetrics metrics,
        TableLikeRegion tableContext)
    {
        ParagraphCandidate? bestCandidate = null;
        var bestScore = double.NegativeInfinity;

        foreach (var paragraph in paragraphs)
        {
            var lastLine = paragraph.LastLine;
            if (line.Bounds.Top < lastLine.Bounds.Top)
                continue;

            if (tableContext.ShouldKeepSeparate(lastLine, line))
                continue;

            if (!CanAppendToParagraph(paragraph, line, metrics, out var confidence))
                continue;

            var horizontalScore = HorizontalOverlapRatio(lastLine.Bounds, line.Bounds) * 3;
            var leftScore = 1 - Math.Min(1, Math.Abs(lastLine.Bounds.Left - line.Bounds.Left) / Math.Max(metrics.LineHeight, 1));
            var gapScore = 1 - Math.Min(1, VerticalGap(lastLine.Bounds, line.Bounds) / Math.Max(metrics.LineHeight, 1));
            var score = horizontalScore + leftScore + gapScore + confidence;

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = new ParagraphCandidate(paragraph, confidence);
            }
        }

        return bestCandidate;
    }

    private static bool CanAppendToParagraph(
        ParagraphGroup paragraph,
        LineSegment current,
        LayoutMetrics metrics,
        out double confidence)
    {
        confidence = 0;
        var previous = paragraph.LastLine;

        var verticalGap = VerticalGap(previous.Bounds, current.Bounds);
        if (verticalGap > metrics.LineHeight * 1.25)
            return false;

        if (IsListStart(current.Text))
            return false;

        if (IsListStart(previous.Text) && current.Bounds.Left > previous.Bounds.Left + metrics.LineHeight * 0.8)
        {
            confidence = 0.95;
            return true;
        }

        if (Math.Max(previous.Bounds.Height, current.Bounds.Height) >
            Math.Min(previous.Bounds.Height, current.Bounds.Height) * 1.45)
        {
            return false;
        }

        var horizontalOverlap = HorizontalOverlapRatio(previous.Bounds, current.Bounds);
        var leftDelta = Math.Abs(previous.Bounds.Left - current.Bounds.Left);
        var hasColumnAffinity = horizontalOverlap >= 0.45 || leftDelta <= metrics.LineHeight * 1.2;
        if (!hasColumnAffinity)
            return false;

        if (ShouldMergeHyphenated(previous.Text, current.Text))
        {
            confidence = 0.95;
            return true;
        }

        if (LooksLikeParagraphBreak(paragraph, previous, current, metrics, verticalGap, horizontalOverlap, leftDelta))
            return false;

        if (LooksLikeGridCell(previous, metrics) && LooksLikeGridCell(current, metrics))
            return false;

        if (LooksLikeStandaloneControl(previous) || LooksLikeStandaloneControl(current))
            return false;

        var indentDelta = current.Bounds.Left - previous.Bounds.Left;
        if (Math.Abs(indentDelta) > metrics.LineHeight * 2.5 && horizontalOverlap < 0.7)
            return false;

        var leftAffinity = 1 - Math.Min(1, leftDelta / Math.Max(metrics.LineHeight * 1.2, 1));
        var gapAffinity = 1 - Math.Min(1, verticalGap / Math.Max(metrics.LineHeight * 1.25, 1));
        var heightAffinity = Math.Min(previous.Bounds.Height, current.Bounds.Height) /
                             Math.Max(Math.Max(previous.Bounds.Height, current.Bounds.Height), 1);
        var horizontalAffinity = Math.Max(horizontalOverlap, leftAffinity);
        confidence = Math.Clamp(horizontalAffinity * 0.45 + gapAffinity * 0.35 + heightAffinity * 0.20, 0, 1);

        return confidence >= MinSmartMergeConfidence;
    }

    private static bool LooksLikeParagraphBreak(
        ParagraphGroup paragraph,
        LineSegment previous,
        LineSegment current,
        LayoutMetrics metrics,
        double verticalGap,
        double horizontalOverlap,
        double leftDelta)
    {
        if (IsListStart(previous.Text) || IsListStart(current.Text))
            return false;

        var sameLeftEdge = leftDelta <= metrics.LineHeight * 0.8;
        var currentIsIndented = current.Bounds.Left > previous.Bounds.Left + metrics.LineHeight * 0.8;
        var currentReturnsToPreviousLeft = current.Bounds.Left <= previous.Bounds.Left + metrics.LineHeight * 0.4;
        var bodyLeft = paragraph.BodyLeft;
        var previousReturnsToBodyLeft = previous.Bounds.Left <= bodyLeft + metrics.LineHeight * 0.45;
        var currentLooksLikeFirstLineIndent = LooksLikeFirstLineIndent(paragraph, current, metrics);
        var previousIsShortLine =
            previous.Bounds.Width <= current.Bounds.Width * 0.82 ||
            paragraph.IsShortLine(previous, metrics);

        if (previousIsShortLine &&
            previousReturnsToBodyLeft &&
            currentLooksLikeFirstLineIndent &&
            horizontalOverlap >= 0.35)
        {
            return true;
        }

        if (verticalGap <= GetParagraphBreakThreshold(metrics))
            return false;

        if (EndsWithSentenceEnding(previous.Text) && (sameLeftEdge || currentIsIndented))
            return true;

        if (StartsWithUpperLatin(current.Text) && (sameLeftEdge || currentLooksLikeFirstLineIndent))
            return true;

        return previousIsShortLine && currentReturnsToPreviousLeft && horizontalOverlap >= 0.45;
    }

    private static bool LooksLikeFirstLineIndent(ParagraphGroup paragraph, LineSegment current, LayoutMetrics metrics)
    {
        var indent = current.Bounds.Left - paragraph.BodyLeft;
        return indent >= metrics.LineHeight * 0.55 &&
               indent <= metrics.LineHeight * 2.5;
    }

    private static double GetParagraphBreakThreshold(LayoutMetrics metrics) =>
        Math.Max(
            metrics.LineHeight * 0.72,
            Math.Min(metrics.NormalLineGap, metrics.LineHeight * 0.35) + metrics.LineHeight * 0.45);

    private static List<LineSegment> BuildLineSegments(List<LayoutItem> items)
    {
        var visualLines = new List<VisualLine>();

        foreach (var item in items.OrderBy(x => x.Bounds.CenterY).ThenBy(x => x.Bounds.Left))
        {
            var line = visualLines
                .Where(x => IsSameVisualLine(x.Bounds, item.Bounds))
                .OrderByDescending(x => VerticalOverlapRatio(x.Bounds, item.Bounds))
                .ThenBy(x => Math.Abs(x.Bounds.CenterY - item.Bounds.CenterY))
                .FirstOrDefault();

            if (line == null)
                visualLines.Add(new VisualLine(item));
            else
                line.Add(item);
        }

        var tableContext = TableVisualLineContext.From(visualLines);
        return visualLines
            .SelectMany(line => SplitVisualLine(line, tableContext))
            .OrderBy(x => x.Bounds.Top)
            .ThenBy(x => x.Bounds.Left)
            .ToList();
    }

    private static IEnumerable<LineSegment> SplitVisualLine(VisualLine line, TableVisualLineContext tableContext)
    {
        var sortedItems = line.Items.OrderBy(x => x.Bounds.Left).ToList();
        var groups = new List<List<LayoutItem>>();
        var group = new List<LayoutItem>();
        var lineHeight = Median(sortedItems.Select(x => x.Bounds.Height));

        foreach (var item in sortedItems)
        {
            if (group.Count > 0)
            {
                var previous = group[^1];
                var gap = item.Bounds.Left - previous.Bounds.Right;
                var maxInlineGap = Math.Max(
                    lineHeight * 1.25,
                    Math.Min(lineHeight * 2.0, Math.Min(previous.Bounds.Width, item.Bounds.Width) * 0.75));

                if (gap > maxInlineGap || tableContext.ShouldSplitAtColumnBoundary(group, item, lineHeight))
                {
                    groups.Add(group);
                    group = [];
                }
            }

            group.Add(item);
        }

        if (group.Count > 0)
            groups.Add(group);

        foreach (var itemGroup in groups)
            yield return LineSegment.From(itemGroup, groups.Count);
    }

    private static bool IsSameVisualLine(Bounds lineBounds, Bounds itemBounds)
    {
        if (VerticalOverlapRatio(lineBounds, itemBounds) >= 0.55)
            return true;

        var centerDelta = Math.Abs(lineBounds.CenterY - itemBounds.CenterY);
        return centerDelta <= Math.Min(lineBounds.Height, itemBounds.Height) * 0.45;
    }

    private static List<LayoutItem> CreateLayoutItems(IReadOnlyList<OcrContent> contents)
    {
        var items = new List<LayoutItem>();

        for (var index = 0; index < contents.Count; index++)
        {
            var item = LayoutItem.TryCreate(contents[index], index);
            if (item == null)
                continue;

            items.AddRange(SplitEmbeddedNumberedList(item, index) ?? [item]);
        }

        return items;
    }

    private static List<LayoutItem>? SplitEmbeddedNumberedList(LayoutItem item, int sourceIndex)
    {
        var lines = item.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length < 2)
            return null;

        var firstTextLine = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var listStarts = new List<(int LineIndex, int Number)>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (TryGetOrderedListNumber(lines[lineIndex], out var number))
                listStarts.Add((lineIndex, number));
        }

        if (listStarts.Count < 2 || listStarts[0].LineIndex != firstTextLine)
            return null;

        for (var i = 1; i < listStarts.Count; i++)
        {
            if (listStarts[i].Number != listStarts[i - 1].Number + 1)
                return null;
        }

        var splitItems = new List<LayoutItem>(listStarts.Count);
        for (var i = 0; i < listStarts.Count; i++)
        {
            var startLine = listStarts[i].LineIndex;
            var endLine = i + 1 < listStarts.Count ? listStarts[i + 1].LineIndex : lines.Length;
            var itemLines = lines[startLine..endLine]
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
            // OCR 只提供整个文本块的坐标，按原始行占比分配子区域能让各编号项独立覆盖，
            // 同时避免凭字符宽度猜测换行位置。
            var bounds = new Bounds(
                item.Bounds.Left,
                item.Bounds.Top + item.Bounds.Height * startLine / lines.Length,
                item.Bounds.Right,
                item.Bounds.Top + item.Bounds.Height * endLine / lines.Length);
            var content = new OcrContent
            {
                Text = JoinTextWithSpacing(itemLines),
                BoxPoints = CreateBoxPoints(bounds)
            };
            var splitItem = LayoutItem.TryCreate(content, sourceIndex);
            if (splitItem != null)
                splitItems.Add(splitItem);
        }

        return splitItems;
    }

    private static List<BoxPoint> CreateBoxPoints(Bounds bounds) =>
    [
        new((float)bounds.Left, (float)bounds.Top),
        new((float)bounds.Right, (float)bounds.Top),
        new((float)bounds.Right, (float)bounds.Bottom),
        new((float)bounds.Left, (float)bounds.Bottom)
    ];

    private static List<BoxPoint> CreateUnionBoxPoints(IReadOnlyList<List<BoxPoint>> boxPointGroups)
    {
        var bounds = boxPointGroups
            .Where(points => points.Count > 0)
            .Select(Bounds.From)
            .ToList();

        return bounds.Count == 0
            ? []
            : CreateBoxPoints(Bounds.Union(bounds));
    }

    private static List<BoxPoint> CloneBoxPoints(IReadOnlyList<BoxPoint> boxPoints) =>
        boxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList();

    private static string JoinLineText(IReadOnlyList<LayoutItem> items)
    {
        var text = items[0].Text;
        for (var i = 1; i < items.Count; i++)
        {
            if (NeedsSpace(text, items[i].Text))
                text += " ";

            text += items[i].Text;
        }

        return text;
    }

    private static string JoinParagraphText(IReadOnlyList<LineSegment> lines) =>
        JoinTextWithSpacing(lines.Select(line => line.Text).ToList());

    private static string JoinProviderParagraphText(IReadOnlyList<OcrContent> lines) =>
        JoinTextWithSpacing(lines.Select(line => line.Text).ToList());

    /// <summary>
    /// 按阅读顺序拼接文本片段：处理连字符断词合并，并在需要时插入空格。
    /// </summary>
    private static string JoinTextWithSpacing(IReadOnlyList<string> texts)
    {
        var text = texts[0];
        for (var i = 1; i < texts.Count; i++)
        {
            if (ShouldMergeHyphenated(text, texts[i]))
            {
                text = text[..^1] + texts[i];
                continue;
            }

            if (NeedsSpace(text, texts[i]))
                text += " ";

            text += texts[i];
        }

        return text;
    }

    private static bool NeedsSpace(string previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;

        var left = previous[^1];
        var right = current[0];
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;

        if (TextHelper.IsCjk(left) || TextHelper.IsCjk(right))
            return false;

        if (char.IsPunctuation(left) || char.IsPunctuation(right))
            return false;

        return true;
    }

    private static bool ShouldMergeHyphenated(string previous, string current)

    {
        if (previous.Length < 2 || string.IsNullOrWhiteSpace(current))
            return false;

        var beforeHyphen = previous[^2];
        var first = current[0];
        return previous[^1] == '-' &&
               IsLatinLetter(beforeHyphen) &&
               IsLatinLetter(first) &&
               char.IsLower(first);
    }

    private static bool LooksLikeStandaloneControl(LineSegment line)
    {
        if (IsListStart(line.Text))
            return false;

        var compactText = line.Text.Replace(" ", string.Empty);
        if (compactText.Length > 14)
            return false;

        if (line.Text.Any(char.IsWhiteSpace))
            return false;

        var widthRatio = line.Bounds.Width / Math.Max(line.Bounds.Height, 1);
        return widthRatio <= 6.2 && !HasSentenceEnding(line.Text);
    }

    private static bool LooksLikeGridCell(LineSegment line, LayoutMetrics metrics)
    {
        if (!line.HasRowPeers || IsListStart(line.Text) || HasSentenceEnding(line.Text))
            return false;

        var wordCount = CountWords(line.Text);
        return wordCount <= 3 &&
               line.Text.Length <= 32 &&
               line.Bounds.Width <= metrics.LineHeight * 7;
    }

    private static bool LooksLikeTableItem(LineSegment line, LayoutMetrics metrics, int inferredColumnCount)
    {
        if (!line.HasRowPeers || IsListStart(line.Text) || HasSentenceEnding(line.Text))
            return false;

        var wordCount = CountWords(line.Text);
        var widthRatio = line.Bounds.Width / Math.Max(metrics.LineHeight, 1);

        if (inferredColumnCount >= 3)
        {
            return wordCount <= 6 &&
                   line.Text.Length <= 72 &&
                   widthRatio <= 18;
        }

        return wordCount <= 3 &&
               line.Text.Length <= 48 &&
               widthRatio <= 12;
    }

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsListStart(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] is '-' or '*' or '+' or '•' or '·' or '●' or '▪')
            return trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1]);

        return TryGetOrderedListNumber(trimmed, out _);
    }

    private static bool TryGetOrderedListNumber(string text, out int number)
    {
        number = 0;
        var trimmed = text.TrimStart();
        var digitCount = 0;
        while (digitCount < trimmed.Length && char.IsDigit(trimmed[digitCount]))
            digitCount++;

        return digitCount > 0 &&
               digitCount < trimmed.Length - 1 &&
               trimmed[digitCount] is '.' or ')' or '、' &&
               char.IsWhiteSpace(trimmed[digitCount + 1]) &&
               int.TryParse(trimmed.AsSpan(0, digitCount), out number);
    }

    private static bool HasSentenceEnding(string text) =>
        text.IndexOfAny(['.', '!', '?', ';', ':', '。', '！', '？', '；', '：']) >= 0;

    private static bool EndsWithSentenceEnding(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or ')' or ']' or '}' or '”' or '’')
                continue;

            return ch is '.' or '!' or '?' or ';' or ':' or '。' or '！' or '？' or '；' or '：';
        }

        return false;
    }

    private static bool StartsWithUpperLatin(string text)
    {
        foreach (var ch in text.TrimStart())
        {
            if (ch is '"' or '\'' or '(' or '[' or '{' or '“' or '‘')
                continue;

            return ch is >= 'A' and <= 'Z';
        }

        return false;
    }

    private static bool IsLatinLetter(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    private static double VerticalGap(Bounds top, Bounds bottom) =>
        Math.Max(0, bottom.Top - top.Bottom);


    private static double HorizontalOverlapRatio(Bounds first, Bounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        return overlap / Math.Max(1, Math.Min(first.Width, second.Width));
    }

    private static double VerticalOverlapRatio(Bounds first, Bounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return overlap / Math.Max(1, Math.Min(first.Height, second.Height));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(x => x > 0).Order().ToList();
        if (ordered.Count == 0)
            return 0;

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private sealed class VisualLine
    {
        internal VisualLine(LayoutItem item)
        {
            Items.Add(item);
            Bounds = item.Bounds;
        }

        internal List<LayoutItem> Items { get; } = [];

        internal Bounds Bounds { get; private set; }

        internal void Add(LayoutItem item)
        {
            Items.Add(item);
            Bounds = Bounds.Union(Bounds, item.Bounds);
        }
    }

    private sealed class LayoutRegion
    {
        internal LayoutRegion(LineSegment line)
        {
            Lines.Add(line);
            Bounds = line.Bounds;
        }

        internal List<LineSegment> Lines { get; } = [];

        internal Bounds Bounds { get; private set; }

        internal void Add(LineSegment line)
        {
            Lines.Add(line);
            Bounds = Bounds.Union(Bounds, line.Bounds);
        }
    }

    private sealed class TableVisualLineContext
    {
        private static readonly TableVisualLineContext Empty = new(false, []);

        private readonly List<PositionCluster> _columnStarts;

        private TableVisualLineContext(bool isTableLike, List<PositionCluster> columnStarts)
        {
            IsTableLike = isTableLike;
            _columnStarts = columnStarts;
        }

        internal bool IsTableLike { get; }

        internal bool ShouldSplitAtColumnBoundary(IReadOnlyList<LayoutItem> group, LayoutItem item, double lineHeight)
        {
            if (!IsTableLike || group.Count == 0)
                return false;

            var groupBounds = Bounds.Union(group.Select(x => x.Bounds));
            var gap = item.Bounds.Left - groupBounds.Right;
            if (gap < lineHeight * 0.75)
                return false;

            if (LooksLikeLeadingAdornment(groupBounds, lineHeight))
                return false;

            return IsRecurringColumnStart(item.Bounds.Left, lineHeight);
        }

        internal static TableVisualLineContext From(IReadOnlyList<VisualLine> lines)
        {
            if (lines.Count < 3)
                return Empty;

            var lineHeight = Math.Max(1, Median(lines.SelectMany(x => x.Items.Select(item => item.Bounds.Height))));
            var peerRows = lines.Where(x => x.Items.Count >= 2).ToList();
            if (peerRows.Count < 3 || !HasTableRowSpacing(lines, lineHeight))
                return Empty;

            var tolerance = Math.Max(lineHeight * 1.4, 1);
            var columnStarts = BuildPositionClusters(
                    peerRows.SelectMany(row => row.Items.Select(item => item.Bounds.Left)),
                    tolerance)
                .Where(x => x.Count >= 3)
                .OrderBy(x => x.Center)
                .ToList();

            if (columnStarts.Count < 2)
                return Empty;

            var startSpan = columnStarts[^1].Center - columnStarts[0].Center;
            if (startSpan < lineHeight * 6)
                return Empty;

            return new(true, columnStarts);
        }

        private bool IsRecurringColumnStart(double left, double lineHeight)
        {
            var tolerance = Math.Max(lineHeight * 1.4, 1);
            return _columnStarts.Any(x => Math.Abs(x.Center - left) <= tolerance);
        }

        private static bool HasTableRowSpacing(IReadOnlyList<VisualLine> lines, double lineHeight)
        {
            var sortedRows = lines.OrderBy(x => x.Bounds.Top).ToList();
            var gaps = new List<double>();

            for (var i = 1; i < sortedRows.Count; i++)
                gaps.Add(VerticalGap(sortedRows[i - 1].Bounds, sortedRows[i].Bounds));

            return gaps.Count > 0 && Median(gaps) >= lineHeight * 0.32;
        }

        private static bool LooksLikeLeadingAdornment(Bounds bounds, double lineHeight) =>
            bounds.Width <= lineHeight * 1.25 && bounds.Height <= lineHeight * 1.35;
    }

    private sealed class TableLikeRegion
    {
        private static readonly TableLikeRegion Empty = new(false, []);

        private readonly HashSet<LineSegment> _itemLines;

        private TableLikeRegion(bool isTableLike, HashSet<LineSegment> itemLines)
        {
            IsTableLike = isTableLike;
            _itemLines = itemLines;
        }

        internal bool IsTableLike { get; }

        internal bool ShouldKeepSeparate(LineSegment previous, LineSegment current) =>
            IsTableLike && _itemLines.Contains(previous) && _itemLines.Contains(current);

        internal static TableLikeRegion From(IReadOnlyList<LineSegment> lines, LayoutMetrics metrics)
        {
            if (lines.Count < 3)
                return Empty;

            var rows = BuildRows(lines, metrics);
            if (rows.Count < 3)
                return Empty;

            if (!HasTableRowSpacing(rows, metrics))
                return Empty;

            var inferredColumnCount = lines
                .Where(x => x.HasRowPeers)
                .Select(x => x.VisualLineSegmentCount)
                .DefaultIfEmpty(1)
                .Max();
            if (inferredColumnCount < 2)
                return Empty;

            var itemLines = lines
                .Where(line => LooksLikeTableItem(line, metrics, inferredColumnCount))
                .ToHashSet();
            if (itemLines.Count < 3)
                return Empty;

            var peerRowCount = rows.Count(row => row.Lines.Any(line => line.HasRowPeers));
            var itemRowCount = rows.Count(row => row.Lines.Any(itemLines.Contains));
            if (peerRowCount < 3 || itemRowCount < 3)
                return Empty;

            if (!HasRecurringColumnAlignment(itemLines, metrics))
                return Empty;

            return new(true, itemLines);
        }

        private static List<TableRow> BuildRows(IReadOnlyList<LineSegment> lines, LayoutMetrics metrics)
        {
            var rows = new List<TableRow>();

            foreach (var line in lines.OrderBy(x => x.Bounds.CenterY).ThenBy(x => x.Bounds.Left))
            {
                var row = rows
                    .Where(x => IsSameTableRow(x.Bounds, line.Bounds, metrics))
                    .OrderByDescending(x => VerticalOverlapRatio(x.Bounds, line.Bounds))
                    .ThenBy(x => Math.Abs(x.Bounds.CenterY - line.Bounds.CenterY))
                    .FirstOrDefault();

                if (row == null)
                    rows.Add(new TableRow(line));
                else
                    row.Add(line);
            }

            return rows;
        }

        private static bool IsSameTableRow(Bounds rowBounds, Bounds lineBounds, LayoutMetrics metrics)
        {
            if (VerticalOverlapRatio(rowBounds, lineBounds) >= 0.35)
                return true;

            return Math.Abs(rowBounds.CenterY - lineBounds.CenterY) <= metrics.LineHeight * 0.55;
        }

        private static bool HasTableRowSpacing(IReadOnlyList<TableRow> rows, LayoutMetrics metrics)
        {
            var sortedRows = rows.OrderBy(x => x.Bounds.Top).ToList();
            var gaps = new List<double>();

            for (var i = 1; i < sortedRows.Count; i++)
                gaps.Add(VerticalGap(sortedRows[i - 1].Bounds, sortedRows[i].Bounds));

            return gaps.Count > 0 && Median(gaps) >= metrics.LineHeight * 0.32;
        }

        private static bool HasRecurringColumnAlignment(HashSet<LineSegment> lines, LayoutMetrics metrics)
        {
            var tolerance = Math.Max(metrics.LineHeight * 1.4, 1);
            return CountRecurringClusters(lines.Select(x => x.Bounds.Left), tolerance) > 0 ||
                   CountRecurringClusters(lines.Select(x => x.Bounds.CenterX), tolerance) > 0;
        }

        private static int CountRecurringClusters(IEnumerable<double> positions, double tolerance)
        {
            return BuildPositionClusters(positions, tolerance).Count(x => x.Count >= 3);
        }
    }

    private static List<PositionCluster> BuildPositionClusters(IEnumerable<double> positions, double tolerance)
    {
        var clusters = new List<PositionCluster>();

        foreach (var position in positions.Order())
        {
            var cluster = clusters
                .Where(x => Math.Abs(x.Center - position) <= tolerance)
                .OrderBy(x => Math.Abs(x.Center - position))
                .FirstOrDefault();

            if (cluster == null)
                clusters.Add(new PositionCluster(position));
            else
                cluster.Add(position);
        }

        return clusters;
    }

    private sealed class TableRow
    {
        internal TableRow(LineSegment line)
        {
            Lines.Add(line);
            Bounds = line.Bounds;
        }

        internal List<LineSegment> Lines { get; } = [];

        internal Bounds Bounds { get; private set; }

        internal void Add(LineSegment line)
        {
            Lines.Add(line);
            Bounds = Bounds.Union(Bounds, line.Bounds);
        }
    }

    private sealed class PositionCluster
    {
        internal PositionCluster(double position)
        {
            Center = position;
            Count = 1;
        }

        internal double Center { get; private set; }

        internal int Count { get; private set; }

        internal void Add(double position)
        {
            Center = (Center * Count + position) / (Count + 1);
            Count++;
        }
    }

    private sealed record ParagraphCandidate(ParagraphGroup Paragraph, double Confidence);

    private sealed class ParagraphGroup
    {
        internal ParagraphGroup(LineSegment line) => Lines.Add(line);

        internal List<LineSegment> Lines { get; } = [];

        internal double Confidence { get; private set; } = 1;

        internal LineSegment LastLine => Lines[^1];

        internal double BodyLeft => Lines.Count == 1
            ? Lines[0].Bounds.Left
            : Median(Lines.Skip(1).Select(x => x.Bounds.Left));

        internal Bounds Bounds => Bounds.Union(Lines.Select(x => x.Bounds));

        internal bool IsShortLine(LineSegment line, LayoutMetrics metrics)
        {
            if (Lines.Count < 2)
                return false;

            var typicalWidth = Median(Lines.Select(x => x.Bounds.Width));
            return typicalWidth >= metrics.LineHeight * 6 &&
                   line.Bounds.Width <= typicalWidth * 0.72 &&
                   typicalWidth - line.Bounds.Width >= metrics.LineHeight * 2.2;
        }

        internal void Add(LineSegment line, double confidence)
        {
            Lines.Add(line);
            Confidence = Math.Min(Confidence, confidence);
        }

        internal OcrLayoutBlock ToLayoutBlock()
        {
            var bounds = Bounds.Union(Lines.Select(x => x.Bounds));
            return new OcrLayoutBlock
            {
                Text = JoinParagraphText(Lines),
                BoxPoints = CreateBoxPoints(bounds),
                LineBoxPoints = Lines.Select(line => CreateBoxPoints(line.Bounds)).ToList(),
                SourceContents = Lines.SelectMany(line => line.Items.Select(item => item.Content)).ToArray(),
                Source = OcrLayoutSource.Smart,
                Confidence = Confidence
            };
        }
    }

    private sealed class LineSegment
    {
        private LineSegment(List<LayoutItem> items, int visualLineSegmentCount)
        {
            Items = items;
            Bounds = Bounds.Union(items.Select(x => x.Bounds));
            Text = JoinLineText(items);
            VisualLineSegmentCount = visualLineSegmentCount;
        }

        internal List<LayoutItem> Items { get; }

        internal Bounds Bounds { get; }

        internal string Text { get; }

        internal int VisualLineSegmentCount { get; }

        internal bool HasRowPeers => VisualLineSegmentCount > 1;

        internal static LineSegment From(List<LayoutItem> items, int visualLineSegmentCount) =>
            new([.. items.OrderBy(x => x.Bounds.Left)], visualLineSegmentCount);
    }

    private sealed class LayoutItem
    {
        private LayoutItem(OcrContent content, Bounds bounds, int index)
        {
            Content = content;
            Bounds = bounds;
            Index = index;
            Text = content.Text.Trim();
        }

        internal OcrContent Content { get; }

        internal Bounds Bounds { get; }

        internal int Index { get; }

        internal string Text { get; }

        internal static LayoutItem? TryCreate(OcrContent content, int index)
        {
            if (string.IsNullOrWhiteSpace(content.Text) || content.BoxPoints.Count == 0)
                return null;

            var bounds = Bounds.From(content.BoxPoints);
            return bounds.Width <= 0 || bounds.Height <= 0
                ? null
                : new LayoutItem(content, bounds, index);
        }
    }

    private readonly record struct LayoutMetrics(double LineHeight, double NormalLineGap)
    {
        internal static LayoutMetrics From(IReadOnlyList<LineSegment> lines) =>
            Create(lines);

        internal LayoutMetrics WithNormalLineGapFrom(IReadOnlyList<LineSegment> lines) =>
            this with { NormalLineGap = EstimateNormalLineGap(lines, LineHeight) };

        private static LayoutMetrics Create(IReadOnlyList<LineSegment> lines)
        {
            var lineHeight = Math.Max(1, Median(lines.Select(x => x.Bounds.Height)));
            return new(lineHeight, EstimateNormalLineGap(lines, lineHeight));
        }

        private static double EstimateNormalLineGap(IReadOnlyList<LineSegment> lines, double lineHeight)
        {
            if (lines.Count < 2)
                return 0;

            var sorted = lines
                .OrderBy(x => x.Bounds.Top)
                .ThenBy(x => x.Bounds.Left)
                .ToList();
            var gaps = new List<double>();

            for (var i = 1; i < sorted.Count; i++)
            {
                var previous = sorted[i - 1];
                var current = sorted[i];
                if (VerticalOverlapRatio(previous.Bounds, current.Bounds) > 0.25)
                    continue;

                gaps.Add(VerticalGap(previous.Bounds, current.Bounds));
            }

            if (gaps.Count == 0)
                return 0;

            var maxNormalGap = lineHeight * 0.65;
            var normalGaps = gaps.Where(x => x <= maxNormalGap).ToList();
            if (normalGaps.Count == 0)
                normalGaps = gaps.Order().Take(Math.Max(1, gaps.Count / 2)).ToList();

            return Median(normalGaps);
        }
    }

    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal double Width => Right - Left;

        internal double Height => Bottom - Top;

        internal double CenterY => (Top + Bottom) / 2;

        internal double CenterX => (Left + Right) / 2;

        internal static Bounds From(IReadOnlyList<BoxPoint> points) =>
            new(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));

        internal static Bounds Union(IEnumerable<Bounds> bounds)
        {
            var list = bounds.ToList();
            return new Bounds(
                list.Min(x => x.Left),
                list.Min(x => x.Top),
                list.Max(x => x.Right),
                list.Max(x => x.Bottom));
        }

        internal static Bounds Union(Bounds first, Bounds second) =>
            new(
                Math.Min(first.Left, second.Left),
                Math.Min(first.Top, second.Top),
                Math.Max(first.Right, second.Right),
                Math.Max(first.Bottom, second.Bottom));
    }
}
