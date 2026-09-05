using STranslate.Core;
using STranslate.Plugin;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STranslate.Helpers;

/// <summary>
/// 图片翻译的矢量译文覆盖文档与 OCR 标注图生成。
/// 从 <see cref="ViewModels.ImageTranslateWindowViewModel"/> 抽离的纯绘制逻辑，无 VM 状态依赖。
/// </summary>
internal static class ImageTranslateRenderer
{
    /// <summary>
    /// 按原图像素尺寸生成当前图片显示内容，供保存等一次性导出场景使用。
    /// </summary>
    internal static BitmapSource RenderDisplayImage(
        BitmapSource image,
        ImageTranslateOverlayDocument? overlayDocument)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (overlayDocument is not { IsEmpty: false })
            return image;

        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            DrawTranslatedOverlay(drawingContext, overlayDocument);
        }

        // Overlay 使用原图像素坐标，96 DPI 可保持一个绘制单位对应一个输出像素。
        var renderBitmap = new RenderTargetBitmap(
            image.PixelWidth,
            image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    /// <summary>
    /// 绘制译文覆盖层，确保屏幕显示与离屏导出使用完全相同的绘制顺序。
    /// </summary>
    internal static void DrawTranslatedOverlay(
        DrawingContext drawingContext,
        ImageTranslateOverlayDocument document)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        ArgumentNullException.ThrowIfNull(document);

        // 先统一绘制背景，避免后一个块的背景覆盖前一个块的译文。
        foreach (var item in document.Items)
        {
            drawingContext.DrawRoundedRectangle(
                item.BackgroundBrush,
                null,
                item.Plan.OverlayRect,
                item.Plan.CornerRadius,
                item.Plan.CornerRadius);
        }

        foreach (var item in document.Items)
        {
            drawingContext.PushClip(new RectangleGeometry(item.Plan.TextClipRect));
            drawingContext.DrawText(
                item.ShadowText,
                new Point(item.TextPosition.X + 0.75, item.TextPosition.Y + 0.75));
            drawingContext.DrawText(item.FormattedText, item.TextPosition);
            drawingContext.Pop();
        }
    }

    /// <summary>
    /// 生成与原图像素坐标一致的矢量译文覆盖文档。
    /// </summary>
    /// <param name="layoutBlocks">包含翻译后文本的布局块</param>
    /// <param name="overlayTheme">覆盖层主题（明/暗）</param>
    /// <returns>矢量绘制项及原图坐标系中的可选译文字符</returns>
    internal static ImageTranslateOverlayDocument CreateTranslatedOverlay(
        IReadOnlyList<OcrLayoutBlock> layoutBlocks,
        ImageTranslateOverlayTheme overlayTheme)
    {
        if (layoutBlocks.Count == 0 ||
            layoutBlocks.All(x => x.BoxPoints.Count == 0))
        {
            return ImageTranslateOverlayDocument.Empty;
        }

        const double pixelsPerDip = 1.0;
        var measureTextBrush = new SolidColorBrush(Colors.Black);
        measureTextBrush.Freeze();
        var overlays = layoutBlocks
            .Where(item => item.BoxPoints.Count > 0 && !string.IsNullOrEmpty(item.Text))
            .Select(item => CreateTranslatedTextOverlay(item, overlayTheme, pixelsPerDip, measureTextBrush))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();

        // 独立覆盖块可能恰好等高，分组可避免双击时把它们误判为同一视觉行。
        var selectableWords = OcrWordBuilder.CreateIndexedCollectionFromGroups(
            overlays.Select(overlay =>
                OcrWordBuilder.CreateFromFormattedText(
                    overlay.Text,
                    overlay.FormattedText,
                    overlay.TextPosition,
                    overlay.Plan.TextClipRect,
                    scaleFactor: 1, preserveClippedText: true)), separateParagraphs: true);

        return new ImageTranslateOverlayDocument(
            overlays.ToArray(),
            selectableWords.ToArray());
    }

    /// <summary>
    /// 生成带有 OCR 识别边框标注的图像。
    /// </summary>
    /// <param name="ocrResult">OCR 识别结果</param>
    /// <param name="image">原始图像</param>
    /// <returns>标注边框后的图像；无位置信息则返回原图</returns>
    internal static BitmapSource GenerateAnnotatedImage(OcrResult ocrResult, BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (!Utilities.HasBoxPoints(ocrResult))
            return image;

        var drawingVisual = new DrawingVisual();

        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // 绘制原始图像
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));

            // 创建并冻结画笔以提高性能
            var pen = new Pen(Brushes.Red, 2);
            pen.Freeze();

            // 绘制所有多边形
            foreach (var item in ocrResult.OcrContents)
            {
                if (item.BoxPoints == null || item.BoxPoints.Count == 0)
                    continue;

                var geometry = CreatePolygonGeometry(item.BoxPoints);
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }

        // 使用标准 96 DPI，Viewbox 会自动处理高 DPI 屏幕的缩放
        var renderBitmap = new RenderTargetBitmap(
            image.PixelWidth,
            image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32
        );

        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    /// <summary>
    /// 在指定区域绘制翻译文本覆盖层
    /// </summary>
    /// <param name="content">包含翻译文本和位置信息的内容</param>
    /// <param name="overlayTheme">覆盖层主题</param>
    /// <param name="pixelsPerDip">DPI缩放比例</param>
    /// <param name="measureTextBrush">测量文本用的画刷</param>
    private static ImageTranslateOverlayItem? CreateTranslatedTextOverlay(
        OcrLayoutBlock content,
        ImageTranslateOverlayTheme overlayTheme,
        double pixelsPerDip,
        Brush measureTextBrush)
    {
        var boundingRect = BoxPointLayout.BoundingRect(content.BoxPoints);
        if (boundingRect.IsEmpty || boundingRect.Width <= 0 || boundingRect.Height <= 0)
            return null;

        var plan = ImageTranslateTextOverlayLayout.Create(
            content,
            boundingRect,
            (fontSize, textRect, isMultiLine) => MeasureFormattedText(
                content.Text,
                fontSize,
                textRect.Width,
                measureTextBrush,
                pixelsPerDip,
                isMultiLine ? fontSize * ImageTranslateTextOverlayPlan.MultilineLineHeightScale : 0,
                isMultiLine ? 0 : 1),
            overlayTheme);

        var textBrush = new SolidColorBrush(plan.ForegroundColor);
        textBrush.Freeze();
        var shadowBrush = new SolidColorBrush(CreateTextShadowColor(plan.ForegroundColor));
        shadowBrush.Freeze();
        var backgroundBrush = new SolidColorBrush(plan.OverlayBackgroundColor);
        backgroundBrush.Freeze();

        // Display 模式在大字号 CJK 文本叠加裁剪/缩放时可能不输出 glyph；
        // 使用 Ideal 保持矢量缩放稳定，并与测量、选择框构建使用的 metrics 一致。
        var formattedText = CreateFormattedText(
            content.Text,
            plan.FontSize,
            textBrush,
            plan.TextRect.Width,
            plan.MaxTextHeight,
            plan.LineHeight,
            plan.ShouldTrim || !plan.IsMultiLine,
            pixelsPerDip,
            plan.MaxLineCount,
            TextFormattingMode.Ideal);
        var shadowText = CreateFormattedText(
            content.Text,
            plan.FontSize,
            shadowBrush,
            plan.TextRect.Width,
            plan.MaxTextHeight,
            plan.LineHeight,
            plan.ShouldTrim || !plan.IsMultiLine,
            pixelsPerDip,
            plan.MaxLineCount,
            TextFormattingMode.Ideal);

        var textPosition = new Point(
            plan.TextRect.Left,
            plan.IsMultiLine
                ? plan.TextRect.Top + Math.Max(0, (plan.TextRect.Height - formattedText.Height) / 2)
                : plan.TextClipRect.Top + Math.Max(0, (plan.TextClipRect.Height - formattedText.Height) / 2)
        );

        return new ImageTranslateOverlayItem(
            content.Text,
            plan,
            backgroundBrush,
            shadowText,
            formattedText,
            textPosition);
    }

    /// <summary>
    /// 测量换行文本的实际占用尺寸。
    /// </summary>
    internal static Size MeasureFormattedText(
        string text,
        double fontSize,
        double maxWidth,
        Brush textBrush,
        double pixelsPerDip,
        double lineHeight = 0,
        int maxLineCount = 0)
    {
        var measureWidth = ShouldMeasureNaturalSingleLine(lineHeight, maxLineCount)
            ? CreateSingleLineMeasureWidth(text, fontSize, maxWidth)
            : maxWidth;
        var formattedText = CreateFormattedText(
            text,
            fontSize,
            textBrush,
            measureWidth,
            double.PositiveInfinity,
            lineHeight,
            false,
            pixelsPerDip,
            maxLineCount);

        return new Size(GetMeasuredTextWidth(formattedText), formattedText.Height);
    }

    /// <summary>
    /// 创建格式化文本对象。
    /// </summary>
    internal static FormattedText CreateFormattedText(
        string text,
        double fontSize,
        Brush textBrush,
        double maxWidth,
        double maxHeight,
        double lineHeight,
        bool shouldTrim,
        double pixelsPerDip,
        int maxLineCount = 0,
        TextFormattingMode textFormattingMode = TextFormattingMode.Ideal)
    {
        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei, Arial, SimSun"),
            fontSize,
            textBrush,
            numberSubstitution: null,
            textFormattingMode,
            pixelsPerDip);

        formattedText.MaxTextWidth = maxWidth;
        if (!double.IsPositiveInfinity(maxHeight))
            formattedText.MaxTextHeight = Math.Max(maxHeight, lineHeight);
        if (lineHeight > 0)
            formattedText.LineHeight = lineHeight;
        if (maxLineCount > 0)
            formattedText.MaxLineCount = maxLineCount;
        formattedText.TextAlignment = TextAlignment.Left;
        formattedText.Trimming = shouldTrim ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        return formattedText;
    }

    private static bool ShouldMeasureNaturalSingleLine(double lineHeight, int maxLineCount) =>
        maxLineCount == 1 && lineHeight <= 0;

    private static double CreateSingleLineMeasureWidth(string text, double fontSize, double maxWidth)
    {
        var estimatedWidth = Math.Max(text.Length, 1) * Math.Max(fontSize, 1) * 2;
        return Math.Clamp(Math.Max(maxWidth, estimatedWidth), 1, 100_000);
    }

    private static double GetMeasuredTextWidth(FormattedText formattedText) =>
        Math.Max(formattedText.Width, formattedText.WidthIncludingTrailingWhitespace);

    private static Color CreateTextShadowColor(Color foregroundColor) =>
        IsLightColor(foregroundColor)
            ? Color.FromArgb(120, 0, 0, 0)
            : Color.FromArgb(120, 255, 255, 255);

    private static bool IsLightColor(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d >= 0.5;

    private static StreamGeometry CreatePolygonGeometry(List<BoxPoint> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), false, true);

            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
