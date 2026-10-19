using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenGuide.DesktopHost.Runtime;
using ScreenGuide.DesktopProtocol;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.DesktopHost.Tests;

public sealed class DesktopFileDiscoverySessionTests
{
    [Theory]
    [InlineData("Text")]
    [InlineData("Voice")]
    public async Task PartialNameOpenRequiresConfirmationOnTheOriginalTurn(string modality)
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.xlsx"]);
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算", modality);
        var waiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        var turn = Assert.Single(waiting.Turns);
        Assert.Equal("OpenFile", turn.IntentKind);
        Assert.Contains(fixture.PathFor("年度预算.xlsx"), turn.ResultSummary);
        Assert.Empty(fixture.Launcher.Opened);

        await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        var completed = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        Assert.Equal(input.TurnId, Assert.Single(completed.Turns).Id);
        Assert.Equal(fixture.PathFor("年度预算.xlsx"), Assert.Single(fixture.Launcher.Opened));
    }

    [Fact]
    public async Task ExactNameWinsOverOtherPartialMatches()
    {
        await using var fixture = await Fixture.CreateAsync(["预算.xlsx", "年度预算.xlsx"]);
        var (_, input) = await fixture.SubmitAsync("打开桌面上的预算");
        var waiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        Assert.Equal(fixture.PathFor("预算.xlsx"), Assert.Single(waiting.Turns).FilePath);
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Fact]
    public async Task MultipleMatchesListEightAndSelectionBeyondTheListStillNeedsConfirmation()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"预算{i:00}.txt").ToArray();
        await using var fixture = await Fixture.CreateAsync(names);
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算");
        var waiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForFile");
        var summary = Assert.Single(waiting.Turns).ResultSummary!;
        Assert.Contains("10 个", summary);
        Assert.Contains("预算08.txt", summary);
        Assert.DoesNotContain("预算09.txt", summary);
        Assert.Contains("没有打开任何文件", summary);

        await fixture.Client.ProvideSessionFileAsync(session.SessionId, input.TurnId, fixture.PathFor("预算10.txt"));
        var selected = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        Assert.Equal(input.TurnId, Assert.Single(selected.Turns).Id);
        Assert.Empty(fixture.Launcher.Opened);
        await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        Assert.Equal(fixture.PathFor("预算10.txt"), Assert.Single(fixture.Launcher.Opened));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("unrelated")]
    [InlineData("executable")]
    public async Task FilePickerCannotWidenTheDesktopRequest(string kind)
    {
        await using var fixture = await Fixture.CreateAsync(["预算01.txt", "预算02.txt", "其他.txt", "预算.exe"]);
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算");
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForFile");
        var outside = Path.Combine(fixture.Environment.RootDirectory, "预算.txt");
        await File.WriteAllTextAsync(outside, "outside fixture");
        var selection = kind switch
        {
            "outside" => outside,
            "unrelated" => fixture.PathFor("其他.txt"),
            _ => fixture.PathFor("预算.exe")
        };
        await fixture.Client.ProvideSessionFileAsync(session.SessionId, input.TurnId, selection);
        var stillWaiting = await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForFile");
        Assert.False(Assert.Single(stillWaiting.Turns).RequiresConfirmation);
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true));
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("cancel")]
    [InlineData("decline")]
    public async Task BoundPartialMatchCannotOpenAfterChangeOrCancellation(string action)
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.txt"]);
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算");
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        if (action == "changed") await File.AppendAllTextAsync(fixture.PathFor("年度预算.txt"), "changed fixture");
        if (action == "cancel") await fixture.Client.CancelSessionTurnAsync(session.SessionId, input.TurnId);
        else await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, action != "decline");
        await WaitForPhaseAsync(fixture.Client, input.TurnId, action == "changed" ? "Failed" : "Cancelled");
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Theory]
    [InlineData("Text", "预算", 1)]
    [InlineData("Voice", "预算", 1)]
    [InlineData("Text", "不存在", 0)]
    [InlineData("Text", "年度  计划", 1)]
    public async Task FindCompletesWithoutOpeningOrNeedingAnAiProvider(string modality, string name, int count)
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.xlsx", "年度  计划.txt"]);
        var (session, input) = await fixture.SubmitAsync($"找桌面上的{name}", modality);
        var completed = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        var turn = Assert.Single(completed.Turns);
        Assert.Equal("FindDesktopFile", turn.IntentKind);
        Assert.False(turn.RequiresConfirmation);
        Assert.Null(turn.FilePath);
        Assert.Contains("没有打开任何文件", turn.ResultSummary);
        if (count > 0) Assert.Contains($"{count} 个", turn.ResultSummary);
        Assert.Empty(fixture.Launcher.Opened);
        await Assert.ThrowsAsync<DesktopApiException>(() => fixture.Client.ProvideSessionFileAsync(
            session.SessionId, input.TurnId, fixture.PathFor("年度预算.xlsx")));
        await Assert.ThrowsAsync<DesktopApiException>(() => fixture.Client.ConfirmSessionTurnAsync(
            session.SessionId, input.TurnId, true));
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Fact]
    public async Task FindListsBoundedNamesAndReportsTheFullCount()
    {
        await using var fixture = await Fixture.CreateAsync(
            Enumerable.Range(1, 12).Select(i => $"会议记录{i:00}.txt").ToArray());
        var (_, input) = await fixture.SubmitAsync("查找桌面上的会议记录");
        var completed = await WaitForPhaseAsync(fixture.Client, input.TurnId, "Completed");
        var summary = Assert.Single(completed.Turns).ResultSummary!;
        Assert.Contains("12 个", summary);
        Assert.Contains("会议记录08.txt", summary);
        Assert.DoesNotContain("会议记录09.txt", summary);
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Theory]
    [InlineData("打开", "WaitingForFile")]
    [InlineData("找", "Failed")]
    public async Task IncompleteEnumerationNeverClaimsUniqueOrComplete(string action, string phase)
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.txt"], catalog => new IncompleteCatalog(catalog));
        var (_, input) = await fixture.SubmitAsync($"{action}桌面上的预算");
        var snapshot = await WaitForPhaseAsync(fixture.Client, input.TurnId, phase);
        var turn = Assert.Single(snapshot.Turns);
        Assert.Contains("尚未完成完整查找", turn.ResultSummary);
        Assert.Contains("没有打开任何文件", turn.ResultSummary);
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Fact]
    public async Task RestartDoesNotReplayAnOpenWaitingForConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.txt"]);
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算");
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");
        await fixture.RestartAsync();
        var restored = await fixture.Client.GetCurrentSessionAsync();
        Assert.NotNull(restored);
        Assert.Equal(session.SessionId, restored.SessionId);
        Assert.Empty(fixture.Launcher.Opened);
        var turn = Assert.Single(restored.Turns);
        Assert.Equal("WaitingForConfirmation", turn.Phase);
        await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "Failed");
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true));
        Assert.Empty(fixture.Launcher.Opened);
    }

    [Fact]
    public async Task ConfirmationReplanningCannotReplaceThePreviouslyShownFingerprint()
    {
        await using var fixture = await Fixture.CreateAsync(["年度预算.txt"],
            catalog => new ChangesAfterFirstValidationCatalog(catalog));
        var (session, input) = await fixture.SubmitAsync("打开桌面上的预算");
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "WaitingForConfirmation");

        await fixture.Client.ConfirmSessionTurnAsync(session.SessionId, input.TurnId, true);
        await WaitForPhaseAsync(fixture.Client, input.TurnId, "Failed");

        Assert.Empty(fixture.Launcher.Opened);
    }

    [Fact]
    public async Task CancellationDuringLookupRejectsTheLateResultAndDoesNotReplayThePlan()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(["年度预算.txt"],
            catalog => new CancellingCatalog(catalog, cancellation));
        var commands = fixture.Host.Services.GetRequiredService<AssistantCommandService>();
        var plan = await commands.PlanAsync(new("找桌面上的预算"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.ExecuteAsync(
            new(plan.PlanId, false, "cancelled-find"), cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.ExecuteAsync(
            new(plan.PlanId, false, "cancelled-find")));
        Assert.Empty(fixture.Launcher.Opened);
    }

    private static async Task<SessionSnapshotDto> WaitForPhaseAsync(IDesktopApiClient client, Guid turnId, string phase)
    {
        SessionSnapshotDto? last = null;
        for (var attempt = 0; attempt < 160; attempt++)
        {
            last = await client.GetCurrentSessionAsync();
            if (last?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase == phase) return last;
            await Task.Delay(25);
        }
        Assert.Fail($"Expected {phase}; got {last?.Turns.SingleOrDefault(turn => turn.Id == turnId)?.Phase}.");
        return last!;
    }

    private sealed class IncompleteCatalog(IDesktopFileCatalog inner) : IDesktopFileCatalog
    {
        public DesktopFileResolution Resolve(string name) => inner.Resolve(name);
        public void RequireUnchanged(DesktopFileBinding binding) => inner.RequireUnchanged(binding);
        public DesktopFileSearchResult Find(string name) => new([], 0, false, "无法完整检查桌面目录。");
    }

    private sealed class CancellingCatalog(IDesktopFileCatalog inner, CancellationTokenSource cancellation) : IDesktopFileCatalog
    {
        public DesktopFileResolution Resolve(string name) => inner.Resolve(name);
        public void RequireUnchanged(DesktopFileBinding binding) => inner.RequireUnchanged(binding);
        public DesktopFileSearchResult Find(string name)
        {
            var result = inner.Find(name);
            cancellation.Cancel();
            return result;
        }
    }

    private sealed class ChangesAfterFirstValidationCatalog(IDesktopFileCatalog inner) : IDesktopFileCatalog
    {
        private bool _changed;
        public DesktopFileResolution Resolve(string name) => inner.Resolve(name);
        public DesktopFileSearchResult Find(string name) => inner.Find(name);
        public DesktopFileResolution BindSelected(string name, string path) => inner.BindSelected(name, path);
        public void RequireUnchanged(DesktopFileBinding binding)
        {
            inner.RequireUnchanged(binding);
            if (_changed) return;
            _changed = true;
            File.AppendAllText(binding.FullPath, "changed between validation and replanning");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(DesktopHostTestEnvironment environment, string desktop, IDesktopFileCatalog catalog)
        {
            Environment = environment;
            Desktop = desktop;
            Catalog = catalog;
            Host = BuildHost();
            Client = new DesktopApiClient(environment.Options.PipeName);
        }

        public DesktopHostTestEnvironment Environment { get; }
        public string Desktop { get; }
        public IDesktopFileCatalog Catalog { get; }
        public Launcher Launcher { get; } = new();
        public IHost Host { get; private set; }
        public IDesktopApiClient Client { get; }
        public string PathFor(string name) => Path.Combine(Desktop, name);

        public static async Task<Fixture> CreateAsync(string[] names, Func<IDesktopFileCatalog, IDesktopFileCatalog>? wrap = null)
        {
            var environment = DesktopHostTestEnvironment.Create();
            var desktop = Path.Combine(environment.RootDirectory, "fixture-desktop");
            Directory.CreateDirectory(desktop);
            foreach (var name in names) await File.WriteAllTextAsync(Path.Combine(desktop, name), "fixture only");
            IDesktopFileCatalog catalog = new DesktopFileCatalog([desktop]);
            var fixture = new Fixture(environment, desktop, wrap?.Invoke(catalog) ?? catalog);
            await fixture.Host.StartAsync();
            return fixture;
        }

        private IHost BuildHost() => Environment.BuildHost(services =>
        {
            services.AddSingleton(Catalog);
            services.AddSingleton<IDesktopProcessLauncher>(Launcher);
        });

        public async Task<(SessionSnapshotDto Session, SessionTurnCommandResultDto Input)> SubmitAsync(string text, string modality = "Text")
        {
            var session = await Client.StartNewSessionAsync("桌面文档测试");
            var input = await Client.SubmitSessionInputAsync(new(text, modality, Guid.NewGuid().ToString("N"), session.SessionId));
            return (session, input);
        }

        public async Task RestartAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            Host = BuildHost();
            await Host.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            await Environment.DisposeAsync();
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
