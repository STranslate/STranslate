using System.Runtime.InteropServices;
using STranslate.Helpers;

namespace STranslate.Tests;

public class UiaTextReaderTests
{
    [Fact]
    public async Task ReadsSelectionOnMtaWorker()
    {
        var reader = new UiaTextReader(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            return "  模型名称\n";
        });

        Assert.Equal("模型名称", await reader.TryGetSelectedTextAsync(500));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task EmptySelectionReturnsNull(string? selection)
    {
        var reader = new UiaTextReader(() => selection);

        Assert.Null(await reader.TryGetSelectedTextAsync(500));
    }

    [Fact]
    public async Task ProviderFailureReturnsNull()
    {
        var reader = new UiaTextReader(() => throw new COMException("选区已失效"));

        Assert.Null(await reader.TryGetSelectedTextAsync(500));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrCancellationDoesNotQueueMoreReads(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var reader = new UiaTextReader(() =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            release.Wait();
            finished.SetResult();
            return "迟到的选区";
        });

        try
        {
            var capture = reader.TryGetSelectedTextAsync(cancel ? 500 : 50, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            }
            else
            {
                Assert.Null(await capture.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.Null(await reader.TryGetSelectedTextAsync(50));
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotReadSelection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var reader = new UiaTextReader(() => { calls++; return "选区"; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.TryGetSelectedTextAsync(500, cancellation.Token));
        Assert.Equal(0, calls);
    }
}
