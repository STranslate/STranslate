using STranslate.Plugin;

namespace STranslate.Core;

internal enum OcrLayoutSource
{
    Provider,
    Smart,
    NoMerge
}

internal sealed class OcrLayoutBlock
{
    public string Text { get; set; } = string.Empty;

    public List<BoxPoint> BoxPoints { get; set; } = [];

    public List<List<BoxPoint>> LineBoxPoints { get; set; } = [];

    // 保留分析器已确定的原始文本归属，选择段落时无需按坐标重新推断。
    internal IReadOnlyList<OcrContent> SourceContents { get; init; } = [];

    public OcrLayoutSource Source { get; set; }

    public double Confidence { get; set; } = 1;

    internal OcrContent ToOcrContent() =>
        new()
        {
            Text = Text,
            BoxPoints = BoxPoints.Select(point => new BoxPoint(point.X, point.Y)).ToList()
        };
}
