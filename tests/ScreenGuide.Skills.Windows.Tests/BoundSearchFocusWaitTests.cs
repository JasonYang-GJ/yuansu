namespace ScreenGuide.Skills.Windows.Tests;

public sealed class BoundSearchFocusWaitTests
{
    [Fact]
    public void AcceptsDelayedFocusBeyondTheFormerQuarterSecondWindow()
    {
        var clock = new FocusClock();
        var reads = 0;
        var validations = 0;

        var focused = BoundSearchFocusWait.Run(
            () => { reads++; return clock.Elapsed >= TimeSpan.FromMilliseconds(400); },
            () => validations++, clock, clock.Advance);

        Assert.True(focused);
        Assert.Equal(TimeSpan.FromMilliseconds(400), clock.Elapsed);
        Assert.Equal(reads * 2, validations);
    }

    [Fact]
    public void ReturnsImmediatelyWhenTheFirstValidatedReceiptIsFocused()
    {
        var events = new List<string>();
        var clock = new FocusClock();

        Assert.True(BoundSearchFocusWait.Run(
            () => { events.Add("read"); return true; },
            () => events.Add("validate"), clock,
            _ => throw new InvalidOperationException("A focused receipt must not wait.")));

        Assert.Equal(["validate", "read", "validate"], events);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public void StopsReadingAtTheOriginalDeadlineWhenFocusNeverArrives()
    {
        var clock = new FocusClock();
        var reads = new List<TimeSpan>();
        var waits = new List<TimeSpan>();

        Assert.False(BoundSearchFocusWait.Run(
            () => { reads.Add(clock.Elapsed); return false; }, () => { }, clock,
            delay => { waits.Add(delay); clock.Advance(delay); }));

        Assert.NotEmpty(reads);
        Assert.All(reads, elapsed => Assert.True(elapsed < TimeSpan.FromSeconds(1)));
        Assert.All(waits, delay => Assert.InRange(delay, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(25)));
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrDuringAReadCannotReturnLateFocus(bool cancelDuringRead)
    {
        using var cancellation = new CancellationTokenSource();
        if (!cancelDuringRead) cancellation.Cancel();
        var reads = 0;
        var waits = 0;

        Assert.Throws<OperationCanceledException>(() => BoundSearchFocusWait.Run(
            () => { reads++; cancellation.Cancel(); return true; },
            cancellation.Token.ThrowIfCancellationRequested, new FocusClock(), _ => waits++));

        Assert.Equal(cancelDuringRead ? 1 : 0, reads);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void WindowChangeAfterAReadRejectsFocusAndStopsFurtherPolling()
    {
        var validations = 0;
        var reads = 0;
        var waits = 0;
        var changed = new WindowIdentityException(WindowIdentityErrorCodes.Changed, "test window changed");

        var actual = Assert.Throws<WindowIdentityException>(() => BoundSearchFocusWait.Run(
            () => { reads++; return true; },
            () => { if (++validations == 2) throw changed; }, new FocusClock(), _ => waits++));

        Assert.Same(changed, actual);
        Assert.Equal(1, reads);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void InputIdentityFailureIsNotRetriedOrReplaced()
    {
        var reads = 0;
        var waits = 0;
        var changed = new DesktopSearchException(DesktopSearchErrorCodes.TargetChanged, "test input changed");

        var actual = Assert.Throws<DesktopSearchException>(() => BoundSearchFocusWait.Run(
            () => { reads++; throw changed; }, () => { }, new FocusClock(), _ => waits++));

        Assert.Same(changed, actual);
        Assert.Equal(1, reads);
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlowSynchronousReadConsumesTheDeadlineWithoutAcceptingLateFocus(bool focused)
    {
        var clock = new FocusClock();
        var reads = 0;
        var waits = new List<TimeSpan>();
        var validations = 0;

        Assert.False(BoundSearchFocusWait.Run(
            () =>
            {
                reads++;
                if (reads > 2) throw new InvalidOperationException("The deadline must not restart after a read.");
                clock.Advance(TimeSpan.FromMilliseconds(reads == 1 ? 900 : 100));
                return reads == 2 && focused;
            }, () => validations++, clock,
            delay => { waits.Add(delay); clock.Advance(delay); }));

        Assert.Equal(2, reads);
        Assert.Equal(4, validations);
        Assert.Equal(TimeSpan.FromMilliseconds(25), Assert.Single(waits));
        Assert.Equal(TimeSpan.FromMilliseconds(1025), clock.Elapsed);
    }

    private sealed class FocusClock : TimeProvider
    {
        private long _timestamp;
        public TimeSpan Elapsed => TimeSpan.FromTicks(_timestamp);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
