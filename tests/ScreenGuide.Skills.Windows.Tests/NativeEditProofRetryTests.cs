using System.Runtime.InteropServices;
using static ScreenGuide.Skills.Windows.LegacyBrowserSearchSemantics;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class NativeEditProofRetryTests
{
    [Fact]
    public void TransientUnreadableNodeRestartsCompleteProofAndAvoidsFallback()
    {
        var attempt = 0;
        var reads = new List<(int Attempt, int Node)>();
        var uiaReads = 0;
        var fallbackReads = 0;
        NativeEditProof ReadProof()
        {
            attempt++;
            return FindNativeEditProof(0, node =>
            {
                reads.Add((attempt, node));
                if (attempt == 1 && node == 2) return null;
                return new EditTreeNode<int>($"{attempt}:{node}", node == 1 ? 42 : 10, node == 0 ? [1, 2, 3] : []);
            });
        }
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(ReadProof,
            () => { uiaReads++; return Control("2:1"); }, 42);
        if (result is null) fallbackReads++;

        Assert.NotNull(result);
        Assert.Equal("2:1", result.Snapshot.NativeSearchIdentity);
        Assert.Equal(2, attempt);
        Assert.Equal(1, uiaReads);
        Assert.Equal(0, fallbackReads);
        Assert.Equal(new[] { (1, 0), (1, 1), (1, 2), (2, 0), (2, 1), (2, 2), (2, 3) }, reads);
    }

    [Theory]
    [InlineData((int)NativeEditProofState.None)]
    [InlineData((int)NativeEditProofState.Multiple)]
    [InlineData((int)NativeEditProofState.Incomplete)]
    public void FailedProofHasOnlyBoundedReadOnlyRetries(int stateValue)
    {
        var state = (NativeEditProofState)stateValue;
        var nativeReads = 0;
        var uiaReads = 0;
        var fallbackReads = 0;
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => { nativeReads++; return new(state); },
            () => { uiaReads++; return Control("input"); }, 42);
        if (result is null) fallbackReads++;

        Assert.Null(result);
        Assert.Equal(state == NativeEditProofState.Incomplete ? 2 : 1, nativeReads);
        Assert.Equal(0, uiaReads);
        Assert.Equal(1, fallbackReads);
    }

    [Theory]
    [InlineData((int)NativeEditProofState.None)]
    [InlineData((int)NativeEditProofState.Multiple)]
    [InlineData((int)NativeEditProofState.Incomplete)]
    public void SecondAttemptMustIndependentlyProveUniqueness(int stateValue)
    {
        var state = (NativeEditProofState)stateValue;
        var reads = 0;
        var uiaReads = 0;
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => ++reads == 1 ? new(NativeEditProofState.Incomplete, "stale-input") : new(state),
            () => { uiaReads++; return Control("stale-input"); }, 42));
        Assert.Equal(2, reads);
        Assert.Equal(0, uiaReads);
    }

    [Theory]
    [InlineData("native-id")]
    [InlineData("owner")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public void RetryNeverSkipsFinalUiaBinding(string changed)
    {
        var reads = 0;
        Assert.Null(WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => ++reads == 1 ? new(NativeEditProofState.Incomplete) : new(NativeEditProofState.Unique, "fresh"),
            () => changed switch
            {
                "native-id" => Control("stale"),
                "owner" => Control("fresh") with { Snapshot = Control("fresh").Snapshot with { OwnerWindowHandle = 99 } },
                "missing" => null,
                _ => throw new System.Windows.Automation.ElementNotAvailableException()
            }, 42));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void StableUniqueProofNeedsNoSecondReadAndStillDoesNotGrantInputAuthority()
    {
        var reads = 0;
        var password = Control("input") with { Snapshot = Control("input").Snapshot with { IsPassword = true } };
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(
            () => { reads++; return new(NativeEditProofState.Unique, "input"); }, () => password, 42);
        Assert.Same(password, result);
        Assert.Equal(1, reads);
        Assert.False(SafeForegroundSearchExecutor.IsReliableCandidate(result!.Snapshot, 42));
    }

    [Theory]
    [InlineData("none", (int)NativeEditProofState.None)]
    [InlineData("one", (int)NativeEditProofState.Unique)]
    [InlineData("multiple", (int)NativeEditProofState.Multiple)]
    [InlineData("unreadable", (int)NativeEditProofState.Incomplete)]
    [InlineData("missing-id", (int)NativeEditProofState.Incomplete)]
    [InlineData("cycle", (int)NativeEditProofState.Incomplete)]
    [InlineData("children-limit", (int)NativeEditProofState.Incomplete)]
    public void CompleteTreeDistinguishesAbsenceAmbiguityAndIncompleteEvidence(string scenario, int expectedValue)
    {
        var expected = (NativeEditProofState)expectedValue;
        var proof = FindNativeEditProof(0, node =>
        {
            if (node == 2 && scenario == "unreadable") return null;
            return new EditTreeNode<int>(node == 2 && scenario == "missing-id" ? "" : node.ToString(),
                node == 1 && scenario != "none" || node == 2 && scenario == "multiple" ? 42 : 10,
                node == 0 ? [1, 2] : node == 2 && scenario == "cycle" ? [0]
                    : node == 2 && scenario == "children-limit" ? Enumerable.Range(3, 129).ToArray() : []);
        });
        Assert.Equal(expected, proof.State);
        Assert.Equal(expected == NativeEditProofState.Unique ? "1" : null, proof.Identity);
    }

    [Fact]
    public void DepthAndTotalNodeLimitsRemainIncomplete()
    {
        Assert.Equal(NativeEditProofState.Incomplete, FindNativeEditProof(0,
            node => new EditTreeNode<int>(node.ToString(), node == 1 ? 42 : 10, [node + 1])).State);
        var reads = 0;
        var proof = FindNativeEditProof(0, node =>
        {
            reads++;
            return new EditTreeNode<int>(node.ToString(), node == 1 ? 42 : 10,
                node < 100 ? Enumerable.Range(node * 100 + 1, 100).ToArray() : []);
        });
        Assert.Equal(NativeEditProofState.Incomplete, proof.State);
        Assert.Equal(4096, reads);
    }

    [Theory]
    [InlineData("initial-owner")]
    [InlineData("final-owner")]
    [InlineData("initial-root")]
    [InlineData("final-root")]
    [InlineData("com")]
    public void WholeProofRequiresOwnerAndRootIdentityAtBothBoundaries(string scenario)
    {
        var ownerReads = 0;
        var rootReads = 0;
        var proof = ReadBoundNativeEditProof(
            () => ++ownerReads == 1 ? scenario != "initial-owner" : scenario != "final-owner",
            () => ++rootReads == 1 ? scenario == "initial-root" ? null : "root" : scenario == "final-root" ? "changed" : "root",
            () => scenario == "com" ? throw new COMException("Test provider unavailable") : new(NativeEditProofState.Unique, "input"));
        Assert.Equal(NativeEditProofState.Incomplete, proof.State);
        Assert.Null(proof.Identity);
    }

    [Fact]
    public void RetryChecksOwnerAndRootAgainAndDoesNotKeepFirstAttemptIdentity()
    {
        var attempts = 0;
        var calls = new List<string>();
        NativeEditProof ReadAttempt()
        {
            attempts++;
            var attempt = attempts;
            var rootReads = 0;
            return ReadBoundNativeEditProof(
                () => { calls.Add($"{attempt}:owner"); return true; },
                () => { calls.Add($"{attempt}:root"); return $"root-{attempt}-{(attempt == 1 ? ++rootReads : 0)}"; },
                () => { calls.Add($"{attempt}:tree"); return new(NativeEditProofState.Unique, $"input-{attempt}"); });
        }
        var result = WindowsDesktopSearchAutomationDriver.TryReadSoleNativeEditWithRetry(ReadAttempt, () => Control("input-2"), 42);
        Assert.NotNull(result);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { "1:owner", "1:root", "1:tree", "1:owner", "1:root", "2:owner", "2:root", "2:tree", "2:owner", "2:root" }, calls);
    }

    private static DesktopSearchControl Control(string identity) => new(new object(), new DesktopSearchControlSnapshot(
        "runtime", 42, "搜索", "search", "", "", true, false, false, false, true, false,
        new DesktopSearchBounds(10, 10, 100, 20), "", identity, true));
}
