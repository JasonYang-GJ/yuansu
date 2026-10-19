using System.Runtime.InteropServices;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed partial class SearchReadinessAuthorityTests
{
    [Fact]
    public void SuccessfulNativeSubmissionProvidesReadOnlyVerificationOfItsExactInput()
    {
        var setup = Submitted();
        Assert.True(setup.Automation.LastResult!.Verified);
        Assert.NotNull(setup.Automation.LastResult.VerifySubmittedInput);

        Assert.True(setup.VerifyReceipt());
        Assert.Equal(SearchResultPageState.Results, setup.ReadResults().State);

        Assert.Equal(1, setup.Input.Discoveries);
        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("read-only")]
    [InlineData("value")]
    [InlineData("native-id")]
    [InlineData("owner")]
    [InlineData("semantics")]
    [InlineData("pending-container")]
    [InlineData("disabled")]
    [InlineData("no-value-pattern")]
    public void SubmittedInputReceiptRejectsChangedControlEvidence(string change)
    {
        var setup = Submitted();
        setup.Input.Mutate("final-a", state => change switch
        {
            "password" => state with { IsPassword = true },
            "read-only" => state with { IsReadOnly = true },
            "value" => state with { Value = "其他查询" },
            "native-id" => state with { NativeSearchIdentity = "replaced" },
            "owner" => state with { OwnerWindowHandle = 43 },
            "semantics" => state with { HasNativeSearchSemantics = false },
            "pending-container" => state with { NativeSearchContainerPending = true },
            "disabled" => state with { IsEnabled = false },
            _ => state with { SupportsValuePattern = false }
        });

        Assert.False(setup.VerifyReceipt());
        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);
        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("process-start")]
    [InlineData("handle")]
    [InlineData("process-name")]
    [InlineData("foreground")]
    public void SubmittedInputReceiptCannotMoveToAnotherWindowOrProcess(string change)
    {
        var setup = Submitted();
        var reads = setup.Input.Reads;
        setup.Windows.Current = change switch
        {
            "pid" => Window with { ProcessId = 456 },
            "process-start" => Window with { ProcessStartTimeUtc = Window.ProcessStartTimeUtc.AddSeconds(1) },
            "handle" => Window with { WindowHandle = 43 },
            "process-name" => Window with { ProcessName = "other" },
            _ => Window
        };
        if (change == "foreground") setup.Windows.Foreground = 43;

        Assert.False(setup.VerifyReceipt());
        Assert.Equal(reads, setup.Input.Reads);
        Assert.Equal(0, setup.ResultDiscoveries);
    }

    [Fact]
    public void SubmittedInputReceiptRejectsAnotherQueryWithoutReadingOrRediscovering()
    {
        var setup = Submitted();
        var reads = setup.Input.Reads;

        Assert.False(setup.VerifyReceipt("其他歌曲"));
        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults("其他歌曲").State);

        Assert.Equal(reads, setup.Input.Reads);
        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.PageReads);
    }

    [Fact]
    public void SubmittedInputReceiptAllowsNavigationTitleChangesWithoutRequiringFocus()
    {
        var setup = Submitted();
        setup.Windows.Current = Window with { WindowTitle = "示例歌曲 - 搜索结果" };
        setup.Input.Mutate("final-a", state => state with { HasKeyboardFocus = false });

        Assert.True(setup.VerifyReceipt());
        Assert.Equal(SearchResultPageState.Results, setup.ReadResults().State);

        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.Input.Focuses);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringSubmittedInputReadbackPropagatesEvenIfComAlsoFails(bool comFailure)
    {
        var setup = Submitted();
        using var cancellation = new CancellationTokenSource();
        setup.Input.OnRead = () =>
        {
            cancellation.Cancel();
            if (comFailure) throw new COMException("Synthetic provider interrupted");
        };

        Assert.ThrowsAny<OperationCanceledException>(() => setup.ReadResults(cancellation: cancellation.Token));

        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("pid")]
    public void IdentityIsCheckedAgainAfterTheSubmittedInputComRead(string change)
    {
        var setup = Submitted();
        setup.Input.OnRead = () =>
        {
            if (change == "foreground") setup.Windows.Foreground = 43;
            else setup.Windows.Current = Window with { ProcessId = 456 };
        };

        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);

        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceiptRemainsBoundOnLaterPollsAndCannotUseAReplacementInput(bool comFailure)
    {
        var setup = Submitted();
        // The compatibility discovery would find an apparently valid same-query replacement.
        // It must never run after the submitted-input receipt fails, including the next poll.
        var replacement = Control("replacement", "replacement-native");
        setup.ResultInputs = [replacement with { Snapshot = replacement.Snapshot with { Value = Query } }];
        if (comFailure) setup.Input.OnRead = () => throw new COMException("Synthetic stale input");
        else setup.Input.Mutate("final-a", state => state with { Value = "edited" });

        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);
        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);

        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Writes);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Fact]
    public void SuccessfulNonNativeSearchKeepsTheExistingDiscoveryReadbackPath()
    {
        var setup = new Harness();
        setup.Prepare();
        var nonNative = Control("final-a", "");
        nonNative = nonNative with { Snapshot = nonNative.Snapshot with { Name = "搜索", HasNativeSearchSemantics = false } };
        setup.Input.Controls = [nonNative];
        setup.Search();

        Assert.True(setup.Automation.LastResult!.Verified);
        Assert.Null(setup.Automation.LastResult.VerifySubmittedInput);
        setup.ResultInputs = [nonNative with { Snapshot = nonNative.Snapshot with { Value = Query } }];
        Assert.Equal(SearchResultPageState.Results, setup.ReadResults().State);
        Assert.Equal(1, setup.ResultDiscoveries);
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("submission")]
    public void FailedSearchCannotReturnASubmittedInputReceipt(string failure)
    {
        var setup = new Harness();
        setup.Prepare();
        if (failure == "discovery") setup.Input.Controls = [];
        else setup.Input.BeforeSubmit = () => setup.Input.Mutate("final-a", state => state with { Value = "changed" });

        Assert.Throws<DesktopSearchException>(() => setup.Search());

        Assert.Null(setup.Automation.LastResult);
        Assert.Equal(0, setup.Input.Submissions);
    }

    [Fact]
    public void OpeningAgainCannotInheritThePreviousSearchReceipt()
    {
        var setup = Submitted();
        setup.Open();
        var reads = setup.Input.Reads;

        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);

        Assert.Equal(1, setup.ResultDiscoveries);
        Assert.Equal(reads, setup.Input.Reads);
        Assert.Equal(1, setup.PageReads);
    }

    [Fact]
    public void AFailedNewSearchCannotInheritThePreviousSearchReceipt()
    {
        var setup = Submitted();
        // Fail before the automation service is called: only the workflow can clear its receipt.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => setup.Search(cancellation.Token));
        var reads = setup.Input.Reads;

        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);

        Assert.Equal(1, setup.ResultDiscoveries);
        Assert.Equal(reads, setup.Input.Reads);
        Assert.Equal(1, setup.PageReads);
        Assert.Equal(1, setup.Input.Submissions);
    }

    [Theory]
    [InlineData("wrong-query")]
    [InlineData("busy")]
    [InlineData("unreadable")]
    [InlineData("no-results")]
    public void ValidInputReceiptDoesNotReplaceActualResultPageVerification(string invalidPage)
    {
        var setup = Submitted();
        setup.ObservePage = _ => new(invalidPage != "unreadable", true,
            invalidPage == "wrong-query" ? "其他查询" : Query, "result-page", "new-results",
            invalidPage == "busy", invalidPage == "no-results" ? [] : [new("song-a", "示例歌曲", new object())]);

        Assert.Equal(SearchResultPageState.Unverified, setup.ReadResults().State);
        Assert.Equal(0, setup.ResultDiscoveries);
        Assert.Equal(2, setup.PageReads);
        Assert.Equal(1, setup.Input.Submissions);
    }

    private static Harness Submitted()
    {
        var setup = new Harness();
        setup.Prepare();
        setup.Search();
        return setup;
    }
}
