using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.Voice.Windows;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static async Task<VoiceSegmentsEvidence> RunVoiceSegmentsScenarioAsync(
        Application application, MainWindow window, ControlledVoiceListener voice)
    {
        // Exercise the actual WPF event handlers, reception gate, and replacement queue.
        // The queue is intentionally occupied: no Host command, native input, or microphone is used.
        var session = await application.Dispatcher.InvokeAsync(() =>
        {
            Set("_voiceCommandBusy", true);
            return (VoiceSessionController)Get("_voiceSession")!;
        }).Task.ConfigureAwait(false);
        await session.StartAsync().ConfigureAwait(false);
        await application.Dispatcher.InvokeAsync(() => Invoke("SynchronizeVoiceReception"))
            .Task.ConfigureAwait(false);

        voice.Recognize("打开网易云，然后搜索");
        await DrainAsync().ConfigureAwait(false);
        voice.Recognize("最近周杰伦的新歌");
        await DrainAsync().ConfigureAwait(false);
        var combined = await QueuedTextAsync().ConfigureAwait(false);
        Require(combined == "打开网易云，然后搜索最近周杰伦的新歌",
            "voice_search_prefix_lost_" + combined);

        await application.Dispatcher.InvokeAsync(() =>
        {
            Invoke("CancelPendingVoiceInput", true, false, false);
            Set("_voiceCommandBusy", true);
        }).Task.ConfigureAwait(false);
        voice.Recognize("打开我的抖音");
        await DrainAsync().ConfigureAwait(false);
        var opening = new CancellationTokenSource(); // Owned and disposed by MainWindow's normal shutdown.
        await application.Dispatcher.InvokeAsync(() => Set("_activeVoiceCommandCancellation", opening))
            .Task.ConfigureAwait(false);
        voice.Partial("然后搜索");
        await DrainAsync().ConfigureAwait(false);
        Require(!opening.IsCancellationRequested, "search_partial_interrupted_application_open");
        voice.Recognize("然后搜索");
        await DrainAsync().ConfigureAwait(false);
        voice.Recognize("猫咪视频");
        await DrainAsync().ConfigureAwait(false);
        Require(await QueuedTextAsync().ConfigureAwait(false) == "打开我的抖音然后搜索猫咪视频",
            "three_voice_segments_lost");

        // A different explicit command replaces the previous request; Stop revokes the chain.
        voice.Recognize("打开微信");
        await DrainAsync().ConfigureAwait(false);
        Require(await QueuedTextAsync().ConfigureAwait(false) == "打开微信", "unrelated_command_was_joined");
        await application.Dispatcher.InvokeAsync(() => Invoke("CancelPendingVoiceInput", true, false, false))
            .Task.ConfigureAwait(false);
        voice.Recognize("搜索朋友");
        await DrainAsync().ConfigureAwait(false);
        Require(await QueuedTextAsync().ConfigureAwait(false) == "搜索朋友", "cancel_kept_voice_prefix");
        await session.StopAsync().ConfigureAwait(false);
        return new VoiceSegmentsEvidence(combined!, true, true, true, false, false);

        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Invoke(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
        async Task DrainAsync() => await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle)
            .Task.ConfigureAwait(false);
        async Task<string?> QueuedTextAsync() => await application.Dispatcher.InvokeAsync(() =>
            ((VoiceUtterance?)Get("_queuedVoiceUtterance"))?.Text).Task.ConfigureAwait(false);
    }

    private sealed record VoiceSegmentsEvidence(string CombinedInput, bool ThreeSegmentsPreserved,
        bool NewCommandReplaced, bool CancellationCleared, bool RealMicrophoneUsed, bool DesktopActionsExecuted);
}
