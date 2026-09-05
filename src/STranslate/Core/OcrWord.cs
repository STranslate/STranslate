using System.Windows;

namespace STranslate.Core;

/// <summary>
/// 表示图片文本选择中的可索引文本片段及其图像坐标框。
/// </summary>
public class OcrWord
{
    public string Text { get; set; } = string.Empty;

    public Rect BoundingBox { get; set; }

    internal int VisualLineIndex { get; set; } = -1;

    internal int ParagraphIndex { get; set; } = -1;

    /// <summary>
    /// 该单词在全文中的起始索引
    /// </summary>
    public int StartIndexInFullText { get; set; }

    public int EndIndexInFullText => StartIndexInFullText + Text.Length;
}
