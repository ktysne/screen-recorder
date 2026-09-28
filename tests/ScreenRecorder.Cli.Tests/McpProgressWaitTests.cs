using ScreenRecorder.Cli;
using Xunit;

namespace ScreenRecorder.Cli.Tests;

public sealed class McpProgressWaitTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task CancellationStopsProgressButWaitsForTheExecutionToFinish()
    {
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var sent = 0;
        var waiting = McpServerHost.WaitWithProgressAsync(execution.Task, (_, _) =>
        {
            Interlocked.Increment(ref sent);
            return Task.CompletedTask;
        }, Interval, cancellation.Token);

        await WaitUntilAsync(() => Volatile.Read(ref sent) > 0);
        cancellation.Cancel();
        await Task.Delay(Interval * 10);
        var sentAfterCancellation = Volatile.Read(ref sent);
        await Task.Delay(Interval * 10);

        Assert.False(waiting.IsCompleted);
        Assert.Equal(sentAfterCancellation, Volatile.Read(ref sent));

        execution.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProgressSendCancelledMidwayDoesNotEndTheWait()
    {
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var waiting = McpServerHost.WaitWithProgressAsync(execution.Task, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, Interval, cancellation.Token);

        await Task.Delay(Interval * 10);
        Assert.False(waiting.IsCompleted);

        execution.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(Interval, deadline.Token);
    }
}
