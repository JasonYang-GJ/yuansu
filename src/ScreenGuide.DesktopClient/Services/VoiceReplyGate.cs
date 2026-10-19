using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed record VoiceReply(Guid RequestId, string Text);

/// <summary>Ephemeral playback eligibility, never a second Session or Turn store.</summary>
public sealed class VoiceReplyGate
{
    private PendingReply? _pending;

    public Guid? BeginInput(SessionSnapshotDto? session, long? listeningGeneration,
        string? coordinatorId = null, DateTimeOffset? coordinatorStartedAt = null)
    {
        Invalidate();
        coordinatorId = session?.CoordinatorInstanceId ?? coordinatorId;
        coordinatorStartedAt = session?.CoordinatorStartedAtUtc ?? coordinatorStartedAt;
        if (listeningGeneration is null || string.IsNullOrEmpty(coordinatorId)
            || coordinatorStartedAt is null) return null;
        // The first input has no Session yet. Preserve only this request's playback
        // eligibility until the original Host receipt supplies its Session and Turn.
        _pending = new(Guid.NewGuid(), session?.SessionId, coordinatorId,
            coordinatorStartedAt.Value, listeningGeneration.Value);
        return _pending.RequestId;
    }

    public void AttachTurn(Guid? requestId, SessionTurnCommandResultDto receipt)
    {
        if (_pending is null || _pending.RequestId != requestId) return;
        if (receipt.WasDuplicate || receipt.SessionId == Guid.Empty || receipt.TurnId == Guid.Empty
            || (_pending.SessionId is { } sessionId && sessionId != receipt.SessionId)
            || (_pending.TurnId is { } turnId && turnId != receipt.TurnId))
        { Invalidate(); return; }
        _pending.SessionId = receipt.SessionId;
        _pending.TurnId = receipt.TurnId;
    }

    public bool IsCurrent(Guid requestId, SessionSnapshotDto? snapshot, long? listeningGeneration)
    {
        if (_pending is not { } pending || pending.RequestId != requestId) return false;
        if (listeningGeneration != pending.ListeningGeneration)
        { Invalidate(); return false; }
        if (snapshot is null)
        {
            // An initial poll may still report no Session while its first input is
            // registering. It cannot authorize speech or discard that input's receipt.
            if (!pending.AwaitingFirstProjection) Invalidate();
            return false;
        }
        if ((pending.SessionId is { } sessionId && snapshot.SessionId != sessionId)
            || snapshot.CoordinatorInstanceId != pending.CoordinatorId
            || snapshot.CoordinatorStartedAtUtc != pending.CoordinatorStartedAt)
        {
            Invalidate();
            return false;
        }
        pending.AwaitingFirstProjection = false;
        var turn = snapshot.Turns.SingleOrDefault(item => item.Id == pending.TurnId);
        if (pending.Consumed && turn is null) { Invalidate(); return false; }
        if (turn is not null && (turn.InputModality != "Voice" || turn.CancellationRequested
            || turn.Phase is "Cancelled" or "Interrupted"
            || snapshot.Turns.Any(item => item.SequenceNumber > turn.SequenceNumber)))
        {
            Invalidate();
            return false;
        }
        return true;
    }

    public VoiceReply? TryTakeReply(SessionSnapshotDto? snapshot, long? listeningGeneration)
    {
        if (_pending is not { } pending || !IsCurrent(pending.RequestId, snapshot, listeningGeneration)
            || pending.Consumed || pending.TurnId is null) return null;
        var turn = snapshot!.Turns.SingleOrDefault(item => item.Id == pending.TurnId);
        if (turn?.Phase is not ("Completed" or "Failed")) return null;
        pending.Consumed = true;
        var text = turn.Phase == "Failed"
            ? turn.FailureMessage ?? turn.ResultSummary ?? "这次请求没有完成，请查看界面提示。"
            : turn.ResultSummary ?? "这次请求已完成。";
        return string.IsNullOrWhiteSpace(text) ? null : new VoiceReply(pending.RequestId, text);
    }

    public void Invalidate() => _pending = null;

    private sealed class PendingReply(Guid requestId, Guid? sessionId, string coordinatorId,
        DateTimeOffset coordinatorStartedAt, long listeningGeneration)
    {
        public Guid RequestId { get; } = requestId;
        public Guid? SessionId { get; set; } = sessionId;
        public bool AwaitingFirstProjection { get; set; } = sessionId is null;
        public string CoordinatorId { get; } = coordinatorId;
        public DateTimeOffset CoordinatorStartedAt { get; } = coordinatorStartedAt;
        public long ListeningGeneration { get; } = listeningGeneration;
        public Guid? TurnId { get; set; }
        public bool Consumed { get; set; }
    }
}
