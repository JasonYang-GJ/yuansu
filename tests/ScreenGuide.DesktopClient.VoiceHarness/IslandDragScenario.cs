using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private sealed record IslandDragEvidence(bool DirectDrag, bool FourEdgesExposeOneThird,
        bool DetachRestoresCapsule, bool RestartRestoresPosition, bool ClickOpensControls,
        bool HeaderDrag, bool EscapeCancelsWithoutSave, bool LongDragKeepsHeartbeat,
        bool AutomaticExpansionDoesNotSave, bool FinalShownBeforeSingleSubmission,
        bool MicrophoneUnaffectedByPlacement, bool DragRestoresPriorForeground,
        bool ClickCollapseRestoresPriorForeground, bool EachVisibleTipOpensControls);

    private static async Task<IslandDragEvidence> RunIslandDragScenarioAsync(Application app, MainWindow window,
        DesktopApiClient api, ControlledVoiceListener voice, RecordingChatProvider provider,
        ControlledSpeechOutput speech, DelayedSubmissionApi delay, string executable)
    {
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        async Task StartAsync()
        {
            await (await app.Dispatcher.InvokeAsync(() =>
            {
                window.ConfigureIslandSurface(executable);
                return window.ShowAssistantAsync(startListening: false);
            })).WaitAsync(TimeSpan.FromSeconds(30));
        }
        async Task StopSurfaceAsync() => await (await app.Dispatcher.InvokeAsync(() => (Task)typeof(MainWindow)
            .GetMethod("CloseIslandSurfaceAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!));
        var profile = "drag-" + Guid.NewGuid().ToString("N");
        var previousProfile = Environment.GetEnvironmentVariable("YUANSHU_ISLAND_PROFILE");
        Environment.SetEnvironmentVariable("YUANSHU_ISLAND_PROFILE", profile);
        var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.yuanshu.island", "profiles", profile);
        var placementPath = Path.Combine(profileRoot, "placement.json");
        GetCursorPos(out var originalPointer);
        Window? fixture = null;
        speech.Override = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            fixture = await app.Dispatcher.InvokeAsync(() =>
            {
                var owned = new Window { Title = "元枢拖动验收 — 隔离测试页面", WindowState = WindowState.Maximized,
                    Content = new System.Windows.Controls.TextBlock { Text = "这是浮窗拖动测试页面，不是用户的浏览器。", FontSize = 24 } };
                owned.Show();
                owned.Activate();
                return owned;
            });
            var fixtureHandle = await app.Dispatcher.InvokeAsync(() => new System.Windows.Interop.WindowInteropHelper(fixture).Handle);
            await StartAsync();
            var bridge = await app.Dispatcher.InvokeAsync(() => Field<AssistantIslandBridge>("_islandBridge"));
            var child = await app.Dispatcher.InvokeAsync(() => Field<Process>("_islandProcess"));
            var handle = FindIslandWindow(child.Id);
            Require(handle != IntPtr.Zero && bridge.IsVisible && !voice.IsListening, "drag_surface_not_ready");
            var root = AutomationElement.FromHandle(handle);
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 80, "drag_initial_not_resting");
            var initialForeground = GetAncestor(GetForegroundWindow(), 2);
            GetWindowThreadProcessId(initialForeground, out var initialProcess);
            var initialProcessName = Process.GetProcessById((int)initialProcess).ProcessName;
            Console.WriteLine($"Focus setup: fixture={fixtureHandle}, harnessProcess={Environment.ProcessId}, island={handle}, islandProcess={child.Id}, foreground={initialForeground}, process={initialProcess}, processName={initialProcessName}.");
            // The startup sequence is not the drag under test. Establish the exact
            // prior foreground after startup, using only an owned visible fixture.
            await app.Dispatcher.InvokeAsync(() => fixture.Topmost = true);
            await ClickOwnedIslandAsync(AutomationElement.FromHandle(fixtureHandle));
            await app.Dispatcher.InvokeAsync(() => fixture.Topmost = false);
            Require(GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "fixture_did_not_receive_owned_click");
            var work = IslandWorkArea(handle);
            var scale = GetDpiForWindow(handle) / 96.0;
            var freeTarget = new IslandPoint { X = work.Left + (work.Right - work.Left) / 3,
                Y = work.Top + (work.Bottom - work.Top) / 2 };
            var initial = root.Current.BoundingRectangle;
            await DragOwnedIslandAsync(root, freeTarget);
            await WaitIslandAsync(() => File.Exists(placementPath), "placement_not_saved");
            await WaitIslandAsync(() => GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "drag_kept_keyboard_focus");
            var free = root.Current.BoundingRectangle;
            Require(free.Width > 200 && Math.Abs(free.Left - initial.Left) > 100, "free_drag_did_not_move_capsule");
            Require(voice.StartCalls == 0 && delay.SubmittedTexts.IsEmpty, "drag_triggered_voice_or_action");
            Console.WriteLine("Drag checkpoint: free capsule moved and persisted.");

            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                var target = new IslandPoint { X = (work.Left + work.Right) / 2, Y = (work.Top + work.Bottom) / 2 };
                if (edge == "left") target.X = work.Left + (int)(8 * scale);
                if (edge == "right") target.X = work.Right - (int)(8 * scale);
                if (edge == "top") target.Y = work.Top + (int)(8 * scale);
                if (edge == "bottom") target.Y = work.Bottom - (int)(8 * scale);
                await DragOwnedIslandAsync(root, target);
                await WaitIslandAsync(() => root.Current.BoundingRectangle.Width < 90, "edge_did_not_become_ball");
                var ball = root.Current.BoundingRectangle;
                var vertical = edge is "left" or "right";
                var expectedWidth = Math.Round((vertical ? 44.0 / 3.0 : 44.0) * scale);
                var expectedHeight = Math.Round((vertical ? 44.0 : 44.0 / 3.0) * scale);
                Require(Math.Abs(ball.Width - expectedWidth) <= 1 && Math.Abs(ball.Height - expectedHeight) <= 1,
                    "docked_ball_does_not_expose_one_third");
                var gap = edge switch { "left" => ball.Left - work.Left, "right" => work.Right - ball.Right,
                    "top" => ball.Top - work.Top, _ => work.Bottom - ball.Bottom };
                Require(Math.Abs(gap) <= 1, "ball_not_flush_with_requested_edge");
                Require(ball.Left >= work.Left && ball.Top >= work.Top && ball.Right <= work.Right && ball.Bottom <= work.Bottom,
                    "docked_ball_outside_workarea");
                var beforeOpen = File.ReadAllBytes(placementPath);
                await ClickOwnedIslandAsync(root);
                await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300, "edge_tip_click_did_not_open_controls");
                var panel = root.Current.BoundingRectangle;
                Require(panel.Left >= work.Left && panel.Top >= work.Top && panel.Right <= work.Right && panel.Bottom <= work.Bottom,
                    "edge_tip_panel_not_fully_visible");
                await InvokeIslandButtonAsync(root, "收起操作面板");
                await WaitIslandAsync(() => NearRect(root.Current.BoundingRectangle, ball), "edge_tip_did_not_return_to_same_place");
                Require(beforeOpen.SequenceEqual(File.ReadAllBytes(placementPath)) && voice.StartCalls == 0,
                    "edge_tip_click_changed_saved_position_or_microphone");
                Console.WriteLine($"Edge peek checkpoint: {edge}, visible={ball.Width}x{ball.Height}, click/collapse kept placement.");
                if (edge != "bottom")
                {
                    await DragOwnedIslandAsync(root, freeTarget);
                    await WaitIslandAsync(() => root.Current.BoundingRectangle.Width > 200, "detach_did_not_restore_capsule");
                }
            }
            var remembered = root.Current.BoundingRectangle;
            await StopSurfaceAsync();
            await StartAsync();
            child = await app.Dispatcher.InvokeAsync(() => Field<Process>("_islandProcess"));
            handle = FindIslandWindow(child.Id);
            root = AutomationElement.FromHandle(handle);
            bridge = await app.Dispatcher.InvokeAsync(() => Field<AssistantIslandBridge>("_islandBridge"));
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Width < 90, "restart_lost_docked_ball");
            Require(NearRect(root.Current.BoundingRectangle, remembered), "restart_did_not_restore_same_position");
            Console.WriteLine("Drag checkpoint: new native process restored the docked position.");

            // Reestablish the same owned foreground after the separate restart.
            await app.Dispatcher.InvokeAsync(() => fixture.Topmost = true);
            await ClickOwnedIslandAsync(AutomationElement.FromHandle(fixtureHandle));
            await app.Dispatcher.InvokeAsync(() => fixture.Topmost = false);
            Require(GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "fixture_not_ready_before_ball_click");
            await ClickOwnedIslandAsync(root);
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300, "ball_click_did_not_open_controls");
            Require(!voice.IsListening, "opening_controls_started_microphone");
            await Task.Delay(350);
            var header = root.Current.BoundingRectangle;
            var headerPoint = new IslandPoint { X = (int)(header.Left + 110 * scale), Y = (int)(header.Top + 30 * scale) };
            await DragOwnedIslandAsync(root, freeTarget, headerPoint);
            Require(!voice.IsListening, "header_drag_started_microphone");
            var saveBeforeCancel = File.ReadAllBytes(placementPath);
            var cancelStart = root.Current.BoundingRectangle;
            headerPoint = new IslandPoint { X = (int)(cancelStart.Left + 110 * scale), Y = (int)(cancelStart.Top + 30 * scale) };
            await DragOwnedIslandAsync(root, new IslandPoint { X = freeTarget.X + 140, Y = freeTarget.Y + 80 }, headerPoint, cancel: true);
            Require(File.ReadAllBytes(placementPath).SequenceEqual(saveBeforeCancel), "cancelled_drag_changed_saved_position");
            Require(NearRect(root.Current.BoundingRectangle, cancelStart), "cancelled_drag_did_not_restore_origin");
            await InvokeIslandButtonAsync(root, "收起操作面板");
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 90, "drag_controls_did_not_close");
            Console.WriteLine($"Collapse focus: fixture={fixtureHandle}, island={handle}, foreground={GetAncestor(GetForegroundWindow(), 2)}.");
            await WaitIslandAsync(() => GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "click_collapse_kept_keyboard_focus");
            await (await app.Dispatcher.InvokeAsync(() => Field<VoiceSessionController>("_voiceSession").StartAsync()));
            await WaitIslandAsync(() => voice.IsListening, "controlled_voice_did_not_start");
            var finalPresented = false;
            delay.BeforeSubmission = request =>
            {
                if (request.InputModality != "Voice") return;
                using var check = new CancellationTokenSource();
                var ack = bridge.WaitForPresentedAsync(Field<long>("_islandFinalRevision"), check.Token);
                finalPresented = ack.IsCompletedSuccessfully && root.Current.BoundingRectangle.Height >= 300;
                check.Cancel();
                Require(finalPresented, "drag_final_submitted_before_paint");
            };
            await DragOwnedIslandAsync(root, new IslandPoint { X = freeTarget.X + 140, Y = freeTarget.Y },
                duringHold: async () =>
                {
                    voice.Partial("这是拖动中的测试转写");
                    await Task.Delay(3800);
                    Require(bridge.IsVisible && voice.IsListening && delay.SubmittedTexts.IsEmpty,
                        "long_drag_lost_heartbeat_or_submitted_partial");
                });
            await WaitForElementByNameAsync(root, "这是拖动中的测试转写", ControlType.Text, TimeSpan.FromSeconds(8));
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300, "partial_did_not_expand_after_drag");
            Require(IslandHitRoot(root) != handle, "automatic_drag_result_intercepts_pointer");
            var savedAfterDrag = File.ReadAllBytes(placementPath);
            voice.Recognize(NewInput);
            var completed = await WaitForSessionAsync(api, s => s.Turns.Any(t => t.InputText == NewInput && t.Phase == "Completed"),
                TimeSpan.FromSeconds(15), "drag_voice_turn_incomplete");
            Require(finalPresented && completed.Turns.Count(t => t.InputText == NewInput) == 1
                && provider.InvocationCount == 1 && delay.SubmittedTexts.Count == 1, "drag_voice_duplicated");
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 90, "drag_result_did_not_return_to_rest", 12);
            Require(savedAfterDrag.SequenceEqual(File.ReadAllBytes(placementPath)), "automatic_expansion_overwrote_user_position");
            Require(voice.IsListening && voice.StartCalls == 1, "placement_changed_microphone_state");
            return new(true, true, true, true, true, true, true, true, true, true, true, true, true, true);
        }
        finally
        {
            MouseButton(false);
            SetCursorPos(originalPointer.X, originalPointer.Y);
            await StopSurfaceAsync();
            if (fixture is not null) await app.Dispatcher.InvokeAsync(fixture.Close);
            Environment.SetEnvironmentVariable("YUANSHU_ISLAND_PROFILE", previousProfile);
            // Delete only the one owned file; do not recursively traverse profile contents.
            var expectedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "com.yuanshu.island", "profiles"));
            var resolvedProfile = Path.GetFullPath(profileRoot);
            if (Directory.Exists(resolvedProfile) && Path.GetDirectoryName(resolvedProfile) == expectedRoot
                && Path.GetFileName(resolvedProfile) == profile && profile.Length == 37
                && profile.StartsWith("drag-", StringComparison.Ordinal)
                && (File.GetAttributes(resolvedProfile) & FileAttributes.ReparsePoint) == 0)
            {
                if (File.Exists(placementPath) && (File.GetAttributes(placementPath) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(placementPath);
                if (!Directory.EnumerateFileSystemEntries(resolvedProfile).Any()) Directory.Delete(resolvedProfile);
            }
        }
    }

    private static IslandRect IslandWorkArea(IntPtr handle)
    {
        var info = new IslandMonitorInfo { Size = Marshal.SizeOf<IslandMonitorInfo>() };
        Require(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info), "drag_monitor_missing");
        return info.Work;
    }
    private static bool NearRect(Rect left, Rect right) => Math.Abs(left.Left - right.Left) <= 3
        && Math.Abs(left.Top - right.Top) <= 3 && Math.Abs(left.Width - right.Width) <= 3 && Math.Abs(left.Height - right.Height) <= 3;
    private static async Task ClickOwnedIslandAsync(AutomationElement root)
    {
        var bounds = root.Current.BoundingRectangle;
        SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        await Task.Delay(100);
        Require(IslandHitRoot(root) == new IntPtr(root.Current.NativeWindowHandle), "click_target_not_owned_island");
        MouseButton(true);
        await Task.Delay(60);
        MouseButton(false);
    }
    private static async Task DragOwnedIslandAsync(AutomationElement root, IslandPoint target,
        IslandPoint? start = null, bool cancel = false, Func<Task>? duringHold = null)
    {
        var handle = new IntPtr(root.Current.NativeWindowHandle);
        var before = root.Current.BoundingRectangle;
        var point = start ?? new IslandPoint { X = (int)(before.Left + before.Width / 2), Y = (int)(before.Top + before.Height / 2) };
        Require(before.Contains(point.X, point.Y), "drag_start_outside_owned_window");
        SetCursorPos(point.X, point.Y);
        await Task.Delay(100);
        Require(GetAncestor(WindowFromPoint(point), 2) == handle, "drag_point_is_not_owned_window");
        MouseButton(true);
        try
        {
            await Task.Delay(90);
            // Start the gesture toward the destination; right-edge tips must move
            // inward, not beyond the physical screen before native drag begins.
            var thresholdStep = (int)Math.Ceiling(4 * GetDpiForWindow(handle) / 96.0) + 1;
            var stepX = Math.Abs(target.X - point.X) >= Math.Abs(target.Y - point.Y)
                ? Math.Sign(target.X - point.X) * thresholdStep : 0;
            var stepY = stepX == 0 ? Math.Sign(target.Y - point.Y) * thresholdStep : 0;
            SetCursorPos(point.X + stepX, point.Y + stepY);
            await Task.Delay(220);
            for (var step = 1; step <= 8; step++)
            {
                SetCursorPos(point.X + (target.X - point.X) * step / 8, point.Y + (target.Y - point.Y) * step / 8);
                await Task.Delay(45);
            }
            if (duringHold is not null) await duringHold();
            if (cancel)
            {
                Require(GetAncestor(GetForegroundWindow(), 2) == handle, "escape_target_not_owned_island");
                var escape = new[] { new IslandKeyInput { Type = 1, Key = 0x1b }, new IslandKeyInput { Type = 1, Key = 0x1b, Flags = 2 } };
                Require(SendInput(2, escape, Marshal.SizeOf<IslandKeyInput>()) == 2, "drag_escape_failed");
                await Task.Delay(120);
            }
        }
        finally { MouseButton(false); }
        await Task.Delay(450);
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out IslandPoint point);
    [DllImport("user32.dll", EntryPoint = "SendInput")] private static extern uint SendMouseInput(uint count, IslandMouseInput[] inputs, int size);
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct IslandMouseInput
    { [FieldOffset(0)] public uint Type; [FieldOffset(20)] public uint Flags; }
    private static bool _dragHarnessMouseHeld;
    private static void MouseButton(bool pressed)
    {
        if (!pressed && !_dragHarnessMouseHeld) return;
        Require(SendMouseInput(1,
            [new IslandMouseInput { Type = 0, Flags = pressed ? 0x0002u : 0x0004u }], Marshal.SizeOf<IslandMouseInput>()) == 1,
            "owned_mouse_button_failed");
        _dragHarnessMouseHeld = pressed;
    }
}
