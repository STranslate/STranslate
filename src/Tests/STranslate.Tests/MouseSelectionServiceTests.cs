using Microsoft.Extensions.Logging.Abstractions;
using STranslate.Core;
using STranslate.Services;
using System.Drawing;
using System.Text.Json;

namespace STranslate.Tests;

public class MouseSelectionServiceTests
{
    [Fact]
    public void MouseSelectionSettingsDefaultToDisabled()
    {
        var settings = new Settings();

        Assert.False(settings.IsMouseSelectionTranslationEnabled);
        Assert.False(settings.IsMouseSelectionIconEnabled);
        Assert.Null(typeof(Settings).GetProperty("IsMouseHook"));
        Assert.Null(typeof(Settings).GetProperty("ShowIconAfterMouseSelection"));
    }

    [Fact]
    public void GestureDetectorIgnoresClickAndMovementBelowSystemThreshold()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 100, isIBeam: true);
        detector.OnMouseMove(new Point(13, 13), isIBeam: true);

        Assert.False(detector.TryComplete(new Point(13, 13), isIBeam: true, out _));
    }

    [Fact]
    public void GestureDetectorAcceptsThresholdMovementWhenEitherEndpointUsesIBeam()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 100, isIBeam: false);
        detector.OnMouseMove(new Point(14, 10), isIBeam: false);

        Assert.True(detector.TryComplete(new Point(14, 10), isIBeam: true, out var completedPoint));
        Assert.Equal(new Point(14, 10), completedPoint);
        Assert.False(detector.TryComplete(completedPoint, isIBeam: true, out _));
    }

    [Fact]
    public void GestureDetectorAcceptsTextCursorObservedBetweenNonTextEndpoints()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(20, 10), timestamp: 100, isIBeam: false);
        detector.OnMouseMove(new Point(18, 10), isIBeam: true);
        detector.OnMouseMove(new Point(10, 10), isIBeam: false);

        Assert.True(detector.TryComplete(new Point(10, 10), isIBeam: false, out _));
    }

    [Fact]
    public void GestureDetectorAcceptsDoubleClickOnText()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: true));
        Assert.True(CompleteClick(detector, new Point(12, 11), 200, isIBeam: true, out var completedPoint));
        Assert.Equal(new Point(12, 11), completedPoint);
    }

    [Fact]
    public void GestureDetectorRejectsDoubleClickAfterSystemTimeout()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: true));
        Assert.False(CompleteClick(detector, new Point(10, 10), 601, isIBeam: true));
    }

    [Fact]
    public void GestureDetectorRejectsDoubleClickOutsideSystemBounds()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: true));
        Assert.False(CompleteClick(detector, new Point(15, 10), 200, isIBeam: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GestureDetectorRequiresTextCursorDuringBothClicks(bool firstClickIBeam, bool secondClickIBeam)
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, firstClickIBeam));
        Assert.False(CompleteClick(detector, new Point(10, 10), 200, secondClickIBeam));
    }

    [Fact]
    public void GestureDetectorTreatsDragAfterClickAsSingleDragSelection()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: true));
        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 200, isIBeam: true);
        detector.OnMouseMove(new Point(20, 10), isIBeam: true);

        Assert.True(detector.TryComplete(new Point(20, 10), isIBeam: true, out _));
        Assert.False(CompleteClick(detector, new Point(20, 10), 300, isIBeam: true));
    }

    [Fact]
    public void GestureDetectorStartsNewSequenceAfterDoubleClick()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: true));
        Assert.True(CompleteClick(detector, new Point(10, 10), 200, isIBeam: true));
        Assert.False(CompleteClick(detector, new Point(10, 10), 300, isIBeam: true));
    }

    [Fact]
    public void GestureDetectorHandlesTimestampWraparound()
    {
        var detector = CreateGestureDetector(doubleClickTime: 50);

        Assert.False(CompleteClick(detector, new Point(10, 10), uint.MaxValue - 10, isIBeam: true));
        Assert.True(CompleteClick(detector, new Point(10, 10), 20, isIBeam: true));
    }

    [Fact]
    public void GestureDetectorAcceptsDragInTextProcessWithoutIBeam()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 100, isIBeam: false, treatAsText: true);
        detector.OnMouseMove(new Point(30, 10), isIBeam: false);

        Assert.True(detector.TryComplete(new Point(30, 10), isIBeam: false, out _));
    }

    [Fact]
    public void GestureDetectorAcceptsDoubleClickInTextProcessWithoutIBeam()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: false, treatAsText: true));
        Assert.True(CompleteClick(detector, new Point(10, 10), 200, isIBeam: false, treatAsText: true));
    }

    [Fact]
    public void GestureDetectorRejectsDragWithoutIBeamOrTextProcess()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 100, isIBeam: false);
        detector.OnMouseMove(new Point(30, 10), isIBeam: false);

        Assert.False(detector.TryComplete(new Point(30, 10), isIBeam: false, out _));
    }

    [Fact]
    public void GestureDetectorRejectsDoubleClickWithoutIBeamOrTextProcess()
    {
        var detector = CreateGestureDetector();

        Assert.False(CompleteClick(detector, new Point(10, 10), 100, isIBeam: false));
        Assert.False(CompleteClick(detector, new Point(10, 10), 200, isIBeam: false));
    }

    [Fact]
    public void GestureDetectorTextProcessFlagOnlyAppliesToItsOwnGesture()
    {
        var detector = CreateGestureDetector();

        detector.OnLeftButtonDown(new Point(10, 10), timestamp: 100, isIBeam: false, treatAsText: true);
        detector.OnMouseMove(new Point(30, 10), isIBeam: false);
        Assert.True(detector.TryComplete(new Point(30, 10), isIBeam: false, out _));

        detector.OnLeftButtonDown(new Point(100, 100), timestamp: 1000, isIBeam: false);
        detector.OnMouseMove(new Point(130, 100), isIBeam: false);
        Assert.False(detector.TryComplete(new Point(130, 100), isIBeam: false, out _));
    }

    [Fact]
    public void MouseSelectionTextProcessesDefaultToWps()
    {
        Assert.Equal(["wps", "wpspdf"], new Settings().MouseSelectionTextProcesses);
    }

    [Fact]
    public void MouseSelectionTextProcessesKeepDefaultWhenMissingFromJson()
    {
        var settings = JsonSerializer.Deserialize<Settings>("""{ "IsMouseSelectionIconEnabled": true }""");

        Assert.NotNull(settings);
        Assert.True(settings.IsMouseSelectionIconEnabled);
        Assert.Equal(["wps", "wpspdf"], settings.MouseSelectionTextProcesses);
    }

    [Fact]
    public void MouseSelectionTextProcessesRoundTripThroughJson()
    {
        var settings = new Settings { MouseSelectionTextProcesses = ["foxitreader"] };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        var stored = document.RootElement.GetProperty(nameof(Settings.MouseSelectionTextProcesses));
        Assert.Equal(["foxitreader"], stored.EnumerateArray().Select(item => item.GetString()));

        // 只反序列化本字段：完整配置的部分 setter 依赖 WPF 运行环境。
        var restored = JsonSerializer.Deserialize<Settings>($$"""{ "MouseSelectionTextProcesses": {{stored.GetRawText()}} }""");

        Assert.NotNull(restored);
        Assert.Equal(["foxitreader"], restored.MouseSelectionTextProcesses);
    }

    [Theory]
    [InlineData("wps", true)]
    [InlineData("WPS", true)]
    [InlineData("wpspdf", true)]
    [InlineData("wps.exe", true)]
    [InlineData("wpsx", false)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProcessListMatchesNamesCaseInsensitively(string? processName, bool expected)
    {
        Assert.Equal(expected, MouseSelectionProcessList.Contains(["wps", "wpspdf"], processName));
    }

    [Fact]
    public void ProcessListIgnoresExeSuffixAndWhitespaceInConfiguredNames()
    {
        Assert.True(MouseSelectionProcessList.Contains([" WPS.EXE "], "wps"));
        Assert.False(MouseSelectionProcessList.Contains([], "wps"));
        Assert.False(MouseSelectionProcessList.Contains(null, "wps"));
    }

    [Fact]
    public void ProcessListParsesCommaSeparatedText()
    {
        Assert.Equal(["wps", "wpspdf", "et"], MouseSelectionProcessList.Parse(" wps, wpspdf.exe，et ; WPS,, "));
        Assert.Empty(MouseSelectionProcessList.Parse("  "));
        Assert.Empty(MouseSelectionProcessList.Parse(null));
    }

    [Fact]
    public void ProcessListFormatsAsCommaSeparatedText()
    {
        Assert.Equal("wps, wpspdf", MouseSelectionProcessList.Format(["wps", "wpspdf"]));
        Assert.Equal(string.Empty, MouseSelectionProcessList.Format(null));
    }

    [Fact]
    public async Task DirectModeCapturesTextWithoutRequestingIcon()
    {
        var hook = new FakeMouseHookService();
        var selectedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        using var service = CreateService(hook, new Settings(), _ =>
        {
            captureCount++;
            return Task.FromResult<string?>("selected");
        });
        var iconRequestCount = 0;
        service.TextSelected += (_, text) => selectedText.TrySetResult(text);
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: false));
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal("selected", await selectedText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, captureCount);
        Assert.Equal(0, iconRequestCount);
    }

    [Fact]
    public async Task IconModeDefersTextCaptureUntilRequested()
    {
        var hook = new FakeMouseHookService();
        var captureCount = 0;
        using var service = CreateService(hook, new Settings(), _ =>
        {
            captureCount++;
            return Task.FromResult<string?>("selected");
        });
        Point? iconPoint = null;
        service.IconRequested += (_, point) => iconPoint = point;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: true));
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal(new Point(20, 30), iconPoint);
        Assert.Equal(0, captureCount);
        Assert.Equal("selected", await service.CaptureIconSelectedTextAsync());
        Assert.Equal(1, captureCount);
    }

    [Fact]
    public async Task DirectTranslationTakesPriorityWhenBothFeaturesAreEnabled()
    {
        var hook = new FakeMouseHookService();
        var selectedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>("selected"));
        var iconRequestCount = 0;
        service.TextSelected += (_, text) => selectedText.TrySetResult(text);
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: true));
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal("selected", await selectedText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, iconRequestCount);
    }

    [Fact]
    public void DisabledPersistentFeaturesDoNotStartHook()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: false));

        Assert.Equal(0, hook.StartCount);
        Assert.False(hook.IsRunning);
    }

    [Fact]
    public void PersistentAndIncrementalConsumersShareHookLifetime()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: false));
        Assert.True(service.StartIncrementalCapture());
        Assert.Equal(1, hook.StartCount);

        service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: false);
        Assert.Equal(0, hook.StopCount);

        service.StopIncrementalCapture();
        Assert.Equal(1, hook.StopCount);
    }

    [Fact]
    public void RepeatedFeatureUpdatesRemainIdempotent()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: false));
        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: false));
        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: false));
        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: false));

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(1, hook.StopCount);
    }

    [Fact]
    public void FailedHookStartDoesNotEnablePersistentFeatures()
    {
        var hook = new FakeMouseHookService { StartSucceeds = false };
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.False(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: true));
        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: false));

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(0, hook.StopCount);
    }

    [Fact]
    public async Task IncrementalCaptureTakesPriorityOverPersistentMode()
    {
        var hook = new FakeMouseHookService();
        var incrementalText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>("incremental"));
        var iconRequestCount = 0;
        service.IncrementalTextSelected += (_, text) => incrementalText.TrySetResult(text);
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: true));
        Assert.True(service.StartIncrementalCapture());
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal("incremental", await incrementalText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, iconRequestCount);
    }

    [Fact]
    public void SwitchingFromBothFeaturesToIconOnlyRestoresIconWithoutRestartingHook()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));
        var dismissCount = 0;
        var iconRequestCount = 0;
        service.IconDismissRequested += (_, _) => dismissCount++;
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: true));
        dismissCount = 0;
        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: true));
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(0, hook.StopCount);
        Assert.Equal(1, dismissCount);
        Assert.Equal(1, iconRequestCount);
    }

    [Fact]
    public void MouseDownIsRelayedWithoutDismissingIcon()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));
        Point? startedPoint = null;
        var dismissCount = 0;
        service.SelectionStarted += (_, point) => startedPoint = point;
        service.IconDismissRequested += (_, _) => dismissCount++;

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: true));
        dismissCount = 0;
        hook.RaiseSelectionStarted(new Point(12, 34));

        Assert.Equal(new Point(12, 34), startedPoint);
        Assert.Equal(0, dismissCount);
    }

    [Fact]
    public async Task IconCaptureIsDiscardedWhenDirectTranslationBecomesEnabled()
    {
        var hook = new FakeMouseHookService();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(hook, new Settings(), async _ =>
        {
            captureStarted.SetResult();
            await releaseCapture.Task;
            return "selected";
        });

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: false, iconEnabled: true));
        var captureTask = service.CaptureIconSelectedTextAsync();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(service.ApplyPersistentFeatures(directTranslationEnabled: true, iconEnabled: true));
        releaseCapture.SetResult();

        Assert.Null(await captureTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static MouseSelectionService CreateService(
        IMouseHookService hook,
        Settings settings,
        Func<int, Task<string?>> getSelectedTextAsync) =>
        new(hook, settings, NullLogger<MouseSelectionService>.Instance, getSelectedTextAsync);

    private static MouseSelectionGestureDetector CreateGestureDetector(uint doubleClickTime = 500) =>
        new(4, 4, 8, 8, doubleClickTime);

    private static bool CompleteClick(
        MouseSelectionGestureDetector detector,
        Point point,
        uint timestamp,
        bool isIBeam,
        bool treatAsText = false) =>
        CompleteClick(detector, point, timestamp, isIBeam, out _, treatAsText);

    private static bool CompleteClick(
        MouseSelectionGestureDetector detector,
        Point point,
        uint timestamp,
        bool isIBeam,
        out Point completedPoint,
        bool treatAsText = false)
    {
        detector.OnLeftButtonDown(point, timestamp, isIBeam, treatAsText);
        return detector.TryComplete(point, isIBeam, out completedPoint);
    }

    private sealed class FakeMouseHookService : IMouseHookService
    {
        public event EventHandler<Point>? SelectionStarted;
        public event EventHandler<MouseSelectionCompletedEventArgs>? SelectionCompleted;

        public bool IsRunning { get; private set; }
        public bool StartSucceeds { get; init; } = true;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public bool Start()
        {
            StartCount++;
            IsRunning = StartSucceeds;
            return StartSucceeds;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void RaiseSelectionStarted(Point point) => SelectionStarted?.Invoke(this, point);

        public void RaiseSelectionCompleted(Point point) =>
            SelectionCompleted?.Invoke(this, new MouseSelectionCompletedEventArgs(point));

        public void Dispose() => Stop();
    }
}
