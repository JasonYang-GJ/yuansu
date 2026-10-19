using Microsoft.Extensions.DependencyInjection;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopNamedFileSessionTests
{
    [Theory]
    [InlineData("confirm", 1)]
    [InlineData("decline", 0)]
    [InlineData("changed", 0)]
    [InlineData("cancel", 0)]
    public async Task NamedFileUsesOriginalTurnAndExactSingleFileConfirmation(string action, int launches)
    {
        await using var env = DesktopHostTestEnvironment.Create();
        var file = Path.Combine(env.RootDirectory, "年度计划.txt"); await File.WriteAllTextAsync(file, "fixture");
        var catalog = new FileCatalog(file); var launcher = new Launcher();
        using var host = env.BuildHost(services => {
            services.AddSingleton<IDesktopFileCatalog>(catalog);
            services.AddSingleton<IDesktopProcessLauncher>(launcher);
        });
        await host.StartAsync();
        IDesktopApiClient client = new DesktopApiClient(env.Options.PipeName);
        var session = await client.StartNewSessionAsync("named-file-fixture");
        var submitted = await client.SubmitSessionInputAsync(new SessionInputRequestDto(
            "打开桌面上的年度计划.txt", "Text", "named-file-once", session.SessionId));
        SessionSnapshotDto? snapshot = null;
        for (var i = 0; i < 100; i++) {
            snapshot = await client.GetCurrentSessionAsync();
            if(snapshot?.Turns.Any(t => t.Id == submitted.TurnId && t.Phase == "WaitingForConfirmation") == true) break;
            await Task.Delay(25);
        }
        var turn = Assert.Single(snapshot!.Turns, t => t.Id == submitted.TurnId);
        Assert.Equal("WaitingForConfirmation", turn.Phase);
        Assert.Contains(file, turn.ResultSummary);
        Assert.Empty(launcher.Opened);
        catalog.Changed = action == "changed";
        if(action == "cancel") await client.CancelSessionTurnAsync(session.SessionId, submitted.TurnId);
        else await client.ConfirmSessionTurnAsync(session.SessionId, submitted.TurnId, action != "decline");
        Assert.Equal(launches, launcher.Opened.Count);
        if(launches == 1) {
            Assert.Equal(file, Assert.Single(launcher.Opened));
            await Assert.ThrowsAsync<DesktopApiException>(() => client.ConfirmSessionTurnAsync(session.SessionId, submitted.TurnId, true));
            Assert.Single(launcher.Opened);
        }
        Assert.Equal(1, catalog.Resolutions);
        await host.StopAsync();
    }

    private sealed class FileCatalog(string path) : IDesktopFileCatalog
    {
        public bool Changed { get; set; }
        public int Resolutions { get; private set; }
        public DesktopFileResolution Resolve(string name) {
            Resolutions++; var f = new FileInfo(path);
            return new(new(f.FullName, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc), "fixture");
        }
        public void RequireUnchanged(DesktopFileBinding binding) {
            Assert.Equal(path, binding.FullPath);
            if(Changed) throw new InvalidOperationException("文件已变化");
        }
    }
    private sealed class Launcher : IDesktopProcessLauncher
    {
        public List<string> Opened { get; } = [];
        public int? Start(string target) { Opened.Add(target); return 123; }
        public VisibleDesktopLaunchResult OpenApplicationVisible(string target) => throw new NotSupportedException();
        public VisibleDesktopLaunchResult OpenWebsiteVisible(string? browser, Uri website) => throw new NotSupportedException();
    }
}
