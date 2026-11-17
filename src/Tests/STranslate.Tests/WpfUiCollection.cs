using System.Windows.Threading;

namespace STranslate.Tests;

// iNKORE 主题资源和 WPF 属性描述符包含进程级缓存，UI 用例不能跨 STA 并行初始化它们。
[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection
{
    private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "STranslate WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    internal static void Run(Action action) => UiDispatcher.Value.Invoke(action);
}
