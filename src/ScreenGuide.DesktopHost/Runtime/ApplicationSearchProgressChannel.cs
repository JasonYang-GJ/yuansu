using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

/// <summary>Async execution-local observer; persisted Turn state belongs to SessionCoordinator.</summary>
internal sealed class ApplicationSearchProgressChannel : IApplicationSearchProgressSink
{
    private readonly AsyncLocal<Subscription?> _current = new();

    public IDisposable Observe(Func<ApplicationSearchProgress, CancellationToken, Task> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var parent = _current.Value;
        var subscription = new Subscription(report);
        _current.Value = subscription;
        return new Scope(this, subscription, parent);
    }

    public Task ReportAsync(ApplicationSearchProgress progress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(progress);
        if (!Enum.IsDefined(progress.Stage)) throw new ArgumentOutOfRangeException(nameof(progress));
        return _current.Value is { Closed: false } subscription
            ? subscription.Report(progress, cancellationToken) : Task.CompletedTask;
    }

    private sealed class Subscription(Func<ApplicationSearchProgress, CancellationToken, Task> report)
    {
        public Func<ApplicationSearchProgress, CancellationToken, Task> Report { get; } = report;
        public volatile bool Closed;
    }

    private sealed class Scope(ApplicationSearchProgressChannel owner, Subscription subscription, Subscription? parent) : IDisposable
    {
        public void Dispose()
        {
            subscription.Closed = true;
            if (ReferenceEquals(owner._current.Value, subscription)) owner._current.Value = parent;
        }
    }
}
