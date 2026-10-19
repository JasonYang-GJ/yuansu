using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class ReadOnlyPreparationProbeTests
{
    [Fact]
    public void ReusesOnlyTheReferenceAndReadsCurrentStateForEveryLaterSample()
    {
        var initial = Control("input", 42);
        var current = initial.Snapshot;
        var discoveries = 0;
        var reads = 0;
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            window =>
            {
                Assert.Equal(42, window);
                discoveries++;
                return [initial];
            },
            control =>
            {
                Assert.Same(initial.NativeReference, control.NativeReference);
                reads++;
                return current;
            });

        Assert.Equal(initial.Snapshot, Assert.Single(probe(42)).Snapshot);
        current = initial.Snapshot with
        {
            Value = "current value",
            NativeSearchIdentity = "replacement-native-id",
            NativeSearchContainerPending = true,
            IsEnabled = false,
            IsPassword = true,
            IsReadOnly = true,
            IsOffscreen = true,
            SupportsValuePattern = false
        };
        var changed = Assert.Single(probe(42));

        Assert.Equal(current, changed.Snapshot);
        Assert.False(SafeForegroundSearchExecutor.IsReliableCandidate(changed.Snapshot, 42));
        Assert.Equal(1, discoveries);
        Assert.Equal(1, reads);

        current = initial.Snapshot with { Value = "newer value", HasKeyboardFocus = true };
        Assert.Equal(current, Assert.Single(probe(42)).Snapshot);
        Assert.Equal(1, discoveries);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ChangingWindowRequiresDiscoveryWithoutReadingThePreviousWindowsReference()
    {
        var discoveredWindows = new List<long>();
        var readWindows = new List<long>();
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            window =>
            {
                discoveredWindows.Add(window);
                return [Control("input-" + window, window)];
            },
            control =>
            {
                readWindows.Add(control.Snapshot.OwnerWindowHandle);
                return control.Snapshot;
            });

        _ = probe(42);
        Assert.Equal(84, Assert.Single(probe(84)).Snapshot.OwnerWindowHandle);
        Assert.Empty(readWindows);
        _ = probe(84);
        Assert.Equal(42, Assert.Single(probe(42)).Snapshot.OwnerWindowHandle);

        Assert.Equal(new long[] { 42, 84, 42 }, discoveredWindows);
        Assert.Equal(new long[] { 84 }, readWindows);
    }

    [Theory]
    [InlineData("element-unavailable")]
    [InlineData("com")]
    [InlineData("invalid-operation")]
    [InlineData("argument")]
    [InlineData("win32")]
    public void AFailedReadDiscardsTheReferenceAndRediscoversInThatSample(string failure)
    {
        var old = Control("old", 42);
        var replacement = Control("replacement", 42);
        var discoveries = 0;
        var referencesRead = new List<object>();
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            _ => ++discoveries == 1 ? [old] : [replacement],
            control =>
            {
                referencesRead.Add(control.NativeReference);
                if (ReferenceEquals(control.NativeReference, old.NativeReference))
                    throw failure switch
                    {
                        "element-unavailable" => new ElementNotAvailableException(),
                        "com" => new COMException("Fixture provider expired."),
                        "invalid-operation" => new InvalidOperationException("Fixture provider expired."),
                        "argument" => new ArgumentException("Fixture provider expired."),
                        "win32" => new Win32Exception(5),
                        _ => new Exception("Unknown fixture failure.")
                    };
                return replacement.Snapshot;
            });

        _ = probe(42);
        var recovered = Assert.Single(probe(42));

        Assert.Same(replacement.NativeReference, recovered.NativeReference);
        Assert.Equal(2, discoveries);
        Assert.Single(referencesRead);
        _ = probe(42);
        Assert.Equal(new[] { old.NativeReference, replacement.NativeReference }, referencesRead);
        Assert.Equal(2, discoveries);
    }

    [Fact]
    public void ChangedOwnerInvalidatesTheSampleAndRediscoversOnlyOnTheNextCall()
    {
        var initial = Control("initial", 42);
        var replacement = Control("replacement", 42);
        var discoveries = 0;
        var reads = 0;
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            _ => ++discoveries == 1 ? [initial] : [replacement],
            control =>
            {
                reads++;
                return control.Snapshot with { OwnerWindowHandle = 84 };
            });

        _ = probe(42);
        Assert.Empty(probe(42));
        Assert.Equal(1, discoveries);
        Assert.Equal(1, reads);
        Assert.Same(replacement.NativeReference, Assert.Single(probe(42)).NativeReference);
        Assert.Equal(2, discoveries);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnEmptyDiscoveryCannotPreventTheNextReadinessAttemptFromDiscovering()
    {
        var input = Control("input", 42);
        var discoveries = 0;
        var reads = 0;
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            _ => ++discoveries == 1 ? [] : [input],
            control =>
            {
                reads++;
                return control.Snapshot;
            });

        Assert.Empty(probe(42));
        Assert.Same(input.NativeReference, Assert.Single(probe(42)).NativeReference);
        Assert.Equal(2, discoveries);
        Assert.Equal(0, reads);
        _ = probe(42);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void SeparatePreparationAttemptsDoNotShareTheirCachedReferences()
    {
        var discoveries = 0;
        var first = Control("first-attempt", 42);
        var second = Control("second-attempt", 42);
        var referencesRead = new List<object>();
        IReadOnlyList<DesktopSearchControl> Discover(long _) =>
            ++discoveries == 1 ? [first] : [second];
        DesktopSearchControlSnapshot Read(DesktopSearchControl control)
        {
            referencesRead.Add(control.NativeReference);
            return control.Snapshot;
        }
        var firstProbe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(Discover, Read);
        var secondProbe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(Discover, Read);

        Assert.Same(first.NativeReference, Assert.Single(firstProbe(42)).NativeReference);
        Assert.Same(second.NativeReference, Assert.Single(secondProbe(42)).NativeReference);
        _ = firstProbe(42);
        _ = secondProbe(42);

        Assert.Equal(2, discoveries);
        Assert.Equal(new[] { first.NativeReference, second.NativeReference }, referencesRead);
    }

    [Fact]
    public void MultipleDiscoveredCandidatesRemainMultipleAndEveryCandidateIsReadAgain()
    {
        var first = Control("first", 42);
        var second = Control("second", 42);
        var referencesRead = new List<object>();
        var probe = WindowsDesktopSearchAutomationDriver.CreateReadOnlyPreparationProbe(
            _ => [first, second],
            control =>
            {
                referencesRead.Add(control.NativeReference);
                return control.Snapshot with { Value = "updated" };
            });

        Assert.Equal(2, probe(42).Count);
        var updated = probe(42);

        Assert.Equal(2, updated.Count);
        Assert.All(updated, control => Assert.Equal("updated", control.Snapshot.Value));
        Assert.Equal(new[] { first.NativeReference, second.NativeReference }, referencesRead);
    }

    private static DesktopSearchControl Control(string identity, long window) => new(new object(),
        new DesktopSearchControlSnapshot("uia:" + identity, window, "搜索", "", "", "", true, false,
            false, false, true, false, new(10, 10, 200, 30), "", identity, true));
}
