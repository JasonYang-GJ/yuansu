using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using ScreenGuide.AI.Core;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.FakeCodexCli;
using ScreenGuide.Persistence.Sqlite;

namespace ScreenGuide.DesktopProduct.Tests;

public sealed class DesktopSessionUiAutomationTests
{
    private const string SyntheticWindowTitle = "元枢阶段一窗口授权测试";

    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ActualReleaseWpfClientKeepsSessionUiAndHostStateInSync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"screen-guide-session-ui-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(testRoot, "user-data");
        var markerPath = Path.Combine(testRoot, "cancelled-provider-child.txt");
        Directory.CreateDirectory(dataRoot);
        await WriteCompletedOnboardingSettingsAsync(dataRoot);
        var legacyRoute = new AiSettings(new ChatModelRoute("codex", "codex-default"));
        using (var routeStore = new FileAiSettingsStore(Path.Combine(dataRoot, "settings", "ai-settings.json"), legacyRoute))
            await routeStore.SaveAsync(legacyRoute);

        Process? clientProcess = null;
        Process? syntheticWindowProcess = null;
        Process? hostProcess = null;
        IDesktopApiClient? api = null;
        try
        {
            var binaries = LocateReleaseBinaries();
            var preexistingHostIds = GetHostProcessIds(binaries.HostPath);
            var pipeName = $"ScreenGuide.SessionUiAcceptance.{Guid.NewGuid():N}";
            clientProcess = StartClient(binaries, dataRoot, pipeName);
            api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(20));

            var clientWindow = await WaitForMainWindowAsync(
                clientProcess,
                dataRoot,
                TimeSpan.FromSeconds(15));
            hostProcess = await WaitForNewHostProcessAsync(
                binaries.HostPath,
                preexistingHostIds,
                TimeSpan.FromSeconds(10));
            var automationRoot = AutomationElement.FromHandle(clientWindow)
                ?? throw new InvalidOperationException("无法连接实际 DesktopClient 的 WPF 自动化树。");

            var title = await WaitForElementAsync(
                automationRoot,
                "SessionTitle",
                TimeSpan.FromSeconds(10));
            var status = await WaitForElementAsync(
                automationRoot,
                "SessionStatus",
                TimeSpan.FromSeconds(10));
            var newTopic = await WaitForElementAsync(
                automationRoot,
                "NewTopic",
                TimeSpan.FromSeconds(10));
            Assert.Equal("还没有开始话题", title.Current.Name);
            Assert.Contains("直接说出", status.Current.Name, StringComparison.Ordinal);

            Invoke(newTopic);
            var newSession = await WaitForSessionAsync(
                api,
                snapshot => snapshot is { Title: "新话题" },
                TimeSpan.FromSeconds(10));
            await WaitForAutomationTextAsync(
                automationRoot,
                "SessionTitle",
                newSession.Title,
                TimeSpan.FromSeconds(10));

            var longTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                $"TEST_LONG_RUNNING{Environment.NewLine}MARKER={markerPath}",
                "Text",
                $"session-ui-stop-{Guid.NewGuid():N}",
                newSession.SessionId));
            var failed = await WaitForTurnPhaseAsync(
                api,
                longTurn.TurnId,
                "Failed",
                TimeSpan.FromSeconds(10));
            var failedTurn = Assert.Single(failed.Turns, turn => turn.Id == longTurn.TurnId);
            Assert.Equal(newSession.SessionId, failed.SessionId);
            Assert.Equal(newSession.ConversationId, failed.ConversationId);
            Assert.Empty(failed.ActiveTurns);
            Assert.NotNull(failedTurn.FailureMessage);
            Assert.Contains("安全原因", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("Codex 普通聊天", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("暂不提供", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("编程任务不受影响", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("TEST_LONG_RUNNING", failedTurn.FailureMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(markerPath, failedTurn.FailureMessage, StringComparison.OrdinalIgnoreCase);
            await WaitForAutomationTextAsync(
                automationRoot,
                "SessionStatus",
                "处理失败",
                TimeSpan.FromSeconds(10));
            Assert.False(
                File.Exists(markerPath),
                "生产普通聊天安全策略必须在 Fake Codex 启动前失败关闭。");
            await using (var sessionStore = new SqliteSessionStore(
                             Path.Combine(dataRoot, "state", "tasking.db")))
            {
                await sessionStore.InitializeAsync();
                var persistedTurn = await sessionStore.GetTurnAsync(longTurn.TurnId);
                Assert.Equal("disabled_by_security_policy", persistedTurn?.FailureCode);
                Assert.Equal(failedTurn.FailureMessage, persistedTurn?.FailureMessage);
            }

            syntheticWindowProcess = StartSyntheticWindow();
            var syntheticWindow = await WaitForTopLevelWindowAsync(
                syntheticWindowProcess,
                SyntheticWindowTitle,
                TimeSpan.FromSeconds(10));
            // Wait for the Host's 400 ms foreground tracker to observe this exact fixture,
            // as well as checking the actual foreground. Focusing alone can still leave
            // the read-only planning cache on the previously focused application.
            // Planning never grants capture consent and never executes the request.
            await WaitUntilAsync(async () =>
            {
                if (GetForegroundWindow() != syntheticWindow) FocusWindow(syntheticWindow);
                var plan = await api.PlanAssistantCommandAsync(new PlanAssistantCommandRequestDto(
                    "看看这个窗口是什么"));
                return GetForegroundWindow() == syntheticWindow
                    && plan.ForegroundApplication?.WindowHandle == syntheticWindow.ToInt64()
                    && plan.ForegroundApplication.WindowTitle == SyntheticWindowTitle;
            }, TimeSpan.FromSeconds(10));

            var windowTurn = await api.SubmitSessionInputAsync(new SessionInputRequestDto(
                "看看这个窗口是什么",
                "Text",
                $"session-ui-window-reject-{Guid.NewGuid():N}",
                newSession.SessionId));
            var waitingForConsent = await WaitForTurnPhaseAsync(
                api,
                windowTurn.TurnId,
                "WaitingForWindowConsent",
                TimeSpan.FromSeconds(10));
            var waitingTurn = Assert.Single(
                waitingForConsent.Turns,
                turn => turn.Id == windowTurn.TurnId);
            Assert.Equal(SyntheticWindowTitle, waitingTurn.WindowTitle);
            await WaitForAutomationTextAsync(
                automationRoot,
                "SessionStatus",
                "允许查看",
                TimeSpan.FromSeconds(10));
            _ = await WaitForElementAsync(
                automationRoot,
                "SessionContextCard",
                TimeSpan.FromSeconds(10));
            var reject = await WaitForElementAsync(
                automationRoot,
                "CancelSessionContext",
                TimeSpan.FromSeconds(10));
            Assert.Equal("拒绝并结束", reject.Current.Name);

            Invoke(reject);
            var rejected = await WaitForTurnPhaseAsync(
                api,
                windowTurn.TurnId,
                "Cancelled",
                TimeSpan.FromSeconds(10));
            Assert.Equal(newSession.SessionId, rejected.SessionId);
            Assert.Equal(newSession.ConversationId, rejected.ConversationId);
            Assert.Empty(rejected.ActiveTurns);
            Assert.Contains(rejected.Turns, turn => turn.Id == longTurn.TurnId && turn.Phase == "Failed");
            Assert.Contains(rejected.Turns, turn => turn.Id == windowTurn.TurnId && turn.Phase == "Cancelled");
            await WaitForAutomationTextAsync(
                automationRoot,
                "SessionStatus",
                "已取消",
                TimeSpan.FromSeconds(10));
            Assert.False(
                File.Exists(markerPath),
                "后续窗口授权流程也不得让已停用的 Codex 普通聊天进程迟到启动。");

            await api.ShutdownHostAsync();
            await hostProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await StopProcessAsync(syntheticWindowProcess, entireProcessTree: true);
            await StopProcessAsync(clientProcess, entireProcessTree: false);
            await StopProcessAsync(hostProcess, entireProcessTree: true);
            await DeleteDirectoryWithRetryAsync(testRoot);
        }
    }

    private static async Task WriteCompletedOnboardingSettingsAsync(string dataRoot) =>
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "client-settings.json"),
            JsonSerializer.Serialize(
                new
                {
                    startWithWindows = false,
                    runInBackground = false,
                    closeToTray = false,
                    notificationsEnabled = false,
                    onboardingCompleted = true,
                    notificationStateInitialized = true,
                    deliveredNotificationKeys = Array.Empty<string>()
                },
                new JsonSerializerOptions { WriteIndented = true }));

    private static Process StartClient(Binaries binaries, string dataRoot, string pipeName)
    {
        var info = new ProcessStartInfo
        {
            FileName = binaries.ClientPath,
            WorkingDirectory = Path.GetDirectoryName(binaries.ClientPath)!,
            UseShellExecute = false
        };
        info.Environment["SCREEN_GUIDE_DATA_DIRECTORY"] = dataRoot;
        info.Environment["SCREEN_GUIDE_PIPE_NAME"] = pipeName;
        info.Environment["SCREEN_GUIDE_DESKTOP_HOST_PATH"] = binaries.HostPath;
        info.Environment["SCREEN_GUIDE_CODEX_PATH"] = binaries.FakeCodexPath;
        info.ArgumentList.Add("--show");
        return Process.Start(info)
               ?? throw new InvalidOperationException("实际 Release DesktopClient 未启动。");
    }

    private static Process StartSyntheticWindow()
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Sta");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(
            "Add-Type -AssemblyName System.Windows.Forms; "
            + "$window = New-Object System.Windows.Forms.Form; "
            + $"$window.Text = '{SyntheticWindowTitle}'; "
            + "$window.Width = 520; $window.Height = 320; "
            + "$label = New-Object System.Windows.Forms.Label; "
            + "$label.AutoSize = $true; $label.Text = 'Stage 1 window consent acceptance'; "
            + "$window.Controls.Add($label); "
            + "[System.Windows.Forms.Application]::Run($window)");
        return Process.Start(info)
               ?? throw new InvalidOperationException("合成窗口进程未启动。");
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process,
        string dataRoot,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"DesktopClient 已退出，退出码 {process.ExitCode}。");
            }

            var window = FindTopLevelWindow(process.Id, expectedTitle: "元枢 · 管理");
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"实际 DesktopClient 主窗口未出现。启动记录：{ReadStartupError(dataRoot)}");
    }

    private static async Task<IntPtr> WaitForTopLevelWindowAsync(
        Process process,
        string expectedTitle,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"合成窗口进程已退出，退出码 {process.ExitCode}。");
            }

            var window = FindTopLevelWindow(process.Id, expectedTitle);
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"没有找到合成窗口“{expectedTitle}”。");
    }

    private static async Task<AutomationElement> WaitForElementAsync(
        AutomationElement root,
        string automationId,
        TimeSpan timeout)
    {
        AutomationElement? match = null;
        await WaitUntilAsync(
            () => Task.FromResult(TryFindElement(root, automationId, out match)),
            timeout);
        return match!;
    }

    private static async Task WaitForAutomationTextAsync(
        AutomationElement root,
        string automationId,
        string expectedText,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            () =>
            {
                if (!TryFindElement(root, automationId, out var element))
                {
                    return Task.FromResult(false);
                }

                try
                {
                    return Task.FromResult(
                        element!.Current.Name.Contains(expectedText, StringComparison.Ordinal));
                }
                catch (ElementNotAvailableException)
                {
                    return Task.FromResult(false);
                }
            },
            timeout);

    private static bool TryFindElement(
        AutomationElement root,
        string automationId,
        out AutomationElement? element)
    {
        try
        {
            element = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            return element is not null;
        }
        catch (ElementNotAvailableException)
        {
            element = null;
            return false;
        }
        catch (COMException)
        {
            element = null;
            return false;
        }
    }

    private static void Invoke(AutomationElement element)
    {
        Assert.True(
            element.TryGetCurrentPattern(InvokePattern.Pattern, out var rawPattern),
            $"AutomationId={element.Current.AutomationId} 不支持 InvokePattern。");
        ((InvokePattern)rawPattern).Invoke();
    }

    private static async Task<SessionSnapshotDto> WaitForSessionAsync(
        IDesktopApiClient api,
        Func<SessionSnapshotDto?, bool> condition,
        TimeSpan timeout)
    {
        SessionSnapshotDto? snapshot = null;
        await WaitUntilAsync(
            async () =>
            {
                snapshot = await api.GetCurrentSessionAsync();
                return condition(snapshot);
            },
            timeout);
        return snapshot!;
    }

    private static async Task<SessionSnapshotDto> WaitForTurnPhaseAsync(
        IDesktopApiClient api,
        Guid turnId,
        string expectedPhase,
        TimeSpan timeout) =>
        await WaitForSessionAsync(
            api,
            snapshot => snapshot?.Turns.Any(turn =>
                turn.Id == turnId
                && string.Equals(turn.Phase, expectedPhase, StringComparison.Ordinal)) == true,
            timeout);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or DesktopApiException)
            {
                lastError = exception;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            lastError is null
                ? "Desktop Session UI 验收条件超时。"
                : $"Desktop Session UI 验收条件超时：{lastError.Message}");
    }

    private static void FocusWindow(IntPtr window)
    {
        _ = ShowWindow(window, 9);
        _ = SetForegroundWindow(window);
        AutomationElement.FromHandle(window)?.SetFocus();
    }

    private static IntPtr FindTopLevelWindow(int processId, string expectedTitle)
    {
        var match = IntPtr.Zero;
        EnumWindows(
            (window, ignored) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var length = GetWindowTextLength(window);
                var title = new StringBuilder(Math.Max(1, length + 1));
                _ = GetWindowText(window, title, title.Capacity);
                if (string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal))
                {
                    match = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return match;
    }

    private static string ReadStartupError(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "state", "client-startup-error.json");
        var stagePath = Path.Combine(dataRoot, "state", "client-startup-stage.txt");
        var error = File.Exists(path) ? File.ReadAllText(path) : "none";
        var stage = File.Exists(stagePath) ? File.ReadAllText(stagePath) : "none";
        return $"stage={stage}; error={error}";
    }

    private static HashSet<int> GetHostProcessIds(string hostPath) =>
        Process.GetProcessesByName("ScreenGuide.DesktopHost")
            .Where(process => IsProcessAtPath(process, hostPath))
            .Select(process => process.Id)
            .ToHashSet();

    private static async Task<Process> WaitForNewHostProcessAsync(
        string hostPath,
        IReadOnlySet<int> excludedProcessIds,
        TimeSpan timeout)
    {
        Process? match = null;
        await WaitUntilAsync(
            () =>
            {
                match = Process.GetProcessesByName("ScreenGuide.DesktopHost")
                    .FirstOrDefault(process =>
                        !excludedProcessIds.Contains(process.Id)
                        && IsProcessAtPath(process, hostPath));
                return Task.FromResult(match is not null);
            },
            timeout);
        return match!;
    }

    private static bool IsProcessAtPath(Process process, string expectedPath)
    {
        try
        {
            return string.Equals(
                process.MainModule?.FileName,
                expectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopProcessAsync(Process? process, bool entireProcessTree)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(250);
            }
        }
    }

    private static Binaries LocateReleaseBinaries()
    {
        var repository = FindRepositoryRoot();
        const string framework = "net10.0-windows10.0.19041.0";
        var client = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopClient",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopClient.exe");
        var host = Path.Combine(
            repository,
            "src",
            "ScreenGuide.DesktopHost",
            "bin",
            "Release",
            framework,
            "ScreenGuide.DesktopHost.exe");
        var fakeCodex = Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe");
        Assert.True(File.Exists(client), $"缺少实际 Release DesktopClient：{client}");
        Assert.True(File.Exists(host), $"缺少实际 Release DesktopHost：{host}");
        Assert.True(File.Exists(fakeCodex), $"缺少 FakeCodex 测试进程：{fakeCodex}");
        return new Binaries(client, host, fakeCodex);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("没有找到 ScreenGuide 仓库根目录。");
    }

    private sealed record Binaries(string ClientPath, string HostPath, string FakeCodexPath);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
