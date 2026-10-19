using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class VoiceReplyGateTests
{
    [Fact]
    public void CompletedVoiceReplyIsConsumedOnceEvenAcrossRepeatedRefreshes()
    {
        var snapshot = Snapshot();
        var gate = Track(snapshot);
        Assert.Equal("回答完成", gate.TryTakeReply(snapshot, 1)!.Text);
        for (var i = 0; i < 5; i++) Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void FailedRequestSpeaksItsFailureInsteadOfAnEarlierActionPlan()
    {
        var snapshot = Snapshot("Failed") with { Turns = [Turn("Failed") with { ResultSummary = "准备打开软件", FailureMessage = "未找到这个软件" }] };
        Assert.Equal("未找到这个软件", Track(snapshot).TryTakeReply(snapshot, 1)!.Text);
    }

    [Theory]
    [InlineData("Understanding")]
    [InlineData("Responding")]
    [InlineData("WaitingForConfirmation")]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    public void IntermediateOrCancelledTurnsAreNeverAutomaticallySpoken(string phase)
    {
        var snapshot = Snapshot(phase);
        Assert.Null(Track(snapshot).TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void HistoryTextRequestsAndDuplicatesNeverBecomeSpeech()
    {
        var snapshot = Snapshot();
        Assert.Null(new VoiceReplyGate().TryTakeReply(snapshot, 1));
        var text = snapshot with { Turns = [snapshot.Turns[0] with { InputModality = "Text" }] };
        Assert.Null(Track(text).TryTakeReply(text, 1));
        var gate = new VoiceReplyGate();
        var request = gate.BeginInput(snapshot, 1);
        gate.AttachTurn(request, Receipt(snapshot, wasDuplicate: true));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("host")]
    [InlineData("host-start")]
    [InlineData("listening-generation")]
    [InlineData("stopped")]
    public void ContextChangePermanentlyRevokesPlayback(string change)
    {
        var snapshot = Snapshot();
        var gate = Track(snapshot);
        var changed = change switch
        {
            "session" => snapshot with { SessionId = Guid.NewGuid() },
            "host" => snapshot with { CoordinatorInstanceId = "restarted" },
            "host-start" => snapshot with { CoordinatorStartedAtUtc = snapshot.CoordinatorStartedAtUtc.AddSeconds(1) },
            _ => snapshot
        };
        long? generation = change == "stopped" ? null : change == "listening-generation" ? 2 : 1;
        Assert.Null(gate.TryTakeReply(changed, generation));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void NewerInputAndCancellationInvalidatePendingResults()
    {
        var snapshot = Snapshot();
        var gate = Track(snapshot);
        gate.Invalidate();
        Assert.Null(gate.TryTakeReply(snapshot, 1));
        gate = Track(snapshot);
        var replaced = snapshot with { Turns = [snapshot.Turns[0], Turn("Responding") with { SequenceNumber = 2 }] };
        Assert.Null(gate.TryTakeReply(replaced, 1));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void LateSubmissionReceiptCannotRestoreAnInvalidatedRequest()
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var oldRequest = gate.BeginInput(snapshot, 1);
        gate.BeginInput(snapshot, 1);
        gate.AttachTurn(oldRequest, Receipt(snapshot));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void APlayingReplyLosesEligibilityWhenItsTurnDisappearsFromTheSnapshot()
    {
        var snapshot = Snapshot();
        var gate = Track(snapshot);
        var reply = gate.TryTakeReply(snapshot, 1)!;
        Assert.False(gate.IsCurrent(reply.RequestId, snapshot with { Turns = [] }, 1));
        Assert.False(gate.IsCurrent(reply.RequestId, snapshot, 1));
    }

    [Fact]
    public void CompletionArrivingBeforeSubmissionReceiptIsStillAnnouncedOnce()
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var request = gate.BeginInput(snapshot, 1);
        Assert.Null(gate.TryTakeReply(snapshot, 1));
        gate.AttachTurn(request, Receipt(snapshot));
        Assert.NotNull(gate.TryTakeReply(snapshot, 1));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", true)]
    [InlineData("Failed", false)]
    public void FirstVoiceReplyBindsToItsReceiptAndSpeaksOnceInEitherArrivalOrder(
        string phase, bool projectionFirst)
    {
        var snapshot = Snapshot(phase);
        var gate = new VoiceReplyGate();
        var request = BeginFirstInput(gate, snapshot);
        Assert.NotNull(request);
        Assert.Null(gate.TryTakeReply(null, 1));
        if (projectionFirst) Assert.Null(gate.TryTakeReply(snapshot, 1));
        gate.AttachTurn(request, Receipt(snapshot));
        // A poll dispatched before registration may still carry the empty initial state.
        if (!projectionFirst) Assert.Null(gate.TryTakeReply(null, 1));
        Assert.Equal("回答完成", gate.TryTakeReply(snapshot, 1)!.Text);
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void FirstVoiceInputRequiresKnownHostAndActiveListening()
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        Assert.Null(gate.BeginInput(null, 1));
        Assert.Null(gate.BeginInput(null, 1, snapshot.CoordinatorInstanceId));
        Assert.Null(gate.BeginInput(null, null, snapshot.CoordinatorInstanceId, snapshot.CoordinatorStartedAtUtc));
        gate.AttachTurn(Guid.NewGuid(), Receipt(snapshot));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("generation")]
    [InlineData("host")]
    [InlineData("host-start")]
    [InlineData("new-topic")]
    [InlineData("new-input")]
    public void LateFirstReceiptCannotRestoreRevokedPlayback(string change)
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var request = BeginFirstInput(gate, snapshot);
        switch (change)
        {
            case "stop": Assert.Null(gate.TryTakeReply(null, null)); break;
            case "generation": Assert.Null(gate.TryTakeReply(null, 2)); break;
            case "host":
                Assert.Null(gate.TryTakeReply(snapshot with { CoordinatorInstanceId = "replacement-host" }, 1));
                break;
            case "host-start":
                Assert.Null(gate.TryTakeReply(snapshot with
                    { CoordinatorStartedAtUtc = snapshot.CoordinatorStartedAtUtc.AddSeconds(1) }, 1));
                break;
            case "new-topic": gate.Invalidate(); break;
            case "new-input": BeginFirstInput(gate, snapshot); break;
        }
        gate.AttachTurn(request, Receipt(snapshot));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void FirstReceiptCannotAuthorizeAnotherSessionOrRestoreEligibilityAfterSwitching()
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var request = BeginFirstInput(gate, snapshot);
        gate.AttachTurn(request, Receipt(snapshot));
        Assert.Null(gate.TryTakeReply(snapshot with { SessionId = Guid.NewGuid() }, 1));
        gate.AttachTurn(request, Receipt(snapshot));
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    [Fact]
    public void ARevokedFirstReceiptDoesNotReplaceTheNewInputPlayback()
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var oldRequest = BeginFirstInput(gate, snapshot);
        var currentRequest = gate.BeginInput(snapshot, 1);
        var oldReceipt = new SessionTurnCommandResultDto(Guid.NewGuid(), Guid.NewGuid(), false);
        gate.AttachTurn(oldRequest, oldReceipt);
        gate.AttachTurn(currentRequest, Receipt(snapshot));
        Assert.Equal("回答完成", gate.TryTakeReply(snapshot, 1)!.Text);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("other-session")]
    [InlineData("other-turn")]
    public void AConflictingReceiptCannotRebindPlayback(string conflict)
    {
        var snapshot = Snapshot();
        var gate = new VoiceReplyGate();
        var request = BeginFirstInput(gate, snapshot);
        gate.AttachTurn(request, Receipt(snapshot));
        var changed = conflict switch
        {
            "duplicate" => Receipt(snapshot, wasDuplicate: true),
            "other-session" => Receipt(snapshot) with { SessionId = Guid.NewGuid() },
            _ => Receipt(snapshot) with { TurnId = Guid.NewGuid() }
        };
        gate.AttachTurn(request, changed);
        Assert.Null(gate.TryTakeReply(snapshot, 1));
    }

    private static Guid? BeginFirstInput(VoiceReplyGate gate, SessionSnapshotDto snapshot) =>
        gate.BeginInput(null, 1, snapshot.CoordinatorInstanceId, snapshot.CoordinatorStartedAtUtc);

    private static VoiceReplyGate Track(SessionSnapshotDto snapshot)
    {
        var gate = new VoiceReplyGate();
        var request = gate.BeginInput(snapshot, 1);
        gate.AttachTurn(request, Receipt(snapshot));
        return gate;
    }

    private static UnifiedSessionTurnDto Turn(string phase) => new(Guid.NewGuid(), 1, "你好", "Voice", "Conversation", phase,
        "None", "Conversation", null, null, null, null, null, null, false, false, "回答完成", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static SessionTurnCommandResultDto Receipt(SessionSnapshotDto snapshot, bool wasDuplicate = false) =>
        new(snapshot.SessionId, snapshot.Turns[0].Id, wasDuplicate);

    private static SessionSnapshotDto Snapshot(string phase = "Completed") => new(1, "host", DateTimeOffset.UtcNow,
        Guid.NewGuid(), Guid.NewGuid(), "topic", phase, null, null, null, [], [Turn(phase)], []);
}
