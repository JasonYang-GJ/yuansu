using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

/// <summary>
/// A short-lived submission receipt, not Session state. Revoking UI input must not lose the
/// exact Host Turn returned after cancellation; closing an IPC read does not cancel that Turn.
/// </summary>
public class RevocableInputSubmission<TReceipt> : IInputRegistrationBoundary, IDisposable where TReceipt : class
{
    private readonly object _sync = new();
    private readonly Func<TReceipt, CancellationToken, Task> _cancel;
    private readonly Action<TReceipt?, Exception>? _unconfirmed;
    private readonly Action<TReceipt>? _confirmed;
    private readonly CancellationToken _inputCancellation;
    private readonly CancellationToken _lifetime;
    private readonly TimeSpan _cancellationTimeout;
    private readonly CancellationTokenSource _receiptDeadline;
    private readonly CancellationTokenRegistration _revocation;
    private readonly Task<TReceipt> _receipt;
    private TReceipt? _received;
    private bool _revoked;
    private bool _cancellationConfirmed;
    private Task? _cancellationTask;
    private int _reported;
    private int _dispatched;

    public RevocableInputSubmission(
        Func<CancellationToken, Task<TReceipt>> submit,
        Func<TReceipt, CancellationToken, Task> cancel,
        CancellationToken inputCancellation,
        CancellationToken lifetime,
        Action<TReceipt?, Exception>? unconfirmed = null,
        TimeSpan? receiptTimeout = null,
        TimeSpan? cancellationTimeout = null,
        Action<TReceipt>? confirmed = null)
    {
        inputCancellation.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        _cancel = cancel;
        _inputCancellation = inputCancellation;
        _lifetime = lifetime;
        _unconfirmed = unconfirmed;
        _confirmed = confirmed;
        _cancellationTimeout = cancellationTimeout ?? TimeSpan.FromSeconds(5);
        _receiptDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _receiptDeadline.CancelAfter(receiptTimeout ?? TimeSpan.FromSeconds(30));
        _revocation = inputCancellation.Register(Revoke);
        var deadlineToken = _receiptDeadline.Token;
        var request = Task.Run(() =>
        {
            // Do not dispatch an already revoked queued input. Once dispatched, retain its
            // receipt independently of the UI wait so its exact Turn can still be stopped.
            inputCancellation.ThrowIfCancellationRequested();
            deadlineToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _dispatched, 1);
            return submit(deadlineToken);
        });
        _receipt = ObserveReceiptAsync(request, deadlineToken);
        ObserveFault(_receipt);
    }

    public Task<TReceipt> WaitAsync() => _receipt.WaitAsync(_inputCancellation);

    // Every input entry uses this receipt boundary before admitting another write.
    // An earlier request must not register after a newer one and replace it at the Host.
    public Task<TReceipt> ReceiptCompletion => _receipt;
    public bool WasDispatched => Volatile.Read(ref _dispatched) != 0;
    public bool HasConfirmedCancellation => ConfirmedCancellationReceipt is not null;
    public object? RegistrationReceipt { get { lock (_sync) return _received; } }
    public bool AcknowledgeCancellation(object receipt)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_received, receipt)) return false;
            _cancellationConfirmed = true;
            return true;
        }
    }

    public bool IsRegistrationBoundaryPending
    {
        get { lock (_sync) return !_receipt.IsCompleted || _cancellationTask is { IsCompleted: false }; }
    }

    public async Task WaitForRegistrationBoundaryAsync()
    {
        await _receipt.ConfigureAwait(false);
        Task? cancellation;
        lock (_sync) cancellation = _cancellationTask;
        // Cancellation may begin just after the receipt task completed but before the UI
        // accepted it. A newer entry must also wait for that exact cancellation response.
        if (cancellation is not null) await cancellation.ConfigureAwait(false);
    }

    public TReceipt? ConfirmedCancellationReceipt
    {
        get { lock (_sync) return _cancellationConfirmed ? _received : null; }
    }

    public void Revoke()
    {
        TReceipt? receipt;
        lock (_sync)
        {
            _revoked = true;
            receipt = _received;
        }
        if (receipt is not null) _ = CancelOnce(receipt);
    }

    private async Task<TReceipt> ObserveReceiptAsync(
        Task<TReceipt> request, CancellationToken deadline)
    {
        try
        {
            var receipt = await request.WaitAsync(deadline).ConfigureAwait(false);
            await Receive(receipt).ConfigureAwait(false);
            return receipt;
        }
        catch (Exception exception)
        {
            Revoke();
            // A transport normally honors its deadline. A delayed/faulty implementation
            // may not: observe its eventual receipt once, without waiting or resubmitting.
            _ = request.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) _ = Receive(completed.Result);
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (Volatile.Read(ref _dispatched) != 0
                && (_inputCancellation.IsCancellationRequested || deadline.IsCancellationRequested))
                ReportUnconfirmed(null, exception);
            if (deadline.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                throw new VoiceSubmissionUnconfirmedException(exception);
            throw;
        }
        finally
        {
            _receiptDeadline.Dispose();
        }
    }

    private Task Receive(TReceipt receipt)
    {
        bool revoke;
        lock (_sync)
        {
            _received = receipt;
            revoke = _revoked || _inputCancellation.IsCancellationRequested;
        }
        return revoke ? CancelOnce(receipt) : Task.CompletedTask;
    }

    private Task CancelOnce(TReceipt receipt)
    {
        lock (_sync)
        {
            if (_cancellationTask is null)
            {
                _cancellationTask = Task.Run(() => CancelCoreAsync(receipt));
                ObserveFault(_cancellationTask);
            }
            return _cancellationTask;
        }
    }

    private async Task CancelCoreAsync(TReceipt receipt)
    {
        // An uncooperative peer must not occupy the UI thread or its voice queue.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        deadline.CancelAfter(_cancellationTimeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var cancellation = _cancel(receipt, deadline.Token);
            ObserveFault(cancellation);
            await cancellation.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (_sync) _cancellationConfirmed = true;
            try { _confirmed?.Invoke(receipt); }
            catch { /* A closed UI cannot invalidate the acknowledged cancellation. */ }
        }
        catch (Exception exception)
        {
            ReportUnconfirmed(receipt, exception);
            throw new VoiceSubmissionUnconfirmedException(exception);
        }
    }

    private void ReportUnconfirmed(TReceipt? receipt, Exception exception)
    {
        if (_lifetime.IsCancellationRequested || Interlocked.Exchange(ref _reported, 1) != 0) return;
        try { _unconfirmed?.Invoke(receipt, exception); }
        catch { /* A closed UI cannot invalidate cancellation cleanup. */ }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    public void Dispose() => _revocation.Dispose();
}

public sealed class VoiceSubmissionUnconfirmedException(Exception innerException)
    : TimeoutException("尚未收到请求回执，不能确认这条请求是否已停止。", innerException);


public interface IInputRegistrationBoundary
{
    bool WasDispatched { get; }
    bool IsRegistrationBoundaryPending { get; }
    bool HasConfirmedCancellation { get; }
    object? RegistrationReceipt { get; }
    bool AcknowledgeCancellation(object receipt);
    Task WaitForRegistrationBoundaryAsync();
}

// Keep the typed voice adapter for its speech-feedback ownership and existing callers.
public sealed class RevocableVoiceSubmission(
    Func<CancellationToken, Task<SessionTurnCommandResultDto>> submit,
    Func<SessionTurnCommandResultDto, CancellationToken, Task> cancel,
    CancellationToken inputCancellation,
    CancellationToken lifetime,
    Action<SessionTurnCommandResultDto?, Exception>? unconfirmed = null,
    TimeSpan? receiptTimeout = null,
    TimeSpan? cancellationTimeout = null,
    Action<SessionTurnCommandResultDto>? confirmed = null)
    : RevocableInputSubmission<SessionTurnCommandResultDto>(submit, cancel, inputCancellation,
        lifetime, unconfirmed, receiptTimeout, cancellationTimeout, confirmed);
