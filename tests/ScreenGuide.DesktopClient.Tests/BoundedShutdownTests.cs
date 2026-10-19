using ScreenGuide.DesktopClient.Services;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task NativeStartupThatNeverAcknowledgesCannotBlockExitForever()
    {
        var nativeStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = await BoundedShutdown.TryCompleteAsync(_ => nativeStartup.Task,
            TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(completed);
    }

    [Fact]
    public async Task HostThatNeverRepliesReceivesCancellation()
    {
        CancellationToken received = default;
        var completed = await BoundedShutdown.TryCompleteAsync(token =>
        {
            received = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(completed);
        Assert.True(received.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulCleanupAndFailureBothAllowExit()
    {
        Assert.True(await BoundedShutdown.TryCompleteAsync(_ => Task.CompletedTask, TimeSpan.FromSeconds(1)));
        Assert.False(await BoundedShutdown.TryCompleteAsync(_ => Task.FromException(new IOException()), TimeSpan.FromSeconds(1)));
    }
}
