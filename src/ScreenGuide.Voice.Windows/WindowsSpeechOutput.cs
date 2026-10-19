using System.Globalization;
using System.Speech.Synthesis;

namespace ScreenGuide.Voice.Windows;

public sealed class WindowsSpeechOutput
{
    public static bool HasChineseVoice()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return synthesizer.GetInstalledVoices().Any(candidate =>
                candidate.Enabled
                && candidate.VoiceInfo.Culture.Name.StartsWith(
                    "zh", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var synthesizer = new SpeechSynthesizer();
        var chineseVoice = synthesizer.GetInstalledVoices().FirstOrDefault(candidate =>
            candidate.Enabled
            && candidate.VoiceInfo.Culture.Name.StartsWith(
                "zh", StringComparison.OrdinalIgnoreCase));
        if (chineseVoice is not null)
        {
            synthesizer.SelectVoice(chineseVoice.VoiceInfo.Name);
        }

        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = 2;
        // Leave acoustic headroom so the always-listening microphone can hear a normal-volume
        // interruption without requiring the user to shout over the assistant.
        synthesizer.Volume = 72;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, SpeakCompletedEventArgs args)
        {
            if (args.Error is not null)
            {
                completion.TrySetException(args.Error);
            }
            else if (args.Cancelled)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        synthesizer.SpeakCompleted += Completed;
        var queueGate = new object();
        using var registration = cancellationToken.Register(() =>
        {
            lock (queueGate)
            {
                synthesizer.SpeakAsyncCancelAll();
            }
        });
        try
        {
            // Cancellation must either prevent enqueueing or cancel the enqueued prompt.
            // Cancelling an empty queue before enqueueing would otherwise still speak.
            lock (queueGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                synthesizer.SpeakAsync(text.Trim());
            }

            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            synthesizer.SpeakCompleted -= Completed;
        }
    }
}
