using System.Windows.Automation;

namespace STranslate.Helpers;

/// <summary>
/// 在后台读取焦点元素的文字选区，不操作剪贴板。
/// </summary>
internal sealed class UiaTextReader(Func<string?>? readSelection = null)
{
    private const int MaxWaitMs = 250;
    private readonly SemaphoreSlim _readLock = new(1, 1);

    public async Task<string?> TryGetSelectedTextAsync(int timeout, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout);

        // 超时不能中断 UIA 跨进程调用；上次读取未结束时跳过，避免堆积后台任务。
        if (!_readLock.Wait(0, cancellation))
            return null;

        var readTask = Task.Run(() => ReadOnWorker(cancellation));
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(timeout, MaxWaitMs)), cancellation)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private string? ReadOnWorker(CancellationToken cancellation)
    {
        try
        {
            if (cancellation.IsCancellationRequested)
                return null;

            // 线程池使用 MTA，UIA 对象只在该线程内创建和访问。
            var text = (readSelection ?? ReadFocusedSelection)()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            // 不支持 TextPattern、元素失效或目标程序拒绝访问时由调用方回退。
            return null;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private static string? ReadFocusedSelection()
    {
        var element = AutomationElement.FocusedElement;
        if (element == null || element.Current.IsPassword ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
        {
            return null;
        }

        // 未选择文字时 GetSelection 返回空范围，GetText 返回空字符串。
        return string.Concat(((TextPattern)pattern).GetSelection().Select(range => range.GetText(-1)));
    }
}
