using System.Windows;
using System.Windows.Threading;

namespace LenxTool.App.Tests.Views;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfRuntimeGroup
{
    public const string Name = "WPF runtime";
}

internal static class WpfRuntimeHost
{
    private static readonly Lazy<HostState> Host =
        new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(
        Action action,
        TimeSpan timeout,
        Func<string>? timeoutMessage = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        DispatcherOperation operation = Host.Value.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Send);
        try
        {
            operation.Task
                .WaitAsync(timeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                timeoutMessage?.Invoke() ?? "The shared WPF runtime timed out.");
        }
    }

    private static HostState Start()
    {
        var ready = new TaskCompletionSource<HostState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new LenxTool.App.App();
                application.InitializeComponent();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                ready.TrySetResult(new(application, dispatcher));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "LenxTool WPF test runtime"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();
    }

    private sealed record HostState(
        LenxTool.App.App Application,
        Dispatcher Dispatcher);
}
