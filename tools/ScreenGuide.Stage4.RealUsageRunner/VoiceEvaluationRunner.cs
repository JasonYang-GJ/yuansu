using System.Diagnostics;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.Stage4.RealUsageRunner;

internal static class VoiceEvaluationRunner
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MultipleFinalGrace = TimeSpan.FromMilliseconds(300);

    public static async Task<EvaluationReport> RunAsync(
        RunnerOptions options,
        CancellationToken cancellationToken)
    {
        var attempts = new List<EvaluationAttempt>();
        using var input = new ConsoleLineInput();
        var listener = new OfflineContinuousVoiceListener();
        var capability = listener.GetCapabilityReport();
        var microphoneBucket = capability.MicrophoneCount switch
        {
            <= 0 => "none",
            1 => "one",
            _ => "multiple"
        };
        var available = capability.RecognitionModelAvailable && capability.MicrophoneCount > 0;
        var diagnosticOnly = options.Mode == "voice-diagnostic";
        var environment = EvaluationEnvironmentFactory.Create(available, microphoneBucket);
        if (!capability.RecognitionModelAvailable)
        {
            attempts.Add(Blocked("voice.model_missing"));
            await listener.DisposeAsync().ConfigureAwait(false);
            return Report("blocked", cleanupConfirmed: true);
        }

        if (capability.MicrophoneCount == 0)
        {
            attempts.Add(Blocked("voice.microphone_missing"));
            await listener.DisposeAsync().ConfigureAwait(false);
            return Report("blocked", cleanupConfirmed: true);
        }

        Console.WriteLine("S4-R2 本机语音评测：音频只在内存中离线识别，不保存录音或识别文字。 ");
        Console.WriteLine(diagnosticOnly
            ? "将执行 1 次脱敏诊断样本，只输出长度和编辑距离分桶；输入 STOP 或按 Ctrl+C 可随时停止。 "
            : "将执行 1 次预热和 20 次正式尝试；输入 STOP 或按 Ctrl+C 可随时停止。 ");
        Console.WriteLine("输入 YES 表示同意本批次使用麦克风：");
        if (!string.Equals(await input.ReadLineAsync(cancellationToken), "YES", StringComparison.Ordinal))
        {
            attempts.Add(Blocked("evaluation.consent_missing"));
            await listener.DisposeAsync().ConfigureAwait(false);
            return Report("blocked", cleanupConfirmed: true);
        }

        var events = new VoiceBatchEventRouter(listener);
        var cleanupConfirmed = false;
        var stage = "completed";
        try
        {
            var attemptCount = diagnosticOnly ? 1 : 21;
            for (var index = 0; index < attemptCount; index++)
            {
                var isWarmup = index == 0;
                Console.WriteLine(diagnosticOnly
                    ? $"诊断样本：按 Enter 准备；看到“监听已就绪”后再清楚说出“{VoiceEvaluationContract.FixedPhrase}”。"
                    : isWarmup
                    ? $"预热：按 Enter 准备；看到“监听已就绪”后再清楚说出“{VoiceEvaluationContract.FixedPhrase}”。"
                    : $"正式 {index}/20：按 Enter 准备；看到“监听已就绪”后再清楚说出“{VoiceEvaluationContract.FixedPhrase}”。");
                var command = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (command is null
                    || string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
                {
                    attempts.Add(new EvaluationAttempt(
                        isWarmup,
                        EvaluationTerminalState.Cancelled,
                        TimeSpan.Zero,
                        "evaluation.cancelled"));
                    stage = "cancelled";
                    break;
                }

                EvaluationAttempt attempt;
                try
                {
                    var controlled = await VoiceReadyAttemptControl.RunAsync(
                            input,
                            listener.StartAsync,
                            listener.StopAsync,
                            () => Console.WriteLine("监听已就绪，现在请开始说。 "),
                            attemptCancellation => events.RunAttemptAsync(
                                VoiceEvaluationContract.FixedPhrase,
                                isWarmup,
                                AttemptTimeout,
                                MultipleFinalGrace,
                                attemptCancellation),
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempt = controlled.StopRequested
                        ? new EvaluationAttempt(
                            isWarmup,
                            EvaluationTerminalState.Cancelled,
                            controlled.Value.EndToEndElapsed,
                            "evaluation.cancelled")
                        : controlled.Value;
                }
                catch (OperationCanceledException)
                {
                    attempt = new EvaluationAttempt(
                        isWarmup,
                        EvaluationTerminalState.Cancelled,
                        TimeSpan.Zero,
                        "evaluation.cancelled");
                }
                catch
                {
                    attempt = new EvaluationAttempt(
                        isWarmup,
                        EvaluationTerminalState.Failure,
                        TimeSpan.Zero,
                        "voice.listener_faulted");
                }

                attempts.Add(attempt);
                Console.WriteLine($"本次终态：{attempt.State}；正文未保存。 ");
                if (BatchTerminationPolicy.ShouldEndBatch(attempt))
                {
                    stage = attempt.State == EvaluationTerminalState.Blocked
                        ? "blocked"
                        : attempt.State == EvaluationTerminalState.Cancelled
                            ? "cancelled"
                            : "failed";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            attempts.Add(new EvaluationAttempt(
                false,
                EvaluationTerminalState.Cancelled,
                TimeSpan.Zero,
                "evaluation.cancelled"));
            stage = "cancelled";
        }
        catch
        {
            attempts.Add(new EvaluationAttempt(
                false,
                EvaluationTerminalState.Failure,
                TimeSpan.Zero,
                "voice.listener_faulted"));
            stage = "failed";
        }
        finally
        {
            events.Dispose();
            await listener.StopAsync().ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
            cleanupConfirmed = !listener.IsListening;
        }

        return Report(stage, cleanupConfirmed);

        EvaluationReport Report(string stage, bool cleanupConfirmed) =>
            EvaluationReport.Create(
                options.Mode,
                options.ExpectedSha,
                stage,
                EvaluationAggregator.Build(attempts),
                environment,
                cleanupConfirmed);
    }

    private static EvaluationAttempt Blocked(string errorCode) =>
        new(false, EvaluationTerminalState.Blocked, TimeSpan.Zero, errorCode);

    private sealed class VoiceBatchEventRouter : IDisposable
    {
        private readonly object _sync = new();
        private readonly IContinuousVoiceListener _listener;
        private VoiceAttemptTracker? _active;
        private TaskCompletionSource<bool>? _firstFinal;
        private TaskCompletionSource<bool>? _faulted;

        public VoiceBatchEventRouter(IContinuousVoiceListener listener)
        {
            _listener = listener;
            listener.PartialTextChanged += OnPartial;
            listener.UtteranceRecognized += OnFinal;
            listener.StateChanged += OnStateChanged;
        }

        public async Task<EvaluationAttempt> RunAttemptAsync(
            string expectedText,
            bool isWarmup,
            TimeSpan timeout,
            TimeSpan multipleFinalGrace,
            CancellationToken cancellationToken)
        {
            Task firstFinal;
            Task faulted;
            lock (_sync)
            {
                if (_active is not null)
                {
                    throw new InvalidOperationException("已有语音评测正在进行。 ");
                }

                _active = new VoiceAttemptTracker(
                    expectedText,
                    isWarmup,
                    Stopwatch.GetTimestamp());
                _firstFinal = NewSignal();
                _faulted = NewSignal();
                firstFinal = _firstFinal.Task;
                faulted = _faulted.Task;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(timeout, timeoutSource.Token);
            var completed = await Task.WhenAny(firstFinal, faulted, timeoutTask).ConfigureAwait(false);
            var timedOut = completed == timeoutTask && !cancellationToken.IsCancellationRequested;
            if (completed == firstFinal)
            {
                try
                {
                    await Task.Delay(multipleFinalGrace, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            timeoutSource.Cancel();
            return Complete(timedOut, cancellationToken.IsCancellationRequested);
        }

        private EvaluationAttempt Complete(bool timedOut, bool cancelled)
        {
            lock (_sync)
            {
                var tracker = _active
                    ?? throw new InvalidOperationException("没有活动的语音评测。 ");
                _active = null;
                _firstFinal = null;
                _faulted = null;
                return tracker.Complete(
                    Stopwatch.GetTimestamp(),
                    timedOut,
                    cancelled);
            }
        }

        private void OnPartial(object? sender, VoicePartialText partial)
        {
            lock (_sync)
            {
                _active?.OnPartial(partial.Text, Stopwatch.GetTimestamp());
            }
        }

        private void OnFinal(object? sender, VoiceUtterance utterance)
        {
            lock (_sync)
            {
                _active?.OnFinal(utterance.Text, Stopwatch.GetTimestamp());
                _firstFinal?.TrySetResult(true);
            }
        }

        private void OnStateChanged(object? sender, ContinuousVoiceStatus status)
        {
            if (status.State != ContinuousVoiceState.Faulted)
            {
                return;
            }

            lock (_sync)
            {
                _active?.OnListenerFaulted();
                _faulted?.TrySetResult(true);
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            _listener.PartialTextChanged -= OnPartial;
            _listener.UtteranceRecognized -= OnFinal;
            _listener.StateChanged -= OnStateChanged;
        }
    }
}
