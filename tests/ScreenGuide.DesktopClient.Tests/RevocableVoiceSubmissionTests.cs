using System.Collections.Concurrent;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Tests;

public sealed class RevocableVoiceSubmissionTests
{
    [Fact]
    public async Task AConfirmedManualStopCanReconcileOnlyTheRecordedCancellationReceipt()
    {
        var receipt = Receipt();
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ => Task.FromResult(receipt),
            (_, _) => Task.FromException(new IOException("controlled cancellation failure")), input.Token, default);
        await operation.WaitAsync();
        input.Cancel();
        await Assert.ThrowsAsync<VoiceSubmissionUnconfirmedException>(() => operation.WaitForRegistrationBoundaryAsync());
        Assert.False(operation.HasConfirmedCancellation);
        Assert.False(operation.AcknowledgeCancellation(Receipt()));
        Assert.False(operation.HasConfirmedCancellation);
        Assert.True(operation.AcknowledgeCancellation(receipt));
        Assert.True(operation.HasConfirmedCancellation);
    }

    [Fact]
    public async Task TopicCreationUsesTheSameBoundaryWithoutCreatingASecondTaskOrReplaying()
    {
        var receipt = NewCompletion<string>();
        var dispatched = NewCompletion<bool>();
        using var input = new CancellationTokenSource();
        var submissions = 0;
        using var operation = new RevocableInputSubmission<string>(_ =>
        {
            submissions++;
            dispatched.TrySetResult(true);
            return receipt.Task;
        }, (_, _) => Task.CompletedTask, input.Token, default);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync());
        var boundary = operation.WaitForRegistrationBoundaryAsync();
        Assert.False(boundary.IsCompleted);
        receipt.SetResult("exact-created-topic");
        await boundary.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("exact-created-topic", operation.ConfirmedCancellationReceipt);
        Assert.Equal(1, submissions);
    }

    [Fact]
    public async Task AnUnconfirmedTopicCreationBlocksNewWritesUntilItsOwnLateReceiptArrives()
    {
        var receipt = NewCompletion<string>();
        var dispatched = NewCompletion<bool>();
        var confirmed = NewCompletion<string>();
        using var operation = new RevocableInputSubmission<string>(_ =>
        {
            dispatched.TrySetResult(true);
            return receipt.Task;
        }, (_, _) => Task.CompletedTask, default, default,
            receiptTimeout: TimeSpan.FromMilliseconds(150), confirmed: value => confirmed.TrySetResult(value));
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<VoiceSubmissionUnconfirmedException>(() => operation.WaitForRegistrationBoundaryAsync());
        Assert.True(operation.WasDispatched);
        Assert.False(operation.HasConfirmedCancellation);
        receipt.SetResult("late-created-topic");
        Assert.Equal("late-created-topic", await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(operation.HasConfirmedCancellation);
    }

    [Fact]
    public async Task RevocationReleasesTheQueueButRetainsAndCancelsItsExactLateReceiptOnce()
    {
        var received = NewCompletion<SessionTurnCommandResultDto>();
        var dispatched = NewCompletion<CancellationToken>();
        var cancelled = NewCompletion<SessionTurnCommandResultDto>();
        using var input = new CancellationTokenSource();
        var calls = 0;
        using var operation = new RevocableVoiceSubmission(token =>
        {
            Interlocked.Increment(ref calls);
            dispatched.TrySetResult(token);
            return received.Task;
        }, (receipt, _) => { cancelled.TrySetResult(receipt); return Task.CompletedTask; }, input.Token, default);
        var transportToken = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(transportToken.IsCancellationRequested);
        operation.Revoke();
        operation.Dispose(); // The UI may leave this scope before the Host receipt arrives.
        var expected = Receipt();
        received.SetResult(expected);
        Assert.Equal(expected, await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationAfterReceiptBeforeUiAttachmentStillCancelsOnlyThatTurn()
    {
        var receipt = Receipt();
        var cancelled = NewCompletion<SessionTurnCommandResultDto>();
        var calls = 0;
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ => Task.FromResult(receipt), (value, _) =>
        {
            Interlocked.Increment(ref calls);
            cancelled.TrySetResult(value);
            return Task.CompletedTask;
        }, input.Token, default);
        Assert.Equal(receipt, await operation.WaitAsync());
        input.Cancel();
        operation.Revoke();
        Assert.Equal(receipt, await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueueBoundaryWaitsForExactCancellationBeforeAdmittingAnotherInput()
    {
        var delayed = NewCompletion<SessionTurnCommandResultDto>();
        var dispatched = NewCompletion<bool>();
        var cancellationEntered = NewCompletion<bool>();
        var releaseCancellation = NewCompletion<bool>();
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ =>
        {
            dispatched.TrySetResult(true);
            return delayed.Task;
        }, (_, _) =>
        {
            cancellationEntered.TrySetResult(true);
            return releaseCancellation.Task;
        }, input.Token, default);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync());
        delayed.SetResult(Receipt());
        await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.ReceiptCompletion.IsCompleted);
        Assert.Null(operation.ConfirmedCancellationReceipt);
        releaseCancellation.SetResult(true);
        var receipt = await operation.ReceiptCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(receipt, operation.ConfirmedCancellationReceipt);
    }

    [Fact]
    public async Task BoundaryAlsoWaitsWhenRevocationStartsJustAfterReceiptCompleted()
    {
        var entered = NewCompletion<bool>();
        var release = NewCompletion<bool>();
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ => Task.FromResult(Receipt()), (_, _) =>
        {
            entered.TrySetResult(true);
            return release.Task;
        }, input.Token, default);
        await operation.WaitAsync();
        Assert.True(operation.ReceiptCompletion.IsCompletedSuccessfully);
        input.Cancel();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var boundary = operation.WaitForRegistrationBoundaryAsync();
        Assert.False(boundary.IsCompleted);
        Assert.True(operation.IsRegistrationBoundaryPending);
        release.SetResult(true);
        await boundary.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsRegistrationBoundaryPending);
        Assert.NotNull(operation.ConfirmedCancellationReceipt);
    }

    [Fact]
    public async Task UnconfirmedCancellationFailsTheQueueBoundaryInsteadOfSendingANewerInput()
    {
        var delayed = NewCompletion<SessionTurnCommandResultDto>();
        var dispatched = NewCompletion<bool>();
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ =>
        {
            dispatched.TrySetResult(true);
            return delayed.Task;
        }, (_, _) => Task.FromException(new IOException("stop not acknowledged")), input.Token, default);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync());
        delayed.SetResult(Receipt());
        await Assert.ThrowsAsync<VoiceSubmissionUnconfirmedException>(() => operation.ReceiptCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(operation.ConfirmedCancellationReceipt);
    }

    [Fact]
    public async Task OldReceiptAfterANewRequestNeverCancelsTheNewTurnOrReplaysInput()
    {
        var oldReceipt = NewCompletion<SessionTurnCommandResultDto>();
        var oldDispatched = NewCompletion<bool>();
        var cancellation = NewCompletion<bool>();
        var cancelledIds = new ConcurrentQueue<Guid>();
        using var oldInput = new CancellationTokenSource();
        using var old = new RevocableVoiceSubmission(_ =>
        {
            oldDispatched.TrySetResult(true);
            return oldReceipt.Task;
        }, (receipt, _) =>
        {
            cancelledIds.Enqueue(receipt.TurnId);
            cancellation.TrySetResult(true);
            return Task.CompletedTask;
        }, oldInput.Token, default);
        await oldDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldInput.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync());
        var newReceipt = Receipt();
        using var current = new RevocableVoiceSubmission(_ => Task.FromResult(newReceipt), (receipt, _) =>
        {
            cancelledIds.Enqueue(receipt.TurnId);
            return Task.CompletedTask;
        }, default, default);
        Assert.Equal(newReceipt, await current.WaitAsync());
        var expectedOld = Receipt();
        oldReceipt.SetResult(expectedOld);
        await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([expectedOld.TurnId], cancelledIds);
    }

    [Fact]
    public void AlreadyCancelledInputIsNeverSubmitted()
    {
        using var input = new CancellationTokenSource();
        input.Cancel();
        var calls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => new RevocableVoiceSubmission(_ =>
        {
            calls++;
            return Task.FromResult(Receipt());
        }, (_, _) => Task.CompletedTask, input.Token, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UncooperativeReceiptHasABoundedWaitAndItsLaterReceiptIsStillCancelled()
    {
        var delayed = NewCompletion<SessionTurnCommandResultDto>();
        var cancelled = NewCompletion<SessionTurnCommandResultDto>();
        var warnings = new ConcurrentQueue<Exception>();
        var calls = 0;
        using var operation = new RevocableVoiceSubmission(_ =>
        {
            Interlocked.Increment(ref calls);
            return delayed.Task;
        }, (receipt, _) => { cancelled.TrySetResult(receipt); return Task.CompletedTask; }, default, default,
            (_, error) => warnings.Enqueue(error), receiptTimeout: TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<VoiceSubmissionUnconfirmedException>(() => operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(warnings);
        var receipt = Receipt();
        delayed.SetResult(receipt);
        Assert.Equal(receipt, await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UncooperativeCancellationDoesNotHangAndReportsUnconfirmedOnlyOnce()
    {
        var neverCompletes = NewCompletion<bool>();
        var warning = NewCompletion<SessionTurnCommandResultDto?>();
        var warnings = 0;
        var receipt = Receipt();
        using var input = new CancellationTokenSource();
        using var operation = new RevocableVoiceSubmission(_ => Task.FromResult(receipt), (_, _) => neverCompletes.Task,
            input.Token, default, (value, _) =>
            {
                Interlocked.Increment(ref warnings);
                warning.TrySetResult(value);
            }, cancellationTimeout: TimeSpan.FromMilliseconds(50));
        await operation.WaitAsync();
        input.Cancel();
        operation.Revoke();
        Assert.Equal(receipt, await warning.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, warnings);
        Assert.Null(operation.ConfirmedCancellationReceipt);
        neverCompletes.SetException(new IOException("late fake failure"));
    }

    [Fact]
    public async Task AReceiptFaultAfterInputCancellationIsObservedWithoutResubmission()
    {
        var delayed = NewCompletion<SessionTurnCommandResultDto>();
        var started = NewCompletion<bool>();
        var warning = NewCompletion<Exception>();
        using var input = new CancellationTokenSource();
        var submissions = 0;
        var cancellations = 0;
        using var operation = new RevocableVoiceSubmission(_ =>
        {
            submissions++;
            started.TrySetResult(true);
            return delayed.Task;
        }, (_, _) => { cancellations++; return Task.CompletedTask; }, input.Token, default,
            (_, error) => warning.TrySetResult(error));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync());
        delayed.SetException(new IOException("disconnected after accepted request"));
        Assert.IsType<IOException>(await warning.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, submissions);
        Assert.Equal(0, cancellations);
    }

    private static SessionTurnCommandResultDto Receipt() => new(Guid.NewGuid(), Guid.NewGuid(), false);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
