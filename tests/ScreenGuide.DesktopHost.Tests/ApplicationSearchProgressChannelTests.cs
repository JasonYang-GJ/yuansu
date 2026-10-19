using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class ApplicationSearchProgressChannelTests
{
    [Fact]
    public async Task SimultaneousExecutionsNeverCrossTheirObservers()
    {
        var channel = new ApplicationSearchProgressChannel();
        var first = new List<string>();
        var second = new List<string>();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Run(List<string> received, string name)
        {
            using var scope = channel.Observe((progress, _) => { received.Add(progress.ApplicationName); return Task.CompletedTask; });
            await barrier.Task;
            await channel.ReportAsync(new(ApplicationSearchStage.Opening, name));
        }
        var a = Run(first, "甲应用");
        var b = Run(second, "乙应用");
        barrier.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(["甲应用"], first);
        Assert.Equal(["乙应用"], second);
    }

    [Fact]
    public async Task DisposedExecutionRejectsLateChildContextProgress()
    {
        var channel = new ApplicationSearchProgressChannel();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = 0;
        Task late;
        using (channel.Observe((_, _) => { updates++; return Task.CompletedTask; }))
        {
            late = Task.Run(async () => { await barrier.Task; await channel.ReportAsync(new(ApplicationSearchStage.Searching, "旧应用")); });
        }
        barrier.SetResult();
        await late;
        Assert.Equal(0, updates);
    }

    [Fact]
    public async Task CancelledReportsNeverReachObserver()
    {
        var channel = new ApplicationSearchProgressChannel();
        var updates = 0;
        using var scope = channel.Observe((_, _) => { updates++; return Task.CompletedTask; });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReportAsync(new(ApplicationSearchStage.Searching, "应用"), cancelled.Token));
        Assert.Equal(0, updates);
    }
}
