namespace ScreenGuide.Skills.Windows.Tests;

public sealed class InstalledApplicationCatalogConcurrencyTests
{
    [Fact]
    public async Task PreferenceWaitingForTheInitialSnapshotDoesNotImmediatelyDiscoverAgain()
    {
        using var discoveryEntered = new ManualResetEventSlim();
        using var allowDiscovery = new ManualResetEventSlim();
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            if (Interlocked.Increment(ref discoveries) == 1)
            {
                discoveryEntered.Set();
                if (!allowDiscovery.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture discovery was not released.");
            }
            return [new("网易云音乐", Environment.ProcessPath!, ApplicationRegistrationSource.AppPath)];
        });
        var initialSnapshot = Task.Run(catalog.GetApplications);
        Assert.True(discoveryEntered.Wait(TimeSpan.FromSeconds(5)));
        Exception? preferenceFailure = null;
        var preferenceThread = StartBlockedReader(() =>
        {
            try { _ = catalog.ResolveApplicationPreference("网易云，搜索周杰伦最新的歌"); }
            catch (Exception exception) { preferenceFailure = exception; }
        }, allowDiscovery);
        _ = await initialSnapshot;
        Assert.True(preferenceThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(InstalledApplicationErrorCodes.NotFound,
            Assert.IsType<InstalledApplicationResolutionException>(preferenceFailure).Code);
        Assert.Equal(Environment.ProcessPath, catalog.ResolveByDisplayName("网易云").LaunchTarget);
        Assert.Equal(1, discoveries);
    }

    [Fact]
    public async Task PreferenceWaitingForAnotherRequestsRefreshUsesThatCompletedSnapshot()
    {
        using var refreshEntered = new ManualResetEventSlim();
        using var allowRefresh = new ManualResetEventSlim();
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            if (Interlocked.Increment(ref discoveries) == 2)
            {
                refreshEntered.Set();
                if (!allowRefresh.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture refresh was not released.");
            }
            return [];
        });
        _ = catalog.GetApplications();
        var otherRequest = Task.Run(() => Assert.Throws<InstalledApplicationResolutionException>(() =>
            catalog.ResolveByDisplayName("其他尚未安装的程序")));
        Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(5)));
        Exception? preferenceFailure = null;
        var preferenceThread = StartBlockedReader(() =>
        {
            try { _ = catalog.ResolveApplicationPreference("网易云，搜索周杰伦最新的歌"); }
            catch (Exception exception) { preferenceFailure = exception; }
        }, allowRefresh);
        Assert.Equal(InstalledApplicationErrorCodes.NotFound, (await otherRequest).Code);
        Assert.True(preferenceThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(InstalledApplicationErrorCodes.NotFound,
            Assert.IsType<InstalledApplicationResolutionException>(preferenceFailure).Code);
        Assert.Equal(2, discoveries);
    }

    [Fact]
    public async Task OrdinaryResolutionKeepsItsRefreshBehaviorAfterWaitingForAnInitialSnapshot()
    {
        using var discoveryEntered = new ManualResetEventSlim();
        using var allowDiscovery = new ManualResetEventSlim();
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            if (Interlocked.Increment(ref discoveries) == 1)
            {
                discoveryEntered.Set();
                if (!allowDiscovery.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture discovery was not released.");
                return [];
            }
            return [new("新安装的完整软件名", Environment.ProcessPath!, ApplicationRegistrationSource.AppPath)];
        });
        var initialSnapshot = Task.Run(catalog.GetApplications);
        Assert.True(discoveryEntered.Wait(TimeSpan.FromSeconds(5)));
        KnownDesktopApplication? resolved = null;
        Exception? failure = null;
        var readerThread = StartBlockedReader(() =>
        {
            try { resolved = catalog.ResolveByDisplayName("新安装的完整软件名"); }
            catch (Exception exception) { failure = exception; }
        }, allowDiscovery);
        _ = await initialSnapshot;
        Assert.True(readerThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Null(failure);
        Assert.Equal(Environment.ProcessPath, Assert.IsType<KnownDesktopApplication>(resolved).LaunchTarget);
        Assert.Equal(2, discoveries);
    }

    [Fact]
    public async Task AFailedConcurrentRefreshDoesNotMarkTheOlderSnapshotFresh()
    {
        using var refreshEntered = new ManualResetEventSlim();
        using var allowRefresh = new ManualResetEventSlim();
        var discoveries = 0;
        var catalog = new InstalledApplicationCatalog(() =>
        {
            var call = Interlocked.Increment(ref discoveries);
            if (call == 1) return [];
            if (call == 2)
            {
                refreshEntered.Set();
                if (!allowRefresh.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture refresh was not released.");
                throw new InvalidOperationException("Fixture refresh failed before publishing.");
            }
            return [new("微信搜索助手", Environment.ProcessPath!, ApplicationRegistrationSource.AppPath)];
        });
        _ = catalog.GetApplications();
        var failedRefresh = Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            catalog.ResolveByDisplayName("其他尚未安装的程序")));
        Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(5)));
        KnownDesktopApplication? resolved = null;
        Exception? failure = null;
        var preferenceThread = StartBlockedReader(() =>
        {
            try { resolved = catalog.ResolveApplicationPreference("微信搜索助手"); }
            catch (Exception exception) { failure = exception; }
        }, allowRefresh);
        _ = await failedRefresh;
        Assert.True(preferenceThread.Join(TimeSpan.FromSeconds(5)));

        Assert.Null(failure);
        Assert.Equal("微信搜索助手", Assert.IsType<KnownDesktopApplication>(resolved).DisplayName);
        Assert.Equal(3, discoveries);
    }

    private static Thread StartBlockedReader(Action read, ManualResetEventSlim releaseDiscovery)
    {
        var thread = new Thread(() => read()) { IsBackground = true, Name = "CatalogConcurrentReaderFixture" };
        try
        {
            thread.Start();
            // The discover delegate holds the catalog lock. The reader has no other waits:
            // wait until it is actually blocked on that lock, rather than relying on a delay.
            Assert.True(SpinWait.SpinUntil(() =>
                (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "The reader did not reach the held catalog lock.");
            return thread;
        }
        finally { releaseDiscovery.Set(); }
    }
}
