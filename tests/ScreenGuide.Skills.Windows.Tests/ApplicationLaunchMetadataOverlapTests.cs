using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class ApplicationLaunchMetadataOverlapTests
{
    [Fact]
    public async Task ReleaseLookupStartsAfterLaunchAndBeforeItsWindowBecomesReady()
    {
        using var fixture = new Fixture();
        var lookedUpBeforeWindow = false;
        fixture.Driver.BeforeWindowReady = () => lookedUpBeforeWindow = fixture.Resolver.Calls == 1;
        fixture.Resolver.Resolve = _ =>
        {
            Assert.True(fixture.Driver.Launched);
            return Task.FromResult(Release());
        };

        var result = await fixture.Execute();

        Assert.True(lookedUpBeforeWindow);
        Assert.True(result.ResultsVerified);
        Assert.Single(fixture.Driver.Queries);
    }

    [Fact]
    public async Task FailedOpenCancelsAndObservesStartedLookupWithoutWriting()
    {
        using var fixture = new Fixture();
        var cancelled = false;
        fixture.Resolver.Resolve = async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Release();
        };
        fixture.Driver.BeforeWindowReady = () => throw new ApplicationSearchWorkflowException("open_failed", "启动失败");
        var failure = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Execute());
        Assert.Equal("open_failed", failure.Code);
        Assert.True(cancelled);
        Assert.Empty(fixture.Driver.Queries);
    }

    [Fact]
    public async Task LateMetadataAfterLaunchCancellationCannotWrite()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var metadata = new TaskCompletionSource<LatestMusicResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolver.Resolve = _ => metadata.Task;
        fixture.Driver.BeforeWindowReady = () => { cancellation.Cancel(); metadata.SetResult(Release()); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Execute(cancellation.Token));
        Assert.Equal(1, fixture.Resolver.Calls);
        Assert.Empty(fixture.Driver.Queries);
    }

    [Fact]
    public async Task ChangedRegisteredTargetAtLaunchCannotStartLookup()
    {
        using var fixture = new Fixture();
        fixture.Driver.BeforeLaunchNotification = () => fixture.Catalog.Application =
            fixture.Catalog.Application with { LaunchTarget = Environment.ProcessPath! };
        var failure = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Execute());
        Assert.Equal("application_search_target_changed", failure.Code);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Driver.Queries);
    }

    [Fact]
    public async Task UnsupportedExecutableCannotStartLookupFromItsDisplayName()
    {
        using var fixture = new Fixture("editor.exe");
        fixture.Catalog.Application = fixture.Catalog.Application with { DisplayName = "网易云音乐" };
        fixture.Driver.ProcessName = "editor";
        var failure = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Execute());
        Assert.Equal("latest_music_application_unsupported", failure.Code);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Driver.Queries);
    }

    [Fact]
    public async Task LiteralSearchNeverStartsMetadataAndDuplicateLaunchNotificationDoesNotRepeatLookup()
    {
        using var fixture = new Fixture();
        fixture.Driver.RepeatLaunchNotification = true;
        await fixture.Execute(latest: false);
        Assert.Equal(0, fixture.Resolver.Calls);
        await fixture.Execute();
        Assert.Equal(1, fixture.Resolver.Calls);
    }

    private static LatestMusicResolution Release() => new("测试歌手", "晨光", new DateOnly(2026, 9, 1),
        ["晨光"], "测试歌手 晨光", false, ["https://music.apple.com/tw/album/example/123"]);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sg-launch-metadata-" + Guid.NewGuid().ToString("N"));
        public Catalog Catalog { get; }
        public Driver Driver { get; } = new();
        public Resolver Resolver { get; } = new();
        public Fixture(string executable = "cloudmusic.exe")
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, executable);
            File.Copy(Environment.ProcessPath!, path);
            Catalog = new(new("registered-music", "测试音乐", path));
        }
        public Task<ApplicationSearchWorkflowResult> Execute(CancellationToken ct = default, bool latest = true)
        {
            var app = Catalog.Application;
            return new WindowsApplicationSearchWorkflowAutomation(Catalog, Driver, TimeSpan.Zero, Resolver)
                .ExecuteAsync(app, InstalledApplicationCatalog.CreateTargetBinding(app),
                    new(app.Id, "测试歌手最新的歌", ApplicationSearchFollowUp.None, latest ? "测试歌手" : null), ct);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
    private sealed class Catalog(KnownDesktopApplication application) : IInstalledApplicationCatalog
    {
        public KnownDesktopApplication Application { get; set; } = application;
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [Application];
        public KnownDesktopApplication? FindById(string id) => id == Application.Id ? Application : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == Application.DisplayName ? Application : null;
        public KnownDesktopApplication? FindBrowser(string name) => null;
    }
    private sealed class Resolver : ILatestMusicResolver
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<LatestMusicResolution>> Resolve { get; set; } = _ => Task.FromResult(Release());
        public Task<LatestMusicResolution> ResolveAsync(string artist, CancellationToken ct = default)
        { Calls++; return Resolve(ct); }
    }
    private sealed class Driver : IApplicationSearchWorkflowDriver
    {
        public bool Launched { get; private set; }
        public bool WindowReady { get; private set; }
        public Action? BeforeWindowReady { get; set; }
        public Action? BeforeLaunchNotification { get; set; }
        public string ProcessName { get; set; } = "cloudmusic";
        public bool RepeatLaunchNotification { get; set; }
        public List<string> Queries { get; } = [];
        public ForegroundWindowSnapshot Open(KnownDesktopApplication app, CancellationToken ct) => OpenCore(ct, null);
        public ForegroundWindowSnapshot Open(KnownDesktopApplication app, CancellationToken ct, Action launched) => OpenCore(ct, launched);
        private ForegroundWindowSnapshot OpenCore(CancellationToken ct, Action? launched)
        {
            ct.ThrowIfCancellationRequested();
            Launched = true;
            BeforeLaunchNotification?.Invoke();
            launched?.Invoke();
            if (RepeatLaunchNotification) launched?.Invoke();
            BeforeWindowReady?.Invoke();
            WindowReady = true;
            return new(42, "测试音乐", ProcessName, 123, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        }
        public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication app, ForegroundWindowSnapshot window, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return window; }
        public void Search(ForegroundWindowSnapshot window, string query, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Queries.Add(query); }
        public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken ct) =>
            new(SearchResultPageState.Results, query, "page", [new("item", "晨光", new object())]);
        public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp, Action validate, CancellationToken ct) => throw new NotSupportedException();
        public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp, CancellationToken ct) => false;
    }
}
