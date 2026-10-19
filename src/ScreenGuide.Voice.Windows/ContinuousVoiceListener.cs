using NAudio;
using NAudio.Wave;
using SherpaOnnx;

namespace ScreenGuide.Voice.Windows;

public enum ContinuousVoiceState
{
    Stopped,
    Listening,
    HearingSpeech,
    Faulted
}

public sealed record ContinuousVoiceStatus(
    ContinuousVoiceState State,
    string Message);

public sealed record VoicePartialText(string Text, long RecognitionGeneration);

public sealed record VoiceUtterance(
    string Text,
    TimeSpan Duration,
    long RecognitionGeneration = 0);

public interface IContinuousVoiceListener : IAsyncDisposable
{
    bool IsListening { get; }

    long RecognitionGeneration { get; }

    Task<long> ResetRecognitionAsync(CancellationToken cancellationToken = default);

    event EventHandler<VoicePartialText>? PartialTextChanged;

    event EventHandler<VoiceUtterance>? UtteranceRecognized;

    event EventHandler<ContinuousVoiceStatus>? StateChanged;

    VoiceCapabilityReport GetCapabilityReport();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Keeps the microphone open while the visible desktop client is active. Audio is decoded
/// locally in memory, is never written to disk, and each endpoint is emitted as one utterance.
/// </summary>
public sealed class OfflineContinuousVoiceListener : IContinuousVoiceListener
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _captureChanges = new(1, 1);
    private long _recognitionGeneration;
    private readonly LocalVoiceModelPaths _paths;
    private WaveInEvent? _microphone;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private DateTimeOffset? _utteranceStartedAt;
    private string _partialText = string.Empty;
    private bool _stopping;
    private CancellationToken _captureCancellation;

    public OfflineContinuousVoiceListener(LocalVoiceModelPaths? paths = null)
    {
        _paths = paths ?? LocalVoiceModelPaths.Create();
    }

    public bool IsListening
    {
        get
        {
            lock (_sync)
            {
                return _microphone is not null && !_stopping;
            }
        }
    }

    public long RecognitionGeneration { get { lock (_sync) return _recognitionGeneration; } }

    public event EventHandler<VoicePartialText>? PartialTextChanged;

    public event EventHandler<VoiceUtterance>? UtteranceRecognized;

    public event EventHandler<ContinuousVoiceStatus>? StateChanged;

    public VoiceCapabilityReport GetCapabilityReport()
    {
        var microphones = WaveInEvent.DeviceCount;
        var outputAvailable = WindowsSpeechOutput.HasChineseVoice();
        var modelAvailable = _paths.IsComplete;
        var message = !modelAvailable
            ? "本地中文语音模型不完整。"
            : microphones == 0
                ? "没有检测到可用麦克风。"
                : "本机离线语音识别已就绪，音频只在内存中处理。";
        return new VoiceCapabilityReport(
            modelAvailable,
            modelAvailable ? "本机离线中文识别" : "不可用",
            microphones,
            outputAvailable,
            message);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ChangeCaptureAsync(() => { StartCore(cancellationToken); return true; }, cancellationToken);

    public Task StopAsync() => ChangeCaptureAsync(() => { StopCore(); return true; }, CancellationToken.None);

    public Task<long> ResetRecognitionAsync(CancellationToken cancellationToken = default) =>
        ChangeCaptureAsync(() => ResetRecognitionCore(cancellationToken), cancellationToken);

    private async Task<T> ChangeCaptureAsync<T>(Func<T> change, CancellationToken cancellationToken)
    {
        await _captureChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(change, cancellationToken).ConfigureAwait(false); }
        finally { _captureChanges.Release(); }
    }

    private void StartCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_microphone is not null)
            {
                return;
            }

            if (!_paths.IsComplete)
            {
                throw new FileNotFoundException(
                    "本地中文语音模型不完整："
                    + string.Join("、", _paths.MissingFiles.Select(Path.GetFileName)));
            }

            if (WaveInEvent.DeviceCount == 0)
            {
                throw new InvalidOperationException("没有检测到可用麦克风。 ");
            }

            try
            {
                _recognitionGeneration++;
                _recognizer = OfflineVoiceRecognizerFactory.Create(_paths);
                _stream = _recognizer.CreateStream();
                _partialText = string.Empty;
                _utteranceStartedAt = null;
                _stopping = false;
                _captureCancellation = cancellationToken;
                cancellationToken.ThrowIfCancellationRequested();
                StartMicrophone(cancellationToken);
                RaiseState(ContinuousVoiceState.Listening, "正在聆听");
            }
            catch
            {
                DisposeCapture();
                throw;
            }
        }

        return;
    }

    private void StopCore()
    {
        WaveInEvent? microphone;
        lock (_sync)
        {
            microphone = _microphone;
            _stopping = true;
        }

        if (microphone is not null)
        {
            try
            {
                microphone.StopRecording();
            }
            catch (MmException)
            {
                // The input device may already have stopped or been unplugged.
            }
        }

        lock (_sync)
        {
            DisposeCapture();
        }

        RaiseState(ContinuousVoiceState.Stopped, "语音监听已停止");
    }

    private void StartMicrophone(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _microphone = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(OfflineVoiceRecognizerFactory.SampleRate, 16, 1),
            BufferMilliseconds = 80,
            NumberOfBuffers = 4
        };
        _microphone.DataAvailable += OnAudio;
        _microphone.RecordingStopped += OnRecordingStopped;
        _microphone.StartRecording();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private long ResetRecognitionCore(CancellationToken cancellationToken)
    {
        WaveInEvent microphone;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _captureCancellation.ThrowIfCancellationRequested();
            if (_microphone is null || _recognizer is null || _stopping)
                throw new OperationCanceledException("语音已停止。", cancellationToken);
            microphone = _microphone;
            _stopping = true;
            _recognitionGeneration++;
        }

        // Stream.Reset alone leaves old PCM queued on WaveIn. Replace the capture
        // instance so every queued callback from the previous sender is rejected.
        microphone.StopRecording();
        lock (_sync)
        {
            microphone.DataAvailable -= OnAudio;
            microphone.RecordingStopped -= OnRecordingStopped;
            microphone.Dispose(); // Keep the old handle reachable if disposal fails.
            _microphone = null;
            // Once replacement begins, finish it while microphone consent remains.
            // Cancelling only a playback must not strand the listener without capture;
            // its caller arbitrates the receiving mode after this fresh generation.
            _captureCancellation.ThrowIfCancellationRequested();
            _stream?.Dispose();
            _stream = _recognizer!.CreateStream();
            _partialText = string.Empty;
            _utteranceStartedAt = null;
            _stopping = false;
            StartMicrophone(_captureCancellation);
            return _recognitionGeneration;
        }
    }

    private void OnAudio(object? sender, WaveInEventArgs eventArgs)
    {
        string? changedText = null;
        VoiceUtterance? completed = null;
        long recognitionGeneration;
        lock (_sync)
        {
            if (_stream is null || _recognizer is null || _stopping
                || _captureCancellation.IsCancellationRequested || !ReferenceEquals(sender, _microphone))
            {
                return;
            }

            recognitionGeneration = _recognitionGeneration;
            var samples = new float[eventArgs.BytesRecorded / 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var offset = index * 2;
                var value = (short)(eventArgs.Buffer[offset] | (eventArgs.Buffer[offset + 1] << 8));
                samples[index] = value / 32768F;
            }

            _stream.AcceptWaveform(OfflineVoiceRecognizerFactory.SampleRate, samples);
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            var text = _recognizer.GetResult(_stream).Text.Trim();
            if (!string.Equals(text, _partialText, StringComparison.Ordinal))
            {
                _partialText = text;
                changedText = text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _utteranceStartedAt ??= DateTimeOffset.UtcNow;
                }
            }

            if (_recognizer.IsEndpoint(_stream))
            {
                var finalText = _recognizer.GetResult(_stream).Text.Trim();
                var duration = _utteranceStartedAt is { } startedAt
                    ? DateTimeOffset.UtcNow - startedAt
                    : TimeSpan.Zero;
                _recognizer.Reset(_stream);
                _partialText = string.Empty;
                _utteranceStartedAt = null;
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    completed = new VoiceUtterance(finalText, duration, recognitionGeneration);
                }
            }
        }

        lock (_sync)
        {
            // Bind publication to the exact capture instance. A buffered callback from an old
            // microphone must never become a command after the user stops and restarts voice.
            if (_stopping || _captureCancellation.IsCancellationRequested || !ReferenceEquals(sender, _microphone)
                || recognitionGeneration != _recognitionGeneration) return;
            if (changedText is not null)
            {
                PartialTextChanged?.Invoke(this, new VoicePartialText(changedText, recognitionGeneration));
                if (!string.IsNullOrWhiteSpace(changedText))
                {
                    RaiseState(ContinuousVoiceState.HearingSpeech, "正在听你说话");
                }
            }

            if (completed is not null)
            {
                UtteranceRecognized?.Invoke(this, completed);
                RaiseState(ContinuousVoiceState.Listening, "正在聆听");
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (eventArgs.Exception is null || _stopping || !ReferenceEquals(sender, _microphone)) return;
            DisposeCapture();
            RaiseState(ContinuousVoiceState.Faulted, "麦克风连接已中断，重新打开元枢后会再次连接。");
        }
    }

    private void DisposeCapture()
    {
        _recognitionGeneration++;
        var microphone = _microphone;
        if (microphone is not null)
        {
            _stopping = true;
            microphone.DataAvailable -= OnAudio;
            microphone.RecordingStopped -= OnRecordingStopped;
            microphone.Dispose();
            // Keep the handle reachable if native disposal throws so a subsequent Stop can
            // retry cleanup; never report an empty capture while a failed handle was discarded.
            _microphone = null;
        }

        _stream?.Dispose();
        _recognizer?.Dispose();
        _stream = null;
        _recognizer = null;
        _partialText = string.Empty;
        _utteranceStartedAt = null;
        _stopping = false;
    }

    private void RaiseState(ContinuousVoiceState state, string message) =>
        StateChanged?.Invoke(this, new ContinuousVoiceStatus(state, message));

    public async ValueTask DisposeAsync() =>
        await StopAsync().ConfigureAwait(false);
}
