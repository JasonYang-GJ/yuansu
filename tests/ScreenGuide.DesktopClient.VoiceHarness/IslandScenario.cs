using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Accessibility;
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using ScreenGuide.DesktopClient;
using ScreenGuide.DesktopClient.Services;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.VoiceHarness;

internal static partial class Program
{
    private static async Task<int> CloseIslandForUpdateAsync(int processId, string expectedExecutable, bool releaseOnly)
    {
        using var owned = Process.GetProcessById(processId);
        _ = owned.SafeHandle;
        Require(string.Equals(owned.MainModule?.FileName, Path.GetFullPath(expectedExecutable),
            StringComparison.OrdinalIgnoreCase) && owned.ProcessName == "yuanshu-island", "update_process_mismatch");
        var handle = FindIslandWindow(processId);
        Require(handle != IntPtr.Zero, "update_window_missing");
        var root = AutomationElement.FromHandle(handle);
        var compact = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(item => item.Current.Name.StartsWith("展开元枢灵动岛", StringComparison.Ordinal));
        if (compact is not null && !compact.Current.IsOffscreen)
            await InvokeIslandButtonAsync(root, compact.Current.Name);
        else
        {
            using var activation = Process.Start(new ProcessStartInfo(expectedExecutable, "--show-controls")
                { UseShellExecute = false, CreateNoWindow = true });
            if (activation is not null) await activation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (releaseOnly)
        {
            await Task.Delay(400);
            await InvokeIslandButtonAsync(root, "收起操作面板");
            await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 90
                && (GetWindowLongPtr(handle, -20).ToInt64() & 0x8000000) != 0,
                "release_did_not_restore_nonactivating_rest_state");
            Console.WriteLine("Owned Island returned to its resting state through its visible Collapse action.");
            return 0;
        }
        await InvokeIslandButtonAsync(root, "退出");
        await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Owned Island exited through its normal visible Exit action.");
        return 0;
    }
    private sealed record IslandHarnessEvidence(bool PausedWithoutMicrophone, bool SidePlacementKeepsTabStripClear,
        bool CompactPointerPassThrough, bool AutomaticPanelPointerPassThrough,
        bool ManualPanelReceivesPointer, bool ShortcutOpensControls, bool CollapseKeepsMicrophone,
        bool FocusLossReleasesControls, bool CollapseRestoresPreviousWindow,
        bool SlowVoiceStartupKeepsVisibleConnection,
        bool PartialExpandedBeforeSubmission, bool ExactFinalPresentedBeforeSubmission,
        bool OneVoiceTurnCompleted, bool ResultReturnsToCompact, bool ManagementReturnsToIsland,
        bool PauseAndRestartControls, bool HiddenIslandStopsMicrophone,
        bool QueuedTextNotReplayed, bool VisibleRecoverySurface);

    private static async Task<IslandHarnessEvidence> RunIslandScenarioAsync(Application app, MainWindow window,
        DesktopApiClient api, ControlledVoiceListener voice, RecordingChatProvider provider,
        ControlledSpeechOutput speech, DelayedSubmissionApi delay, string executable)
    {
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var fixture = await app.Dispatcher.InvokeAsync(() =>
        {
            var owned = new Window { Title = "元枢鼠标穿透验收 — 隔离测试页面", WindowState = WindowState.Maximized,
                Content = new System.Windows.Controls.TextBlock { Text = "这是测试页面，不是用户的浏览器。", FontSize = 24 } };
            owned.Show();
            owned.Activate();
            return owned;
        });
        var fixtureHandle = await app.Dispatcher.InvokeAsync(() => new System.Windows.Interop.WindowInteropHelper(fixture).Handle);
        speech.Override = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await (await app.Dispatcher.InvokeAsync(() =>
        {
            window.ConfigureIslandSurface(executable);
            return window.ShowAssistantAsync(startListening: false);
        })).WaitAsync(TimeSpan.FromSeconds(30));
        var bridge = await app.Dispatcher.InvokeAsync(() => Field<AssistantIslandBridge>("_islandBridge"));
        var child = await app.Dispatcher.InvokeAsync(() => Field<Process>("_islandProcess"));
        Require(bridge is { IsVisible: true }, "real_island_not_ready");
        Require(!voice.IsListening && voice.StartCalls == 0, "island_started_microphone_without_consent");
        Require(await app.Dispatcher.InvokeAsync(() => !window.IsVisible && !app.Windows.Cast<Window>()
            .Any(w => w.GetType().Name == "AssistantOrbWindow" && w.IsVisible)), "island_not_primary_surface");
        var childId = child.Id;
        var handle = FindIslandWindow(childId);
        Require(handle != IntPtr.Zero, "island_native_window_missing");
        var root = AutomationElement.FromHandle(handle);
        var compact = root.Current.BoundingRectangle;
        Console.WriteLine($"Island initial input: own={handle}, fixture={fixtureHandle}, hit={IslandHitRoot(root)}, foreground={GetForegroundWindow()}, style={GetWindowLongPtr(handle, -20).ToInt64():X}");
        Require(compact.Width > 200 && compact.Width < 360 && compact.Height < 50,
            "island_not_compact");
        AssertIslandSidePlacement(root);
        Require(IslandHitRoot(root) != handle && (GetWindowLongPtr(handle, -20).ToInt64() & 0x80020) == 0x80020,
            "compact_intercepts_underlying_page");
        await app.Dispatcher.InvokeAsync(window.ShowAssistantSurface);
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300
            && IslandHitRoot(root) == handle, "desktop_activation_did_not_open_controls");
        Require(!voice.IsListening && voice.StartCalls == 0, "manual_panel_started_microphone");
        AssertIslandSidePlacement(root);
        await app.Dispatcher.InvokeAsync(() => FocusIslandFixture(fixtureHandle, handle));
        await WaitIslandAsync(() => IslandHitRoot(root) == fixtureHandle, "focus_loss_kept_interception");
        Require(GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "focus_loss_stole_focus_back");
        await app.Dispatcher.InvokeAsync(window.ShowAssistantSurface);
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300
            && IslandHitRoot(root) == handle, "reopen_after_blur_failed");
        await Task.Delay(400);
        await InvokeIslandButtonAsync(root, "收起操作面板");
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 50, "manual_collapse_failed");
        Console.WriteLine($"Island collapsed input: own={handle}, fixture={fixtureHandle}, hit={IslandHitRoot(root)}, foreground={GetForegroundWindow()}");
        await WaitIslandAsync(() => GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "collapse_did_not_restore_previous_window");
        Require(GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "shortcut_fixture_not_foreground");
        // This owned fixture has focus; the product advertises successful registration.
        await WaitIslandAsync(() => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
            "元枢，麦克风已关闭，Ctrl+Alt+空格 打开操作")) is not null, "shortcut_not_registered");
        var hotkey = new[] { new IslandKeyInput { Type = 1, Key = 0x11 }, new IslandKeyInput { Type = 1, Key = 0x12 },
            new IslandKeyInput { Type = 1, Key = 0x20 }, new IslandKeyInput { Type = 1, Key = 0x20, Flags = 2 },
            new IslandKeyInput { Type = 1, Key = 0x12, Flags = 2 }, new IslandKeyInput { Type = 1, Key = 0x11, Flags = 2 } };
        Require(SendInput((uint)hotkey.Length, hotkey, Marshal.SizeOf<IslandKeyInput>()) == hotkey.Length,
            "shortcut_input_failed");
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height >= 300
            && IslandHitRoot(root) == handle, "shortcut_did_not_open_controls");
        voice.HoldStart = true;
        await Task.Delay(400); // Native activation and accessibility layout must settle before invoking.
        await InvokeIslandButtonAsync(root, "开始语音");
        try { await voice.StartHeld.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch
        {
            Console.WriteLine($"Start evidence: calls={voice.StartCalls}, visible={bridge.IsVisible}, foreground={GetForegroundWindow()}, hit={IslandHitRoot(root)}");
            Console.WriteLine(string.Join(" | ", root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen).Select(item => item.Current.Name)));
            throw;
        }
        await Task.Delay(3500);
        Require(bridge.IsVisible && !voice.IsListening, "slow_start_blocked_island_heartbeat");
        voice.ReleaseStart.TrySetResult();
        await WaitIslandAsync(() => voice.IsListening, "island_start_button_failed");
        Require(voice.StartCalls == 1, "island_started_multiple_listeners");
        Console.WriteLine($"Island listening input: own={handle}, fixture={fixtureHandle}, hit={IslandHitRoot(root)}, foreground={GetForegroundWindow()}, height={root.Current.BoundingRectangle.Height}");
        await InvokeIslandButtonAsync(root, "收起操作面板");
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 50, "listening_collapse_failed");
        Require(voice.IsListening, "collapse_stopped_microphone");
        await app.Dispatcher.InvokeAsync(() => FocusIslandFixture(fixtureHandle, handle));

        const string partial = "请回答独立语音";
        voice.Partial(partial);
        await WaitForElementByNameAsync(root, partial, ControlType.Text, TimeSpan.FromSeconds(8));
        Require(root.Current.BoundingRectangle.Height >= 300, "partial_not_expanded");
        Require(IslandHitRoot(root) == fixtureHandle, "automatic_panel_intercepts_underlying_page");
        AssertIslandSidePlacement(root);
        Require(GetAncestor(GetForegroundWindow(), 2) == fixtureHandle, "automatic_panel_stole_focus");
        Require(delay.SubmittedTexts.IsEmpty && provider.InvocationCount == 0, "partial_submitted_before_final");
        var finalPresentedAtSubmission = false;
        delay.BeforeSubmission = request =>
        {
            if (request.InputModality != "Voice") return;
            var revision = Field<long>("_islandFinalRevision");
            using var check = new CancellationTokenSource();
            var acknowledgement = bridge.WaitForPresentedAsync(revision, check.Token);
            finalPresentedAtSubmission = acknowledgement.IsCompletedSuccessfully && bridge.IsVisible
                && root.Current.BoundingRectangle.Height >= 300;
            check.Cancel();
            Require(finalPresentedAtSubmission, "host_submission_before_exact_final_presentation");
        };
        voice.Recognize(NewInput);
        var completed = await WaitForSessionAsync(api, s => s.Turns.Any(t => t.InputText == NewInput && t.Phase == "Completed"),
            TimeSpan.FromSeconds(15), "island_voice_turn_incomplete");
        Require(completed.Turns.Count(t => t.InputText == NewInput) == 1 && delay.SubmittedTexts.Count == 1,
            "island_duplicate_submission");
        Require(finalPresentedAtSubmission, "island_final_ack_not_observed");
        await WaitIslandAsync(() => root.Current.BoundingRectangle.Height < 100,
            "result_did_not_return_to_compact", 12);
        AssertIslandSidePlacement(root);
        await app.Dispatcher.InvokeAsync(window.ShowAssistantSurface);
        await WaitIslandAsync(() => IslandHitRoot(root) == handle, "manual_panel_did_not_reopen");
        await InvokeIslandButtonAsync(root, "管理");
        await WaitIslandAsync(() => app.Dispatcher.Invoke(() => window.IsVisible), "island_management_not_visible");
        await app.Dispatcher.InvokeAsync(window.Close);
        Require(await app.Dispatcher.InvokeAsync(() => !window.IsVisible) && voice.IsListening,
            "management_close_stopped_island");
        await InvokeIslandButtonAsync(root, "暂停语音");
        await WaitIslandAsync(() => !voice.IsListening && bridge.IsVisible, "island_pause_button_failed");
        await InvokeIslandButtonAsync(root, "开始语音");
        await WaitIslandAsync(() => voice.IsListening && voice.StartCalls == 2, "island_restart_button_failed");

        // Hide the exact owned test window before a new final can be acknowledged.
        // Native presented() must use actual visibility, not a frontend assertion.
        ShowWindow(handle, 0);
        const string neverSubmit = "这条未展示的指令绝不能执行";
        voice.Recognize(neverSubmit);
        await WaitIslandAsync(() => !voice.IsListening, "hidden_island_kept_microphone_open", 10);
        await WaitIslandAsync(() => app.Dispatcher.Invoke(() => app.Windows.Cast<Window>()
            .Any(w => w.GetType().Name == "AssistantOrbWindow" && w.IsVisible)), "no_visible_recovery_surface", 10);
        Require(!delay.SubmittedTexts.Contains(neverSubmit), "hidden_final_submitted");
        await Task.Delay(1000);
        Require(!delay.SubmittedTexts.Contains(neverSubmit) && provider.InvocationCount == 1,
            "disconnected_voice_replayed");
        await WaitIslandAsync(() =>
        {
            try { using var remaining = Process.GetProcessById(childId); return remaining.HasExited; }
            catch (ArgumentException) { return true; }
        }, "owned_island_not_closed_on_loss");
        await app.Dispatcher.InvokeAsync(fixture.Close);
        return new(true, true, true, true, true, true, true, true, true, true, true, finalPresentedAtSubmission,
            true, true, true, true, true, true, true);
    }

    private static async Task InvokeIslandButtonAsync(AutomationElement root, string name)
    {
        AutomationElement button;
        try { button = await WaitForElementByNameAsync(root, name, ControlType.Button, TimeSpan.FromSeconds(10)); }
        catch
        {
            Console.WriteLine($"Island missing button={name}, hit={IslandHitRoot(root)}, foreground={GetForegroundWindow()}, height={root.Current.BoundingRectangle.Height}");
            Console.WriteLine("Visible buttons: " + string.Join(" | ", root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen).Select(item => item.Current.Name)));
            throw;
        }
        await WaitIslandAsync(() => button.Current.IsEnabled && !button.Current.IsOffscreen,
            "island_button_not_ready");
        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        if (InvokeAccessibleIslandButton(root, button)) return;
        // WebView2 may expose a real HTML button without UIA InvokePattern.
        // Keyboard activation is restricted to this owned test window and the
        // exact accessible button; never send keys to the user's foreground app.
        var handle = new IntPtr(root.Current.NativeWindowHandle);
        SetForegroundWindow(handle);
        button.SetFocus();
        Require(GetAncestor(GetForegroundWindow(), 2) == handle && button.Current.HasKeyboardFocus,
            "island_button_focus_precondition_failed");
        var enter = new[] { new IslandKeyInput { Type = 1, Key = 0x0d },
            new IslandKeyInput { Type = 1, Key = 0x0d, Flags = 2 } };
        Require(SendInput(2, enter, Marshal.SizeOf<IslandKeyInput>()) == 2, "island_keyboard_activation_failed");
    }

    private static bool InvokeAccessibleIslandButton(AutomationElement root, AutomationElement button)
    {
        // Reuse only the existing read-only, window-bound renderer discovery.
        // This test alone invokes the exact accessible button's default action.
        var semantics = typeof(ScreenGuide.Skills.Windows.WindowsDesktopSkillAdapter).Assembly
            .GetType("ScreenGuide.Skills.Windows.LegacyBrowserSearchSemantics")!;
        var roots = (IEnumerable)semantics.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [(long)root.Current.NativeWindowHandle])!;
        var bounds = button.Current.BoundingRectangle;
        foreach (var renderer in roots)
        {
            var tree = (IAccessible)renderer.GetType().GetProperty("Accessible")!.GetValue(renderer)!;
            if (tree.accHitTest((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2))
                is not IAccessible target) continue;
            for (var depth = 0; depth < 5; depth++)
            {
                target.accLocation(out var x, out var y, out var width, out var height, 0);
                if (target.get_accName(0) == button.Current.Name && Convert.ToInt32(target.get_accRole(0)) == 43
                    && (Convert.ToInt32(target.get_accState(0)) & 0x18001) == 0
                    && x == bounds.X && y == bounds.Y && width == bounds.Width && height == bounds.Height)
                {
                    target.accDoDefaultAction(0);
                    return true;
                }
                if (target.accParent is not IAccessible parent) break;
                target = parent;
            }
        }
        return false;
    }

    private static async Task WaitIslandAsync(Func<bool> predicate, string error, int seconds = 8)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (predicate()) return;
            await Task.Delay(60);
        }
        throw new HarnessFailureException(error);
    }

    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(IslandPoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref IslandMonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct IslandRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct IslandMonitorInfo { public int Size; public IslandRect Monitor, Work; public uint Flags; }
    private static void AssertIslandSidePlacement(AutomationElement root)
    {
        var handle = new IntPtr(root.Current.NativeWindowHandle);
        var info = new IslandMonitorInfo { Size = Marshal.SizeOf<IslandMonitorInfo>() };
        Require(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info), "island_monitor_missing");
        var bounds = root.Current.BoundingRectangle;
        var scale = GetDpiForWindow(handle) / 96.0;
        Require(Math.Abs(info.Work.Right - bounds.Right - 12 * scale) <= 2, "island_not_at_right_edge");
        Require(Math.Abs((bounds.Top + bounds.Bottom) / 2 - (info.Work.Top + info.Work.Bottom) / 2.0) <= 2,
            "island_not_vertically_centered");
        Require(bounds.Top >= info.Work.Top + 100 * scale && bounds.Bottom <= info.Work.Bottom,
            "island_visually_overlaps_tab_strip_or_workarea");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static void FocusIslandFixture(IntPtr fixture, IntPtr island)
    {
        var foreground = GetAncestor(GetForegroundWindow(), 2);
        Require(foreground == fixture || foreground == island, "foreground_not_owned_test_surface");
        if (foreground == fixture) return;
        var targetThread = GetWindowThreadProcessId(island, out _);
        var currentThread = GetCurrentThreadId();
        Require(AttachThreadInput(currentThread, targetThread, true), "fixture_input_attach_failed");
        try { Require(SetForegroundWindow(fixture), "fixture_activation_rejected"); }
        finally { AttachThreadInput(currentThread, targetThread, false); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct IslandPoint { public int X; public int Y; }
    private static IntPtr IslandHitRoot(AutomationElement root)
    {
        var bounds = root.Current.BoundingRectangle;
        return GetAncestor(WindowFromPoint(new IslandPoint {
            X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) }), 2);
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, IslandKeyInput[] inputs, int size);
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct IslandKeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    private static IntPtr FindIslandWindow(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var process);
            if (process != pid || !IsWindowVisible(hwnd)) return true;
            var window = AutomationElement.FromHandle(hwnd);
            if (window.Current.Name != "元枢灵动岛" || window.Current.BoundingRectangle.Width < 10) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
