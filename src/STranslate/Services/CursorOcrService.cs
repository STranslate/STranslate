using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace STranslate.Services;

public sealed class CursorOcrService
{
    private const int CaptureWidth = 480;
    private const int CaptureHeight = 72;
    private const int FallbackCaptureWidth = 840;
    private const int FallbackCaptureHeight = 104;
    private const int ImageScale = 3;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly ILogger<CursorOcrService> _logger;
    private readonly SemaphoreSlim _recognizeLock = new(1, 1);
    private OcrEngine? _ocrEngine;
    private OcrEngine? _profileOcrEngine;

    public CursorOcrService(ILogger<CursorOcrService> logger)
    {
        _logger = logger;
        _ocrEngine = CreateOcrEngine();
        _profileOcrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public async Task<CursorOcrResult> RecognizeWordUnderCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await _recognizeLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ocrEngine ??= CreateOcrEngine();

            var selected = await RecognizePassAsync(CaptureWidth, CaptureHeight, cancellationToken);
            if (selected == null || selected.TouchesHorizontalEdge)
            {
                var fallback = await RecognizePassAsync(
                    FallbackCaptureWidth,
                    FallbackCaptureHeight,
                    cancellationToken);
                selected = fallback ?? selected;
            }

            return string.IsNullOrWhiteSpace(selected?.Text)
                ? CursorOcrResult.Fail("没有识别到光标下方的文字")
                : CursorOcrResult.Success(selected.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cursor OCR failed");
            return CursorOcrResult.Fail(ex.Message);
        }
        finally
        {
            _recognizeLock.Release();
        }
    }

    private async Task<SelectedWord?> RecognizePassAsync(
        int captureWidth,
        int captureHeight,
        CancellationToken cancellationToken)
    {
        using var captured = CaptureAroundCursor(captureWidth, captureHeight);
        using var prepared = PreprocessForOcr(captured.Bitmap, ImageScale);
        var selected = await RecognizeBitmapAsync(prepared, captured, _ocrEngine!, cancellationToken);
        if (selected != null && !LooksSuspicious(selected.Text))
            return selected;

        using var colorFallback = Resize(captured.Bitmap, ImageScale);
        var fallback = await RecognizeBitmapAsync(
            colorFallback,
            captured,
            _profileOcrEngine ?? _ocrEngine!,
            cancellationToken);
        return ChooseBetterResult(selected, fallback);
    }

    private async Task<SelectedWord?> RecognizeBitmapAsync(
        Bitmap bitmap,
        CapturedRegion captured,
        OcrEngine engine,
        CancellationToken cancellationToken)
    {
        using var softwareBitmap = await ToSoftwareBitmapAsync(bitmap);
        var ocrResult = await engine.RecognizeAsync(softwareBitmap);
        cancellationToken.ThrowIfCancellationRequested();

        return SelectWordAtPoint(
            ocrResult,
            bitmap.Width,
            bitmap.Height,
            captured.CursorX * ImageScale,
            captured.CursorY * ImageScale);
    }

    private static SelectedWord? ChooseBetterResult(SelectedWord? primary, SelectedWord? fallback)
    {
        if (primary == null)
            return fallback;
        if (fallback == null)
            return primary;

        var primarySuspicious = LooksSuspicious(primary.Text);
        var fallbackSuspicious = LooksSuspicious(fallback.Text);
        if (primarySuspicious != fallbackSuspicious)
            return primarySuspicious ? fallback : primary;

        return fallback.Text.Length > primary.Text.Length ? fallback : primary;
    }

    private static bool LooksSuspicious(string text)
    {
        var hasAsciiLetter = text.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        var hasUnexpectedLatin = text.Any(character => character > 127
            && character <= 0x024F
            && !IsCjk(character));
        return hasAsciiLetter && hasUnexpectedLatin;
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF';

    private static OcrEngine CreateOcrEngine()
    {
        try
        {
            var english = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (english != null)
                return english;
        }
        catch
        {
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR 初始化失败");
    }

    private static CapturedRegion CaptureAroundCursor(int width, int height)
    {
        if (!GetCursorPos(out var cursor))
            throw new InvalidOperationException("无法获取鼠标位置");

        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        var maxLeft = Math.Max(virtualLeft, virtualLeft + virtualWidth - width);
        var maxTop = Math.Max(virtualTop, virtualTop + virtualHeight - height);
        var left = Math.Clamp(cursor.X - width / 2, virtualLeft, maxLeft);
        var top = Math.Clamp(cursor.Y - height / 2, virtualTop, maxTop);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return new CapturedRegion(bitmap, cursor.X - left, cursor.Y - top);
    }

    private static Bitmap Resize(Bitmap source, int scale)
    {
        var resized = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, resized.Width, resized.Height);
        return resized;
    }

    private static Bitmap PreprocessForOcr(Bitmap source, int scale)
    {
        using var resized = Resize(source, scale);
        var width = resized.Width;
        var height = resized.Height;
        var rectangle = new Rectangle(0, 0, width, height);
        var sourceData = resized.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var sourceStrideIsPositive = sourceData.Stride >= 0;
        var sourceStride = Math.Abs(sourceData.Stride);
        var sourceBytes = new byte[sourceStride * height];
        Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);
        resized.UnlockBits(sourceData);

        var grayscale = new byte[width * height];
        var histogram = new int[256];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = sourceStrideIsPositive ? y * sourceStride : (height - 1 - y) * sourceStride;
            var grayRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var offset = sourceRow + x * 4;
                var gray = (sourceBytes[offset + 2] * 77
                    + sourceBytes[offset + 1] * 150
                    + sourceBytes[offset] * 29 + 128) >> 8;
                grayscale[grayRow + x] = (byte)gray;
                histogram[gray]++;
            }
        }

        var clipCount = Math.Max(1, (int)(grayscale.Length * 0.005));
        var minValue = FindHistogramBoundary(histogram, clipCount, fromStart: true);
        var maxValue = FindHistogramBoundary(histogram, clipCount, fromStart: false);
        var contrasted = new byte[grayscale.Length];
        if (maxValue - minValue < 10)
        {
            System.Buffer.BlockCopy(grayscale, 0, contrasted, 0, grayscale.Length);
        }
        else
        {
            var contrastScale = 255d / (maxValue - minValue);
            for (var i = 0; i < grayscale.Length; i++)
            {
                contrasted[i] = (byte)Math.Clamp(
                    (int)Math.Round((grayscale[i] - minValue) * contrastScale),
                    0,
                    255);
            }
        }

        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var outputData = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var outputStride = Math.Abs(outputData.Stride);
        var outputBytes = new byte[outputStride * height];

        for (var y = 0; y < height; y++)
        {
            var outputRow = outputData.Stride >= 0 ? y * outputStride : (height - 1 - y) * outputStride;
            for (var x = 0; x < width; x++)
            {
                var center = contrasted[y * width + x];
                var sharpened = center;
                if (x > 0 && x < width - 1 && y > 0 && y < height - 1)
                {
                    var blur = (
                        contrasted[(y - 1) * width + x - 1]
                        + contrasted[(y - 1) * width + x + 1]
                        + contrasted[(y + 1) * width + x - 1]
                        + contrasted[(y + 1) * width + x + 1]
                        + 2 * (contrasted[(y - 1) * width + x]
                            + contrasted[(y + 1) * width + x]
                            + contrasted[y * width + x - 1]
                            + contrasted[y * width + x + 1])
                        + 4 * center) / 16;
                    sharpened = (byte)Math.Clamp(center + (center - blur) / 2, 0, 255);
                }

                var offset = outputRow + x * 4;
                outputBytes[offset] = sharpened;
                outputBytes[offset + 1] = sharpened;
                outputBytes[offset + 2] = sharpened;
                outputBytes[offset + 3] = 255;
            }
        }

        Marshal.Copy(outputBytes, 0, outputData.Scan0, outputBytes.Length);
        output.UnlockBits(outputData);
        return output;
    }

    private static int FindHistogramBoundary(int[] histogram, int clipCount, bool fromStart)
    {
        var accumulated = 0;
        if (fromStart)
        {
            for (var i = 0; i < histogram.Length; i++)
            {
                accumulated += histogram[i];
                if (accumulated > clipCount)
                    return i;
            }
            return 0;
        }

        for (var i = histogram.Length - 1; i >= 0; i--)
        {
            accumulated += histogram[i];
            if (accumulated > clipCount)
                return i;
        }
        return 255;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
    }

    private static SelectedWord? SelectWordAtPoint(
        OcrResult result,
        int imageWidth,
        int imageHeight,
        double cursorX,
        double cursorY)
    {
        var words = result.Lines.SelectMany(line => line.Words).ToList();
        if (words.Count == 0)
            return null;

        if (words.Count == 1)
            return CreateSelectedWord(words[0], imageWidth, cursorX);

        var typicalWordHeight = words
            .Select(word => word.BoundingRect.Height)
            .Where(height => height > 0)
            .OrderBy(height => height)
            .ElementAtOrDefault(words.Count / 2);
        var toleranceX = Math.Max(12, typicalWordHeight * 0.25);
        var toleranceY = Math.Max(15, typicalWordHeight * 0.35);

        var candidates = words.Select(word =>
        {
            var rect = word.BoundingRect;
            var exact = cursorX >= rect.X && cursorX <= rect.X + rect.Width
                && cursorY >= rect.Y && cursorY <= rect.Y + rect.Height;
            var fuzzy = !exact
                && cursorX >= rect.X - toleranceX
                && cursorX <= rect.X + rect.Width + toleranceX
                && cursorY >= rect.Y - toleranceY
                && cursorY <= rect.Y + rect.Height + toleranceY;
            var wordCenterX = rect.X + rect.Width / 2d;
            var wordCenterY = rect.Y + rect.Height / 2d;
            var distance = Math.Pow(wordCenterX - cursorX, 2) + Math.Pow(wordCenterY - cursorY, 2);
            return new { Word = word, Exact = exact, Fuzzy = fuzzy, Distance = distance };
        }).ToList();

        var match = candidates
            .Where(item => item.Exact)
            .OrderBy(item => item.Distance)
            .FirstOrDefault()
            ?? candidates
                .Where(item => item.Fuzzy)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();

        return match == null ? null : CreateSelectedWord(match.Word, imageWidth, cursorX);
    }

    private static SelectedWord? CreateSelectedWord(OcrWord word, int imageWidth, double cursorX)
    {
        var rect = word.BoundingRect;
        var text = ExtractTokenAtCursor(word.Text, rect.X, rect.Width, cursorX);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var edgeTolerance = Math.Max(4, imageWidth * 0.006);
        return new SelectedWord(
            text,
            rect.X <= edgeTolerance || rect.X + rect.Width >= imageWidth - edgeTolerance);
    }

    private static string ExtractTokenAtCursor(string text, double wordX, double wordWidth, double cursorX)
    {
        var value = text.Trim();
        if (value.Length == 0)
            return string.Empty;

        var ranges = new List<(int Start, int End)>();
        var tokenStart = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var isConnector = (value[i] == '\'' || value[i] == '-')
                && i > 0
                && i + 1 < value.Length
                && char.IsLetterOrDigit(value[i - 1])
                && char.IsLetterOrDigit(value[i + 1]);
            var isTokenCharacter = char.IsLetterOrDigit(value[i]) || isConnector;

            if (isTokenCharacter)
            {
                if (tokenStart < 0)
                    tokenStart = i;
            }
            else if (tokenStart >= 0)
            {
                ranges.Add((tokenStart, i));
                tokenStart = -1;
            }
        }

        if (tokenStart >= 0)
            ranges.Add((tokenStart, value.Length));

        if (ranges.Count == 0)
            return string.Empty;

        var relativeX = wordWidth <= 0 ? 0.5 : Math.Clamp((cursorX - wordX) / wordWidth, 0, 0.999999);
        var characterIndex = relativeX * value.Length;
        var selected = ranges
            .OrderBy(range => DistanceToRange(characterIndex, range.Start, range.End))
            .First();

        return NormalizeCommonOcrConfusions(value[selected.Start..selected.End]);
    }

    private static string NormalizeCommonOcrConfusions(string text)
    {
        if (text.Length < 3 || !text.Contains('1'))
            return text;

        var characters = text.ToCharArray();
        for (var i = 1; i < characters.Length - 1; i++)
        {
            if (characters[i] == '1'
                && char.IsLower(characters[i - 1])
                && char.IsLower(characters[i + 1]))
            {
                characters[i] = 'l';
            }
        }

        return new string(characters);
    }

    private static double DistanceToRange(double index, int start, int end)
    {
        if (index < start)
            return start - index;
        if (index >= end)
            return index - end;
        return 0;
    }

    private sealed class CapturedRegion(Bitmap bitmap, double cursorX, double cursorY) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;
        public double CursorX { get; } = cursorX;
        public double CursorY { get; } = cursorY;

        public void Dispose() => Bitmap.Dispose();
    }

    private sealed record SelectedWord(string Text, bool TouchesHorizontalEdge);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

public sealed class CursorOcrResult
{
    public string Text { get; private init; } = string.Empty;
    public bool IsSuccess { get; private init; }
    public string ErrorMessage { get; private init; } = string.Empty;

    public static CursorOcrResult Success(string text) => new()
    {
        Text = text,
        IsSuccess = true
    };

    public static CursorOcrResult Fail(string message) => new()
    {
        ErrorMessage = message,
        IsSuccess = false
    };
}
