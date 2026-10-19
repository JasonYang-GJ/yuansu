using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.FakeCodexCli;

namespace ScreenGuide.DesktopProduct.Tests;

public sealed class DesktopProcessAcceptanceTests
{
    [Fact]
    [Trait("Category", "DesktopAcceptance")]
    public async Task ClientHostAndHistorySurviveWindowCloseClientCrashAndHostRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"screen-guide-product-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Process? client = null;
        var startedHosts = new List<Process>();
        try
        {
            var binaries = LocateBinaries();
            var pipeName = $"ScreenGuide.ProductTest.{Guid.NewGuid():N}";
            var dataRoot = Path.Combine(root, "user-data");
            var projectRoot = Path.Combine(root, "authorized-project");
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(projectRoot);
            await RunGitAsync(projectRoot, "init", "--quiet");
            await File.WriteAllTextAsync(
                Path.Combine(dataRoot, "client-settings.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        startWithWindows = false,
                        runInBackground = true,
                        closeToTray = true,
                        notificationsEnabled = true,
                        onboardingCompleted = true,
                        notificationStateInitialized = true,
                        deliveredNotificationKeys = Array.Empty<string>()
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            client = StartClient(binaries, dataRoot, pipeName);
            var api = new DesktopApiClient(pipeName, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => api.PingAsync(), TimeSpan.FromSeconds(15));
            var firstWindow = await WaitForWindowAsync(client, dataRoot, TimeSpan.FromSeconds(10));
            startedHosts.Add(RequireHostProcess(binaries.HostPath));

            var project = await api.AddProjectAsync(new AddProjectRequestDto(projectRoot, "Desktop product acceptance"));
            var created = await api.CreateTaskAsync(new CreateTaskRequestDto(
                project.Id,
                "TEST_DELAYED_SUCCESS",
                "UI close background task"));
            await WaitForStatusAsync(api, created.TaskId, "Running", TimeSpan.FromSeconds(5));
            Assert.True(PostMessage(firstWindow, 0x0010, IntPtr.Zero, IntPtr.Zero));
            await Task.Delay(300);
            Assert.False(client.HasExited);

            var completed = await WaitForTerminalAsync(api, created.TaskId, TimeSpan.FromSeconds(15));
            Assert.Equal("Succeeded", completed.Summary.Status);
            Assert.Equal("Unverified", completed.Evidence?.VerificationStatus);
            await WaitUntilAsync(
                () => Task.FromResult(NotificationWasDelivered(dataRoot)),
                TimeSpan.FromSeconds(8));
            Assert.True(await api.PingAsync());

            client.Kill(entireProcessTree: false);
            await client.WaitForExitAsync();
            Assert.True(await api.PingAsync());

            client = StartClient(binaries, dataRoot, pipeName);
            _ = await WaitForWindowAsync(client, dataRoot, TimeSpan.FromSeconds(10));
            var historical = await api.GetTaskAsync(created.TaskId);
            Assert.Equal("Succeeded", historical?.Summary.Status);

            var oldHost = RequireHostProcess(binaries.HostPath);
            oldHost.Kill(entireProcessTree: true);
            await oldHost.WaitForExitAsync();
            await WaitUntilAsync(
                async () =>
                {
                    var replacement = TryGetHostProcess(binaries.HostPath);
                    return replacement is not null
                           && replacement.Id != oldHost.Id
                           && await api.PingAsync();
                },
                TimeSpan.FromSeconds(20));
            startedHosts.Add(RequireHostProcess(binaries.HostPath));
            Assert.Equal("Succeeded", (await api.GetTaskAsync(created.TaskId))?.Summary.Status);

            await api.ShutdownHostAsync();
        }
        finally
        {
            if (client is { HasExited: false })
            {
                client.Kill(entireProcessTree: false);
                await client.WaitForExitAsync();
            }

            foreach (var host in startedHosts.DistinctBy(process => process.Id))
            {
                try
                {
                    if (!host.HasExited)
                    {
                        host.Kill(entireProcessTree: true);
                        await host.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    host.Dispose();
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
        return Process.Start(info) ?? throw new InvalidOperationException("DesktopClient did not start.");
    }

    private static async Task<IntPtr> WaitForWindowAsync(
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
                throw new InvalidOperationException($"DesktopClient exited with {process.ExitCode}.");
            }

            var window = FindWindow(process.Id, "元枢 · 管理");
            if (window != IntPtr.Zero)
            {
                return window;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"DesktopClient main window did not appear. Windows: {string.Join(" | ", GetWindowTitles(process.Id))}. "
            + $"Startup error: {ReadStartupError(dataRoot)}");
    }

    private static string ReadStartupError(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "state", "client-startup-error.json");
        var stagePath = Path.Combine(dataRoot, "state", "client-startup-stage.txt");
        var error = File.Exists(path) ? File.ReadAllText(path) : "none";
        var stage = File.Exists(stagePath) ? File.ReadAllText(stagePath) : "none";
        return $"stage={stage}; error={error}";
    }

    private static bool NotificationWasDelivered(string dataRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dataRoot, "client-settings.json")));
            return document.RootElement
                .GetProperty("deliveredNotificationKeys")
                .GetArrayLength() > 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetWindowTitles(int processId)
    {
        var titles = new List<string>();
        EnumWindows(
            (window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId == processId)
                {
                    var length = GetWindowTextLength(window);
                    var title = new StringBuilder(Math.Max(1, length + 1));
                    _ = GetWindowText(window, title, title.Capacity);
                    var className = new StringBuilder(256);
                    _ = GetClassName(window, className, className.Capacity);
                    titles.Add(
                        $"0x{window.ToInt64():X}:{title}:{className}:visible={IsWindowVisible(window)}");
                }

                return true;
            },
            IntPtr.Zero);
        return titles;
    }

    private static IntPtr FindWindow(int processId, string expectedTitle)
    {
        var match = IntPtr.Zero;
        EnumWindows(
            (window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId != processId)
                {
                    return true;
                }

                var titleLength = GetWindowTextLength(window);
                var title = new StringBuilder(Math.Max(1, titleLength + 1));
                _ = GetWindowText(window, title, title.Capacity);
                var className = new StringBuilder(256);
                _ = GetClassName(window, className, className.Capacity);
                if (string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal)
                    && IsWindowVisible(window))
                {
                    match = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return match;
    }

    private static async Task WaitForStatusAsync(
        IDesktopApiClient api,
        Guid taskId,
        string status,
        TimeSpan timeout) =>
        await WaitUntilAsync(
            async () => string.Equals(
                (await api.GetTaskAsync(taskId))?.Summary.Status,
                status,
                StringComparison.Ordinal),
            timeout);

    private static async Task<TaskDetailsDto> WaitForTerminalAsync(
        IDesktopApiClient api,
        Guid taskId,
        TimeSpan timeout)
    {
        TaskDetailsDto? details = null;
        await WaitUntilAsync(
            async () =>
            {
                details = await api.GetTaskAsync(taskId);
                return details?.Summary.Status switch
                {
                    "Succeeded" or "Failed" => details.Evidence is not null,
                    "Cancelled" or "Interrupted" => true,
                    _ => false
                };
            },
            timeout);
        return details!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Desktop product acceptance condition timed out.");
    }

    private static Process RequireHostProcess(string path) =>
        TryGetHostProcess(path) ?? throw new InvalidOperationException("DesktopHost process not found.");

    private static Process? TryGetHostProcess(string path) =>
        Process.GetProcessesByName("ScreenGuide.DesktopHost")
            .FirstOrDefault(process =>
            {
                try
                {
                    return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });

    private static Binaries LocateBinaries()
    {
        var repo = FindRepositoryRoot();
        const string framework = "net10.0-windows10.0.19041.0";
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var client = Path.Combine(repo, "src", "ScreenGuide.DesktopClient", "bin", configuration, framework, "ScreenGuide.DesktopClient.exe");
        var host = Path.Combine(repo, "src", "ScreenGuide.DesktopHost", "bin", configuration, framework, "ScreenGuide.DesktopHost.exe");
        var fake = Path.ChangeExtension(typeof(FakeCodexMarker).Assembly.Location, ".exe");
        return new Binaries(client, host, fake);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScreenGuide.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
