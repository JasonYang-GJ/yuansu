using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class LatestMusicWorkflowTests
{
    private static readonly KnownDesktopApplication App = new("music", "测试音乐", Environment.ProcessPath!);
    private static string Binding => InstalledApplicationCatalog.CreateTargetBinding(App);
    private static ApplicationSearchWorkflow Request(ApplicationSearchFollowUp followUp = ApplicationSearchFollowUp.None) =>
        new(App.Id, "测试歌手最新的歌", followUp, "测试歌手");
    private static LatestMusicResolution Release(bool available = false) =>
        new("测试歌手", "晨光", new DateOnly(2026, 9, 1), ["晨光"], "测试歌手 晨光", available,
            ["https://music.apple.com/tw/album/example/123"]);

    [Fact]
    public async Task OpensBeforeLookupAndPreparesInputWhileReleaseMetadataIsInFlight()
    {
        var metadata = new TaskCompletionSource<LatestMusicResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new Driver();
        var resolver = new Resolver { Resolve = _ =>
        {
            Assert.True(driver.Opened);
            return metadata.Task;
        } };
        var operation = Executor(driver, resolver).ExecuteAsync(App, Binding, Request());
        var preparedBeforeMetadata = driver.Prepared;
        Assert.Empty(driver.Queries);
        metadata.SetResult(Release());
        await operation;
        Assert.True(preparedBeforeMetadata);
    }

    [Fact]
    public async Task ReportsActualExecutionStagesInOrderWithoutWaitingForFinalResponse()
    {
        var driver = new Driver();
        var progress = new ProgressSink(driver);
        var operation = new WindowsApplicationSearchWorkflowAutomation(new Catalog(), driver, TimeSpan.Zero, new Resolver(), progress);
        await operation.ExecuteAsync(App, Binding, Request());
        Assert.Equal([ApplicationSearchStage.Opening, ApplicationSearchStage.Opened,
            ApplicationSearchStage.ResolvingMusic, ApplicationSearchStage.Searching,
            ApplicationSearchStage.VerifyingResults], progress.Stages);
    }

    [Fact]
    public async Task PreparationFailureCancelsInflightLookupAndNeverWrites()
    {
        var cancelled = false;
        var resolver = new Resolver { Resolve = async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Release();
        } };
        var driver = new Driver { Prepare = _ => throw new ApplicationSearchWorkflowException("window_changed", "窗口变化") };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request()));
        Assert.Equal("window_changed", error.Code);
        Assert.True(cancelled);
        Assert.Empty(driver.Queries);
    }

    [Fact]
    public async Task LatestLookupRewritesOnlyAfterMetadataAndKeepsDateAndAvailabilityHonest()
    {
        var resolver = new Resolver();
        var driver = new Driver();
        var result = await Executor(driver, resolver).ExecuteAsync(App, Binding, Request());
        Assert.Equal(["测试歌手"], resolver.Artists);
        Assert.Equal(["测试歌手 晨光"], driver.Queries);
        Assert.Contains("2026-09-01", result.Summary);
        Assert.Contains("未", result.Summary);
        Assert.NotNull(result.LatestMusic);
        Assert.False(result.LatestMusic.PlatformMatchFound);
        Assert.True(result.ResultsVerified);
    }

    [Fact]
    public async Task LiteralSearchNeverCallsReleaseServices()
    {
        var resolver = new Resolver();
        var driver = new Driver();
        var result = await Executor(driver, resolver).ExecuteAsync(App, Binding, Request() with { LatestMusicArtist = null });
        Assert.Empty(resolver.Artists);
        Assert.Equal(["测试歌手最新的歌"], driver.Queries);
        Assert.Null(result.LatestMusic);
    }

    [Fact]
    public async Task UnavailableProviderDoesNotFallBackToLiteralSearchOrInventDates()
    {
        var resolver = new Resolver { Resolve = _ => Task.FromException<LatestMusicResolution>(
            new ApplicationSearchWorkflowException("latest_music_unavailable", "发行资料暂时不可用。")) };
        var driver = new Driver();
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request()));
        Assert.Equal("latest_music_unavailable", error.Code);
        Assert.Empty(driver.Queries);
    }

    [Fact]
    public async Task CancelDuringLookupNeverWritesSearch()
    {
        using var cancel = new CancellationTokenSource();
        var resolver = new Resolver { Resolve = async ct => { cancel.Cancel(); await Task.Delay(100, ct); return Release(); } };
        var driver = new Driver();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request(), cancel.Token));
        Assert.Empty(driver.Queries);
    }

    [Fact]
    public async Task LateMetadataAfterCancellationNeverReachesSearch()
    {
        using var cancel = new CancellationTokenSource();
        var resolver = new Resolver { Resolve = _ => { cancel.Cancel(); return Task.FromResult(Release()); } };
        var driver = new Driver();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request(), cancel.Token));
        Assert.Empty(driver.Queries);
    }

    [Fact]
    public async Task CatalogSongMatchDoesNotProveThatTheFirstUiCardIsTheOriginal()
    {
        var driver = new Driver();
        var resolver = new Resolver { Resolve = _ => Task.FromResult(Release(true)) };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst)));
        Assert.Equal("latest_music_followup_unverified", error.Code);
        Assert.Equal(0, driver.Invocations);
    }

    [Fact]
    public async Task PlatformNotVerifiedCannotPlayFirstCoverInstead()
    {
        var driver = new Driver();
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, new()).ExecuteAsync(App, Binding, Request(ApplicationSearchFollowUp.PlayFirst)));
        Assert.Equal("latest_music_followup_unverified", error.Code);
        Assert.Equal(0, driver.Invocations);
    }

    [Fact]
    public async Task DifferentApplicationCannotReceiveMusicSpecificSearch()
    {
        var driver = new Driver { ProcessName = "editor" };
        await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, new()).ExecuteAsync(App, Binding, Request()));
        Assert.Empty(driver.Queries);
    }

    [Fact]
    public async Task BoundWindowIsRecheckedAfterNetworkWait()
    {
        var driver = new Driver();
        var resolver = new Resolver { Resolve = _ => { driver.Invalidated = true; return Task.FromResult(Release()); } };
        await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => Executor(driver, resolver).ExecuteAsync(App, Binding, Request()));
        Assert.Empty(driver.Queries);
    }

    private static WindowsApplicationSearchWorkflowAutomation Executor(Driver driver, Resolver resolver) =>
        new(new Catalog(), driver, TimeSpan.Zero, resolver);

    private sealed class Resolver : ILatestMusicResolver
    {
        public List<string> Artists { get; } = [];
        public Func<CancellationToken, Task<LatestMusicResolution>> Resolve { get; init; } = _ => Task.FromResult(Release());
        public Task<LatestMusicResolution> ResolveAsync(string artist, CancellationToken cancellationToken = default)
        { Artists.Add(artist); return Resolve(cancellationToken); }
    }

    private sealed class Catalog : IInstalledApplicationCatalog
    {
        public IReadOnlyList<KnownDesktopApplication> GetApplications() => [App];
        public KnownDesktopApplication? FindById(string id) => id == App.Id ? App : null;
        public KnownDesktopApplication? FindByDisplayName(string name) => name == App.DisplayName ? App : null;
        public KnownDesktopApplication? FindBrowser(string browserName) => null;
    }

    private sealed class Driver : IApplicationSearchWorkflowDriver
    {
        public string ProcessName { get; init; } = "cloudmusic";
        public List<string> Queries { get; } = [];
        public int Invocations { get; private set; }
        public bool Invalidated { get; set; }
        public bool Opened { get; private set; }
        public bool Prepared { get; private set; }
        public Action<CancellationToken>? Prepare { get; init; }
        public ForegroundWindowSnapshot Open(KnownDesktopApplication application, CancellationToken ct)
        {
            Opened = true;
            return new(42, "测试音乐", ProcessName, 123, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        }
        public void PrepareSearch(ForegroundWindowSnapshot window, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Prepare?.Invoke(ct); Prepared = true; }
        public ForegroundWindowSnapshot RequireBound(KnownDesktopApplication app, ForegroundWindowSnapshot window, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (Invalidated) throw new ApplicationSearchWorkflowException("window_changed", "窗口变化"); return window; }
        public void Search(ForegroundWindowSnapshot window, string query, CancellationToken ct) => Queries.Add(query);
        public SearchResultPage ReadResults(ForegroundWindowSnapshot window, string query, CancellationToken ct) =>
            new(SearchResultPageState.Results, query, "page", [new("cover", "晨光", new object())]);
        public void Invoke(ForegroundWindowSnapshot window, SearchResultPage page, ApplicationSearchFollowUp followUp, Action validate, CancellationToken ct)
        { validate(); Invocations++; }
        public bool VerifyFollowUp(ForegroundWindowSnapshot window, SearchResultItem item, ApplicationSearchFollowUp followUp, CancellationToken ct) => true;
    }

    private sealed class ProgressSink(Driver driver) : IApplicationSearchProgressSink
    {
        public List<ApplicationSearchStage> Stages { get; } = [];
        public Task ReportAsync(ApplicationSearchProgress progress, CancellationToken cancellationToken = default)
        {
            Assert.Equal(App.DisplayName, progress.ApplicationName);
            if (progress.Stage == ApplicationSearchStage.Opening) Assert.False(driver.Opened);
            else Assert.True(driver.Opened);
            if (progress.Stage == ApplicationSearchStage.VerifyingResults) Assert.NotEmpty(driver.Queries);
            else Assert.Empty(driver.Queries);
            Stages.Add(progress.Stage);
            return Task.CompletedTask;
        }
    }
}
