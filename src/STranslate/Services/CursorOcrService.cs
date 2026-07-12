using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using STranslate.Core;
using STranslate.Plugin;

namespace STranslate.Services;

/// <summary>
/// 使用 STranslate 当前启用的 OCR 服务识别光标下的英文单词。
/// 截图保持原始尺寸和颜色，便于对比不同 OCR 服务的原生效果。
/// </summary>
public sealed class CursorOcrService
{
    private const int CaptureWidth = 480;
    private const int CaptureHeight = 72;
    private const int FallbackCaptureWidth = 840;
    private const int FallbackCaptureHeight = 104;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly OcrService _ocrService;
    private readonly SemaphoreSlim _recognizeLock = new(1, 1);

    public CursorOcrService(OcrService ocrService) => _ocrService = ocrService;

    public async Task<CursorOcrResult> RecognizeWordUnderCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await _recognizeLock.WaitAsync(cancellationToken);
        try
        {
            var plugin = _ocrService.GetActiveSvc<IOcrPlugin>();
            if (plugin == null)
                return CursorOcrResult.Fail("未找到已启用的 OCR 服务");

            var selected = await RecognizePassAsync(plugin, CaptureWidth, CaptureHeight, cancellationToken);
            if (selected == null || selected.TouchesHorizontalEdge)
            {
                var fallback = await RecognizePassAsync(
                    plugin, FallbackCaptureWidth, FallbackCaptureHeight, cancellationToken);
                selected = fallback ?? selected;
            }

            if (selected == null || string.IsNullOrWhiteSpace(selected.Text))
                return CursorOcrResult.Fail("没有识别到光标下方的文字");

            return IsEnglishWord(selected.Text)
                ? CursorOcrResult.Success(selected.Text)
                : CursorOcrResult.Fail("光标取词仅支持英文单词");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CursorOcrResult.Fail(ex.Message);
        }
        finally
        {
            _recognizeLock.Release();
        }
    }

    private static async Task<SelectedWord?> RecognizePassAsync(
        IOcrPlugin plugin, int width, int height, CancellationToken cancellationToken)
    {
        using var captured = CaptureAroundCursor(width, height);
        using var stream = new MemoryStream();
        captured.Bitmap.Save(stream, ImageFormat.Png);

        var result = await plugin.RecognizeAsync(
            new OcrRequest(stream.ToArray(), LangEnum.English, captured.Bitmap.Width, captured.Bitmap.Height),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.IsSuccess)
            return null;

        // 与主程序其它 OCR 入口保持一致：将仅返回 Regions 的插件结果投影为 OcrContents。
        Utilities.PrepareOcrResult(result);

        var contents = result.OcrContents
            .Where(content => !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => new OcrItem(content.Text.Trim(), GetBounds(content.BoxPoints)))
            .Where(item => item.Bounds.HasValue)
            .ToList();

        if (contents.Count == 0)
            return null;

        var cursorX = captured.CursorX;
        var cursorY = captured.CursorY;
        var match = contents
            .Select(item => new
            {
                item.Text,
                Bounds = item.Bounds!.Value,
                Exact = item.Bounds.Value.Contains((float)cursorX, (float)cursorY)
            })
            .OrderByDescending(item => item.Exact)
            .ThenBy(item => DistanceToCenter(item.Bounds, cursorX, cursorY))
            .FirstOrDefault();

        if (match == null)
            return null;

        var text = ExtractTokenAtCursor(match.Text, match.Bounds.X, match.Bounds.Width, cursorX);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new SelectedWord(text, match.Bounds.Left <= 4 || match.Bounds.Right >= width - 4);
    }

    private static CapturedRegion CaptureAroundCursor(int width, int height)
    {
        if (!GetCursorPos(out var cursor))
            throw new InvalidOperationException("无法获取鼠标位置");

        var leftBound = GetSystemMetrics(SmXVirtualScreen);
        var topBound = GetSystemMetrics(SmYVirtualScreen);
        var screenWidth = GetSystemMetrics(SmCxVirtualScreen);
        var screenHeight = GetSystemMetrics(SmCyVirtualScreen);
        var left = Math.Clamp(cursor.X - width / 2, leftBound, Math.Max(leftBound, leftBound + screenWidth - width));
        var top = Math.Clamp(cursor.Y - height / 2, topBound, Math.Max(topBound, topBound + screenHeight - height));

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return new CapturedRegion(bitmap, cursor.X - left, cursor.Y - top);
    }

    private static RectangleF? GetBounds(List<BoxPoint> points)
    {
        if (points == null || points.Count == 0)
            return null;
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return right > left && bottom > top ? new RectangleF(left, top, right - left, bottom - top) : null;
    }

    private static double DistanceToCenter(RectangleF bounds, double x, double y)
        => Math.Pow(bounds.X + bounds.Width / 2 - x, 2) + Math.Pow(bounds.Y + bounds.Height / 2 - y, 2);

    private static string ExtractTokenAtCursor(string text, double wordX, double wordWidth, double cursorX)
    {
        var value = text.Trim();
        var tokens = System.Text.RegularExpressions.Regex.Matches(value, @"[A-Za-z]+(?:['-][A-Za-z]+)*")
            .Select(match => (Start: match.Index, End: match.Index + match.Length)).ToList();
        if (tokens.Count == 0) return string.Empty;
        var index = Math.Clamp((cursorX - wordX) / Math.Max(1, wordWidth) * value.Length, 0, value.Length - 1);
        var token = tokens.OrderBy(range => index < range.Start ? range.Start - index : index >= range.End ? index - range.End : 0).First();
        return value[token.Start..token.End];
    }

    private static bool IsEnglishWord(string text) =>
        !string.IsNullOrWhiteSpace(text) && System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Za-z]+(?:['-][A-Za-z]+)*$");

    private sealed record OcrItem(string Text, RectangleF? Bounds);
    private sealed record SelectedWord(string Text, bool TouchesHorizontalEdge);
    private sealed class CapturedRegion(Bitmap bitmap, double cursorX, double cursorY) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;
        public double CursorX { get; } = cursorX;
        public double CursorY { get; } = cursorY;
        public void Dispose() => Bitmap.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
}

public sealed class CursorOcrResult
{
    public string Text { get; private init; } = string.Empty;
    public bool IsSuccess { get; private init; }
    public string ErrorMessage { get; private init; } = string.Empty;
    public static CursorOcrResult Success(string text) => new() { Text = text, IsSuccess = true };
    public static CursorOcrResult Fail(string message) => new() { ErrorMessage = message, IsSuccess = false };
}
