using Korat.Cloud.Push;
using Microsoft.Extensions.Logging;

namespace Korat.Cloud.IntegrationTests.Push;

public sealed class DetachedNotifyRunnerTests
{
    /// <summary>Captures whether any Warning was logged (mirrors NodeWakeCoordinatorTests.LogSpy).</summary>
    private sealed class LogSpy : ILogger
    {
        public bool HasWarning { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) HasWarning = true;
        }
    }

    [Fact]
    public async Task RunAsync_Swallows_Exception_From_Notify()
    {
        var logSpy = new LogSpy();

        await DetachedNotifyRunner.RunAsync(
            _ => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(1), logSpy, "space-1");

        Assert.True(logSpy.HasWarning);
    }

    [Fact]
    public async Task RunAsync_Times_Out_When_Notify_Never_Completes()
    {
        var logSpy = new LogSpy();
        var tcs = new TaskCompletionSource();

        await DetachedNotifyRunner.RunAsync(
            _ => tcs.Task, TimeSpan.FromMilliseconds(50), logSpy, "space-1");

        // Single-timer design: WaitAsync(cts.Token) throws OperationCanceledException once the
        // CTS's own timeout fires → caught by the catch-all and logged.
        Assert.True(logSpy.HasWarning);
    }

    [Fact]
    public async Task RunAsync_Passes_A_Fresh_Cancellable_Token_Not_Requested_Up_Front()
    {
        CancellationToken? seen = null;

        await DetachedNotifyRunner.RunAsync(
            ct => { seen = ct; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), NullLogger(), "space-1");

        Assert.NotNull(seen);
        Assert.True(seen!.Value.CanBeCanceled); // NOT CancellationToken.None — it must be cancellable so a timeout can actually abort the work
        Assert.False(seen!.Value.IsCancellationRequested); // not cancelled up front — only once the timeout elapses
    }

    [Fact]
    public async Task RunAsync_Cancels_The_Notify_Token_When_The_Timeout_Elapses()
    {
        var logSpy = new LogSpy();
        var workObservedCancellation = new TaskCompletionSource();

        await DetachedNotifyRunner.RunAsync(
            async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    workObservedCancellation.TrySetResult();
                    throw;
                }
            },
            TimeSpan.FromMilliseconds(50),
            logSpy,
            "space-1");

        // A timed-out fan-out must actually CANCEL the underlying work (not just abandon the
        // await and let it keep running, e.g. up to HttpClient's ~100s default) — give the
        // cancellation callback a little extra time beyond RunAsync's own return to fire.
        var completed = await Task.WhenAny(workObservedCancellation.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(workObservedCancellation.Task, completed);
        Assert.True(logSpy.HasWarning);
    }

    [Fact]
    public async Task RunAsync_Does_Not_Log_Warning_On_Success()
    {
        var logSpy = new LogSpy();

        await DetachedNotifyRunner.RunAsync(_ => Task.CompletedTask, TimeSpan.FromSeconds(1), logSpy, "space-1");

        Assert.False(logSpy.HasWarning);
    }

    private static ILogger NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
