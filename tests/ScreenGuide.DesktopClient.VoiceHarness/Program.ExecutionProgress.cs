using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private sealed record ExecutionProgressStepEvidence(string ReceptionMode, string VisibleStatus,
        bool ActualNativeUiaVisible, bool WpfStatusMatched);

    private sealed record ExecutionProgressEvidence(IReadOnlyList<ExecutionProgressStepEvidence> Steps,
        bool PausedWithoutListenerStart, bool ControlledStandbyOnly, bool RealMicrophoneUsed,
        bool RealNativeIsland, bool SyntheticSessionSnapshots, bool DesktopActionsExecuted,
        bool ProviderInvoked, bool IsolatedPlacementProfile);

    private static async Task<ExecutionProgressEvidence> RunExecutionProgressScenarioAsync(
        Application app, MainWindow window, DesktopApiClient api, ControlledVoiceListener voice,
        RecordingChatProvider provider, ControlledSpeechOutput speech, DelayedSubmissionApi delay,
        string executable)
    {
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var previousProfile = Environment.GetEnvironmentVariable("YUANSHU_ISLAND_PROFILE");
        var profile = "phase-" + Guid.NewGuid().ToString("N");
        var profilesRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.yuanshu.island", "profiles"));
        var profileRoot = Path.GetFullPath(Path.Combine(profilesRoot, profile));
        Environment.SetEnvironmentVariable("YUANSHU_ISLAND_PROFILE", profile);
        var stages = new[]
        {
            "正在打开网易云音乐…", "正在查找最近发行的歌曲…", "正在搜索…", "正在检查搜索结果…"
        };
        const string input = "打开网易云，搜索最近周杰伦的新歌";
        var observed = new List<ExecutionProgressStepEvidence>();
        speech.Override = (_, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            await (await app.Dispatcher.InvokeAsync(() =>
            {
                window.ConfigureIslandSurface(Path.GetFullPath(executable));
                return window.ShowAssistantAsync(startListening: false);
            })).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var bridge = await app.Dispatcher.InvokeAsync(() => Field<AssistantIslandBridge>("_islandBridge"));
            var child = await app.Dispatcher.InvokeAsync(() => Field<Process>("_islandProcess"));
            if (bridge is null || child is null)
                throw new HarnessFailureException("progress_owned_island_startup_failed");
            Require(bridge.IsVisible && !child.HasExited
                && string.Equals(child.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase),
                "progress_owned_island_not_ready");
            var handle = FindIslandWindow(child.Id);
            Require(handle != IntPtr.Zero, "progress_native_window_missing");
            var root = AutomationElement.FromHandle(handle);
            Require(root.Current.ProcessId == child.Id, "progress_native_process_mismatch");
            Require(!voice.IsListening && voice.StartCalls == 0, "progress_paused_started_listener");

            // Only the Session envelope comes from the isolated Host. These explicitly
            // synthetic phase snapshots exercise production WPF -> IPC -> native rendering;
            // they never submit a search or assert that a real desktop action took place.
            var session = await api.StartNewSessionAsync("执行进度界面验收").ConfigureAwait(false);
            var change = session.ChangeVersion + 1000;
            var turnId = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow;
            foreach (var mode in new[] { "Paused", "Standby" })
            {
                if (mode == "Standby")
                {
                    // ControlledVoiceListener contains no NAudio device or PCM capture.
                    // A controlled listening state is required to exercise the actual
                    // Standby branch; the real microphone is never opened by this harness.
                    var controller = await app.Dispatcher.InvokeAsync(() => Field<VoiceSessionController>("_voiceSession"));
                    await controller.StartAsync().ConfigureAwait(false);
                    await app.Dispatcher.InvokeAsync(() =>
                    {
                        typeof(MainWindow).GetMethod("SynchronizeVoiceReception",
                            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                        var gate = Field<VoiceReceptionGate>("_voiceReception");
                        var transition = gate.BeginTransition();
                        Require(gate.CompleteTransition(transition, controller.ListeningGeneration,
                            voice.RecognitionGeneration, VoiceReceptionMode.Standby), "progress_standby_setup_failed");
                    });
                }

                foreach (var stage in stages)
                {
                    var now = DateTimeOffset.UtcNow;
                    var turn = new UnifiedSessionTurnDto(turnId, 1, input, "Text", "DesktopAction", "Executing",
                        "None", "ApplicationSearch", null, null, null, null, null, null, false, false,
                        stage, null, created, now, null);
                    var snapshot = session with
                    {
                        ChangeVersion = ++change, Status = "Active", ForegroundTurn = turn,
                        ActiveTurns = [turn], Turns = [turn], Messages = []
                    };
                    await app.Dispatcher.InvokeAsync(() =>
                    {
                        typeof(MainWindow).GetMethod("RenderSession", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .Invoke(window, [snapshot]);
                        Require(((TextBlock)window.FindName("SessionStatusText")).Text == stage,
                            "progress_wpf_status_not_projected");
                    });
                    await WaitForVisibleProgressTextAsync(root, handle, stage).ConfigureAwait(false);
                    await WaitForVisibleProgressTextAsync(root, handle, input).ConfigureAwait(false);
                    Require(bridge.IsVisible && !child.HasExited, "progress_island_lost_visibility");
                    if (mode == "Paused") Require(!voice.IsListening && voice.StartCalls == 0,
                        "progress_text_command_opened_listener");
                    observed.Add(new(mode, stage, true, true));
                }
            }

            Require(delay.SubmittedTexts.IsEmpty && provider.InvocationCount == 0,
                "progress_fixture_submitted_command_or_called_provider");
            Require(voice.StartCalls == 1, "progress_controlled_standby_restarted_listener");
            var stopping = await app.Dispatcher.InvokeAsync(() => Field<VoiceSessionController>("_voiceSession").StopAsync());
            await stopping.ConfigureAwait(false);
            Require(!voice.IsListening, "progress_controlled_listener_not_stopped");
            return new(observed, true, true, false, true, true, false, false, true);
        }
        finally
        {
            await (await app.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
                .GetMethod("CloseIslandSurfaceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, null)!)).ConfigureAwait(false);
            Environment.SetEnvironmentVariable("YUANSHU_ISLAND_PROFILE", previousProfile);
            // Only the isolated profile's own placement file can be removed, never a tree.
            if (Directory.Exists(profileRoot) && Path.GetDirectoryName(profileRoot) == profilesRoot
                && Path.GetFileName(profileRoot) == profile && profile.Length == 38
                && (File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint) == 0)
            {
                var placement = Path.Combine(profileRoot, "placement.json");
                if (File.Exists(placement) && (File.GetAttributes(placement) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(placement);
                if (!Directory.EnumerateFileSystemEntries(profileRoot).Any()) Directory.Delete(profileRoot);
            }
        }
    }

    private static async Task WaitForVisibleProgressTextAsync(AutomationElement root, IntPtr handle, string text)
    {
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, text));
        await WaitIslandAsync(() =>
        {
            try
            {
                if (!IsWindowVisible(handle) || root.Current.BoundingRectangle.Height < 200) return false;
                return root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Any(element =>
                    !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty
                    && element.Current.BoundingRectangle.Width > 0 && element.Current.BoundingRectangle.Height > 0);
            }
            catch (ElementNotAvailableException) { return false; }
        }, "progress_native_visible_text_missing_" + text, 10).ConfigureAwait(false);
    }
}
