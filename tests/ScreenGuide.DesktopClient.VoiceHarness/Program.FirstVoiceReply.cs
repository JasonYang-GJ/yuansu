using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static async Task<FirstVoiceReplyEvidence> RunFirstVoiceReplyScenarioAsync(
        Application application, MainWindow window, DesktopApiClient api,
        ControlledVoiceListener voice, ControlledSpeechOutput speech, DelayedSubmissionApi delay)
    {
        // Keep this fixture's WPF window hidden. Only its normal controls/events run;
        // the real Host and IPC use a new temporary profile, with no native audio or model calls.
        var initialization = await application.Dispatcher.InvokeAsync(window.InitializeClientAsync)
            .Task.ConfigureAwait(false);
        await initialization.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        Require(await api.GetCurrentSessionAsync().ConfigureAwait(false) is null, "first_voice_profile_not_empty");
        Require(!voice.IsListening && speech.Texts.IsEmpty, "first_voice_started_without_control");
        speech.Override = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await ClickAsync("StartVoiceButton").ConfigureAwait(false);
        await voice.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle)
            .Task.ConfigureAwait(false);

        delay.DelayNextVoice = true;
        try
        {
            voice.Recognize(NewInput);
            await delay.ReceiptHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var completed = await WaitForSessionAsync(api, snapshot => snapshot.Turns.Count == 1
                && snapshot.Turns[0].InputModality == "Voice" && snapshot.Turns[0].Phase == "Completed",
                TimeSpan.FromSeconds(10), "first_voice_turn_not_completed").ConfigureAwait(false);
            Require(delay.HeldReceipt?.SessionId == completed.SessionId
                && delay.HeldReceipt.TurnId == completed.Turns[0].Id, "first_voice_receipt_target_mismatch");
            await WaitForAsync(async () => await application.Dispatcher.InvokeAsync(() =>
                ((TextBlock)window.FindName("AssistantResultText")).Text == RecordingChatProvider.NewReply)
                .Task.ConfigureAwait(false), "first_voice_reply_not_rendered").ConfigureAwait(false);
            Require(speech.Texts.IsEmpty, "first_voice_spoke_before_receipt");
        }
        finally { delay.ReleaseReceipt.TrySetResult(); }

        await WaitForAsync(() => Task.FromResult(speech.Texts.Count == 1), "first_voice_reply_not_spoken")
            .ConfigureAwait(false);
        Require(speech.Texts.Single() == RecordingChatProvider.NewReply, "first_voice_wrong_reply_spoken");
        await Task.Delay(700).ConfigureAwait(false);
        Require(speech.Texts.Count == 1 && delay.SubmittedTexts.Count == 1 && delay.NewTopicCalls == 0,
            "first_voice_repeated_or_created_extra_topic");
        await ClickAsync("StopVoiceButton").ConfigureAwait(false);
        await WaitForAsync(() => Task.FromResult(!voice.IsListening), "first_voice_stop_failed").ConfigureAwait(false);
        return new(true, true, true, true, false, false, false);

        async Task ClickAsync(string name) => await application.Dispatcher.InvokeAsync(() =>
        {
            var button = (Button)window.FindName(name);
            Require(button.IsEnabled, "first_voice_control_unavailable");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }).Task.ConfigureAwait(false);

        static async Task WaitForAsync(Func<Task<bool>> condition, string failure)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await condition().ConfigureAwait(false)) return;
                await Task.Delay(25).ConfigureAwait(false);
            }
            Require(false, failure);
        }
    }

    private sealed record FirstVoiceReplyEvidence(bool InitiallyNoSession, bool HostCreatedOnlySession,
        bool CompletedBeforeReceipt, bool AutomaticReplyExactlyOnce, bool RealMicrophoneUsed,
        bool RealSpeechOutputUsed, bool RealModelUsed);
}
